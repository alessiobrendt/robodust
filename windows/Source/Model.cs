using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RoboDust;

public enum Source { Sample, Real }
public enum Stage { Ready, Preparing, Running }
public enum SupportCard { Hidden, Asking, Thanked }

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
    public bool SkipFinalClick { get; set; }
    public bool ClearWindows { get; set; } = true;
    public bool SupportCardShown { get; set; }

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

    public Action Start, TogglePause, Stop, PutBack, About, Undo, DismissResult, UndoLast, OpenShelf, Quit;
    public Action<CleanMode> Choose;
    public Action<string> Support_;
    public Action Changed = () => { };
}
