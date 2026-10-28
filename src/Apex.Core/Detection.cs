namespace Apex.Core;

// Apex's detection engine, ported from the Mac (DetectionCore): samples in,
// events out. No clock and no I/O: time comes only from each sample's date,
// so the rules are unit-tested and behave the same on both editions.

// ---- Input ------------------------------------------------------------------------

public sealed record AppLoad(string Key, string Name, double Cpu, double DiskWritePerSec = 0, double NetPerSec = 0,
                             long Memory = 0, int[]? Pids = null)
{
    public int[] PidList => Pids ?? Array.Empty<int>();
}

public enum ThermalState { Nominal, Fair, Serious, Critical }

public sealed record DetectionSample
{
    public DateTime Date { get; init; }
    public IReadOnlyList<AppLoad> Apps { get; init; } = Array.Empty<AppLoad>();
    public PressureLevel Pressure { get; init; } = PressureLevel.Normal;
    public double MemoryUsedPercent { get; init; }
    public long PageFileUsed { get; init; }
    public long MemoryTotal { get; init; }
    /// Windows has no general heat signal: stays Nominal unless a source reports it.
    public ThermalState Thermal { get; init; } = ThermalState.Nominal;
    public long DiskAvailable { get; init; }
    public long DiskCapacity { get; init; }
    public string VolumeName { get; init; } = "C:";
    public double DiskWritePerSec { get; init; }
    public double NetworkPerSec { get; init; }
}

// ---- Output -----------------------------------------------------------------------

// Severity is shared with Network Doctor (Info, Warning, Critical are used here).

public enum Rule { AppCpu, MemoryPressure, Thermal, DiskSpace, DiskFilling, DiskWrites, MemoryGrowth, SwapGrowth, DriveHealth, Network }

public static class RuleText
{
    public static string Title(Rule r) => r switch
    {
        Rule.DriveHealth => "Drive health", Rule.AppCpu => "High app CPU", Rule.MemoryPressure => "Memory pressure",
        Rule.MemoryGrowth => "Memory keeps growing", Rule.SwapGrowth => "Page file growing", Rule.Thermal => "Heat",
        Rule.DiskSpace => "Low disk space", Rule.DiskFilling => "Disk filling fast", Rule.DiskWrites => "Heavy disk writing",
        _ => "Heavy network traffic",
    };
}

/// Sustained = held long enough to alert. Spike = went over but recovered
/// before that (recorded, never notified). Instant = a state, not a trend.
public enum EventKind { Sustained, Spike, Instant }

public sealed record Evidence(string Label, string Value);

public sealed record DetectionEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Rule Rule { get; init; }
    public string Key { get; init; } = "";          // dedupe / cooldown identity
    public DateTime Date { get; init; }              // when it was detected
    public DateTime Started { get; init; }           // when the condition began
    public Severity Severity { get; init; }
    public EventKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    /// Plain-language "why this fired": the rule and the numbers that met it.
    public string Reason { get; init; } = "";
    public List<Evidence> Evidence { get; init; } = new();
    public string? Subject { get; init; }
    public int[] Pids { get; init; } = Array.Empty<int>();
    public bool Notify { get; init; }
    /// Values behind the event, evenly spaced from Started to Date (a small chart).
    public List<double>? Series { get; init; }
}

// ---- Settings ---------------------------------------------------------------------

public sealed record DetectionSettings
{
    public bool AppCpu { get; init; } = true;
    public double CpuThreshold { get; init; } = 80;           // % of one core
    public double CpuMinutes { get; init; } = 3;
    public bool Memory { get; init; } = true;
    public bool Heat { get; init; } = true;
    public bool Disk { get; init; } = true;
    public bool DiskWrites { get; init; } = true;
    public double WriteThreshold { get; init; } = 100_000_000; // bytes/s
    public double WriteMinutes { get; init; } = 2;
    /// Keep short spikes in the event history (never notified).
    public bool RecordSpikes { get; init; } = true;
    /// Apps whose memory climbs steadily (possible leak), and the page file growing fast.
    public bool MemoryGrowth { get; init; } = true;
    public bool Network { get; init; } = true;
    public double NetworkThreshold { get; init; } = 10_000_000;
    public double NetworkMinutes { get; init; } = 2;
}

// ---- Spike vs sustained -----------------------------------------------------------

