using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace RoboDust;

/// Runs a clean from start to finish. A straight port of the Mac AppController, except that the
/// outcomes are Ordnen, Wegräumen and Ausblenden, and nothing is ever deleted.
public sealed class AppController
{
    public readonly Model Model = new();
    public Panel Panel;
    Sim sim;
    readonly List<Overlay> overlays = new();
    List<Screen> screens = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    double lastFrame; float frameAcc;
    bool doneHandled, fading;
    DesktopView view;
    readonly Synth synth = new();
    readonly List<IntPtr> minimized = new();
    DispatcherTimer uiTimer;
    Forms.NotifyIcon tray;
    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;
    public bool AutoDemo, AutoPutBack, AutoUndo, AutoQuit;
    public double AutoStop;   // dev only: stop after this many seconds, which changes nothing
    bool quitting;
    public string TestRoot;
    bool actionStarted;
    Action undoAction;

    public void Start()
    {
        var m = Model;
        m.Start = Begin;
        m.TogglePause = () => { if (sim != null) sim.Paused = !sim.Paused; Sync(); };
        m.Stop = () => { if (!m.Applying) StopAndRestore(); };
        m.PutBack = () => sim?.PutBack();
        m.Choose = Perform;
        m.About = ShowAbout;
        m.Undo = RunUndo;
        m.UndoLast = UndoLastRun;
        m.OpenShelf = () => { var f = m.LastRun?.Folder; if (f != null && Directory.Exists(f)) Process.Start("explorer.exe", $"\"{f}\""); };
        m.DismissResult = () => { m.Result = null; undoAction = null; m.LastRun = TestRoot == null ? FileOps.LatestUndoable() : null; m.Changed(); };
        m.Support_ = link =>
        {
            if (link != null) { try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); } catch { } m.Support = SupportCard.Thanked; }
            else m.Support = SupportCard.Hidden;
            m.Changed();
        };
        m.Quit = () => Quit(false);
        m.Changed = () => Panel?.Refresh();
        var st = m.Settings;
        m.SetAutostart = on =>
        {
            try { Startup.SetAutostart(on); st.Autostart = on; }
            catch (Exception e) { m.Note = "Autostart ließ sich nicht ändern: " + e.Message; }
            st.Save(); m.Changed();
        };
        m.SetHotkey = on =>
        {
            var ok = Startup.SetHotkey(PanelHandle, on);
            st.Hotkey = on && ok;
            if (!ok) m.Note = "Strg+Alt+R ist schon von einem anderen Programm belegt.";
            st.Save(); m.Changed();
        };
        m.SetSchedule = v =>
        {
            var err = Startup.SetSchedule(v, st.ScheduleHour);
            if (err == null) st.Schedule = v; else m.Note = "Zeitplan ließ sich nicht einrichten: " + err;
            st.Save(); m.Changed();
        };
        m.NeverTouch = path => m.Set(() => { if (!st.NeverTouch.Contains(path, StringComparer.OrdinalIgnoreCase)) st.NeverTouch.Add(path); });
        if (TestRoot == null) m.LastRun = FileOps.LatestUndoable();
        Panel = new Panel(m);
        Panel.Closing += (_, e) =>
        {
            // With the shortcut, autostart or a schedule, RoboDust keeps running in the tray.
            if (!quitting && KeepsRunning) { e.Cancel = true; Panel.Hide(); HintTray(); }
        };
        Panel.Closed += (_, _) => Quit(true);
        if (!StartInTray) Panel.Show();
        PanelHandle = new System.Windows.Interop.WindowInteropHelper(Panel).EnsureHandle();
        System.Windows.Interop.HwndSource.FromHwnd(PanelHandle).AddHook(PanelHook);
        if (st.Hotkey && !Startup.SetHotkey(PanelHandle, true)) { st.Hotkey = false; st.Save(); }
        // The registry and Task Scheduler are the truth: settings follow them if changed outside.
        st.Autostart = Startup.AutostartOn;
        BuildTray();
        // A scheduled clean runs on the real desktop and leaves the user's windows alone.
        if (Scheduled) { m.Source = Source.Real; ui.BeginInvoke(Begin, DispatcherPriority.ApplicationIdle); }
        if (AutoDemo) ui.BeginInvoke(Begin, DispatcherPriority.ApplicationIdle);
        if (AutoStop > 0) Delay(AutoStop, () => { FileOps.Trace($"auto stop: {overlays.Count} window(s), {sim?.Items.Count} items, {sim?.Phase}, robot at {sim?.Pos}, room {(sim != null ? sim.Plan.RoomOf(sim.Pos) : -1)}"); StopAndRestore(); Quit(); });
    }

    public bool StartInTray, Scheduled;
    public CatActor.Mood? ForcedCat;   // dev only: always the same character

    public void ShowPanel() { Panel.Show(); Panel.Activate(); }

    /// Coins were given from the command line while RoboDust was running: read them in.
    public void ReloadCoins()
    {
        var fresh = Settings.Load();
        Model.Settings.Coins = fresh.Coins;
        Model.Changed();
    }

    /// The schedule fired while RoboDust was already running in the tray.
    public void StartScheduled()
    {
        if (Model.Stage != Stage.Ready) return;
        Scheduled = true;
        Model.Source = Source.Real;
        Model.Page = Page.Main;
        Begin();
    }
    IntPtr PanelHandle;
    bool trayHinted;
    bool KeepsRunning => Model.Settings.Hotkey || Model.Settings.Autostart || Model.Settings.Schedule != 0;

    IntPtr PanelHook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Startup.WM_HOTKEY && (int)w == Startup.HotkeyId)
        {
            handled = true;
            if (Model.Stage == Stage.Ready) { Model.Page = Page.Main; Panel.Show(); Begin(); }
        }
        return IntPtr.Zero;
    }

    void HintTray()
    {
        if (trayHinted || tray == null) return;
        trayHinted = true;
        tray.ShowBalloonTip(4000, "RoboDust läuft weiter", "Im Infobereich, für Strg+Alt+R und den Zeitplan. Beenden über das Symbol dort.", Forms.ToolTipIcon.None);
    }

    /// A tray notification; clicking it brings the panel up, where Undo is.
    void Notify(string title, string text)
    {
        if (tray == null) return;
        tray.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.None);
    }

    void BuildTray()
    {
        tray = new Forms.NotifyIcon { Text = "RoboDust", Icon = AppIcon.Make(32), Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("RoboDust anzeigen", null, (_, _) => { Panel.Show(); Panel.Activate(); });
        menu.Items.Add("Stoppen und Desktop wiederherstellen", null, (_, _) => { if (!Model.Applying) StopAndRestore(); });
        menu.Items.Add("Über RoboDust", null, (_, _) => ShowAbout());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => Quit());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => { Panel.Show(); Panel.Activate(); };
        tray.BalloonTipClicked += (_, _) => { Panel.Show(); Panel.Activate(); };
    }

    void ShowAbout()
    {
        MessageBox.Show(Panel,
            "RoboDust\n\nEin Saugroboter für deinen Desktop.\nEntwickelt von Muhammad Raufan Yusup, muhraufan.com\nWindows-Version entwickelt von Alessio Brendt\n\n" +
            "Die Windows-Version löscht nie etwas: Sie sortiert, räumt weg oder blendet aus, und alles lässt sich zurückholen.\n\n" +
            "© 2026 Muhammad Raufan Yusup. Windows-Version: Alessio Brendt. MIT-Lizenz.\nhttps://github.com/muhraufan/robodust",
            "Über RoboDust", MessageBoxButton.OK, MessageBoxImage.None);
    }

    /// force: really quit. Otherwise, with the shortcut, autostart or a schedule on, the panel just hides.
    void Quit(bool force = true)
    {
        if (Model.Applying || quitting) return;
        if (!force && KeepsRunning && Model.Stage == Stage.Ready) { Panel.Hide(); HintTray(); return; }
        quitting = true;
        Teardown();
        if (PanelHandle != IntPtr.Zero) Startup.SetHotkey(PanelHandle, false);
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        Application.Current.Shutdown();
    }



    // MARK: - Run

    void Begin()
    {
        var m = Model;
        if (m.Stage != Stage.Ready) return;
        var all = Screen.All();
        Floorplan.Layout(all);
        var primary = all[0];
        m.Note = null; m.Result = null; m.Support = SupportCard.Hidden; undoAction = null;
        view = null;
        // Explorer's medium icons, unless the real desktop says otherwise.
        Metrics.IconHalf = 24; Metrics.LabelHalf = new V2(38, 19);
        List<DesktopEntry> entries;
        screens = all;
        if (TestRoot != null) { screens = new() { primary }; entries = DesktopReader.Sandbox(TestRoot, primary.SizePts); }
        else if (m.Source == Source.Real)
        {
            try
            {
                entries = DesktopReader.Read(all, out view).SelectMany(l => l).ToList();
                Metrics.IconHalf = view.IconPt / 2;
                Metrics.LabelHalf = new V2(MathF.Max(30, view.LabelWidthPts / 2 - 1), 19);
            }
            catch (DesktopException e) when (e.NoItems)
            {
                m.Note = "Dein Desktop ist schon leer, deshalb gibt es stattdessen das Beispiel-Chaos.";
                entries = DesktopReader.SampleAcross(all);
            }
            catch (Exception e)
            {
                m.Note = "RoboDust kann deinen Desktop gerade nicht lesen, deshalb gibt es das Beispiel-Chaos. (" + e.Message + ")";
                m.Source = Source.Sample;
                entries = DesktopReader.SampleAcross(all);
            }
        }
        else entries = DesktopReader.SampleAcross(all);
        Sprites.TexScale = MathF.Max(2, screens.Max(s => s.Scale));
        m.Stage = Stage.Preparing; m.Prepared = 0; m.PrepareTotal = entries.Count;
        m.Changed();
        Sprites.Build(entries, n => { m.Prepared = Math.Max(m.Prepared, n); m.Changed(); }, sprites => Launch(entries, sprites), ui);
    }

    void Launch(List<DesktopEntry> entries, List<Sprite> sprites)
    {
        var m = Model;
        try
        {
            m.Outcome = null;
            actionStarted = false;
            doneHandled = false;
            // Entries with no file behind them (the sample mess) can never reach FileOps.
            var real = entries.Any(e => e.Path != null);
            m.RealRun = real;
            // The dock stands on the display the user picked, the main one unless they said otherwise.
            var plan = Floorplan.Of(screens, screens.FindIndex(x => x.Name == m.Settings.DockScreen));
            var s = new Sim(plan, entries);
            for (int i = 0; i < sprites.Count; i++) s.Items[i].Color = sprites[i].Average;
            s.Mode = m.Mode;
            s.Fun = m.Fun;
            s.Look = Look.Of(m.Settings);
            s.Skin = s.Look.Skin;
            s.RemoteControl = m.Settings.RemoteControl;
            // The cat: one of six characters, if the user has said they have one.
            if (m.Settings.HasCat)
            {
                s.Cat = ForcedCat is { } fm ? CatActor.PickMood(fm) : CatActor.Random();
                s.Cat.Audio = synth.Cat;
                var scr = screens;
                s.Cat.Windows = () => DesktopWindows.WorldRects(scr);
            }
            s.SkipFinalClick = false;   // handled here, so that it goes through the real file action
            s.RealRun = real;
            // Ordnen fills Explorer's own cells, so that every icon lands exactly where its copy did.
            if (view != null && real) s.Grid = new V2(view.Spacing.X, view.Spacing.Y) / Native.PrimaryScale();
            foreach (var it in s.Items)
            {
                var e = it.Entry;
                it.Protected = e.Missing
                    || (s.Mode != CleanMode.Tidy && e.ProtectedReason != null)
                    || (s.Mode == CleanMode.Move && e.CloudOnly)
                    || (s.Mode == CleanMode.Hide && e.Hidden);
                if (s.Mode == CleanMode.Tidy && view == null && real) it.Protected = true;   // a test folder has no icons to arrange
                // The user's own rules: things they said never to touch, and things too recently changed.
                if (s.Mode != CleanMode.Tidy && e.Path != null && !it.Protected)
                {
                    if (m.Settings.NeverTouch.Contains(e.Path, StringComparer.OrdinalIgnoreCase)) { it.Protected = true; e.ProtectedReason = "Von dir geschützt"; }
                    else if (m.Settings.MinAgeDays > 0 && e.Modified is { } mod && (DateTime.UtcNow - mod).TotalDays < m.Settings.MinAgeDays) { it.Protected = true; e.ProtectedReason = $"Jünger als {m.Settings.MinAgeDays} Tage"; }
                }
            }
            // One window per display, each showing its own slice of the same floor.
            overlays.Clear();
            foreach (var screen in screens)
            {
                var o = new Overlay(screen) { Screens = screens };
                o.Prepare();
                o.MakeCurrent();
                using (var wall = SafeWallpaper(screen)) o.Renderer.SetWallpaper(wall);
                o.Renderer.SetSprites(sprites);
                o.Renderer.LidarRadius = MathF.Max(plan.Bounds.W, plan.Bounds.H) * 0.85f;
                o.Renderer.Skin = (RobotSkin)m.Settings.Skin; o.Renderer.Hat = m.Settings.Hat; o.Renderer.Look = Look.Of(m.Settings);
                o.Sim = s;
                o.OnEscape = () => { if (!m.Applying) StopAndRestore(); };
                o.OnSpace = () => m.TogglePause();
                overlays.Add(o);
            }
            var collectable = s.Items.Where(i => !i.Protected).ToList();
            m.Excluded = new HashSet<int>();
            m.ReviewOpen = false;
            m.ReviewRows = collectable.Select(i => (i.Index, i.Entry.Name)).ToList();
            m.FileBytes = collectable.Where(i => i.Entry.Kind != Kind.Folder).Sum(i => i.Entry.Bytes);
            m.FolderCount = collectable.Count(i => i.Entry.Kind == Kind.Folder);
            m.LeftAlone = s.Items.Count - collectable.Count;
            m.UsesCloud = collectable.Any(i => i.Entry.Cloud);
            m.HiddenShown = DesktopReader.HiddenFilesShown();
            s.Audio = synth;
            synth.Muted = m.Muted;
            synth.StartLive();
            sim = s;
            if (m.ClearWindows && !Scheduled) MinimizeWindows();
            foreach (var o in overlays) o.ShowAboveDesktop();
            m.Total = collectable.Count;
            m.TotalBytes = collectable.Sum(i => i.Entry.Bytes);
            m.Sim = s;
            m.Screens = screens;
            m.Stage = Stage.Running;
            m.Phase = s.Phase;
            lastFrame = clock.Elapsed.TotalSeconds; frameAcc = 0;
            CompositionTarget.Rendering += OnFrame;
            uiTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.0 / 15), DispatcherPriority.Normal, (_, _) => Sync(), ui);
            m.Changed();
            Panel.Activate();
        }
        catch (Exception e)
        {
            Teardown();
            m.Note = "Konnte nicht starten: " + e.Message;
            m.Stage = Stage.Ready;
            m.Changed();
        }
    }

    static System.Drawing.Bitmap SafeWallpaper(Screen screen)
    {
        try { return Wallpaper.Capture(screen); }
        catch { return Wallpaper.Fallback(screen.WorkArea.Width, screen.WorkArea.Height); }
    }

    /// One step of the one robot, then every display draws its slice.
    void OnFrame(object sender, EventArgs e)
    {
        var s = sim;
        if (s == null || fading && s.Phase == Phase.Finished) return;
        if (s.RemoteControl) s.SetManual(ArrowKeys.Throttle(), ArrowKeys.Steer());
        var dt = Overlay.Step(s, overlays, clock, ref lastFrame, ref frameAcc);
        foreach (var o in overlays.ToList()) o.Render(dt);
        if (s.Phase == Phase.Finished) FinishRun();
    }

    void Sync()
    {
        var s = sim; var m = Model;
        if (s == null) return;
        m.Status = s.Status;
        if (s.RaceLine is { } race) m.Status = s.Phase >= Phase.Done ? $"Sieger: {s.Winner}! ({race})" : $"{s.Status} · {race}";
        foreach (var o in overlays) o.KeepAboveDesktop();   // after Win+D
        if (m.Phase != s.Phase) FileOps.Trace($"phase {s.Phase} t={s.T:0.0} collected={s.Collected}/{s.Items.Count} room={s.Plan.RoomOf(s.Pos)}");
        m.Phase = s.Phase;
        if (s.Discarded is { } d) m.Outcome = d;
        m.Paused = s.Paused;
        m.Battery = s.Battery;
        m.Bin = (float)s.BinCount / Math.Max(s.BinCapacity, 1);
        m.Collected = s.Collected;
        m.CollectedBytes = s.CollectedBytes;
        m.RunTime = s.RunTime;
        synth.Muted = m.Muted;
        if (s.Phase == Phase.Done && !doneHandled)
        {
            doneHandled = true;
            if (AutoPutBack) Delay(2, () => sim?.PutBack());
        }
        // Skip-the-question path. Nothing is ever deleted, so there's no countdown to wait out.
        if (s.Phase == Phase.Done && !actionStarted && m.SkipFinalClick && s.Mode != CleanMode.Tidy) Perform(s.Mode);
        if (s.Phase == Phase.Tidied && s.RealRun && !actionStarted) { actionStarted = true; ApplyTidy(s); }
        m.Changed();
    }

    static void Delay(double seconds, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    // MARK: - Real actions

    void Perform(CleanMode choice)
    {
        var s = sim; var m = Model;
        if (s == null || actionStarted || s.Phase != Phase.Done || choice == CleanMode.Tidy) return;
        actionStarted = true;
        m.Outcome = choice;
        s.Discard(choice);
        if (!s.RealRun) { m.Changed(); return; }

        var root = TestRoot ?? DesktopReader.DesktopFolder;
        var excluded = new HashSet<int>(m.Excluded);
        var chosen = s.Items.Where(i => i.InDock && !i.Protected && !excluded.Contains(i.Index) && i.Entry.Path != null).ToList();
        var targets = chosen.Select(i => new FileOps.Target { Index = i.Index, Path = i.Entry.Path, Modified = i.Entry.Modified, ViewPos = i.Entry.ViewPos }).ToList();
        m.Applying = true;
        m.Changed();
        var started = Stopwatch.StartNew();
        var sortBy = m.SortBy;
        string Group(FileOps.Target t) { var e = s.Items[t.Index].Entry; return FileOps.GroupOf(Path.GetFileName(e.Path), e.Kind, e.Modified, sortBy); }
        Task.Run(() => choice switch
        {
            CleanMode.Move => FileOps.Move(targets, root),
            CleanMode.Sort => FileOps.Sort(targets, root, Group),
            _ => FileOps.Hide(targets, root),
        }).ContinueWith(t =>
        {
            var o = t.IsFaulted ? new FileOps.Outcome { Failed = targets.Select(x => (x.Index, t.Exception?.InnerException?.Message ?? "Fehler")).ToList() } : t.Result;
            Native.SHChangeNotify(Native.SHCNE_UPDATEDIR, Native.SHCNF_PATHW | Native.SHCNF_FLUSH, root, IntPtr.Zero);
            Report(choice, o, excluded.Count);
            var failed = new HashSet<int>(o.Failed.Select(f => f.index));
            var back = s.Items.Where(i => i.InDock && (failed.Contains(i.Index) || excluded.Contains(i.Index))).ToList();
            // Let the bag finish draining, and give Explorer time to redraw beneath the overlay.
            var wait = Math.Max(0, 1.5 - started.Elapsed.TotalSeconds) + 0.8;
            Delay(wait, () => { m.Applying = false; s.FinishAfterAction(back); m.Changed(); });
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
    void Report(CleanMode choice, FileOps.Outcome o, int excludedCount)
    {
        var m = Model;
        var n = o.Done.Count;
        var noun = n == 1 ? "Element" : "Elemente";
        var folders = o.Run?.Items.Select(i => Path.GetFileName(Path.GetDirectoryName(i.Now))).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList() ?? new();
        var title = choice switch
        {
            CleanMode.Move => $"{n} {noun} in die Ablage geräumt",
            CleanMode.Sort => $"{n} {noun} in {folders.Count} {(folders.Count == 1 ? "Ordner" : "Ordner")} sortiert",
            _ => $"{n} {noun} ausgeblendet",
        };
        var verb = choice switch { CleanMode.Move => "weggeräumt", CleanMode.Sort => "einsortiert", _ => "ausgeblendet" };
        var notes = new List<string>();
        if (choice == CleanMode.Move && n > 0 && o.Run?.Folder != null) notes.Add($"Sie liegen in „{ShortPath(o.Run.Folder)}“.");
        if (choice == CleanMode.Sort && n > 0) notes.Add($"Auf deinem Desktop: {string.Join(", ", folders.Take(8))}{(folders.Count > 8 ? " …" : "")}.");
        if (o.Failed.Count > 0) notes.Add($"{o.Failed.Count} {(o.Failed.Count == 1 ? "konnte" : "konnten")} nicht {verb} werden und {(o.Failed.Count == 1 ? "bleibt" : "bleiben")} auf deinem Desktop.");
        if (excludedCount > 0) notes.Add($"{excludedCount} behalten, wie gewünscht.");
        if (m.LeftAlone > 0) notes.Add($"{m.LeftAlone} zur Sicherheit liegen gelassen.");
        if (n == 0) title = "Nichts wurde verändert";
        CountStats(o);
        var run = o.Run;
        if (n > 0 && run != null)
        {
            undoAction = () =>
            {
                m.Result = new RunResult { Title = "Hole alles zurück…" }; m.Changed();
                UndoRun(run);
            };
        }
        m.Result = new RunResult { Title = title, Detail = notes.Count > 0 ? string.Join(" ", notes) : null, CanUndo = undoAction != null, IsError = n == 0 && o.Failed.Count > 0 };
    }


    /// What this run cleaned up, added to the totals on the panel. Kept on this PC only.
    void CountStats(FileOps.Outcome o)
    {
        if (TestRoot != null || sim == null) return;
        var st = Model.Settings.Stats;
        foreach (var i in o.Done)
        {
            if (i < 0 || i >= sim.Items.Count) continue;
            var e = sim.Items[i].Entry;
            st.Items++; st.Bytes += e.Bytes;
            var k = e.Kind switch { Kind.Folder => "Ordner", Kind.Image => "Bilder", Kind.Video => "Videos", Kind.Document => "Dokumente", Kind.Archive => "Archive", Kind.Audio => "Musik", _ => "Sonstiges" };
            st.Kinds[k] = st.Kinds.TryGetValue(k, out var n) ? n + 1 : 1;
        }
        Model.Settings.Save();
    }

    /// A finished run: one more, and the distance driven, measured as if the robot were a real one.
    void CountRun()
    {
        if (TestRoot != null || sim == null) return;
        var st = Model.Settings.Stats;
        st.Runs++;
        st.Meters += sim.Distance * 0.17 / sim.R;   // a real robot's radius is about 17 cm
        Model.Settings.Save();
    }
    static string ShortPath(string p)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return p.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + p[home.Length..] : p;
    }

    void RunUndo() { var a = undoAction; undoAction = null; a?.Invoke(); }

    void UndoLastRun()
    {
        var run = Model.LastRun;
        if (run == null || Model.Stage != Stage.Ready) return;
        Model.Result = new RunResult { Title = "Hole alles zurück…" };
        Model.Changed();
        UndoRun(run);
    }

    /// Files first, then, once Explorer shows them again, their icons go back to where they were.
    void UndoRun(FileOps.Run run)
    {
        var m = Model;
        Task.Run(() => FileOps.Undo(run)).ContinueWith((Task<(List<FileOps.Record> back, List<(FileOps.Record item, string reason)> left)> t) =>
        {
            List<FileOps.Record> back; List<(FileOps.Record item, string reason)> left;
            if (t.IsFaulted) { back = new(); left = run.Items.Select(i => (i, "Fehler")).ToList(); }
            else (back, left) = t.Result;
            var root = TestRoot ?? DesktopReader.DesktopFolder;
            Native.SHChangeNotify(Native.SHCNE_UPDATEDIR, Native.SHCNF_PATHW | Native.SHCNF_FLUSH, root, IntPtr.Zero);
            var detail = left.Count > 0 ? $"{left.Count} {(left.Count == 1 ? "konnte" : "konnten")} nicht zurück: {string.Join(", ", left.Take(3).Select(l => Path.GetFileName(l.item.Original) + " (" + l.reason + ")"))}{(left.Count > 3 ? " …" : "")}" : null;
            m.Result = new RunResult { Title = $"{back.Count} {(back.Count == 1 ? "Element" : "Elemente")} zurückgeholt", Detail = detail, IsError = back.Count == 0 && left.Count > 0 };
            m.LastRun = null;
            m.Changed();
            if (TestRoot == null) RestorePositions(back, 0);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void RestorePositions(List<FileOps.Record> back, int attempt)
    {
        var moves = back.Where(r => r.X != null && r.Y != null).Select(r => (r.Original, new POINT(r.X.Value, r.Y.Value))).ToList();
        if (moves.Count == 0) return;
        try
        {
            var v = DesktopReader.OpenView();
            var shown = v.Paths();
            var ready = moves.Count(x => shown.Contains(Path.GetFullPath(x.Original).TrimEnd('\\')));
            // Explorer picks up new files with a short delay.
            if (ready < moves.Count && attempt < 10) { Delay(0.4, () => RestorePositions(back, attempt + 1)); return; }
            if (!v.AutoArrange) v.SetPositions(moves);
        }
        catch { }
    }

    /// Each display's icons go into columns on that same display.
    void ApplyTidy(Sim s)
    {
        var m = Model;
        m.Applying = true; m.Changed();
        var v = view;
        string error = null;
        var placed = s.Items.Where(i => !i.Protected && i.Entry.Path != null).Select(i => (f: 0, i)).ToList();
        var old = placed.Where(p => p.i.Entry.ViewPos != null).Select(p => (p.i.Entry.Path, p.i.Entry.ViewPos.Value)).ToList();
        if (v == null) error = "none";
        else if (v.AutoArrange) error = "auto";
        else
        {
            try
            {
                var moves = placed.Select(p => (p.i.Entry.Path, v.ToViewPos(p.i.Rest, screens))).ToList();
                var done = v.SetPositions(moves);
                if (done < moves.Count) error = "partial";
                undoAction = () =>
                {
                    m.Result = new RunResult { Title = "Lege die Symbole zurück…" }; m.Changed();
                    try { DesktopReader.OpenView().SetPositions(old); } catch { }
                    m.Result = new RunResult { Title = "Die Symbole sind wieder, wo sie waren" }; m.Changed();
                };
                FileOps.AppendLog($"ordnen: {done} Symbole angeordnet auf {screens.Count} Bildschirm(en)");
            }
            catch { error = "partial"; }
        }
        m.Result = error switch
        {
            "auto" => new RunResult { Title = "Dein Desktop wurde nicht sortiert", Detail = "Explorer ordnet deine Symbole automatisch an. Für Ordnen: Rechtsklick auf den Desktop › Ansicht › „Symbole automatisch anordnen“ ausschalten.", IsError = true },
            "none" => new RunResult { Title = "Nichts zu ordnen", Detail = "Ein Testordner hat keine Desktop-Symbole.", IsError = true },
            "partial" => new RunResult { Title = "Ordnen ist nicht ganz fertig geworden", Detail = "Explorer hat nicht jede Position übernommen. Keine Datei wurde verändert.", CanUndo = undoAction != null, IsError = true },
            _ => new RunResult { Title = $"{placed.Count} Elemente in Spalten sortiert", Detail = "Nur die Symbole haben sich bewegt. Deine Dateien sind genau, wo sie waren.", CanUndo = true },
        };

        Delay(0.5, () => { m.Applying = false; s.FinishAfterAction(new List<Item>()); m.Changed(); });
    }

    // MARK: - Windows

    void MinimizeWindows() { minimized.Clear(); minimized.AddRange(DesktopWindows.MinimizeAll()); }
    void RestoreWindows() { DesktopWindows.Restore(minimized); minimized.Clear(); }

    /// The copies fade to reveal the real desktop, which now matches them.
    void FinishRun()
    {
        if (fading) return;
        CountRun();
        fading = true;
        FileOps.Trace("finish run");
        var fading_ = overlays.ToList();
        if (fading_.Count == 0) { Teardown(); Model.Stage = Stage.Ready; Model.Changed(); return; }
        var sw = Stopwatch.StartNew();
        var t = new DispatcherTimer(DispatcherPriority.Render, ui) { Interval = TimeSpan.FromMilliseconds(16) };
        t.Tick += (_, _) =>
        {
            var k = sw.Elapsed.TotalSeconds / 0.3;
            foreach (var o in fading_) try { o.SetAlpha((float)(1 - k)); } catch { }
            if (k < 1) return;
            t.Stop();
            Teardown();
            Model.Stage = Stage.Ready;
            if (TestRoot == null && Model.Result == null) Model.LastRun = FileOps.LatestUndoable();
            if (Scheduled && Model.Result != null) Notify("RoboDust: " + Model.Result.Title, (Model.Result.Detail ?? "") + (Model.Result.CanUndo ? " Klick hier, um es rückgängig zu machen." : ""));
            OfferSupportOnce();
            if (AutoUndo && undoAction != null) Delay(1, () => { RunUndo(); if (AutoQuit) Delay(2.5, () => Quit()); });
            else if (AutoQuit) Delay(1, () => Quit());
            Model.Changed();
        };
        t.Start();
    }

    /// Once per PC, ever, after a clean that finished properly. Nothing leaves the PC: this setting
    /// is the only record that it happened.
    void OfferSupportOnce()
    {
        var st = Model.Settings;
        if (st.SupportCardShown || Model.Result?.IsError == true) return;
        st.SupportCardShown = true; st.Save();
        Model.Support = SupportCard.Asking;
    }

    void StopAndRestore() { Teardown(); Model.Stage = Stage.Ready; Model.Changed(); }

    /// Removing the overlays is the whole restore: the real desktop was never changed.
    void Teardown()
    {
        uiTimer?.Stop(); uiTimer = null;
        CompositionTarget.Rendering -= OnFrame;
        synth.SetMotor(0, 0); synth.SetRoar(0); synth.Cat.SetPurr(0);
        // Coins earned this run go to the shop, even if it was stopped early.
        if (sim != null && TestRoot == null && sim.CoinsEarned > 0)
        {
            Model.Settings.Coins += sim.CoinsEarned;
            Model.Settings.Save();
            Model.Note = $"+{sim.CoinsEarned} Münzen für den Shop";
        }
        synth.StopLive();

        foreach (var o in overlays) { o.Sim = null; o.Hide(); o.Dispose(); }
        overlays.Clear();
        sim = null; Model.Sim = null;
        Model.Screens = new List<Screen>();
        fading = false;
        RestoreWindows();
    }
}

public static class AppIcon
{
    /// The header glyph, as an icon: a white shell, a dark top plate and the cyan lidar dome.
    public static System.Drawing.Icon Make(int size)
    {
        using var bmp = Draw(size);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    static System.Drawing.Bitmap Draw(int size)
    {
        var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float s = size;
        g.FillEllipse(new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(232, 232, 232)), 0.5f, 0.5f, s - 1, s - 1);
        g.FillEllipse(new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(31, 31, 31)), s * 0.15f, s * 0.15f, s * 0.7f, s * 0.7f);
        g.FillEllipse(new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(92, 214, 250)), s * 0.34f, s * 0.2f, s * 0.32f, s * 0.32f);
        return bmp;
    }

    /// Writes a .ico with PNG frames from 16 to 256 pixels, for the .exe.
    public static void WriteIco(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var frames = sizes.Select(s => { using var b = Draw(s); using var ms = new MemoryStream(); b.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray(); }).ToList();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] % 256)); w.Write((byte)(sizes[i] % 256)); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32); w.Write(frames[i].Length); w.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var f in frames) w.Write(f);
    }
}

