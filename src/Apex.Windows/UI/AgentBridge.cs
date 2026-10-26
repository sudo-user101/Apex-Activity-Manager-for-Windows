using Apex.Core;
using Apex.Services;

namespace Apex.UI;

/// Connects Apex Agent (pure, in Apex.Core) to this PC: what it reads, and
/// what its buttons do. Every action that ends or changes a process goes
/// through the same identity-checked code as the rest of Apex.
internal static class AgentBridge
{
    /// What the agent knows about this PC right now, from the latest sample.
    public static ApexAgent.Readings Readings()
    {
        var s = AppState.Latest;
        int self = Environment.ProcessId;
        var frames = FrameMonitor.Apps().ToDictionary(a => a.Pid, a => a.Stats.Fps);
        var apps = s.Processes.Where(p => p.Path.Length > 0 && p.Pid != self)
            .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Any(p => p.HasWindow))
            .Select(g =>
            {
                var main = g.First(p => p.HasWindow);
                return new ApexAgent.Item(main.DisplayName.Length > 0 ? main.DisplayName : main.Name, g.Key, g.Select(p => p.Identity).ToArray(),
                    g.Sum(p => p.Cpu), g.Sum(p => p.Memory), g.Select(p => frames.GetValueOrDefault(p.Pid)).DefaultIfEmpty(0).Max());
            }).ToList();
        var processes = s.Processes.Where(p => p.Kind == ProcessKind.Background && p.Pid != self && !p.HasWindow)
            .OrderByDescending(p => p.Cpu)
            .Select(p => new ApexAgent.Item(System.IO.Path.GetFileNameWithoutExtension(p.Name), p.Path, new[] { p.Identity }, p.Cpu, p.Memory)).ToList();
        var disk = s.Disks.FirstOrDefault(d => d.IsSystem) ?? s.Disks.FirstOrDefault();
        var net = s.Networks.Where(n => n.Up).OrderByDescending(n => n.InPerSec + n.OutPerSec).FirstOrDefault();
        return new ApexAgent.Readings
        {
            Apps = apps, Processes = processes, Cpu = s.Cpu.Total, MemoryUsed = s.Memory.Used, MemoryTotal = s.Memory.Total,
            Pressure = MemoryPressure.Title(s.Memory.Pressure), DiskFree = disk?.Free ?? 0, DiskTotal = disk?.Capacity ?? 0,
            Battery = s.Battery.HasBattery ? s.Battery.Percent : null, OnPower = !s.Battery.HasBattery || s.Battery.OnPower,
            Gpu = s.Gpus.Select(g => g.Utilization).DefaultIfEmpty(0).Max(), Network = net?.Name ?? "",
        };
    }

    /// The same, as text for a local model.
    public static string ReadingsText()
    {
        var r = Readings();
        var lines = new List<string>
        {
            $"CPU {r.Cpu:0}%, GPU {r.Gpu:0}%, memory {Fmt.Memory(r.MemoryUsed)} of {Fmt.Memory(r.MemoryTotal)} (pressure {r.Pressure}), system disk {Fmt.Bytes(r.DiskFree)} free of {Fmt.Bytes(r.DiskTotal)}.",
            r.Battery is { } b ? $"Battery {b}%{(r.OnPower ? ", plugged in" : ", on battery")}." : "No battery.",
            "Busiest apps: " + string.Join(", ", r.Apps.OrderByDescending(a => a.Cpu).Take(5).Select(a => $"{a.Name} {a.Cpu:0}% CPU {Fmt.Memory(a.Memory)}" + (a.Fps > 0 ? $" {a.Fps:0} FPS" : ""))) + ".",
        };
        if (GameMode.Active is { } g) lines.Add($"Game mode is on for {g.GameName}, target {g.TargetFps:0} FPS, now {GameMode.Latest.Fps:0} FPS. {GameMode.Diagnosis.Advice}");
        return string.Join("\n", lines);
    }

    /// Runs a button's action; returns what the agent says afterwards (null: nothing to say).
    public static string? Perform(ApexAgent.AgentAction a, Microsoft.UI.Xaml.FrameworkElement anchor)
    {
        switch (a)
        {
            case ApexAgent.OpenPage p: MainWindow.Navigate(p.Page); return null;
            case ApexAgent.FreeMemory: _ = FreeUpMemory.Show(anchor); return null;
            case ApexAgent.QuitApps q:
            {
                var report = ProcessActions.End(q.Targets);
                var note = report.Message(NamesFor(q.Targets, q.Names), "end");
                return note == null ? $"Done: ended {(q.Names.Length == 1 ? q.Names[0] : $"{q.Names.Length} apps")}." : "Mostly done. " + note;
            }
            case ApexAgent.StopProcesses sp:
            {
                var report = ProcessActions.End(sp.Targets);
                var note = report.Message(NamesFor(sp.Targets, sp.Names), "stop");
                return note == null ? $"Stopped {(sp.Names.Length == 1 ? sp.Names[0] : $"{sp.Names.Length} processes")}." : note;
            }
            case ApexAgent.StartGameMode g:
            {
                var quiet = GameModePlan.ToQuiet(AppState.Latest.Processes, g.GamePath, GameMode.Keep, Environment.ProcessId).Select(x => x.Path);
                var result = GameMode.Start(g.GamePath, g.GameName, g.TargetFps, quiet);
                MainWindow.Navigate("gaming");
                return result + " I’m watching the frame rate on the Gaming page.";
            }
        }
        return null;
    }

    /// Names per process for messages (each program's name for all its processes).
    static Dictionary<ProcessIdentity, string> NamesFor(ProcessIdentity[] targets, string[] names)
    {
        var byPath = new Dictionary<ProcessIdentity, string>();
        foreach (var p in AppState.Latest.Processes)
            if (targets.Contains(p.Identity)) byPath[p.Identity] = p.DisplayName.Length > 0 ? p.DisplayName : p.Name;
        if (names.Length == 1) foreach (var t in targets) byPath.TryAdd(t, names[0]);
        return byPath;
    }
}
