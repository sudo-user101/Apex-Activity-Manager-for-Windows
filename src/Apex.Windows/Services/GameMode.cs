using System.Diagnostics;
using System.Runtime.InteropServices;
using Apex.Core;
using Apex.UI;

namespace Apex.Services;

/// Game mode: while a game runs, Apex quiets other apps (Efficiency mode),
/// switches to the fastest power plan, raises the game's priority, and
/// watches its frame rate against the target, saying what holds it back.
/// Everything is put back when it stops or the game quits. It can't promise
/// a frame rate (the game and the hardware decide); it reports how close it
/// gets and why.
internal static class GameMode
{
    public sealed record Session(string GamePath, string GameName, double TargetFps, ProcessIdentity[] Game, List<string> Quieted, Guid? PreviousPlan);
    public static Session? Active { get; private set; }
    public static TimeSeries Fps { get; } = new(TimeSpan.FromSeconds(60));
    public static FrameStats Latest { get; private set; }
    public static (Limit Limit, string Advice) Diagnosis { get; private set; } = (Limit.None, "");
    public static event Action? Changed;

    /// Apps (by file name) never quieted: voice chat, music, the user's choice.
    public static HashSet<string> Keep { get; } = new(Settings.Get("gameKeep", "").Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
    public static void SaveKeep() => Settings.Set("gameKeep", string.Join('|', Keep));

    public static List<PowerPlan> Plans()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("powercfg.exe", "/list") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var text = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
            return PowerPlanList.Parse(text);
        }
        catch { return new(); }
    }

    static bool SetPlan(Guid id)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("powercfg.exe", $"/setactive {id}") { UseShellExecute = false, CreateNoWindow = true })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// Starts Game mode for the program at `gamePath`. `quiet`: the programs
    /// to put in Efficiency mode (the page lets the user untick some).
    public static string Start(string gamePath, string gameName, double target, IEnumerable<string> quiet)
    {
        Stop();
        var processes = AppState.Latest.Processes;
        var game = processes.Where(p => p.Path.Equals(gamePath, StringComparison.OrdinalIgnoreCase)).Select(p => p.Identity).ToArray();
        var notes = new List<string>();

        var quieted = new List<string>();
        foreach (var path in quiet.Where(p => !Efficiency.Programs.Contains(p)))
            if (Efficiency.Set(path, true, processes)) quieted.Add(path);
        if (quieted.Count > 0) notes.Add($"{quieted.Count} app{(quieted.Count == 1 ? "" : "s")} quieted");

        var plans = Plans();
        Guid? previous = plans.FirstOrDefault(p => p.Active)?.Id;
        if (PowerPlanList.Fastest(plans) is { } fast && fast.Id != previous && SetPlan(fast.Id)) notes.Add($"power plan: {fast.Name}");
        else previous = null;

        int raised = game.Count(t => SetPriority(t, Native.HIGH_PRIORITY_CLASS));
        if (raised > 0) notes.Add("game priority: high");

        Fps.Clear();
        Active = new Session(gamePath, gameName, target, game, quieted, previous);
        FrameMonitor.Acquire();
        Changed?.Invoke();
        return notes.Count == 0 ? "Game mode is on." : "Game mode is on: " + string.Join(", ", notes) + ".";
    }

    /// Puts everything back: the apps it quieted, the power plan, the game's priority.
    public static void Stop()
    {
        if (Active is not { } s) return;
        foreach (var path in s.Quieted.Where(Efficiency.Programs.Contains)) Efficiency.Set(path, false, AppState.Latest.Processes);
        if (s.PreviousPlan is { } plan) SetPlan(plan);
        foreach (var t in s.Game) SetPriority(t, Native.NORMAL_PRIORITY_CLASS);
        Active = null;
        FrameMonitor.Release();
        Changed?.Invoke();
    }

    /// Each tick: the game's frame rate, what limits it; stops when the game quits.
    public static void Tick(Snapshot snap)
    {
        if (Active is not { } s) return;
        var mine = snap.Processes.Where(p => p.Path.Equals(s.GamePath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0) { Stop(); return; }
        // Processes the game started since join in (launchers often hand over to a child).
        foreach (var p in mine.Where(p => !s.Game.Contains(p.Identity))) SetPriority(p.Identity, Native.HIGH_PRIORITY_CLASS);
        Active = s with { Game = mine.Select(p => p.Identity).ToArray() };

        var stats = mine.Select(p => FrameMonitor.Stats(p.Pid)).OrderByDescending(f => f.Fps).FirstOrDefault();
        Latest = stats;
        if (stats.Fps > 0) Fps.Add(snap.Time, stats.Fps);
        double gpu = snap.Gpus.Select(g => g.Utilization).DefaultIfEmpty(0).Max();
        double maxCore = snap.Cpu.PerCore.DefaultIfEmpty(0).Max();
        double background = snap.Processes.Where(p => p.Kind != ProcessKind.Windows && !p.Path.Equals(s.GamePath, StringComparison.OrdinalIgnoreCase)).Sum(p => p.Cpu);
        Diagnosis = stats.Fps > 0 ? Bottleneck.Diagnose(stats.Fps, s.TargetFps, gpu, maxCore, background, snap.Memory.Pressure)
                                  : (Limit.Unknown, FrameMonitor.Status == FrameMonitor.State.Running ? "Waiting for the game to draw frames…" : FrameMonitor.Detail);
        Changed?.Invoke();
    }

    static bool SetPriority(ProcessIdentity t, uint priority)
    {
        var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, t.Pid);
        if (h == IntPtr.Zero) return false;
        try { return Native.GetProcessTimes(h, out long c, out _, out _, out _) && c == t.CreateTime && Native.SetPriorityClass(h, priority); }
        finally { Native.CloseHandle(h); }
    }
}
