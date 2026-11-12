using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace RoboDust;

public enum Kind { Folder, Image, Video, Document, Archive, Audio, Other }

public static class KindInfo
{
    static readonly string[] image = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".heic", ".heif", ".webp", ".tif", ".tiff", ".svg", ".ico", ".raw", ".psd", ".avif" };
    static readonly string[] video = { ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".flv" };
    static readonly string[] audio = { ".mp3", ".wav", ".m4a", ".flac", ".aac", ".ogg", ".wma", ".opus", ".aiff" };
    static readonly string[] archive = { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso", ".cab", ".tgz" };
    static readonly string[] document = { ".pdf", ".txt", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".log", ".pages", ".numbers", ".key", ".epub" };

    public static Kind Of(string name, bool isDirectory)
    {
        if (isDirectory) return Kind.Folder;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (image.Contains(ext)) return Kind.Image;
        if (video.Contains(ext)) return Kind.Video;
        if (audio.Contains(ext)) return Kind.Audio;
        if (archive.Contains(ext)) return Kind.Archive;
        if (document.Contains(ext)) return Kind.Document;
        return Kind.Other;
    }
}

/// One thing on the desktop, as far as drawing is concerned. Holds nothing that can change a file.
public sealed class DesktopEntry
{
    public string Name;
    public V2 Center;               // icon centre, points, top-left of the work area
    public long Bytes;
    public Kind Kind;
    public string Path;             // null for sample items
    public byte[] Pidl;             // the shell's id for the item, to ask for its icon
    public Bitmap Image;            // sample items carry their picture
    public DateTime? Modified;
    public string ProtectedReason;  // Move and Hide leave these alone
    public bool Missing;            // shown on the desktop without being a file in the Desktop folder
    public bool Cloud;              // in a OneDrive-synced folder
    public bool Hidden;             // already hidden, and only visible because hidden files are shown
    public bool CloudOnly;          // a OneDrive placeholder with nothing on this PC yet
    public POINT? ViewPos;          // where Explorer has the item, in the desktop view's pixels
}

public sealed class DesktopException : Exception
{
    public readonly bool NoItems;
    public DesktopException(string message, bool noItems = false) : base(message) { NoItems = noItems; }
}

/// The primary display, as RoboDust uses it: pixels, and the scale to points.
public sealed class Screen
{
    public Rectangle Bounds, WorkArea;   // physical pixels
    public float Scale;                  // pixels per point
    public V2 SizePts => new(WorkArea.Width / Scale, WorkArea.Height / Scale);

    public static Screen Primary()
    {
        var p = System.Windows.Forms.Screen.PrimaryScreen;
        return new Screen { Bounds = p.Bounds, WorkArea = p.WorkingArea, Scale = Native.PrimaryScale() };
    }
}

/// Where the real icons are, and what the desktop view will let RoboDust do with them.
public sealed class DesktopView
{
    public IFolderView View;
    public IntPtr ListView;
    public float IconPx = 48;
    public POINT Spacing = new(75, 75);
    public bool AutoArrange;
    public bool SnapsToGrid;

    /// Item position to icon centre, both in list-view pixels. Explorer reports the top-left of the
    /// icon itself, not of its cell: the first column sits at (spacing - icon) / 2.
    public POINT CenterOffset => new((int)MathF.Round(IconPx / 2), (int)MathF.Round(IconPx / 2 + 2 * Native.PrimaryScale()));

    public POINT Origin { get { var o = new POINT(0, 0); Native.ClientToScreen(ListView, ref o); return o; } }

    /// An icon centre in RoboDust's points, as the item position Explorer wants.
    public POINT ToViewPos(V2 centerPts, Screen screen)
    {
        var o = Origin; var off = CenterOffset;
        return new POINT((int)MathF.Round(centerPts.X * screen.Scale + screen.WorkArea.Left - o.X - off.X),
                         (int)MathF.Round(centerPts.Y * screen.Scale + screen.WorkArea.Top - o.Y - off.Y));
    }

