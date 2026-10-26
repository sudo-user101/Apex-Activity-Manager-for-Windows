using System.Runtime.InteropServices;
using Apex.Core;
using Apex.UI;

namespace Apex.Services;

/// Efficiency mode, the Windows counterpart of the Mac app's Optimize: the
/// app keeps running, but costs the PC as little as possible.
///
/// - EcoQoS (`ProcessPowerThrottling` execution speed): Windows runs it on
///   power-saving cores at lower clock speeds (Windows 11; on Windows 10
///   only the priority part applies);
/// - the idle priority class;
/// - very low memory priority: its pages are the first Windows trims when
///   memory is short;
/// - its windows are minimized, so it isn't drawing anything.
///
/// It's remembered by program: processes the app starts later join in, and
/// bringing the app to the front again takes it out (it's in use). Restore
/// puts everything back, windows included.
internal static class Efficiency
{
    /// Programs (by executable path) in Efficiency mode.
    public static readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<ProcessIdentity> applied = new();
    static readonly Dictionary<string, List<IntPtr>> minimized = new(StringComparer.OrdinalIgnoreCase);
    static Native.WinEventProc? foregroundHook;     // kept alive: Windows calls it

    public static event Action? Changed;

    /// Watches which app comes to the front (needs the UI thread's message loop).
    public static void Start()
    {
        foregroundHook = (_, _, hwnd, _, _, _, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out int pid);
            var path = Native.ImagePath(pid);
            if (path != null && Programs.Contains(path)) Set(path, false, AppState.Latest.Processes);
        };
        Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, foregroundHook, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    /// Turns Efficiency mode on or off for every process of the program at `path`.
    /// False when Windows refused it for any of them.
    public static bool Set(string path, bool on, IEnumerable<ProcessSample> processes)
    {
        var mine = processes.Where(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
        bool ok = true;
        foreach (var p in mine) ok &= Apply(p.Identity, on);
        if (on)
        {
            Programs.Add(path);
            foreach (var p in mine) applied.Add(p.Identity);
            var windows = mine.SelectMany(p => Native.AppWindows(p.Pid)).Where(h => !Native.IsIconic(h)).ToList();
            foreach (var h in windows) Native.ShowWindowAsync(h, Native.SW_MINIMIZE);
            minimized[path] = windows;
        }
        else
        {
            Programs.Remove(path);
            applied.RemoveWhere(t => mine.Any(p => p.Identity == t));
            // Bring back exactly the windows Efficiency mode minimized.
            if (minimized.Remove(path, out var windows)) foreach (var h in windows) Native.ShowWindowAsync(h, Native.SW_RESTORE);
        }
        Changed?.Invoke();
        return ok;
    }

    public static void RestoreAll(IEnumerable<ProcessSample> processes)
    {
        var list = processes.ToList();
        foreach (var path in Programs.ToList()) Set(path, false, list);
    }

    /// Each tick: processes a remembered program started since join in;
    /// programs that have quit are forgotten.
    public static void Keep(IReadOnlyList<ProcessSample> processes)
    {
        if (Programs.Count == 0) return;
        bool changed = false;
        foreach (var path in Programs.ToList())
        {
            var mine = processes.Where(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0) { Programs.Remove(path); minimized.Remove(path); changed = true; continue; }
            foreach (var p in mine.Where(p => !applied.Contains(p.Identity))) { Apply(p.Identity, true); applied.Add(p.Identity); }
        }
        applied.RemoveWhere(t => !processes.Any(p => p.Identity == t));
        if (changed) Changed?.Invoke();
    }

    /// Through a handle checked against the identity (never a reused ID).
    internal static bool Apply(ProcessIdentity t, bool on)
    {
        var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, t.Pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (!Native.GetProcessTimes(h, out long created, out _, out _, out _) || created != t.CreateTime) return false;
            var power = new Native.PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = on ? 1u : 0u };
            bool ok = Native.SetProcessInformation(h, Native.ProcessPowerThrottling, ref power, Marshal.SizeOf(power));
            ok &= Native.SetPriorityClass(h, on ? Native.IDLE_PRIORITY_CLASS : Native.NORMAL_PRIORITY_CLASS);
            var memory = new Native.MEMORY_PRIORITY_INFORMATION { MemoryPriority = on ? Native.MEMORY_PRIORITY_VERY_LOW : Native.MEMORY_PRIORITY_NORMAL };
            Native.SetProcessInformation(h, Native.ProcessMemoryPriority, ref memory, Marshal.SizeOf(memory));   // a hint: best effort
            return ok;
        }
        finally { Native.CloseHandle(h); }
    }

    /// Trims these processes' memory now: it moves to the standby list and
    /// comes back as it's used (a temporary saving, said so in the UI).
    /// Returns how many were trimmed.
    public static int Trim(IEnumerable<ProcessSample> processes)
    {
        int count = 0;
        foreach (var p in processes)
        {
            var h = Native.OpenProcess(Native.PROCESS_SET_QUOTA | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
            if (h == IntPtr.Zero) continue;
            try
            {
                if (Native.GetProcessTimes(h, out long created, out _, out _, out _) && created == p.CreateTime && Native.EmptyWorkingSet(h)) count++;
            }
            finally { Native.CloseHandle(h); }
        }
        return count;
    }
}
