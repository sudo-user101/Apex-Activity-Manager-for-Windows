using System.Globalization;
using System.Text.Json;

namespace Apex.Core;

/// One app's use on one day, as on the Mac's App History.
public sealed class AppUsageDay
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public double CpuSeconds { get; set; }          // core-seconds (one core busy for one second = 1)
    public double DiskBytes { get; set; }
    public double NetBytes { get; set; }
    public long PeakMemory { get; set; }
    public DateTime LastSeen { get; set; }
}

/// App History: each app's CPU time, disk and network use and peak memory, per
/// day, for 30 days. An app's helper processes count towards it (same program
/// path). Pure: the Windows app feeds samples and saves the JSON.
public sealed class AppHistory
{
    public Dictionary<string, Dictionary<string, AppUsageDay>> Days { get; set; } = new();
    DateTime? lastSample;
    public const int KeepDays = 30;

    public sealed record Use(string Key, string Name, string Path, double Cores, double DiskPerSec, double NetPerSec, long Memory);

    static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// One app's key: its program path (helpers share it), or its name.
    public static string KeyFor(string path, string name) => path.Length > 0 ? path.ToLowerInvariant() : "n:" + name.ToLowerInvariant();

    /// Adds one sample. Gaps over 2 minutes (sleep) aren't counted.
    public void Record(DateTime date, IEnumerable<Use> uses)
    {
        var last = lastSample;
        lastSample = date;
        if (last is not { } l) return;
        double dt = (date - l).TotalSeconds;
        if (dt <= 0 || dt >= 120) return;
        var key = Day(date);
        if (!Days.TryGetValue(key, out var today)) Days[key] = today = new();
        var mem = new Dictionary<string, long>();
        foreach (var u in uses)
        {
            mem[u.Key] = mem.GetValueOrDefault(u.Key) + u.Memory;
            double cpu = u.Cores * dt, disk = u.DiskPerSec * dt, net = u.NetPerSec * dt;
            if (cpu <= 0.001 && disk <= 0 && net <= 0 && !today.ContainsKey(u.Key)) continue;
            if (!today.TryGetValue(u.Key, out var d)) today[u.Key] = d = new AppUsageDay { Name = u.Name, Path = u.Path };
            d.CpuSeconds += cpu; d.DiskBytes += disk; d.NetBytes += net; d.LastSeen = date;
        }
        foreach (var (k, m) in mem) if (today.TryGetValue(k, out var d)) d.PeakMemory = Math.Max(d.PeakMemory, m);
    }

    /// Totals over the last `days` days, most CPU time first.
    public List<(string Key, AppUsageDay Usage)> Totals(int days, DateTime now)
    {
        var cutoff = Day(now.AddDays(-(days - 1)));
        var sum = new Dictionary<string, AppUsageDay>();
        foreach (var (day, apps) in Days)
        {
            if (string.CompareOrdinal(day, cutoff) < 0) continue;
            foreach (var (k, u) in apps)
            {
                if (!sum.TryGetValue(k, out var t)) sum[k] = t = new AppUsageDay { Name = u.Name, Path = u.Path };
                t.CpuSeconds += u.CpuSeconds; t.DiskBytes += u.DiskBytes; t.NetBytes += u.NetBytes;
                t.PeakMemory = Math.Max(t.PeakMemory, u.PeakMemory);
                if (u.LastSeen > t.LastSeen) t.LastSeen = u.LastSeen;
            }
        }
        return sum.Select(x => (x.Key, x.Value)).OrderByDescending(x => x.Value.CpuSeconds).ToList();
    }

    /// One app, day by day (oldest first).
    public List<(string Day, AppUsageDay Usage)> Daily(string key, int days, DateTime now)
    {
        var cutoff = Day(now.AddDays(-(days - 1)));
        return Days.Where(d => string.CompareOrdinal(d.Key, cutoff) >= 0 && d.Value.ContainsKey(key))
                   .Select(d => (d.Key, d.Value[key])).OrderBy(x => x.Key).ToList();
    }

    public void Prune(DateTime now)
    {
        var cutoff = Day(now.AddDays(-KeepDays));
        foreach (var k in Days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList()) Days.Remove(k);
    }

    public DateTime? Since => Days.Keys.Count == 0 ? null : DateTime.ParseExact(Days.Keys.Min()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string ToJson() => JsonSerializer.Serialize(Days);
    public static AppHistory FromJson(string json)
    {
        try { return new AppHistory { Days = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, AppUsageDay>>>(json) ?? new() }; }
        catch { return new AppHistory(); }
    }
}
