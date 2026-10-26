namespace Apex.Core;

/// Turning Windows' cumulative CPU times into percentages.
public static class CpuMath
{
    /// A process's share of the whole PC since the last reading, like Windows'
    /// CPU column: CPU time used ÷ (wall time × logical processors).
    public static double ProcessPercent(TimeSpan cpuBefore, TimeSpan cpuNow, TimeSpan elapsed, int logicalProcessors)
    {
        if (elapsed <= TimeSpan.Zero || logicalProcessors <= 0 || cpuNow < cpuBefore) return 0;
        var pct = (cpuNow - cpuBefore).TotalMilliseconds / (elapsed.TotalMilliseconds * logicalProcessors) * 100;
        return Math.Clamp(pct, 0, 100);
    }

    /// Whole-PC busy % from GetSystemTimes deltas (kernel time includes idle).
    public static double SystemPercent(ulong idleBefore, ulong kernelBefore, ulong userBefore,
                                       ulong idleNow, ulong kernelNow, ulong userNow)
    {
        ulong idle = idleNow - idleBefore, kernel = kernelNow - kernelBefore, user = userNow - userBefore;
        ulong total = kernel + user;
        if (total == 0 || idle > total) return 0;
        return Math.Clamp((total - idle) * 100.0 / total, 0, 100);
    }
}