/// Tracks one condition over time. Brief dips shorter than `grace` don't break
/// an episode, and the episode's peak and average are kept as evidence.
public sealed class Sustained
{
    public sealed record Episode(DateTime Start, DateTime End, double Peak, double Average)
    {
        public TimeSpan Duration => End - Start;
    }

    public TimeSpan Grace { get; }
    public DateTime? Start { get; private set; }
    DateTime? lastActive, lastSeen;
    double peak, sum;
    int count;
    public bool Fired { get; private set; }
    public static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(10);

    public Sustained(double graceSeconds = 10) { Grace = TimeSpan.FromSeconds(graceSeconds); }
    public bool IsActive => Start != null;

    /// Feeds one sample. Returns the finished episode when it ends. Each sample
    /// stands for the interval since the previous one (capped, so a gap after
    /// sleep doesn't count).
    public Episode? Update(bool active, double value, DateTime now)
    {
        var step = lastSeen is { } ls ? TimeSpan.FromTicks(Math.Clamp((now - ls).Ticks, 0, MaxStep.Ticks)) : TimeSpan.Zero;
        lastSeen = now;
        if (active)
        {
            if (Start == null) { Start = now - step; peak = value; sum = 0; count = 0; Fired = false; }
            lastActive = now;
            peak = Math.Max(peak, value); sum += value; count++;
            return null;
        }
        if (Start is not { } s || lastActive is not { } last) return null;
        if (now - last <= Grace) return null;                    // a brief dip
        var ep = new Episode(s, last, peak, count > 0 ? sum / count : 0);
        Reset();
        return ep;
    }

    /// The running episode (for "has it lasted long enough?").
    public Episode? Current(DateTime now) =>
        Start is { } s && lastActive is { } last ? new Episode(s, last > now ? last : now, peak, count > 0 ? sum / count : 0) : null;

    public void MarkFired() => Fired = true;
    public void Reset() { Start = null; lastActive = null; peak = 0; sum = 0; count = 0; Fired = false; }
}

// ---- Engine -----------------------------------------------------------------------

public sealed class DetectionEngine
{
    public DetectionSettings Settings { get; set; }
    readonly Func<Rule, double, string> format;

    readonly Dictionary<string, Sustained> appCpu = new();
    readonly Dictionary<string, (string Name, int[] Pids)> appNames = new();
    readonly Sustained critical = new(5), pressureWarning = new(15), hot = new(10), writes = new(10), traffic = new(10);
    readonly List<(DateTime At, long Free)> diskTrail = new();
    readonly Dictionary<string, List<(DateTime At, double Bytes)>> memoryTrails = new();
    readonly List<(DateTime At, long Used)> swapTrail = new();
    readonly Dictionary<string, DateTime> lastFired = new();

    public static readonly TimeSpan MemoryCriticalAfter = TimeSpan.FromSeconds(30), MemoryWarningAfter = TimeSpan.FromMinutes(5), HeatAfter = TimeSpan.FromSeconds(30);
    public const long LowDiskBytes = 10_000_000_000, VeryLowDiskBytes = 2_000_000_000, FillingBytes = 5_000_000_000;
    public static readonly TimeSpan FillingWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan GrowthPointEvery = TimeSpan.FromMinutes(1), GrowthWindow = TimeSpan.FromHours(1), GrowthMinSpan = TimeSpan.FromMinutes(30);
    public const double GrowthMinBytes = 500_000_000, GrowthMinRatio = 0.5, GrowthMinFit = 0.8;
    public const long GrowthTrackFrom = 150_000_000, SwapGrowthBytes = 1_000_000_000;
    public static readonly TimeSpan SwapWindow = TimeSpan.FromMinutes(15);

    static TimeSpan MinSpike(Rule r) => TimeSpan.FromSeconds(r switch { Rule.AppCpu => 30, Rule.DiskWrites or Rule.Network => 20, _ => 0 });
    static TimeSpan Cooldown(Rule r, EventKind k) => k == EventKind.Spike ? TimeSpan.FromMinutes(10) : r switch
    {
        Rule.AppCpu or Rule.MemoryPressure => TimeSpan.FromMinutes(30),
        Rule.Thermal or Rule.DiskFilling or Rule.DiskWrites or Rule.Network or Rule.SwapGrowth => TimeSpan.FromHours(1),
        Rule.MemoryGrowth => TimeSpan.FromHours(6),
        _ => TimeSpan.FromDays(1),
    };

