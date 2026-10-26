using System.Runtime.InteropServices;

namespace Apex.Services;

/// A small wrapper over Windows performance counters. Counters with "*"
/// return one value per instance (each disk, each GPU engine…).
internal sealed class Pdh : IDisposable
{
    readonly IntPtr query;
    readonly Dictionary<string, IntPtr> counters = new();

    public Pdh() { Native.PdhOpenQuery(null, IntPtr.Zero, out query); }

    public void Add(string path)
    {
        if (counters.ContainsKey(path)) return;
        if (Native.PdhAddEnglishCounter(query, path, IntPtr.Zero, out var c) == 0) counters[path] = c;
    }

    public void Collect() => Native.PdhCollectQueryData(query);

    /// Every instance's value ("_Total" included) for a counter added before.
    public List<(string Instance, double Value)> Values(string path)
    {
        var list = new List<(string, double)>();
        if (!counters.TryGetValue(path, out var c)) return list;
        int size = 0;
        int r = Native.PdhGetFormattedCounterArray(c, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out int count, IntPtr.Zero);
        if (r != Native.PDH_MORE_DATA || size <= 0) return list;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (Native.PdhGetFormattedCounterArray(c, Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100, ref size, out count, buf) != 0) return list;
            // PDH_FMT_COUNTERVALUE_ITEM: name pointer, then { status (4) + padding (4), double }.
            int itemSize = IntPtr.Size + 16;
            for (int i = 0; i < count; i++)
            {
                var item = buf + i * itemSize;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                int status = Marshal.ReadInt32(item + IntPtr.Size);
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8));
                if (status == 0 || status == 1) list.Add((name, value));   // PDH_CSTATUS_VALID_DATA / NEW_DATA
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public double Total(string path) =>
        Values(path).FirstOrDefault(v => v.Instance == "_Total" || v.Instance == "").Value;

    public double Single(string path) => Values(path).Select(v => v.Value).FirstOrDefault();

    public void Dispose() => Native.PdhCloseQuery(query);
}