    /// Paths of the items the view currently shows. Read-only.
    public HashSet<string> Paths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ForEachItem((path, _) => set.Add(path));
        return set;
    }

    void ForEachItem(Action<string, IntPtr> body)
    {
        if (View.ItemCount(Native.SVGIO_ALLVIEW, out var count) != 0) return;
        var iid = Native.IID_IShellItem;
        for (int i = 0; i < count; i++)
        {
            if (View.Item(i, out var pidl) != 0 || pidl == IntPtr.Zero) continue;
            try
            {
                if (Native.SHCreateItemFromIDList(pidl, ref iid, out var obj) != 0 || obj is not IShellItem si) continue;
                string path = null;
                if (si.GetDisplayName(Native.SIGDN_FILESYSPATH, out var p) == 0) path = Native.TakeString(p);
                Marshal.ReleaseComObject(si);
                if (path != null) body(Path.GetFullPath(path).TrimEnd('\\'), pidl);
            }
            finally { Native.ILFree(pidl); }
        }
    }

    /// Moves icons, never files. Returns how many Explorer accepted.
    public int SetPositions(List<(string path, POINT pos)> moves)
    {
        var want = new Dictionary<string, POINT>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, pos) in moves) want[Path.GetFullPath(p).TrimEnd('\\')] = pos;
        int done = 0;
        ForEachItem((path, pidl) =>
        {
            if (!want.TryGetValue(path, out var pos)) return;
            if (View.SelectAndPositionItems(1, new[] { pidl }, new[] { pos }, Native.SVSI_POSITIONITEM) == 0) done++;
        });
        return done;
    }
}

public static class DesktopReader
{
    public static string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static DesktopView OpenView()
    {
        var swType = Type.GetTypeFromCLSID(Native.CLSID_ShellWindows);
        var sw = (IShellWindows)Activator.CreateInstance(swType);
        object loc = 0, root = null;
        var disp = sw.FindWindowSW(ref loc, ref root, Native.SWC_DESKTOP, out _, Native.SWFO_NEEDDISPATCH);
        if (disp == null) throw new DesktopException("Der Desktop von Explorer antwortet nicht.");
        var sid = Native.SID_STopLevelBrowser; var iid = Native.IID_IShellBrowser;
        if (((IServiceProvider)disp).QueryService(ref sid, ref iid, out var browserObj) != 0)
            throw new DesktopException("Der Desktop von Explorer antwortet nicht.");
        var browser = (IShellBrowser)browserObj;
        if (browser.QueryActiveShellView(out var viewObj) != 0 || viewObj is not IFolderView fv)
            throw new DesktopException("Der Desktop von Explorer antwortet nicht.");
        var v = new DesktopView { View = fv };
        ((IShellView)viewObj).GetWindow(out var defView);
        v.ListView = Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        if (v.ListView == IntPtr.Zero) v.ListView = defView;
        var sp = new POINT();
        if (fv.GetSpacing(ref sp) == 0 && sp.X > 0) v.Spacing = sp;
        v.AutoArrange = fv.GetAutoArrange() == 0;
        if (viewObj is IFolderView2 f2)
        {
            // Explorer reports the size before display scaling: 48 is "Mittelgroße Symbole".
            if (f2.GetViewModeAndIconSize(out _, out var size) == 0 && size > 0 && size <= 256) v.IconPx = size * Native.PrimaryScale();
            if (f2.GetCurrentFolderFlags(out var flags) == 0) { v.SnapsToGrid = (flags & 0x00080000) != 0; v.AutoArrange |= (flags & 0x1) != 0; }
        }
        return v;
    }

    /// Read-only: asks Explorer for the desktop's items and where their icons sit.
    public static List<DesktopEntry> Read(Screen screen, out DesktopView view)
    {
        view = OpenView();
        var fv = view.View;
        if (fv.ItemCount(Native.SVGIO_ALLVIEW, out var count) != 0) throw new DesktopException("Der Desktop von Explorer antwortet nicht.");
        if (count == 0) throw new DesktopException("Leer", noItems: true);
        var origin = new POINT(0, 0);
        Native.ClientToScreen(view.ListView, ref origin);
        var off = view.CenterOffset;
        var desk = Path.GetFullPath(DesktopFolder).TrimEnd('\\');
        var showHidden = HiddenFilesShown();
        var result = new List<DesktopEntry>();
        var iid = Native.IID_IShellItem;
        for (int i = 0; i < count; i++)
        {
            if (fv.Item(i, out var pidl) != 0 || pidl == IntPtr.Zero) continue;
            try
            {
                if (fv.GetItemPosition(pidl, out var pt) != 0) continue;
                var sx = origin.X + pt.X + off.X - screen.WorkArea.Left;
                var sy = origin.Y + pt.Y + off.Y - screen.WorkArea.Top;
                var c = new V2(sx / screen.Scale, sy / screen.Scale);
                var size = screen.SizePts;
                if (c.X < 0 || c.Y < 0 || c.X > size.X || c.Y > size.Y) continue;
                var bytes = new byte[Native.ILGetSize(pidl)];
                Marshal.Copy(pidl, bytes, 0, bytes.Length);
                string name = null, path = null;
                if (Native.SHCreateItemFromIDList(pidl, ref iid, out var itemObj) == 0 && itemObj is IShellItem si)
                {
                    if (si.GetDisplayName(Native.SIGDN_NORMALDISPLAY, out var n) == 0) name = Native.TakeString(n);
                    if (si.GetDisplayName(Native.SIGDN_FILESYSPATH, out var p) == 0) path = Native.TakeString(p);
                    Marshal.ReleaseComObject(si);
                }
                var e = Entry(path, name ?? "?", c, desk, showHidden);
                e.Pidl = bytes;
                e.ViewPos = pt;
                result.Add(e);
            }
            finally { Native.ILFree(pidl); }
        }
        if (result.Count == 0) throw new DesktopException("Leer", noItems: true);
        return result;
    }