    public DetectionEngine(DetectionSettings? settings = null, Func<Rule, double, string>? format = null)
    {
        Settings = settings ?? new DetectionSettings();
        this.format = format ?? ((_, v) => v.ToString("0"));
    }

    public List<DetectionEvent> Evaluate(DetectionSample s)
    {
        var o = new List<DetectionEvent>();
        if (Settings.AppCpu) Apps(s, o); else appCpu.Clear();
        if (Settings.Memory) MemoryRule(s, o); else { critical.Reset(); pressureWarning.Reset(); }
        if (Settings.Heat) HeatRule(s, o); else hot.Reset();
        if (Settings.Disk) DiskRule(s, o); else diskTrail.Clear();
        if (Settings.DiskWrites) Writes(s, o); else writes.Reset();
        if (Settings.Network) NetworkRule(s, o); else traffic.Reset();
        if (Settings.MemoryGrowth) { Growth(s, o); Swap(s, o); } else { memoryTrails.Clear(); swapTrail.Clear(); }
        return o.Where(e =>
        {
            if (lastFired.TryGetValue(e.Key, out var last) && s.Date - last < Cooldown(e.Rule, e.Kind)) return false;
            lastFired[e.Key] = s.Date;
            return true;
        }).ToList();
    }

    string F(Rule r, double v) => format(r, v);
    public static string Dur(TimeSpan t)
    {
        int s = (int)Math.Round(t.TotalSeconds);
        if (s < 60) return $"{s}s";
        if (s < 3600) return s % 60 == 0 ? $"{s / 60}m" : $"{s / 60}m {s % 60}s";
        return $"{s / 3600}h {(s % 3600) / 60}m";
    }

    // 1. An app using lots of CPU for a while.
    void Apps(DetectionSample s, List<DetectionEvent> o)
    {
        var need = TimeSpan.FromMinutes(Settings.CpuMinutes);
        var byKey = new Dictionary<string, AppLoad>();
        foreach (var a in s.Apps) { byKey.TryAdd(a.Key, a); appNames[a.Key] = (a.Name, a.PidList); }
        foreach (var key in appCpu.Keys.Union(byKey.Keys).ToList())
        {
            double load = byKey.TryGetValue(key, out var al) ? al.Cpu : 0;
            if (!appCpu.TryGetValue(key, out var t)) t = new Sustained(10);
            var (name, pids) = appNames.TryGetValue(key, out var n) ? n : (key, Array.Empty<int>());
            bool wasFired = t.Fired;
            if (t.Update(load >= Settings.CpuThreshold, load, s.Date) is { } ep
                && !wasFired && Settings.RecordSpikes && ep.Duration >= MinSpike(Rule.AppCpu) && ep.Duration < need)
                o.Add(Event(Rule.AppCpu, $"spike:appCPU:{key}", s.Date, ep, Severity.Info, EventKind.Spike,
                    $"{name} briefly used a lot of CPU", $"Up to {F(Rule.AppCpu, ep.Peak)} for {Dur(ep.Duration)}, then settled.",
                    $"Went over your {F(Rule.AppCpu, Settings.CpuThreshold)} threshold but recovered before {Dur(need)}, so no alert was sent.",
                    subject: name, pids: pids, notify: false));
            if (!t.Fired && t.Current(s.Date) is { } cur && cur.Duration >= need)
            {
                t.MarkFired();
                o.Add(Event(Rule.AppCpu, $"appCPU:{key}", s.Date, cur, Severity.Warning, EventKind.Sustained,
                    $"{name} is using a lot of CPU", $"{F(Rule.AppCpu, load)} now, averaging {F(Rule.AppCpu, cur.Average)} for {Dur(cur.Duration)}.",
                    $"Stayed above your {F(Rule.AppCpu, Settings.CpuThreshold)} threshold for {Dur(cur.Duration)} (alert after {Dur(need)}). Short spikes don’t alert.",
                    subject: name, pids: pids, notify: true));
            }
            if (t.IsActive) appCpu[key] = t;
            else { appCpu.Remove(key); if (!byKey.ContainsKey(key)) appNames.Remove(key); }
        }
    }

