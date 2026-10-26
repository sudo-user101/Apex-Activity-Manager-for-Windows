using Apex.Core;

namespace Apex.UI;

/// What every page shares: the latest snapshot, the last minute of history
/// for the graphs, and the apps put in Efficiency mode (`Efficiency`).
internal static class AppState
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    public static Snapshot Latest { get; private set; } = new();
    internal static void SetLatest(Snapshot s) => Latest = s;

    public static readonly TimeSeries Cpu = new(Window);
    public static readonly TimeSeries Memory = new(Window);
    public static readonly TimeSeries Gpu = new(Window);
    public static readonly Dictionary<string, TimeSeries> Series = new();
    /// Programs (by executable) in Efficiency mode, remembered while they run.
    public static HashSet<string> Efficient => Services.Efficiency.Programs;

    public static TimeSeries Get(string key)
    {
        if (!Series.TryGetValue(key, out var s)) Series[key] = s = new TimeSeries(Window);
        return s;
    }

    public static void Record(Snapshot s)
    {
        Latest = s;
        var t = s.Time;
        Cpu.Add(t, s.Cpu.Total);
        Memory.Add(t, s.Memory.Percent);
        foreach (var d in s.Disks)
        {
            Get("disk:" + d.Name).Add(t, d.ActivePercent);
            Get("diskr:" + d.Name).Add(t, d.ReadPerSec);
            Get("diskw:" + d.Name).Add(t, d.WritePerSec);
        }
        foreach (var n in s.Networks)
        {
            Get("netin:" + n.Id).Add(t, n.InPerSec);
            Get("netout:" + n.Id).Add(t, n.OutPerSec);
        }
        foreach (var g in s.Gpus)
        {
            Gpu.Add(t, g.Utilization);
            foreach (var e in new[] { "3D", "Copy", "VideoDecode", "VideoEncode" }) Get("gpu:" + e).Add(t, g.Engines.GetValueOrDefault(e));
            Get("gpumem").Add(t, g.DedicatedUsed);
        }
        for (int i = 0; i < s.Cpu.PerCore.Length; i++) Get("core:" + i).Add(t, s.Cpu.PerCore[i]);
    }
}

/// Every page: built once, refreshed with each snapshot while it's on screen.
internal interface IPage
{
    Microsoft.UI.Xaml.UIElement View { get; }
    void Update(Snapshot s);
    void Shown() { }
    /// Another page took its place.
    void Hidden() { }
}

/// For the self-test: sets the latest snapshot without a sampler.
internal static class AppStateTest
{
    public static void AppState_ForTest(Snapshot s) => AppState.SetLatest(s);
}
