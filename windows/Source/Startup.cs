using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RoboDust;

/// Starting with Windows, the shortcut, and the schedule. Each is switched on only by the user, on
/// the settings page, and switching it off removes it again. All of it is for this user only.
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "RoboDust";
    public const string TaskName = "RoboDust Aufräumen";

    public static string ExePath => Environment.ProcessPath;

    // MARK: Starting with Windows

    /// Starts quietly in the tray at sign-in, so the shortcut and the schedule work.
    public static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(ValueName, $"\"{ExePath}\" --tray");
        else k.DeleteValue(ValueName, false);
    }

    public static bool AutostartOn
    {
        get { try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(ValueName) != null; } catch { return false; } }
    }

    // MARK: Shortcut

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    public const int WM_HOTKEY = 0x0312, HotkeyId = 0x5244;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;

    /// Strg+Alt+R starts a clean, from anywhere.
    public static bool SetHotkey(IntPtr hwnd, bool on)
    {
        UnregisterHotKey(hwnd, HotkeyId);
        return !on || RegisterHotKey(hwnd, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'R');
    }

    // MARK: Schedule

    /// The Windows Task Scheduler arguments for a schedule: 1 daily, 2 every Friday, at the given hour.
    public static string[] ScheduleArgs(int schedule, int hour)
    {
        var at = $"{Math.Clamp(hour, 0, 23):00}:00";
        var run = $"\"{ExePath}\" --scheduled";
        return schedule == 1
            ? new[] { "/Create", "/F", "/TN", TaskName, "/SC", "DAILY", "/ST", at, "/TR", run }
            : new[] { "/Create", "/F", "/TN", TaskName, "/SC", "WEEKLY", "/D", "FRI", "/ST", at, "/TR", run };
    }

    /// Returns null on success, or what went wrong.
    public static string SetSchedule(int schedule, int hour)
    {
        var args = schedule == 0 ? new[] { "/Delete", "/F", "/TN", TaskName } : ScheduleArgs(schedule, hour);
        try
        {
            var p = new ProcessStartInfo("schtasks.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) p.ArgumentList.Add(a);
            using var proc = Process.Start(p);
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10000);
            // Deleting a task that isn't there is fine.
            if (proc.ExitCode != 0 && schedule != 0) return string.IsNullOrWhiteSpace(err) ? "Die Aufgabenplanung hat abgelehnt." : err.Trim();
            return null;
        }
        catch (Exception e) { return e.Message; }
    }
}