    // 2. Memory pressure: critical for 30 s alerts; elevated for 5 min is recorded.
    void MemoryRule(DetectionSample s, List<DetectionEvent> o)
    {
        var pf = new Evidence("Page file used", F(Rule.DiskSpace, s.PageFileUsed));
        bool critFired = critical.Fired;
        if (critical.Update(s.Pressure == PressureLevel.Critical, s.MemoryUsedPercent, s.Date) is { } ep
            && !critFired && Settings.RecordSpikes && ep.Duration < MemoryCriticalAfter)
            o.Add(Event(Rule.MemoryPressure, "spike:memory", s.Date, ep, Severity.Info, EventKind.Spike,
                "Memory pressure briefly went critical", $"Critical for {Dur(ep.Duration)}, then recovered.",
                $"Memory was critically short for less than {Dur(MemoryCriticalAfter)}, so no alert was sent.", extra: new() { pf }, notify: false));
        if (!critical.Fired && critical.Current(s.Date) is { } c && c.Duration >= MemoryCriticalAfter)
        {
            critical.MarkFired();
            o.Add(Event(Rule.MemoryPressure, "memory:critical", s.Date, c, Severity.Critical, EventKind.Sustained,
                "Your PC is running out of memory", $"Memory has been critically short for {Dur(c.Duration)}. Free up memory to keep things responsive.",
                $"Less than 5% of memory was free, or the commit charge was nearly at its limit, for {Dur(c.Duration)}; Apex alerts after {Dur(MemoryCriticalAfter)}.",
                extra: new() { pf }, notify: true));
        }
        pressureWarning.Update(s.Pressure >= PressureLevel.Elevated, s.MemoryUsedPercent, s.Date);
        if (!pressureWarning.Fired && pressureWarning.Current(s.Date) is { } w && w.Duration >= MemoryWarningAfter)
        {
            pressureWarning.MarkFired();
            o.Add(Event(Rule.MemoryPressure, "memory:warning", s.Date, w, Severity.Warning, EventKind.Sustained,
                "Memory pressure has been elevated for a while", $"Elevated for {Dur(w.Duration)}. Windows is relying on compression and the page file, but it isn’t critical.",
                $"Memory pressure stayed elevated or above for {Dur(w.Duration)}; this is recorded after {Dur(MemoryWarningAfter)}. Only critical pressure sends an alert.",
                extra: new() { pf }, notify: false));
        }
    }

    // 3. Running hot (only where a heat source reports it).
    void HeatRule(DetectionSample s, List<DetectionEvent> o)
    {
        double level = (int)s.Thermal;
        bool hotFired = hot.Fired;
        if (hot.Update(s.Thermal >= ThermalState.Serious, level, s.Date) is { } ep && !hotFired && Settings.RecordSpikes && ep.Duration < HeatAfter)
            o.Add(Event(Rule.Thermal, "spike:heat", s.Date, ep, Severity.Info, EventKind.Spike,
                "Your PC briefly ran hot", $"It throttled for {Dur(ep.Duration)}, then cooled down.",
                $"The heat level was serious or critical for less than {Dur(HeatAfter)}.", notify: false));
        if (!hot.Fired && hot.Current(s.Date) is { } c && c.Duration >= HeatAfter)
        {
            hot.MarkFired();
            bool worst = c.Peak >= (int)ThermalState.Critical;
            o.Add(Event(Rule.Thermal, "heat", s.Date, c, worst ? Severity.Critical : Severity.Warning, EventKind.Sustained,
                "Your PC is running hot", "It’s slowing down to cool off. Check which apps use the most CPU.",
                $"The heat level has been {(worst ? "critical" : "serious")} for {Dur(c.Duration)} (alert after {Dur(HeatAfter)}).", notify: true));
        }
    }

