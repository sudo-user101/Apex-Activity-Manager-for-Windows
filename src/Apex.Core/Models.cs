namespace Apex.Core;

/// Where a process belongs, like the Processes page groups them.
public enum ProcessKind { App, Background, Windows }

/// Memory pressure in Apex's words: Normal, Elevated, Critical.
public enum PressureLevel { Normal, Elevated, Critical }

/// One process on one sample.
public sealed record ProcessSample
{
    public int Pid { get; init; }
    public int ParentPid { get; init; }
    public string Name { get; init; } = "";
    /// Friendly name (the app's description, e.g. "Microsoft Edge").
    public string DisplayName { get; init; } = "";
    public string Path { get; init; } = "";
    public ProcessKind Kind { get; init; }
    /// Share of the whole PC (0–100), like Windows.
    public double Cpu { get; init; }
    /// Private working set (what Windows' Memory column shows).
    public long Memory { get; init; }
    public double DiskPerSec { get; init; }
    public double Gpu { get; init; }
    public int Threads { get; init; }
    public int Handles { get; init; }
    public string User { get; init; } = "";
    public bool Suspended { get; init; }
    public bool HasWindow { get; init; }
    /// When it started (FILETIME): with the ID, it names this process and no other.
    public long CreateTime { get; init; }
    public ProcessIdentity Identity => new(Pid, CreateTime);
}

public sealed record CpuStats
{
    public double Total { get; init; }
    public double[] PerCore { get; init; } = Array.Empty<double>();
    public double SpeedGHz { get; init; }
    public double BaseSpeedGHz { get; init; }
    public string Name { get; init; } = "";
    public int Cores { get; init; }
    public int LogicalProcessors { get; init; }
    public int Processes { get; init; }
    public int Threads { get; init; }
    public int Handles { get; init; }
    public TimeSpan UpTime { get; init; }
}

public sealed record MemoryStats
{
    public long Total { get; init; }
    public long Available { get; init; }
    public long Used => Math.Max(0, Total - Available);
    public long Committed { get; init; }
    public long CommitLimit { get; init; }
    public long Cached { get; init; }
    public long PagedPool { get; init; }
    public long NonPagedPool { get; init; }
    public long Compressed { get; init; }
    /// Memory compression is on (Windows' "Memory Compression" process is running).
    public bool CompressionOn { get; init; }
    /// The page file (Windows' swap): in use, and its current size.
    public long PageFileUsed { get; init; }
    public long PageFileSize { get; init; }
    /// Pages read back from disk each second (hard faults): memory that had to
    /// leave for the page file and is needed again.
    public double PagesInPerSec { get; init; }
    public double Percent => Total > 0 ? Used * 100.0 / Total : 0;
    public PressureLevel Pressure => MemoryPressure.Level(this);
}

public sealed record DiskStats
{
    public string Name { get; init; } = "";       // "Disk 0 (C:)"
    public string Model { get; init; } = "";
    public double ActivePercent { get; init; }
    public double ReadPerSec { get; init; }
    public double WritePerSec { get; init; }
    public double ResponseMs { get; init; }
    public long Capacity { get; init; }
    public long Free { get; init; }
    public bool IsSystem { get; init; }
}

public sealed record NetworkStats
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";        // "Wi-Fi", "Ethernet"
    public string Description { get; init; } = "";
    public string Kind { get; init; } = "";        // "Wi-Fi", "Ethernet", "VPN"
    public double InPerSec { get; init; }
    public double OutPerSec { get; init; }
    public long SpeedBitsPerSec { get; init; }
    public string IPv4 { get; init; } = "";
    public string IPv6 { get; init; } = "";
    public bool Up { get; init; }
}

public sealed record GpuStats
{
    public string Name { get; init; } = "";
    public double Utilization { get; init; }
    public Dictionary<string, double> Engines { get; init; } = new();   // "3D" → 12.5
    public long DedicatedUsed { get; init; }
    public long DedicatedTotal { get; init; }
    public long SharedUsed { get; init; }
}

public sealed record BatteryStats
{
    public bool HasBattery { get; init; }
    public int Percent { get; init; }
    public bool OnPower { get; init; }
    public bool Charging { get; init; }
    public TimeSpan? Remaining { get; init; }
}

/// Everything one sampling tick produced.
public sealed record Snapshot
{
    public DateTime Time { get; init; } = DateTime.Now;
    public CpuStats Cpu { get; init; } = new();
    public MemoryStats Memory { get; init; } = new();
    public IReadOnlyList<DiskStats> Disks { get; init; } = Array.Empty<DiskStats>();
    public IReadOnlyList<NetworkStats> Networks { get; init; } = Array.Empty<NetworkStats>();
    public IReadOnlyList<GpuStats> Gpus { get; init; } = Array.Empty<GpuStats>();
    public BatteryStats Battery { get; init; } = new();
    public IReadOnlyList<ProcessSample> Processes { get; init; } = Array.Empty<ProcessSample>();
}
