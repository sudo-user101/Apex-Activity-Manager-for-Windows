using System.Text.Json;
using Apex.Core;

namespace Apex.Services;

/// App History and Events, as on the Mac: each reading is added to the
/// per-app history and checked by the same alert rules; events are kept (the
/// last 500) and the ones worth it are shown as Windows notifications. Saved
/// in AppData › Local › Apex every 5 minutes and when Apex quits.
internal static class History
{
    static readonly string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apex");
    static string HistoryFile => Path.Combine(dir, "history.json");
    static string EventsFile => Path.Combine(dir, "events.json");

    public static AppHistory Usage { get; private set; } = new();
    public static List<DetectionEvent> Events { get; private set; } = new();
    public static readonly DetectionEngine Engine = new(LoadSettings(), Format);
    public static event Action? Changed;
    /// Shows a notification (the tray sets it).
    public static Action<string, string, bool>? Notify;
    static DateTime lastSave = DateTime.Now;
    public const int KeepEvents = 500;

    public static bool AlertsOn
    {
        get => Settings.Get("alertsEnabled", true);
        set => Settings.Set("alertsEnabled", value);
    }

    public static DetectionSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<DetectionSettings>(Settings.Get("detection", "{}")) ?? new(); } catch { return new(); }
    }

    public static void SaveSettings(DetectionSettings s)
    {
        Engine.Settings = s;
        Settings.Set("detection", JsonSerializer.Serialize(s));
        Changed?.Invoke();
    }

    public static void Load()
    {
        try { if (File.Exists(HistoryFile)) Usage = AppHistory.FromJson(File.ReadAllText(HistoryFile)); } catch { }
        try { if (File.Exists(EventsFile)) Events = JsonSerializer.Deserialize<List<DetectionEvent>>(File.ReadAllText(EventsFile)) ?? new(); } catch { }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(dir);
            Usage.Prune(DateTime.Now);
            File.WriteAllText(HistoryFile, Usage.ToJson());
            File.WriteAllText(EventsFile, JsonSerializer.Serialize(Events));
        }
        catch { }
        lastSave = DateTime.Now;
    }

    public static void ClearUsage() { Usage = new AppHistory(); Save(); Changed?.Invoke(); }
    public static void ClearEvents() { Events.Clear(); Save(); Changed?.Invoke(); }

    /// Each reading (called on the UI thread with each snapshot).
    public static void Tick(Snapshot s)
    {
        int logical = Math.Max(1, s.Cpu.LogicalProcessors);
        var byApp = s.Processes.GroupBy(p => AppHistory.KeyFor(p.Path, p.Name)).ToList();
        string NameOf(IGrouping<string, ProcessSample> g)
        {
            var first = g.OrderByDescending(p => p.HasWindow).First();
            return first.DisplayName.Length > 0 ? first.DisplayName : first.Name;
        }
        // Windows counts CPU as a share of the whole PC; the rules (like the Mac's) count one core as 100 %.
        Usage.Record(s.Time, byApp.Select(g => new AppHistory.Use(g.Key, NameOf(g), g.First().Path,
            g.Sum(p => p.Cpu) / 100 * logical, g.Sum(p => p.DiskPerSec), 0, g.Sum(p => p.Memory))));

        var system = s.Disks.FirstOrDefault(d => d.IsSystem);
        var sample = new DetectionSample
        {
            Date = s.Time,
            Apps = byApp.Select(g => new AppLoad(g.Key, NameOf(g), g.Sum(p => p.Cpu) * logical, g.Sum(p => p.DiskPerSec), 0, g.Sum(p => p.Memory),
                                                 g.Select(p => p.Pid).ToArray())).ToList(),
            Pressure = s.Memory.Pressure, MemoryUsedPercent = s.Memory.Percent, PageFileUsed = s.Memory.PageFileUsed, MemoryTotal = s.Memory.Total,
            DiskAvailable = system?.Free ?? 0, DiskCapacity = system?.Capacity ?? 0, VolumeName = system?.Name ?? "C:",
            DiskWritePerSec = s.Disks.Sum(d => d.WritePerSec), NetworkPerSec = s.Networks.Sum(n => n.InPerSec + n.OutPerSec),
        };
        var found = Engine.Evaluate(sample);
        if (found.Count > 0)
        {
            Events.InsertRange(0, found.OrderByDescending(e => e.Date));
            if (Events.Count > KeepEvents) Events.RemoveRange(KeepEvents, Events.Count - KeepEvents);
            foreach (var e in found.Where(e => e.Notify && AlertsOn)) Notify?.Invoke(e.Title, e.Summary, e.Severity >= Severity.Warning);
            Changed?.Invoke();
        }
        if (DateTime.Now - lastSave > TimeSpan.FromMinutes(5)) Save();
    }

    static string Format(Rule r, double v) => r switch
    {
        Rule.AppCpu => $"{v:0}%",
        Rule.DiskWrites or Rule.Network => Fmt.Rate(v),
        _ => Fmt.Bytes((long)v),
    };
}