    // 4. Low space, and space disappearing fast.
    void DiskRule(DetectionSample s, List<DetectionEvent> o)
    {
        if (s.DiskCapacity <= 0) return;
        var left = new Evidence("Available", F(Rule.DiskSpace, s.DiskAvailable));
        var cap = new Evidence("Capacity", F(Rule.DiskSpace, s.DiskCapacity));
        if (s.DiskAvailable < LowDiskBytes)
        {
            bool very = s.DiskAvailable < VeryLowDiskBytes;
            o.Add(new DetectionEvent
            {
                Rule = Rule.DiskSpace, Key = "disk:low", Date = s.Date, Started = s.Date, Severity = very ? Severity.Critical : Severity.Warning, Kind = EventKind.Instant,
                Title = "Your disk is almost full", Summary = $"Only {F(Rule.DiskSpace, s.DiskAvailable)} left on {s.VolumeName}.",
                Reason = $"Free space is below {F(Rule.DiskSpace, LowDiskBytes)}. Windows needs room for the page file, updates and caches.",
                Evidence = new() { left, cap, new("Free", $"{(double)s.DiskAvailable / s.DiskCapacity * 100:0.0}%") }, Subject = s.VolumeName, Notify = true,
            });
        }
        diskTrail.Add((s.Date, s.DiskAvailable));
        diskTrail.RemoveAll(x => s.Date - x.At > FillingWindow);
        var most = diskTrail.OrderByDescending(x => x.Free).First();
        if (most.Free > s.DiskAvailable && most.Free - s.DiskAvailable >= FillingBytes)
        {
            long used = most.Free - s.DiskAvailable;
            var over = s.Date - most.At;
            var writers = s.Apps.Where(a => a.DiskWritePerSec > 1_000_000).OrderByDescending(a => a.DiskWritePerSec).Take(3).ToList();
            var ev = new List<Evidence> { new("Used", F(Rule.DiskSpace, used)), new("Over", Dur(over)), left };
            ev.AddRange(writers.Select(a => new Evidence("Writing now", $"{a.Name} · {F(Rule.DiskWrites, a.DiskWritePerSec)}")));
            o.Add(new DetectionEvent
            {
                Rule = Rule.DiskFilling, Key = "disk:filling", Date = s.Date, Started = most.At, Severity = Severity.Warning, Kind = EventKind.Sustained,
                Title = "Your disk is filling up fast", Summary = $"{F(Rule.DiskSpace, used)} used in {Dur(over)}; {F(Rule.DiskSpace, s.DiskAvailable)} left.",
                Reason = $"Free space dropped by at least {F(Rule.DiskSpace, FillingBytes)} within {Dur(FillingWindow)}.",
                Evidence = ev, Subject = s.VolumeName, Pids = writers.SelectMany(a => a.PidList).ToArray(), Notify = true,
            });
            diskTrail.Clear();
        }
    }

    // 5. Heavy disk writing for minutes (recorded).
    void Writes(DetectionSample s, List<DetectionEvent> o)
    {
        var need = TimeSpan.FromMinutes(Settings.WriteMinutes);
        var writers = s.Apps.Where(a => a.DiskWritePerSec > 1_000_000).OrderByDescending(a => a.DiskWritePerSec).Take(3).ToList();
        var top = writers.Select(a => new Evidence("Top writer", $"{a.Name} · {F(Rule.DiskWrites, a.DiskWritePerSec)}")).ToList();
        bool fired = writes.Fired;
        if (writes.Update(s.DiskWritePerSec >= Settings.WriteThreshold, s.DiskWritePerSec, s.Date) is { } ep
            && !fired && Settings.RecordSpikes && ep.Duration >= MinSpike(Rule.DiskWrites) && ep.Duration < need)
            o.Add(Event(Rule.DiskWrites, "spike:writes", s.Date, ep, Severity.Info, EventKind.Spike,
                "Burst of disk writing", $"Up to {F(Rule.DiskWrites, ep.Peak)} for {Dur(ep.Duration)}.",
                $"Writes went over {F(Rule.DiskWrites, Settings.WriteThreshold)} but stopped before {Dur(need)}.", notify: false));
        if (!writes.Fired && writes.Current(s.Date) is { } c && c.Duration >= need)
        {
            writes.MarkFired();
            o.Add(Event(Rule.DiskWrites, "writes", s.Date, c, Severity.Info, EventKind.Sustained,
                "Heavy disk writing", $"Averaging {F(Rule.DiskWrites, c.Average)} for {Dur(c.Duration)}.",
                $"The disk was written at over {F(Rule.DiskWrites, Settings.WriteThreshold)} for {Dur(c.Duration)} (recorded after {Dur(need)}).",
                extra: top, subject: writers.FirstOrDefault()?.Name, pids: writers.SelectMany(a => a.PidList).ToArray(), notify: false));
        }
    }

