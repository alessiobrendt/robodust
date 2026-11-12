using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RoboDust;

public enum Source { Sample, Real }
public enum Stage { Ready, Preparing, Running }
public enum SupportCard { Hidden, Asking, Thanked }

/// Kept on this PC only: what RoboDust has done so far, for the line on the panel.
public sealed class Stats
{
    public int Runs { get; set; }
    public int Items { get; set; }
    public long Bytes { get; set; }
    public double Meters { get; set; }
    public Dictionary<string, int> Kinds { get; set; } = new();
    public string FavouriteKind
    {
        get
        {
            string best = null; int n = 0;
            foreach (var (k, c) in Kinds) if (c > n) { n = c; best = k; }
            return best;
        }
    }
}

public enum Page { Main, Settings, Shelf }
public enum RobotSkin { White = 0, Black = 1, Gold = 2, Retro = 3, Pink = 4 }

public sealed class RunResult
{
    public string Title, Detail;
    public bool CanUndo, IsError;
}

/// Remembered between launches. Nothing here ever leaves the PC.
public sealed class Settings
{
    public int Mode { get; set; } = (int)CleanMode.Move;
    public int SortBy { get; set; }
    public int Fun { get; set; }
    public string DockScreen { get; set; }   // device name of the display the dock stands on; null for the main one
    public bool SkipFinalClick { get; set; }
    public bool ClearWindows { get; set; } = true;
    public bool SupportCardShown { get; set; }
    // Einstellungen page.
    public int Skin { get; set; }              // RobotSkin
    public bool Hat { get; set; }
    public bool RemoteControl { get; set; }    // arrow keys drive the robot
    public int MinAgeDays { get; set; }        // rule: only things untouched this long
    public List<string> NeverTouch { get; set; } = new();   // rule: paths RoboDust always leaves alone
    public int Schedule { get; set; }          // 0 off, 1 daily, 2 weekly (Friday)
    public int ScheduleHour { get; set; } = 17;
    public bool Autostart { get; set; }
    public bool Hotkey { get; set; }
    public bool HasCat { get; set; }
    public Stats Stats { get; set; } = new();

    public static bool Persists = true;
    static string FilePath => Path.Combine(FileOps.DataDir, "settings.json");

    public static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); } catch { }
        return new Settings();
    }
    public void Save()
    {
        if (!Persists) return;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); } catch { }
    }
}

/// Everything the panel shows. The controller writes it; the panel reads it.
public sealed class Model
{
    public readonly Settings Settings = Settings.Load();
    public Stage Stage = Stage.Ready;
    public Source Source = Source.Sample;
    public CleanMode Mode { get => (CleanMode)Settings.Mode; set { Settings.Mode = (int)value; Settings.Save(); } }
    public SortBy SortBy { get => (SortBy)Settings.SortBy; set { Settings.SortBy = (int)value; Settings.Save(); } }
    public string DockScreen { get => Settings.DockScreen; set { Settings.DockScreen = value; Settings.Save(); } }
    public FunMode Fun { get => (FunMode)Settings.Fun; set { Settings.Fun = (int)value; Settings.Save(); } }
    public bool SkipFinalClick { get => Settings.SkipFinalClick; set { Settings.SkipFinalClick = value; Settings.Save(); } }
    public bool ClearWindows { get => Settings.ClearWindows; set { Settings.ClearWindows = value; Settings.Save(); } }
    public CleanMode? Outcome;
    public bool RealRun, Applying;
    public RunResult Result;
    public SupportCard Support = SupportCard.Hidden;
    public HashSet<int> Excluded = new();
    public bool ReviewOpen;
    public long FileBytes;
    public int FolderCount, LeftAlone;
    public bool UsesCloud, HiddenShown;
    public List<(int index, string name)> ReviewRows = new();
    public bool Muted;
    public int Prepared, PrepareTotal;
    public string Note;
    public FileOps.Run LastRun;          // a run from earlier that can still be undone
    // Live.
    public string Status = "";
    public Phase Phase = Phase.Dropping;
    public bool Paused;
    public float Battery = 1, Bin;
    public int Collected, Total;
    public long CollectedBytes, TotalBytes;
    public float RunTime;
    public Sim Sim;
    public List<Screen> Screens = new();
    public Page Page = Page.Main;
    /// Changes a setting and saves it.
    public void Set(Action change) { change(); Settings.Save(); Changed(); }
    public Action<bool> SetAutostart, SetHotkey;
    public Action<int> SetSchedule;
    public Action<string> NeverTouch;

    public Action Start, TogglePause, Stop, PutBack, About, Undo, DismissResult, UndoLast, OpenShelf, Quit;
    public Action<CleanMode> Choose;
    public Action<string> Support_;
    public Action Changed = () => { };
}
