namespace Apex.Core;

/// Windows has no single "memory pressure" signal like macOS, so Apex reads
/// it from what runs out first: free memory, and the commit charge against
/// its limit (when that fills, Windows starts refusing memory).
public static class MemoryPressure
{
    public static PressureLevel Level(MemoryStats m)
    {
        if (m.Total <= 0) return PressureLevel.Normal;
        double availPct = m.Available * 100.0 / m.Total;
        double commitPct = m.CommitLimit > 0 ? m.Committed * 100.0 / m.CommitLimit : 0;
        if (availPct < 5 || commitPct > 95) return PressureLevel.Critical;
        if (availPct < 15 || commitPct > 85) return PressureLevel.Elevated;
        return PressureLevel.Normal;
    }

    public static string Title(PressureLevel l) => l switch
    {
        PressureLevel.Critical => "Critical",
        PressureLevel.Elevated => "Elevated",
        _ => "Normal",
    };
}