    // 5b. Heavy network traffic for minutes (recorded).
    void NetworkRule(DetectionSample s, List<DetectionEvent> o)
    {
        var need = TimeSpan.FromMinutes(Settings.NetworkMinutes);
        var users = s.Apps.Where(a => a.NetPerSec > 100_000).OrderByDescending(a => a.NetPerSec).Take(3).ToList();
        var top = users.Select(a => new Evidence("Top app", $"{a.Name} · {F(Rule.Network, a.NetPerSec)}")).ToList();
        bool fired = traffic.Fired;
        if (traffic.Update(s.NetworkPerSec >= Settings.NetworkThreshold, s.NetworkPerSec, s.Date) is { } ep
            && !fired && Settings.RecordSpikes && ep.Duration >= MinSpike(Rule.Network) && ep.Duration < need)
            o.Add(Event(Rule.Network, "spike:network", s.Date, ep, Severity.Info, EventKind.Spike,
                "Burst of network traffic", $"Up to {F(Rule.Network, ep.Peak)} for {Dur(ep.Duration)}.",
                $"Traffic went over {F(Rule.Network, Settings.NetworkThreshold)} but dropped before {Dur(need)}.", notify: false));
        if (!traffic.Fired && traffic.Current(s.Date) is { } c && c.Duration >= need)
        {
            traffic.MarkFired();
            o.Add(Event(Rule.Network, "network", s.Date, c, Severity.Info, EventKind.Sustained,
                "Heavy network traffic", $"Averaging {F(Rule.Network, c.Average)} for {Dur(c.Duration)}.",
                $"This PC sent and received over {F(Rule.Network, Settings.NetworkThreshold)} for {Dur(c.Duration)} (recorded after {Dur(need)}).",
                extra: top, subject: users.FirstOrDefault()?.Name, pids: users.SelectMany(a => a.PidList).ToArray(), notify: false));
        }
    }

    // 6. An app whose memory climbs steadily for 30+ minutes (possible leak).
    void Growth(DetectionSample s, List<DetectionEvent> o)
    {
        var present = s.Apps.Select(a => a.Key).ToHashSet();
        foreach (var k in memoryTrails.Keys.Where(k => !present.Contains(k)).ToList()) memoryTrails.Remove(k);   // quit: forget
        foreach (var a in s.Apps)
        {
            memoryTrails.TryGetValue(a.Key, out var trail);
            trail ??= new();
            if (trail.Count == 0 && a.Memory < GrowthTrackFrom) continue;
            if (trail.Count > 0 && s.Date - trail[^1].At < GrowthPointEvery) continue;
            trail.Add((s.Date, a.Memory));
            trail.RemoveAll(x => s.Date - x.At > GrowthWindow);
            memoryTrails[a.Key] = trail;
            if (GrowthOf(trail) is not { } g) continue;
            o.Add(new DetectionEvent
            {
                Rule = Rule.MemoryGrowth, Key = $"growth:{a.Key}", Date = s.Date, Started = trail[0].At, Severity = Severity.Warning, Kind = EventKind.Sustained,
                Title = $"{a.Name}’s memory keeps growing",
                Summary = $"From {F(Rule.MemoryGrowth, g.Start)} to {F(Rule.MemoryGrowth, g.End)} in {Dur(g.Span)}, about {F(Rule.MemoryGrowth, g.PerHour)} an hour. Quitting and reopening it usually gives the memory back.",
                Reason = $"Its memory rose steadily for {Dur(g.Span)} (alert after {Dur(GrowthMinSpan)} of growth of at least {F(Rule.MemoryGrowth, GrowthMinBytes)} and half its starting size). A steady climb like this, rather than ups and downs, often means a memory leak.",
                Evidence = new()
                {
                    new("Started at", F(Rule.MemoryGrowth, g.Start)), new("Now", F(Rule.MemoryGrowth, g.End)), new("Grew by", F(Rule.MemoryGrowth, g.End - g.Start)),
                    new("Rate", F(Rule.MemoryGrowth, g.PerHour) + " an hour"), new("Over", Dur(g.Span)), new("Steadiness", $"{g.Fit * 100:0}% (fit R² {g.Fit:0.00})"),
                },
                Subject = a.Name, Pids = a.PidList, Notify = true, Series = trail.Select(x => x.Bytes).ToList(),
            });
        }
    }

    public sealed record GrowthFit(double Start, double End, double PerHour, double Fit, TimeSpan Span);

