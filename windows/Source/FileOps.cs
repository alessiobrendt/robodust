using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RoboDust;

/// The only code in RoboDust for Windows that changes anything on disk. It never deletes a file:
/// it either moves an item into a folder of its own ("Wegräumen") or sets its hidden attribute
/// ("Ausblenden"). Every item is checked again immediately before it is touched, anything that
/// fails a check is left alone and reported, and every run is written down so it can be undone,
/// even after RoboDust has been closed.
public static class FileOps
{
    public sealed class Target
    {
        public int Index; public string Path; public DateTime? Modified; public POINT? ViewPos;
    }

    public sealed class Record
    {
        public string Original { get; set; }
        public string Now { get; set; }          // where it is after the run (same path when hidden)
        public bool WasHidden { get; set; }      // already hidden before, so undo leaves it hidden
        public int? X { get; set; }
        public int? Y { get; set; }               // where its icon sat, in the desktop view's own pixels
    }

    public sealed class Run
    {
        public string Id { get; set; }
        public DateTime When { get; set; }
        public string Mode { get; set; }          // "move" or "hide"
        public string Folder { get; set; }        // where moved items went
        public List<Record> Items { get; set; } = new();
        public bool Undone { get; set; }
        public List<string> CreatedFolders { get; set; } = new();   // sort folders RoboDust made, removed again on undo if empty
    }

    public sealed class Outcome
    {
        public List<int> Done = new();
        public List<(int index, string reason)> Failed = new();
        public Run Run;
    }

    public static bool HistoryEnabled = true;
    /// Test runs keep their records next to the test folder, never in the real list.
    public static string DataDirOverride;