    public static bool HiddenFilesShown()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return (k?.GetValue("Hidden") as int?) == 1;
        }
        catch { return false; }
    }

    public static bool IsCloudPath(string path)
    {
        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var root = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrEmpty(root) && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static DesktopEntry Entry(string path, string name, V2 center, string desk, bool showHidden)
    {
        var e = new DesktopEntry { Name = name, Center = center, Path = path, Kind = Kind.Other, Bytes = 200_000 };
        if (path == null || !(File.Exists(path) || Directory.Exists(path)))
        {
            // This PC, the Recycle Bin and the like: shown on the desktop, but not files in it.
            e.Missing = true; e.ProtectedReason = "Keine Datei auf dem Desktop";
            e.Path = null;
            return e;
        }
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        var isDir = info is DirectoryInfo;
        e.Kind = KindInfo.Of(path, isDir);
        e.Bytes = isDir ? FolderBytes((DirectoryInfo)info) : ((FileInfo)info).Length;
        e.Modified = info.LastWriteTimeUtc;
        e.Cloud = IsCloudPath(path);
        var attrs = info.Attributes;
        e.Hidden = (attrs & FileAttributes.Hidden) != 0;
        // Recall-on-data-access (0x400000) or offline: moving it would download it first.
        e.CloudOnly = ((int)attrs & 0x400000) != 0 || (attrs & FileAttributes.Offline) != 0;
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd('\\');
        if (!string.Equals(parent, desk, StringComparison.OrdinalIgnoreCase)) e.ProtectedReason = "Gehört allen Benutzern dieses PCs";
        else if ((attrs & FileAttributes.System) != 0) e.ProtectedReason = "Systemdatei";
        else if (!isDir && (attrs & FileAttributes.ReadOnly) != 0) e.ProtectedReason = "Schreibgeschützt";
        else if ((DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds < 600) e.ProtectedReason = "In den letzten 10 Minuten geändert";
        else if (isDir && Directory.Exists(Path.Combine(path, ".git"))) e.ProtectedReason = "Code-Projekt";
        else if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) e.ProtectedReason = "Programm";
        return e;
    }

    /// Big folders should feel heavy, but counting a huge tree isn't worth the wait.
    static long FolderBytes(DirectoryInfo d)
    {
        long total = 0; int seen = 0;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in d.EnumerateFiles("*", opts))
            {
                total += f.Length;
                if (++seen > 4000) return Math.Max(total * 2, 40_000_000);
            }
        }
        catch { return 40_000_000; }
        return total;
    }

    /// Dev only: treats a throwaway folder as if it were the desktop.
    public static List<DesktopEntry> Sandbox(string root, V2 size)
    {
        var names = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Where(n => !n.StartsWith(".")).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        V2 Spot(int i) => new(size.X * 0.3f + (i % 8) * 110, 140 + (i / 8) * 110);
        var full = Path.GetFullPath(root).TrimEnd('\\');
        var showHidden = HiddenFilesShown();
        return names.Select((n, i) => Entry(Path.Combine(full, n), n, Spot(i), full, showHidden)).ToList();
    }

    /// A believable mess that contains nothing of the user's. Same code path after this point.
    public static List<DesktopEntry> Sample(V2 size)
    {
        var rng = new SeededRng(20261101);
        float W = size.X, H = size.Y;
        (string, Kind, long)[] specs =
        {
            ("final-final-v2.pdf", Kind.Document, 2_400_000), ("meeting-that-could-be-an-email.txt", Kind.Document, 3_000),
            ("Screenshot 2026-10-12 091422.png", Kind.Image, 900_000), ("Screenshot 2026-10-12 091431.png", Kind.Image, 880_000),
            ("Screenshot 2026-10-14 170211.png", Kind.Image, 1_400_000), ("Screenshot 2026-10-19 114708.png", Kind.Image, 760_000),
            ("Screenshot 2026-10-21 223051.png", Kind.Image, 1_100_000), ("Screenshot 2026-10-28 080540.png", Kind.Image, 650_000),
            ("Bildschirmaufnahme 2026-10-30 145030.mp4", Kind.Video, 4_200_000_000), ("weekend-take-12.mov", Kind.Video, 2_100_000_000),
            ("demo-for-nobody.mp4", Kind.Video, 880_000_000), ("backup-of-the-backup.zip", Kind.Archive, 6_300_000_000),
            ("old-site.zip", Kind.Archive, 310_000_000), ("taxes-2023.zip", Kind.Archive, 44_000_000),
            ("budget-snacks.xlsx", Kind.Document, 90_000), ("q3-slides-actually-final.pptx", Kind.Document, 38_000_000),
            ("do-not-delete", Kind.Folder, 900_000_000), ("to-delete-later", Kind.Folder, 120_000_000), ("desktop-cleaning", Kind.Folder, 40_000_000),
            ("icons", Kind.Folder, 15_000_000), ("misc", Kind.Folder, 80_000_000), ("misc (2)", Kind.Folder, 60_000_000), ("Neuer Ordner", Kind.Folder, 0),
            ("three-dollar-song.mp3", Kind.Audio, 7_000_000), ("voice-memo-idea.m4a", Kind.Audio, 3_000_000),
            ("IMG_2041.jpeg", Kind.Image, 3_800_000), ("IMG_2042.jpeg", Kind.Image, 3_600_000), ("IMG_2043.jpeg", Kind.Image, 4_100_000),
            ("blurry-pigeon.heic", Kind.Image, 2_200_000), ("coffee-receipt.jpg", Kind.Image, 400_000), ("logo-new-NEW.png", Kind.Image, 300_000),
            ("logo-old.png", Kind.Image, 220_000), ("notes.txt", Kind.Document, 1_000), ("notes - Kopie.txt", Kind.Document, 1_000),
            ("notes - Kopie (2).txt", Kind.Document, 1_000), ("invoice-sample.pdf", Kind.Document, 120_000), ("resume_v14.docx", Kind.Document, 210_000),
            ("ergonomic-chair.pdf", Kind.Document, 3_100_000), ("Neues Textdokument.txt", Kind.Document, 12), ("Neues Textdokument (2).txt", Kind.Document, 40),
            ("wallpaper-idea.png", Kind.Image, 5_400_000), ("mood-board.png", Kind.Image, 8_100_000),
        };
        var outList = new List<DesktopEntry>();
        // A tidy column on the left, as Windows lines them up, a dense pile in the middle, stragglers everywhere else.
        for (int i = 0; i < specs.Length; i++)
        {
            var s = specs[i];
            V2 c;
            var bucket = i % 5;
            if (bucket == 0)
            {
                float col = (i / 5) / 6, row = (i / 5) % 6;
                c = new V2(48 + col * 90, 50 + row * 100);
            }
            else if (bucket == 4) c = new V2(rng.Range(120, W - 120), rng.Range(110, H - 300));
            else
            {
                float a = rng.Range(0, 6.283f), d = rng.Range(0.25f, 1) * rng.Range(170, 420);
                c = new V2(W * 0.52f + MathF.Cos(a) * d * 1.5f, H * 0.42f + MathF.Sin(a) * d);
            }
            c.X = M.Clamp(c.X, 60, W - 60); c.Y = M.Clamp(c.Y, 50, H - 230);
            var img = s.Item2 == Kind.Image ? Sprites.SampleThumbnail(i, s.Item1.StartsWith("Screenshot")) : null;
            outList.Add(new DesktopEntry { Name = s.Item1, Center = c, Bytes = s.Item3, Kind = s.Item2, Image = img });
        }
        return outList;
    }
}
