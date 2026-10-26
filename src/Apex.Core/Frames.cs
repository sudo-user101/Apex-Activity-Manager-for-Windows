using System.Globalization;

namespace Apex.Core;

/// One frame an app presented, as PresentMon reports it.
public readonly record struct FrameSample(string App, int Pid, double MsBetweenPresents, string Runtime);

/// Reads PresentMon's CSV output (columns found by name, so versions with
/// more or fewer columns still work). Pure: tested.
public sealed class PresentMonCsv
{
    int app = -1, pid = -1, between = -1, runtime = -1;
    public bool HasHeader => between >= 0 && pid >= 0;

    /// A frame, or null for the header and anything that isn't a frame row.
    public FrameSample? Parse(string line)
    {
        var f = line.Split(',');
        if (!HasHeader || f[0] == "Application")
        {
            if (f.Contains("MsBetweenPresents") || f.Contains("FrameTime"))
            {
                app = Array.IndexOf(f, "Application");
                pid = Array.IndexOf(f, "ProcessID");
                between = Array.IndexOf(f, "MsBetweenPresents");
                if (between < 0) between = Array.IndexOf(f, "FrameTime");
                runtime = Array.IndexOf(f, "PresentRuntime");
                if (runtime < 0) runtime = Array.IndexOf(f, "Runtime");
            }
            return null;
        }
        if (f.Length <= Math.Max(between, pid)) return null;
        if (!int.TryParse(f[pid], out int id)) return null;
        if (!double.TryParse(f[between], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) || ms <= 0 || ms > 5000) return null;
        return new FrameSample(app >= 0 ? f[app] : "", id, ms, runtime >= 0 && runtime < f.Length ? f[runtime] : "");
    }
}

/// Frame rate figures for one app over the last few seconds.
public readonly record struct FrameStats(double Fps, double OnePercentLow, double FrameTimeMs, double JitterMs, int Frames);

/// Recent frames per process: average FPS, 1% low and frame-time jitter. Pure: tested.
public sealed class FrameHistory
{
    readonly Dictionary<int, Queue<(double At, double Ms)>> frames = new();
    readonly Dictionary<int, string> names = new();
    public double WindowSeconds { get; init; } = 10;

    /// `at`: seconds on any steady clock.
    public void Add(FrameSample f, double at)
    {
        if (!frames.TryGetValue(f.Pid, out var q)) frames[f.Pid] = q = new();
        q.Enqueue((at, f.MsBetweenPresents));
        names[f.Pid] = f.App;
        while (q.Count > 0 && q.Peek().At < at - WindowSeconds) q.Dequeue();
    }

    /// Drops processes with no frames for `quietSeconds`.
    public void Prune(double now, double quietSeconds = 3)
    {
        foreach (var pid in frames.Keys.ToList())
        {
            var q = frames[pid];
            while (q.Count > 0 && q.Peek().At < now - WindowSeconds) q.Dequeue();
            if (q.Count == 0 || q.Last().At < now - quietSeconds) { frames.Remove(pid); names.Remove(pid); }
        }
    }

    public IEnumerable<(int Pid, string App)> Apps => frames.Keys.Select(p => (p, names.GetValueOrDefault(p, "")));

    /// FPS over the last `seconds` (by frame times, so it's exact even between ticks);
    /// 1% low and jitter over the whole window.
    public FrameStats Stats(int pid, double now, double seconds = 2)
    {
        if (!frames.TryGetValue(pid, out var q) || q.Count == 0) return default;
        var recent = q.Where(x => x.At >= now - seconds).Select(x => x.Ms).ToList();
        var all = q.Select(x => x.Ms).ToList();
        if (recent.Count == 0) return new FrameStats(0, 0, 0, 0, all.Count);
        double avgMs = recent.Average();
        // 1% low: the frame rate of the slowest 1% of frames (at least one), averaged.
        var slowest = all.OrderByDescending(x => x).Take(Math.Max(1, all.Count / 100)).ToList();
        double lowMs = slowest.Average();
        double mean = all.Average();
        double jitter = Math.Sqrt(all.Sum(x => (x - mean) * (x - mean)) / all.Count);
        return new FrameStats(1000 / avgMs, 1000 / lowMs, avgMs, jitter, all.Count);
    }
}

/// What's holding a game below its frame-rate target, in plain words.
public enum Limit { None, Gpu, Cpu, Background, Memory, Unknown }

public static class Bottleneck
{
    /// Pure: tested. `maxCore`: the busiest logical processor (%), since games
    /// are usually held back by one busy thread, not the average.
    public static (Limit Limit, string Advice) Diagnose(double fps, double target, double gpu, double maxCore,
        double backgroundCpu, PressureLevel memory)
    {
        if (target <= 0 || fps >= target * 0.97) return (Limit.None, $"Holding {target:0} FPS.");
        if (memory == PressureLevel.Critical)
            return (Limit.Memory, "Memory is nearly full, so Windows is swapping. Close apps you aren’t using, or let Game mode quiet them.");
        if (backgroundCpu >= 15)
            return (Limit.Background, $"Other apps are using {backgroundCpu:0}% of the CPU. Game mode can quiet them.");
        if (gpu >= 95)
            return (Limit.Gpu, "The graphics card is working flat out. Lower the resolution or graphics settings, or turn on upscaling in the game.");
        if (maxCore >= 92)
            return (Limit.Cpu, "One processor core is maxed out, so the CPU can’t prepare frames faster. Lower CPU-heavy settings (view distance, crowds, physics).");
        return (Limit.Unknown, "Nothing on the PC is maxed out. The game may be capped (V-Sync or a frame limit), or loading.");
    }
}

/// Which apps Game mode quiets: apps with windows other than the game,
/// Apex, Windows itself, and anything the user keeps (voice chat, music). Pure: tested.
public static class GameModePlan
{
    /// Programs (by path, one entry each) to put in Efficiency mode while the game runs.
    public static List<(string Path, string Name)> ToQuiet(IEnumerable<ProcessSample> processes, string gamePath, ISet<string> keep, int self)
    {
        static string FileName(string path) => path[(path.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        return processes
            .Where(p => p.Kind == ProcessKind.App && p.Pid != self && p.Path.Length > 0)
            .Where(p => !p.Path.Equals(gamePath, StringComparison.OrdinalIgnoreCase))
            .Where(p => !keep.Contains(p.Path) && !keep.Contains(FileName(p.Path)))
            .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Select(p => p.DisplayName.Length > 0 ? p.DisplayName : p.Name).First()))
            .ToList();
    }
}