/// Clears the user's windows out of the way for a run, and brings exactly those back afterwards.
public static class DesktopWindows
{
    /// Minimizes the user's windows so the desktop is visible, remembering which ones to bring back.
    public static List<IntPtr> MinimizeAll()
    {
        var minimized = new List<IntPtr>();
        var me = (uint)Environment.ProcessId;
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == me) return true;
            var ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return true;
            if ((ex & Native.WS_EX_TOPMOST) != 0 && Native.GetWindowTextLength(h) == 0) return true;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            if ((Native.GetWindowLong(h, Native.GWL_STYLE) & Native.WS_MINIMIZEBOX) == 0) return true;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0) return true;
            var cls = Native.ClassOf(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        foreach (var h in list) { Native.ShowWindowAsync(h, Native.SW_SHOWMINNOACTIVE); minimized.Add(h); }
        return minimized;
    }


    /// The user's windows as rectangles in the shared world, for the cat that hides under them.
    public static List<RectF> WorldRects(List<Screen> screens)
    {
        var list = new List<RectF>();
        var me = (uint)Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == me) return true;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0) return true;
            var cls = Native.ClassOf(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (!Native.GetWindowRect(h, out var r)) return true;
            var a = Screen.Under(screens, r.Left + 8, r.Top + 8);
            if (a == null) return true;
            var tl = a.ToWorld(Math.Max(r.Left, a.WorkArea.Left), Math.Max(r.Top, a.WorkArea.Top));
            var br = a.ToWorld(Math.Min(r.Right, a.WorkArea.Right), Math.Min(r.Bottom, a.WorkArea.Bottom));
            var rect = new RectF(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
            if (rect.W >= 260 && rect.H >= 180) list.Add(rect);
            return true;
        }, IntPtr.Zero);
        return list;
    }
    public static void Restore(List<IntPtr> minimized)
    {
        // Bottom first, so the window that was in front ends up in front again.
        for (int i = minimized.Count - 1; i >= 0; i--)
        {
            var h = minimized[i];
            if (Native.IsWindow(h) && Native.IsIconic(h)) Native.ShowWindowAsync(h, Native.SW_RESTORE);
        }
    }
}

/// Arrow keys for driving the robot yourself. Only while RoboDust itself is in front, so they
/// never steer the robot while you type somewhere else.
public static class ArrowKeys
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    static bool Ours()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
        return pid == (uint)Environment.ProcessId;
    }
    public static float Throttle() => !Ours() ? 0 : (Down(0x26) ? 1 : 0) - (Down(0x28) ? 0.6f : 0);   // up, down
    public static float Steer() => !Ours() ? 0 : (Down(0x27) ? 1 : 0) - (Down(0x25) ? 1 : 0);         // right, left
}
