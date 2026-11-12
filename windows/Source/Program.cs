using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace RoboDust;

public static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    static string[] args;
    /// Names for the signals between a second start and the running RoboDust, in this user's session.
    const string Ipc = @"Local\RoboDust.";
    static string Value(string flag) { var i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    static bool Has(string flag) => args.Contains(flag);

    /// Lets the dev commands print to the terminal they were started from.
    static void Console_()
    {
        AttachConsole(-1);
        var w = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(w);
        Console.WriteLine();
    }


    /// Waits on a named signal from a second start, and hands it to the UI thread.
    static void Listen(string name, Action onSignal)
    {
        var ev = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, name);
        var t = new System.Threading.Thread(() =>
        {
            while (true)
            {
                ev.WaitOne();
                Application.Current?.Dispatcher.BeginInvoke(onSignal);
            }
        }) { IsBackground = true };
        t.Start();
    }
    [STAThread]
    public static int Main(string[] argv)
    {
        args = argv;
        if (Value("--fileops-test") is { } dir)
        {
            Console_();
            Directory.CreateDirectory(dir);
            var ok = FileOps.SelfTest(dir);
            Console.WriteLine(ok ? "ALL PASSED" : "SOME FAILED");
            return ok ? 0 : 1;
        }
        if (Has("--dump"))
        {
            Console_();
            var times = (Value("--dump") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var mode = int.TryParse(Value("--mode"), out var mm) ? (CleanMode)mm : CleanMode.Move;
            var scale = float.TryParse(Value("--pixel-scale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ps) ? ps : 1;
            var follow = int.TryParse(Value("--follow"), out var f) ? f : 0;
            try { Offline.Dump(times, Value("--out") ?? ".", mode, Has("--plain-floor"), scale, follow, Has("--all-screens"), int.TryParse(Value("--dock-screen"), out var ds) ? ds : -1, int.TryParse(Value("--fun"), out var fn) ? (FunMode)fn : FunMode.Off, int.TryParse(Value("--skin"), out var sk) ? (RobotSkin)sk : RobotSkin.White, Has("--hat"), int.TryParse(Value("--cat"), out var dc) ? dc : -1, Value("--look")); }
            catch (Exception e) { Console.WriteLine("ERROR " + e); return 1; }
            return 0;
        }
        if (Value("--write-icon") is { } ico) { AppIcon.WriteIco(ico); return 0; }
        if (Value("--coins") is { } give)
        {
            // Gives (or with a minus, takes) shop coins. A running RoboDust picks them up straight away.
            Console_();
            if (!int.TryParse(give, out var n)) { Console.WriteLine("Aufruf: RoboDust.exe --coins 500"); return 2; }
            var st = Settings.Load();
            st.Coins = Math.Max(0, st.Coins + n);
            st.Save();
            try { System.Threading.EventWaitHandle.OpenExisting(Ipc + @"Coins").Set(); } catch { }
            Console.WriteLine($"Münzen: {st.Coins}");
            return 0;
        }
        if (Value("--panel-shot") is { } shots)
        {
            Console_();
            try { PanelShot.Render(shots); Console.WriteLine("ok"); } catch (Exception e) { Console.WriteLine("ERROR " + e); return 1; }
            return 0;
        }
        if (Value("--align-test") is { } shot)
        {
            Console_();
            try { Offline.AlignTest(shot, int.TryParse(Value("--screen"), out var si) ? si : 0); } catch (Exception e) { Console.WriteLine("ERROR " + e); return 1; }
            return 0;
        }
        if (Has("--save-view-test"))
        {
            // Nudges one icon by a few pixels, has Explorer save and refresh the desktop, checks the icon
            // stayed put, then puts it back exactly. Moves no file and prints no names.
            Console_();
            try
            {
                var v = DesktopReader.OpenView();
                var path = System.IO.Directory.EnumerateFileSystemEntries(DesktopReader.DesktopFolder).FirstOrDefault(p => v.PositionOf(p) != null);
                if (path == null) { Console.WriteLine("no icon"); return 1; }
                var p0 = v.PositionOf(path).Value;
                // A free cell next to it, on the same display.
                var o = v.Origin;
                var scr = Screen.All().First(s => s.Bounds.Contains(o.X + p0.X + 2, o.Y + p0.Y + 2));
                float k = scr.Scale / Native.PrimaryScale();
                int sx = (int)MathF.Round(v.Spacing.X * k), sy = (int)MathF.Round(v.Spacing.Y * k);
                var all = v.AllPositions();
                var moved = new[] { (0, 1), (0, -1), (1, 0), (-1, 0), (0, 2), (2, 0), (-2, 0), (0, -2), (1, 1), (-1, 1), (1, -1), (-1, -1) }
                    .Select(d => new POINT(p0.X + d.Item1 * sx, p0.Y + d.Item2 * sy))
                    .FirstOrDefault(c => scr.WorkArea.Contains(o.X + c.X + sx / 2, o.Y + c.Y + sy / 2) && scr.WorkArea.Contains(o.X + c.X, o.Y + c.Y) && !all.Any(a => Math.Abs(a.X - c.X) < sx / 2 && Math.Abs(a.Y - c.Y) < sy / 2));
                if (moved.X == 0 && moved.Y == 0) { Console.WriteLine("no free cell"); return 1; }
                Console.WriteLine($"moving by {moved.X - p0.X},{moved.Y - p0.Y}");
                static string Layout() { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Bags\1\Desktop"); return k?.GetValue("IconLayouts") is byte[] b ? $"{b.Length}b {Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b))[..8]}" : "none"; }
                Console.WriteLine("layout before " + Layout());
                Console.WriteLine($"set {v.SetPositions(new() { (path, moved) })}");
                Console.WriteLine($"save hr 0x{v.Shell?.SaveViewState() ?? -1:X8}, before refresh {(DesktopReader.OpenView().PositionOf(path) is { } b ? $"{b.X - p0.X},{b.Y - p0.Y}" : "gone")}");
                Console.WriteLine("layout after save " + Layout());
                System.Threading.Thread.Sleep(2500);
                Console.WriteLine($"after wait {(DesktopReader.OpenView().PositionOf(path) is { } w ? $"{w.X - p0.X},{w.Y - p0.Y}" : "gone")}, layout " + Layout());
                Native.SHChangeNotify(Native.SHCNE_UPDATEDIR, Native.SHCNF_PATHW | Native.SHCNF_FLUSH, DesktopReader.DesktopFolder, IntPtr.Zero);
                System.Threading.Thread.Sleep(2500);
                var after = DesktopReader.OpenView().PositionOf(path);
                Console.WriteLine($"after refresh: {(after is { } a ? (a.X == moved.X && a.Y == moved.Y ? "kept" : $"moved by {a.X - p0.X},{a.Y - p0.Y}") : "gone")}");
                Console.WriteLine($"back {DesktopReader.OpenView().SetPositions(new() { (path, p0) })}");
            }
            catch (Exception e) { Console.WriteLine("ERROR " + e); return 1; }
            return 0;
        }
        if (Has("--read-test"))
        {
            // Exercises the real-desktop path, printing only counts and geometry: no names, no pixels.
            Console_();
            try
            {
                var screens = Screen.All();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var lists = DesktopReader.Read(screens, out var view);
                var e = lists.SelectMany(l => l).ToList();
                Console.WriteLine($"items {e.Count} explorer {sw.Elapsed.TotalSeconds:0.00}s");
                for (int i = 0; i < screens.Count; i++)
                    Console.WriteLine($"screen {i} {screens[i].Bounds} work {screens[i].WorkArea} scale {screens[i].Scale} primary {screens[i].IsPrimary}: {lists[i].Count} items, x {(lists[i].Count > 0 ? lists[i].Min(x => x.Center.X) : 0):0}..{(lists[i].Count > 0 ? lists[i].Max(x => x.Center.X) : 0):0} pts of {screens[i].SizePts}");
                Console.WriteLine($"icon {view.IconPt}pt spacing {view.Spacing.X}x{view.Spacing.Y} auto-arrange {view.AutoArrange} grid {view.SnapsToGrid} origin {view.Origin.X},{view.Origin.Y}");
                foreach (var g in e.GroupBy(x => x.Kind).OrderBy(g => g.Key)) Console.WriteLine($"  {g.Key}: {g.Count()}");
                Console.WriteLine($"left alone {e.Count(x => x.ProtectedReason != null)}, not files {e.Count(x => x.Missing)}, cloud {e.Count(x => x.Cloud)}");
                foreach (var g in e.Where(x => x.ProtectedReason != null).GroupBy(x => x.ProtectedReason)) Console.WriteLine($"  {g.Key}: {g.Count()}");
                Console.WriteLine($"total {Fmt.Bytes(e.Sum(x => x.Bytes))}");
                Console.WriteLine("first positions (pts): " + string.Join(" ", e.Take(6).Select(x => x.Center)));
                view.View.ItemCount(Native.SVGIO_ALLVIEW, out var all);
                Console.WriteLine($"explorer items {all}, raw x {e.Min(x => x.ViewPos?.X)}..{e.Max(x => x.ViewPos?.X)} y {e.Min(x => x.ViewPos?.Y)}..{e.Max(x => x.ViewPos?.Y)}");
                foreach (var s in System.Windows.Forms.Screen.AllScreens) Console.WriteLine($"monitor {s.Bounds} primary {s.Primary}");
                var org = view.Origin;
                for (int i = 0; i < screens.Count; i++)
                {
                    var wa = screens[i].WorkArea;
                    var ps = lists[i].Where(x => x.ViewPos != null).Select(x => (x: x.ViewPos.Value.X + org.X - wa.Left, y: x.ViewPos.Value.Y + org.Y - wa.Top)).ToList();
                    Console.WriteLine($"grid {i}: x mod " + string.Join(" ", ps.GroupBy(p => ((p.x % view.Spacing.X) + view.Spacing.X) % view.Spacing.X).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key}x{g.Count()}"))
                        + " | y mod " + string.Join(" ", ps.GroupBy(p => ((p.y % view.Spacing.Y) + view.Spacing.Y) % view.Spacing.Y).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key}x{g.Count()}"))
                        + " | min " + (ps.Count > 0 ? $"{ps.Min(p => p.x)},{ps.Min(p => p.y)}" : "-"));
                }
                Native.GetWindowRect(view.ListView, out var lr);
                Console.WriteLine($"listview class {Native.ClassOf(view.ListView)} rect {lr.Left},{lr.Top} {lr.Width}x{lr.Height}");
                sw.Restart();
                Metrics.IconHalf = view.IconPt / 2;
                Sprites.TexScale = MathF.Max(2, screens.Max(s => s.Scale));
                var sprites = Sprites.BuildNow(e);
                Console.WriteLine($"sprites {sprites.Count} {sw.Elapsed.TotalSeconds:0.00}s");
            }
            catch (Exception ex) { Console.WriteLine("ERROR " + ex); return 1; }
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var controller = new AppController();
        if (Has("--no-hide")) controller.Model.Settings.ClearWindows = false;
        controller.AutoDemo = Has("--auto");
        controller.AutoPutBack = Has("--auto-putback");
        controller.AutoUndo = Has("--auto-undo");
        controller.AutoQuit = Has("--auto-quit");
        if (double.TryParse(Value("--auto-stop"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var stopAfter)) controller.AutoStop = stopAfter;
        if (Has("--real")) controller.Model.Source = Source.Real;
        if (Has("--no-save")) Settings.Persists = false;
        // Dev only: always the same cat. 0 Supervisor, 1 Loaf, 2 Rider, 3 Swatter, 4 Saboteur, 5 Coward.
        if (int.TryParse(Value("--cat"), out var cm)) { controller.ForcedCat = (CatActor.Mood)cm; controller.Model.Settings.HasCat = true; }
        if (Value("--test-root") is { } root)
        {
            // Dev only: a throwaway folder stands in for the desktop, and settings and history aren't saved.
            if (!Directory.Exists(root)) { Console_(); Console.WriteLine("ERROR test root does not exist: " + root); return 2; }
            Settings.Persists = false;
            FileOps.HistoryEnabled = false;
            var parent = Path.GetDirectoryName(Path.GetFullPath(root).TrimEnd('\\'));
            FileOps.ShelfOverride = Path.Combine(parent, "shelf");
            FileOps.DataDirOverride = Path.Combine(parent, "data");
            controller.TestRoot = Path.GetFullPath(root);
            controller.Model.Source = Source.Real;
            if (int.TryParse(Value("--test-mode"), out var tm)) controller.Model.Settings.Mode = tm;
            controller.Model.Settings.SkipFinalClick = true;
        }
        if (Has("--sample-auto")) { Settings.Persists = false; controller.Model.Settings.SkipFinalClick = true; }
        controller.StartInTray = Has("--tray") || Has("--scheduled");
        controller.Scheduled = Has("--scheduled");
        // One RoboDust at a time. A second start (a double-click, or the schedule while it sits in the
        // tray) wakes the one that is running instead. Dev runs with flags stay separate.
        var plain = args.All(a => a is "--tray" or "--scheduled");
        System.Threading.Mutex single = null;
        if (plain)
        {
            single = new System.Threading.Mutex(true, Ipc + @"Single", out var first);
            if (!first)
            {
                try { System.Threading.EventWaitHandle.OpenExisting(Has("--scheduled") ? Ipc + @"Scheduled" : Ipc + @"Show").Set(); } catch { }
                return 0;
            }
            Listen(Ipc + @"Show", () => controller.ShowPanel());
            Listen(Ipc + @"Scheduled", () => controller.StartScheduled());
            Listen(Ipc + @"Coins", () => controller.ReloadCoins());
        }
        app.Startup += (_, _) => controller.Start();
        app.DispatcherUnhandledException += (_, e) =>
        {
            FileOps.AppendLog("Fehler: " + e.Exception);
            MessageBox.Show("RoboDust ist auf einen Fehler gestoßen und hat deinen Desktop wiederhergestellt.\n\n" + e.Exception.Message, "RoboDust");
            e.Handled = true;
        };
        var code = app.Run();
        GC.KeepAlive(single);
        return code;
    }
}
