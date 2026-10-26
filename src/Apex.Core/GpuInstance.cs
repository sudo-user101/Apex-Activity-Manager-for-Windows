using System.Globalization;
using System.Text.RegularExpressions;

namespace Apex.Core;

/// The "GPU Engine" performance counter names one instance per process and
/// engine: "pid_1234_luid_0x00000000_0x0000C9F3_phys_0_eng_0_engtype_3D".
public static partial class GpuInstance
{
    [GeneratedRegex(@"pid_(\d+)_luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_\d+_eng_\d+_engtype_([A-Za-z0-9 ]+)")]
    private static partial Regex Pattern();

    public readonly record struct Parts(int Pid, string Luid, string EngineType);

    public static Parts? Parse(string instance)
    {
        var m = Pattern().Match(instance);
        if (!m.Success) return null;
        return new Parts(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value, m.Groups[3].Value.Trim());
    }

    /// Per process: the busiest engine's utilisation (what Windows' GPU column shows).
    public static Dictionary<int, double> PerProcess(IEnumerable<(string Instance, double Value)> readings)
    {
        var byPidEngine = new Dictionary<(int, string), double>();
        foreach (var (inst, v) in readings)
        {
            if (Parse(inst) is not { } p) continue;
            byPidEngine[(p.Pid, p.EngineType)] = byPidEngine.GetValueOrDefault((p.Pid, p.EngineType)) + v;
        }
        var result = new Dictionary<int, double>();
        foreach (var ((pid, _), v) in byPidEngine)
            result[pid] = Math.Min(100, Math.Max(result.GetValueOrDefault(pid), v));
        return result;
    }

    /// Whole GPU: each engine type's total across processes; the busiest is the GPU's load.
    public static Dictionary<string, double> PerEngine(IEnumerable<(string Instance, double Value)> readings)
    {
        var d = new Dictionary<string, double>();
        foreach (var (inst, v) in readings)
            if (Parse(inst) is { } p) d[p.EngineType] = Math.Min(100, d.GetValueOrDefault(p.EngineType) + v);
        return d;
    }
}