    public static string DataDir
    {
        get
        {
            var d = DataDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoboDust");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    // MARK: - Checks

    static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\');

    static FileSystemInfo Info(string path)
    {
        // Never follows links: a link is checked, moved or hidden as itself.
        var fi = new FileInfo(path);
        if (fi.Exists) return fi;
        var di = new DirectoryInfo(path);
        if (di.Exists) return di;
        if (fi.LinkTarget != null) return fi;
        return null;
    }

    /// null means safe to act on.
    public static string Refusal(Target t, string root)
    {
        var name = Path.GetFileName(t.Path ?? "");
        if (string.IsNullOrEmpty(name) || name == "." || name == "..") return "Unsicherer Name";
        string parent;
        try { parent = Path.GetDirectoryName(Norm(t.Path)); } catch { return "Unsicherer Name"; }
        if (!string.Equals(parent?.TrimEnd('\\'), Norm(root), StringComparison.OrdinalIgnoreCase)) return "Nicht direkt auf dem Desktop";
        var info = Info(t.Path);
        if (info == null) return "Nicht mehr da";
        if (t.Modified is { } then && Math.Abs((info.LastWriteTimeUtc - then).TotalSeconds) > 1) return "Während des Aufräumens geändert";
        if ((info.Attributes & FileAttributes.System) != 0) return "Systemdatei";
        return null;
    }

    /// Moved items stay on the same drive, so moving is only ever a rename: instant, and nothing is copied.
    /// Test runs on a throwaway folder keep their shelf next to it, never in the real one.
    public static string ShelfOverride;

    public static string ShelfFor(string root)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var baseDir = string.Equals(Path.GetPathRoot(Norm(root)), Path.GetPathRoot(profile), StringComparison.OrdinalIgnoreCase)
            ? profile : Path.GetPathRoot(Norm(root));
        return Path.Combine(baseDir, "RoboDust Ablage");
    }

    // MARK: - Move

    public static Outcome Move(List<Target> targets, string root, string shelfOverride = null)
    {
        var o = new Outcome();
        var stamp = DateTime.Now;
        var shelf = shelfOverride ?? ShelfOverride ?? ShelfFor(root);
        var folder = Path.Combine(shelf, stamp.ToString("yyyy-MM-dd HH.mm.ss"));
        for (int n = 2; Directory.Exists(folder) || File.Exists(folder); n++) folder = Path.Combine(shelf, stamp.ToString("yyyy-MM-dd HH.mm.ss") + $" ({n})");
        var run = new Run { Id = Guid.NewGuid().ToString("N")[..12], When = stamp, Mode = "move", Folder = folder };
        o.Run = run;
        bool made = false;
        foreach (var t in targets)
        {
            var why = Refusal(t, root);
            if (why != null) { o.Failed.Add((t.Index, why)); continue; }
            try
            {
                if (!made) { Directory.CreateDirectory(folder); made = true; }
                var dst = Path.Combine(folder, Path.GetFileName(t.Path));
                if (File.Exists(dst) || Directory.Exists(dst)) { o.Failed.Add((t.Index, "Ziel existiert schon")); continue; }
                if (!string.Equals(Path.GetPathRoot(Norm(t.Path)), Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase))
                { o.Failed.Add((t.Index, "Liegt auf einem anderen Laufwerk")); continue; }
                var info = Info(t.Path);
                if (info is DirectoryInfo) Directory.Move(t.Path, dst); else File.Move(t.Path, dst, false);
                o.Done.Add(t.Index);
                run.Items.Add(new Record { Original = Norm(t.Path), Now = dst, X = t.ViewPos?.X, Y = t.ViewPos?.Y });
            }
            catch (Exception e) { o.Failed.Add((t.Index, Short(e))); }
        }
        if (made && run.Items.Count == 0) { try { Directory.Delete(folder, false); } catch { } }  // only ever an empty folder RoboDust just made
        if (run.Items.Count > 0) { WriteNote(folder, run); SaveRun(run); }
        Log("wegräumen", o, targets, folder);
        return o;
    }

    /// A short note in the folder, so the files make sense to anyone who finds them later.
    static void WriteNote(string folder, Run run)
    {
        try
        {
            var lines = new List<string>
            {
                "RoboDust hat diese Dateien am " + run.When.ToString("dd.MM.yyyy 'um' HH:mm") + " von deinem Desktop hierher geräumt.",
                "Nichts wurde gelöscht. Zurückholen geht im RoboDust-Fenster mit \"Zurückholen\", oder einfach zurück auf den Desktop ziehen.",
                "",
            };
            lines.AddRange(run.Items.Select(i => Path.GetFileName(i.Now) + "  ←  " + i.Original));
            File.WriteAllLines(Path.Combine(folder, "_RoboDust-Info.txt"), lines);
        }
        catch { }
    }

    // MARK: - Sort into folders

    static readonly System.Globalization.CultureInfo German = System.Globalization.CultureInfo.GetCultureInfo("de-DE");

    /// The folder an item belongs in. Same answer for the panel's preview and the real move.
    public static string GroupOf(string name, Kind kind, DateTime? modifiedUtc, SortBy by)
    {
        switch (by)
        {
            case SortBy.Alphabet:
            {
                var first = name.TrimStart('.', '_', '-', ' ', '(', '[').FirstOrDefault();
                var c = char.ToUpperInvariant(RemoveAccent(first));
                if (c >= 'A' && c <= 'E') return "A–E";
                if (c >= 'F' && c <= 'J') return "F–J";
                if (c >= 'K' && c <= 'O') return "K–O";
                if (c >= 'P' && c <= 'T') return "P–T";
                if (c >= 'U' && c <= 'Z') return "U–Z";
                return "0–9 und Sonstiges";
            }
            case SortBy.Date:
            {
                var d = (modifiedUtc ?? DateTime.UtcNow).ToLocalTime();
                // Year and month number first, so the folders line up in order.
                return d.ToString("yyyy-MM MMMM", German);
            }
            default:
            {
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (kind != Kind.Folder && ext is ".lnk" or ".url") return "Verknüpfungen";
                return kind switch
                {
                    Kind.Folder => "Ordner", Kind.Image => "Bilder", Kind.Video => "Videos", Kind.Document => "Dokumente",
                    Kind.Archive => "Archive", Kind.Audio => "Musik", _ => "Sonstiges",
                };
            }
        }
    }

    static char RemoveAccent(char c)
    {
        var s = c.ToString().Normalize(System.Text.NormalizationForm.FormD);
        return s.Length > 0 ? s[0] : c;
    }

    /// Moves each item into a folder on the desktop named for its group. A folder that already exists
    /// with that name is used as it is; one that RoboDust makes is noted, so undo can take it away again.
    public static Outcome Sort(List<Target> targets, string root, Func<Target, string> group)
    {
        var o = new Outcome();
        var run = new Run { Id = Guid.NewGuid().ToString("N")[..12], When = DateTime.Now, Mode = "sort", Folder = root };
        o.Run = run;
        var groups = new HashSet<string>(targets.Select(group), StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            var why = Refusal(t, root);
            if (why != null) { o.Failed.Add((t.Index, why)); continue; }
            var name = Path.GetFileName(t.Path);
            // A folder that is itself one of the sort folders stays put and takes the others in.
            if (groups.Contains(name) && Directory.Exists(t.Path)) { o.Failed.Add((t.Index, "Ist selbst ein Sammelordner")); continue; }
            try
            {
                var folder = Path.Combine(Norm(root), group(t));
                if (File.Exists(folder)) { o.Failed.Add((t.Index, "Eine Datei heißt schon wie der Ordner")); continue; }
                if (!Directory.Exists(folder)) { Directory.CreateDirectory(folder); run.CreatedFolders.Add(folder); }
                var dst = Path.Combine(folder, name);
                if (File.Exists(dst) || Directory.Exists(dst)) { o.Failed.Add((t.Index, "Im Ordner liegt schon etwas mit dem Namen")); continue; }
                var info = Info(t.Path);
                if (info is DirectoryInfo) Directory.Move(t.Path, dst); else File.Move(t.Path, dst, false);
                o.Done.Add(t.Index);
                run.Items.Add(new Record { Original = Norm(t.Path), Now = dst, X = t.ViewPos?.X, Y = t.ViewPos?.Y });
            }
            catch (Exception e) { o.Failed.Add((t.Index, Short(e))); }
        }
        // Only ever an empty folder RoboDust just made.
        foreach (var d in run.CreatedFolders.ToList())
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) { Directory.Delete(d, false); run.CreatedFolders.Remove(d); } } catch { }
        if (run.Items.Count > 0) SaveRun(run);
        Log("in ordner", o, targets, null);
        return o;
    }

    // MARK: - Hide

    public static Outcome Hide(List<Target> targets, string root)
    {
        var o = new Outcome();
        var run = new Run { Id = Guid.NewGuid().ToString("N")[..12], When = DateTime.Now, Mode = "hide" };
        o.Run = run;
        foreach (var t in targets)
        {
            var why = Refusal(t, root);
            if (why != null) { o.Failed.Add((t.Index, why)); continue; }
            try
            {
                var info = Info(t.Path);
                var was = (info.Attributes & FileAttributes.Hidden) != 0;
                if (!was) info.Attributes |= FileAttributes.Hidden;
                info.Refresh();
                if ((info.Attributes & FileAttributes.Hidden) == 0) { o.Failed.Add((t.Index, "Ließ sich nicht ausblenden")); continue; }
                o.Done.Add(t.Index);
                run.Items.Add(new Record { Original = Norm(t.Path), Now = Norm(t.Path), WasHidden = was, X = t.ViewPos?.X, Y = t.ViewPos?.Y });
            }
            catch (Exception e) { o.Failed.Add((t.Index, Short(e))); }
        }
        if (run.Items.Count > 0) SaveRun(run);
        Log("ausblenden", o, targets, null);
        return o;
    }

    // MARK: - Undo

    /// Brings a run's items back to where they were. Returns how many came back, and the ones that didn't with why.
    public static (List<Record> back, List<(Record item, string reason)> left) Undo(Run run)
    {
        var back = new List<Record>();
        var left = new List<(Record, string)>();
        foreach (var r in run.Items)
        {
            try
            {
                if (run.Mode is "move" or "sort")
                {
                    var src = Info(r.Now);
                    if (src == null) { left.Add((r, "Nicht mehr in der Ablage")); continue; }
                    if (Info(r.Original) != null) { left.Add((r, "Am alten Platz liegt schon etwas mit dem Namen")); continue; }
                    if (src is DirectoryInfo) Directory.Move(r.Now, r.Original); else File.Move(r.Now, r.Original, false);
                    back.Add(r);
                }
                else
                {
                    var info = Info(r.Original);
                    if (info == null) { left.Add((r, "Nicht mehr da")); continue; }
                    if (!r.WasHidden) info.Attributes &= ~FileAttributes.Hidden;
                    back.Add(r);
                }
            }
            catch (Exception e) { left.Add((r, Short(e))); }
        }
        if (run.Mode == "move" && run.Folder != null)
        {
            // The shelf folder goes only if nothing but RoboDust's own note is left in it.
            try
            {
                var rest = Directory.EnumerateFileSystemEntries(run.Folder).ToList();
                if (rest.All(p => Path.GetFileName(p) == "_RoboDust-Info.txt"))
                {
                    foreach (var p in rest) File.Delete(p);
                    Directory.Delete(run.Folder, false);
                }
            }
            catch { }
        }
        if (run.Mode == "sort")
        {
            // A sort folder RoboDust made goes again, but only once it's completely empty.
            foreach (var d in run.CreatedFolders)
                try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d, false); } catch { }
        }
        run.Undone = left.Count == 0;
        if (!run.Undone) run.Items = left.Select(l => l.Item1).ToList();
        SaveRun(run);
        AppendLog($"zurückholen ({run.Mode}): {back.Count} zurück, {left.Count} nicht" + string.Concat(left.Select(l => $"\n  nicht {l.Item1.Original} ({l.Item2})")));
        return (back, left);
    }

    // MARK: - Records

    static string RunsDir { get { var d = Path.Combine(DataDir, "runs"); Directory.CreateDirectory(d); return d; } }

    public static void SaveRun(Run run)
    {
        if (!HistoryEnabled && DataDirOverride == null) return;
        try { File.WriteAllText(Path.Combine(RunsDir, run.Id + ".json"), JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    /// The newest run that can still be undone, if any.
    public static Run LatestUndoable()
    {
        try
        {
            return Directory.EnumerateFiles(RunsDir, "*.json")
                .Select(f => { try { return JsonSerializer.Deserialize<Run>(File.ReadAllText(f)); } catch { return null; } })
                .Where(r => r != null && !r.Undone && r.Items.Count > 0)
                .OrderByDescending(r => r.When).FirstOrDefault();
        }
        catch { return null; }
    }

    // MARK: - History, kept on this PC only

    public static string HistoryPath => Path.Combine(DataDir, "history.log");

    static void Log(string action, Outcome o, List<Target> targets, string folder)
    {
        var byIndex = targets.ToDictionary(t => t.Index, t => t.Path);
        var lines = new List<string> { $"{action}: {o.Done.Count} erledigt, {o.Failed.Count} in Ruhe gelassen" + (folder != null && o.Done.Count > 0 ? $" → {folder}" : "") };
        lines.AddRange(o.Done.Where(byIndex.ContainsKey).Select(i => "  erledigt " + byIndex[i]));
        lines.AddRange(o.Failed.Where(f => byIndex.ContainsKey(f.index)).Select(f => $"  gelassen {byIndex[f.index]} ({f.reason})"));
        AppendLog(string.Join("\n", lines));
    }

    /// Dev only: set ROBODUST_TRACE=1 to follow a run step by step. Contains no file names.
    public static void Trace(string text)
    {
        if (Environment.GetEnvironmentVariable("ROBODUST_TRACE") != "1") return;
        try { File.AppendAllText(Path.Combine(DataDir, "trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {text}\n"); } catch { }
    }

    public static void AppendLog(string text)
    {
        if (!HistoryEnabled) return;
        try { File.AppendAllText(HistoryPath, $"[{DateTime.Now:yyyy-MM-ddTHH:mm:ss}] {text}\n"); } catch { }
    }

    static string Short(Exception e) => e switch
    {
        UnauthorizedAccessException => "Kein Zugriff",
        PathTooLongException => "Pfad zu lang",
        IOException io when io.HResult == unchecked((int)0x80070020) => "Wird gerade benutzt",
        _ => e.Message,
    };

    // MARK: - Self-test, on a throwaway folder only

    public static bool SelfTest(string sandbox)
    {
        var all = true;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) all = false; }
        sandbox = Norm(sandbox);
        DataDirOverride = Path.Combine(sandbox, "data");
        var root = Path.Combine(sandbox, "desk");
        var outside = Path.Combine(sandbox, "outside");
        var shelf = Path.Combine(sandbox, "shelf");
        foreach (var d in new[] { root, outside, shelf }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }
        string Make(string dir, string n) { var p = Path.Combine(dir, n); File.WriteAllText(p, "x"); return p; }
        DateTime Mod(string p) => Info(p).LastWriteTimeUtc;
        Target T(int i, string p, DateTime? m = null) => new() { Index = i, Path = p, Modified = m };

        var plain = Make(root, "plain.txt");
        var tricky = Make(root, "it's \"odd\" & %USERNAME% $(rm) name.txt".Replace("\"", "'"));
        var keepOutside = Make(outside, "outside.txt");
        var viaDots = Path.Combine(root, @"..\outside\outside.txt");
        var changed = Make(root, "changed.txt");
        var gone = Path.Combine(root, "already-gone.txt");
        var folder = Path.Combine(root, "folder"); Directory.CreateDirectory(folder); Make(folder, "inner.txt");
        var sys = Make(root, "system.txt"); File.SetAttributes(sys, FileAttributes.System);

        var o = Move(new() { T(1, plain, Mod(plain)), T(2, tricky), T(3, keepOutside), T(4, viaDots), T(5, changed, DateTime.UtcNow.AddHours(-3)), T(6, gone), T(7, folder), T(8, sys) }, root, shelf);
        var dest = o.Run.Folder;
        Check("moves a normal file into the shelf", !File.Exists(plain) && File.Exists(Path.Combine(dest, "plain.txt")));
        Check("moves a file with quotes and odd characters in its name", !File.Exists(tricky) && File.Exists(Path.Combine(dest, Path.GetFileName(tricky))));
        Check("moves a folder with its contents", !Directory.Exists(folder) && File.Exists(Path.Combine(dest, "folder", "inner.txt")));
        Check("refuses a file outside the desktop", File.Exists(keepOutside) && o.Failed.Any(f => f.index == 3));
        Check("refuses a ..\\ path escaping the desktop", File.Exists(keepOutside) && o.Failed.Any(f => f.index == 4));
        Check("refuses a file changed since it was collected", File.Exists(changed) && o.Failed.Any(f => f.index == 5));
        Check("reports a file that already vanished", o.Failed.Any(f => f.index == 6));
        Check("leaves system files alone", File.Exists(sys) && o.Failed.Any(f => f.index == 8));
        Check("writes a note into the shelf folder", File.Exists(Path.Combine(dest, "_RoboDust-Info.txt")));
        Check("saves a record of the run", LatestUndoable()?.Id == o.Run.Id);
        Check("refuses the desktop folder itself", Refusal(T(9, root), root) != null);

        // Undo puts everything back under its own name, and tidies the empty shelf folder away.
        var (back, left) = Undo(o.Run);
        Check("undo brings every moved item back", back.Count == 3 && left.Count == 0 && File.Exists(plain) && File.Exists(tricky) && File.Exists(Path.Combine(folder, "inner.txt")));
        Check("undo removes the then-empty shelf folder", !Directory.Exists(dest));
        Check("a finished undo isn't offered again", LatestUndoable() == null);

        // Undo never overwrites something new with the same name.
        var again = Move(new() { T(10, plain, Mod(plain)) }, root, shelf);
        File.WriteAllText(plain, "new and different");
        var (b2, l2) = Undo(again.Run);
        Check("undo won't overwrite a new file of the same name", b2.Count == 0 && l2.Count == 1 && File.ReadAllText(plain) == "new and different" && File.Exists(again.Run.Items[0].Now));
        Check("an unfinished undo is still offered", LatestUndoable()?.Id == again.Run.Id);

        // Hide sets one attribute and nothing else, and undo clears only what it set.
        var h1 = Make(root, "hide-me.txt");
        var h2 = Make(root, "already-hidden.txt"); File.SetAttributes(h2, FileAttributes.Hidden);
        var hidden = Hide(new() { T(20, h1, Mod(h1)), T(21, h2, Mod(h2)), T(22, keepOutside) }, root);
        Check("hides a file", (File.GetAttributes(h1) & FileAttributes.Hidden) != 0 && File.ReadAllText(h1) == "x");
        Check("hide refuses a file outside the desktop", (File.GetAttributes(keepOutside) & FileAttributes.Hidden) == 0 && hidden.Failed.Any(f => f.index == 22));
        Undo(hidden.Run);
        Check("undo shows the hidden file again", (File.GetAttributes(h1) & FileAttributes.Hidden) == 0);
        Check("undo leaves a file hidden that was hidden before", (File.GetAttributes(h2) & FileAttributes.Hidden) != 0);

        // Sorting into folders on the desktop, by type.
        var s1 = Make(root, "photo.jpg"); var s2 = Make(root, "notes2.txt"); var s3 = Make(root, "clip.mp4");
        var mine = Path.Combine(root, "Bilder"); Directory.CreateDirectory(mine); Make(mine, "old.png");   // the user's own folder of that name
        var sub = Path.Combine(root, "stuff"); Directory.CreateDirectory(sub);
        var sortTargets = new List<Target> { T(40, s1, Mod(s1)), T(41, s2, Mod(s2)), T(42, s3, Mod(s3)), T(43, mine, Info(mine).LastWriteTimeUtc), T(44, sub, Info(sub).LastWriteTimeUtc), T(45, keepOutside) };
        Kind KindOf(Target t) => KindInfo.Of(t.Path, Directory.Exists(t.Path));
        var so = Sort(sortTargets, root, t => GroupOf(Path.GetFileName(t.Path), KindOf(t), null, SortBy.Type));
        Check("sorts an image into the existing Bilder folder", File.Exists(Path.Combine(mine, "photo.jpg")) && File.Exists(Path.Combine(mine, "old.png")));
        Check("makes Dokumente and Videos folders for the rest", File.Exists(Path.Combine(root, "Dokumente", "notes2.txt")) && File.Exists(Path.Combine(root, "Videos", "clip.mp4")));
        Check("moves a folder into Ordner", Directory.Exists(Path.Combine(root, "Ordner", "stuff")));
        Check("leaves a folder alone that is itself a sort folder", Directory.Exists(mine) && so.Failed.Any(f => f.index == 43));
        Check("sort refuses a file outside the desktop", File.Exists(keepOutside) && so.Failed.Any(f => f.index == 45));
        Check("notes only the folders it made", so.Run.CreatedFolders.Count == 3 && !so.Run.CreatedFolders.Any(d => d.EndsWith("Bilder")));
        Undo(so.Run);
        Check("undo puts every sorted item back", File.Exists(s1) && File.Exists(s2) && File.Exists(s3) && Directory.Exists(sub));
        Check("undo removes the folders it made, and keeps the user's", !Directory.Exists(Path.Combine(root, "Dokumente")) && !Directory.Exists(Path.Combine(root, "Ordner")) && File.Exists(Path.Combine(mine, "old.png")));
        Check("groups by alphabet", GroupOf("Äpfel.txt", Kind.Document, null, SortBy.Alphabet) == "A–E" && GroupOf("zebra", Kind.Folder, null, SortBy.Alphabet) == "U–Z" && GroupOf("2024.pdf", Kind.Document, null, SortBy.Alphabet) == "0–9 und Sonstiges");
        Check("groups by month", GroupOf("x", Kind.Other, new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc), SortBy.Date) == "2026-10 Oktober");

        // Links are moved as links: what they point to stays where it is.
        var linkTarget = Make(outside, "link-target.txt");
        var link = Path.Combine(root, "link.txt");
        bool linked = false;
        try { File.CreateSymbolicLink(link, linkTarget); linked = true; } catch { Console.WriteLine("SKIP links: creating a symbolic link needs Developer Mode"); }
        if (linked)
        {
            var lo = Move(new() { T(30, link, Info(link).LastWriteTimeUtc) }, root, shelf);
            Check("moves a link without touching what it points to", lo.Done.Contains(30) && File.Exists(linkTarget) && new FileInfo(Path.Combine(lo.Run.Folder, "link.txt")).LinkTarget != null);
        }
        File.SetAttributes(sys, FileAttributes.Normal);
        DataDirOverride = null;
        return all;
    }
}