    /// Steady growth over a memory trail, or null. A least-squares line, so a
    /// single jump or a sawtooth (normal caching) doesn't count. Pure: tested.
    public static GrowthFit? GrowthOf(IReadOnlyList<(DateTime At, double Bytes)> trail)
    {
        if (trail.Count < 10) return null;
        var span = trail[^1].At - trail[0].At;
        if (span < GrowthMinSpan) return null;
        var xs = trail.Select(x => (x.At - trail[0].At).TotalSeconds).ToArray();
        var ys = trail.Select(x => x.Bytes).ToArray();
        double n = trail.Count, mx = xs.Sum() / n, my = ys.Sum() / n, sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < xs.Length; i++) { double dx = xs[i] - mx, dy = ys[i] - my; sxy += dx * dy; sxx += dx * dx; syy += dy * dy; }
        if (sxx <= 0 || syy <= 0) return null;
        double slope = sxy / sxx, fit = sxy * sxy / (sxx * syy);
        double start = my - slope * mx, end = start + slope * span.TotalSeconds;
        if (slope <= 0 || fit < GrowthMinFit || end - start < GrowthMinBytes || start <= 0 || (end - start) / start < GrowthMinRatio) return null;
        return new GrowthFit(ys[0], ys[^1], slope * 3600, fit, span);
    }

    // 7. The page file growing fast while memory is under pressure.
    void Swap(DetectionSample s, List<DetectionEvent> o)
    {
        swapTrail.Add((s.Date, s.PageFileUsed));
        swapTrail.RemoveAll(x => s.Date - x.At > SwapWindow);
        if (s.Pressure < PressureLevel.Elevated) return;
        var low = swapTrail.OrderBy(x => x.Used).First();
        if (s.PageFileUsed <= low.Used || s.PageFileUsed - low.Used < SwapGrowthBytes) return;
        long grew = s.PageFileUsed - low.Used;
        var over = s.Date - low.At;
        var biggest = s.Apps.OrderByDescending(a => a.Memory).Take(3).ToList();
        var ev = new List<Evidence> { new("Page file now", F(Rule.SwapGrowth, s.PageFileUsed)), new("Grew by", F(Rule.SwapGrowth, grew)), new("Over", Dur(over)) };
        if (s.MemoryTotal > 0) ev.Add(new("RAM", F(Rule.SwapGrowth, s.MemoryTotal)));
        ev.AddRange(biggest.Select(a => new Evidence("Largest app", $"{a.Name} · {F(Rule.SwapGrowth, a.Memory)}")));
        o.Add(new DetectionEvent
        {
            Rule = Rule.SwapGrowth, Key = "swap:growing", Date = s.Date, Started = low.At, Severity = Severity.Warning, Kind = EventKind.Sustained,
            Title = "Your PC is using the page file more and more",
            Summary = $"The page file grew by {F(Rule.SwapGrowth, grew)} in {Dur(over)} and is now {F(Rule.SwapGrowth, s.PageFileUsed)}. Things may start to feel slow.",
            Reason = $"Page file use rose by at least {F(Rule.SwapGrowth, SwapGrowthBytes)} within {Dur(SwapWindow)} while memory pressure was elevated or critical. Windows is moving memory to the disk because RAM is full.",
            Evidence = ev, Pids = biggest.SelectMany(a => a.PidList).ToArray(), Notify = true,
        });
        swapTrail.Clear();
    }

    DetectionEvent Event(Rule rule, string key, DateTime now, Sustained.Episode ep, Severity sev, EventKind kind, string title, string summary,
                         string reason, List<Evidence>? extra = null, string? subject = null, int[]? pids = null, bool notify = false)
    {
        var ev = new List<Evidence> { new("Started", ep.Start.ToString("HH:mm:ss")), new("Lasted", Dur(ep.Duration)) };
        if (rule != Rule.Thermal) { ev.Add(new("Average", F(rule, ep.Average))); ev.Add(new("Peak", F(rule, ep.Peak))); }
        if (extra != null) ev.AddRange(extra);
        return new DetectionEvent
        {
            Rule = rule, Key = key, Date = now, Started = ep.Start, Severity = sev, Kind = kind, Title = title, Summary = summary,
            Reason = reason, Evidence = ev, Subject = subject, Pids = pids ?? Array.Empty<int>(), Notify = notify,
        };
    }
}
