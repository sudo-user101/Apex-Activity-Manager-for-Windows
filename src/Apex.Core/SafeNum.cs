using System.Globalization;

namespace Apex.Core;

/// Numbers from outside Apex (WMI, the registry, other programs) can be any
/// type, out of range, NaN or infinite. Convert.ToInt32 and friends throw on
/// those and stop the app; these give the nearest value instead, or 0.
public static class SafeNum
{
    public static long FromDouble(double d) =>
        double.IsNaN(d) ? 0 : d >= long.MaxValue ? long.MaxValue : d <= long.MinValue ? long.MinValue : (long)d;

    public static long ToLong(object? v) => v switch
    {
        null => 0,
        long l => l,
        int i => i,
        uint u => u,
        short s => s,
        ushort s => s,
        byte b => b,
        sbyte b => b,
        ulong u => u > long.MaxValue ? long.MaxValue : (long)u,
        double d => FromDouble(d),
        float f => FromDouble(f),
        decimal m => m >= long.MaxValue ? long.MaxValue : m <= long.MinValue ? long.MinValue : (long)m,
        string s => long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r
                  : double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? FromDouble(d) : 0,
        _ => 0,
    };

    public static int ToInt(object? v) => (int)Math.Clamp(ToLong(v), int.MinValue, int.MaxValue);

    public static ulong ToULong(object? v) => v switch
    {
        ulong u => u,
        string s when ulong.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) => r,
        _ => (ulong)Math.Max(0, ToLong(v)),
    };

    /// NaN or infinite → 0, for anything shown on screen.
    public static double Finite(double d) => double.IsFinite(d) ? d : 0;
}
