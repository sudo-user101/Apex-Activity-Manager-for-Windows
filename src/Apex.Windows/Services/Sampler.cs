using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Apex.Core;
using Microsoft.Win32;

namespace Apex.Services;

/// Reads the whole PC once per tick on a background thread and hands the
/// snapshot to the UI. Everything per process comes from one call
/// (NtQuerySystemInformation), like Task Manager, so a tick stays cheap.
internal sealed class Sampler : IDisposable
{
    public event Action<Snapshot>? Updated;
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    readonly Pdh pdh = new();
    readonly CancellationTokenSource cts = new();
    readonly int logical = Environment.ProcessorCount;

    // Previous readings, for rates.
    DateTime lastTime;
    long[] lastIdle = Array.Empty<long>(), lastBusy = Array.Empty<long>();
    readonly Dictionary<int, (long CreateTime, long CpuTime, long IoBytes)> lastProc = new();
    readonly Dictionary<string, (long In, long Out)> lastNet = new();
    // Slow facts, read once per process / once per run.
    readonly Dictionary<(int, long), (string Path, string Display)> procInfo = new();
    readonly string cpuName, windowsDir;
    readonly double baseGHz;
    readonly int physicalCores;
    readonly Dictionary<int, string> diskModels = new();
    readonly List<(string Name, long DedicatedTotal)> gpuNames = new();

    public Sampler()
    {
        windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
        {
            cpuName = (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Processor";
            baseGHz = ((k?.GetValue("~MHz") as int?) ?? 0) / 1000.0;
        }
        physicalCores = CountPhysicalCores();
        ReadHardwareNames();
        foreach (var p in new[] {
            @"\Processor Information(_Total)\% Processor Performance",
            @"\PhysicalDisk(*)\% Idle Time", @"\PhysicalDisk(*)\Disk Read Bytes/sec", @"\PhysicalDisk(*)\Disk Write Bytes/sec",
            @"\PhysicalDisk(*)\Avg. Disk sec/Transfer",
            @"\GPU Engine(*)\Utilization Percentage", @"\GPU Adapter Memory(*)\Dedicated Usage", @"\GPU Adapter Memory(*)\Shared Usage",
            @"\Memory\Cache Bytes", @"\Memory\Pool Paged Bytes", @"\Memory\Pool Nonpaged Bytes" })
            pdh.Add(p);
        pdh.Collect();
    }

    public void Start() => Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            try { Updated?.Invoke(Sample()); } catch (Exception e) { Debug.WriteLine(e); }
            try { await Task.Delay(Interval, cts.Token); } catch (TaskCanceledException) { }
        }
    });

    public void Dispose() { cts.Cancel(); pdh.Dispose(); }

    Snapshot Sample()
    {
        var now = DateTime.Now;
        var elapsed = lastTime == default ? TimeSpan.Zero : now - lastTime;
        lastTime = now;
        pdh.Collect();

        var gpuReadings = pdh.Values(@"\GPU Engine(*)\Utilization Percentage");
        var gpuPerProcess = GpuInstance.PerProcess(gpuReadings);
        var procs = Processes(elapsed, gpuPerProcess, out int threads, out int handles);
        var cpu = Cpu(procs.Count, threads, handles);
        return new Snapshot
        {
            Time = now, Cpu = cpu, Memory = Memory(procs), Disks = Disks(), Networks = Networks(elapsed),
            Gpus = Gpus(gpuReadings), Battery = Battery(), Processes = procs,
        };
    }

    // ---- CPU -------------------------------------------------------------------

    CpuStats Cpu(int processes, int threads, int handles)
    {
        int n = logical;
        int size = Marshal.SizeOf<Native.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        var buf = Marshal.AllocHGlobal(size * n);
        var perCore = new double[n];
        try
        {
            if (Native.NtQuerySystemInformation(Native.SystemProcessorPerformanceInformation, buf, size * n, out _) == 0)
            {
                var idle = new long[n]; var busy = new long[n];
                for (int i = 0; i < n; i++)
                {
                    var s = Marshal.PtrToStructure<Native.SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(buf + i * size);
                    idle[i] = s.IdleTime; busy[i] = s.KernelTime + s.UserTime;   // kernel time includes idle
                    if (lastIdle.Length == n)
                    {
                        long dIdle = idle[i] - lastIdle[i], dTotal = busy[i] - lastBusy[i];
                        perCore[i] = dTotal > 0 ? Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100) : 0;
                    }
                }
                lastIdle = idle; lastBusy = busy;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        double perf = pdh.Total(@"\Processor Information(_Total)\% Processor Performance");
        return new CpuStats
        {
            Total = n > 0 ? perCore.Average() : 0, PerCore = perCore, Name = cpuName, BaseSpeedGHz = baseGHz,
            SpeedGHz = perf > 0 ? baseGHz * perf / 100 : baseGHz, Cores = physicalCores, LogicalProcessors = n,
            Processes = processes, Threads = threads, Handles = handles, UpTime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }

    static int CountPhysicalCores()
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor");
            return s.Get().Cast<System.Management.ManagementObject>().Sum(o => Convert.ToInt32(o["NumberOfCores"]));
        }
        catch { return Environment.ProcessorCount; }
    }

    void ReadHardwareNames()
    {
        try
        {
            using var d = new System.Management.ManagementObjectSearcher("SELECT Index, Model FROM Win32_DiskDrive");
            foreach (System.Management.ManagementObject o in d.Get())
                diskModels[Convert.ToInt32(o["Index"])] = (o["Model"] as string)?.Trim() ?? "";
            using var g = new System.Management.ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            foreach (System.Management.ManagementObject o in g.Get())
                gpuNames.Add(((o["Name"] as string) ?? "GPU", Convert.ToInt64(o["AdapterRAM"] ?? 0L)));
        }
        catch { }
    }

    // ---- Processes ----------------------------------------------------------------

    List<ProcessSample> Processes(TimeSpan elapsed, Dictionary<int, double> gpu, out int threads, out int handles)
    {
        threads = 0; handles = 0;
        var list = new List<ProcessSample>(400);
        int len = 1 << 20;
        IntPtr buf;
        while (true)
        {
            buf = Marshal.AllocHGlobal(len);
            int st = Native.NtQuerySystemInformation(Native.SystemProcessInformation, buf, len, out int needed);
            if (st == 0) break;
            Marshal.FreeHGlobal(buf);
            if (st != unchecked((int)0xC0000004)) return list;     // anything but "buffer too small"
            len = Math.Max(len * 2, needed + 65536);
        }
        var windows = Native.ProcessesWithWindows();
        var seen = new HashSet<int>();
        try
        {
            IntPtr p = buf;
            while (true)
            {
                // SYSTEM_PROCESS_INFORMATION (64-bit layout).
                int next = Marshal.ReadInt32(p, 0);
                int nThreads = Marshal.ReadInt32(p, 4);
                long privateWs = Marshal.ReadInt64(p, 8);
                long create = Marshal.ReadInt64(p, 32);
                long user = Marshal.ReadInt64(p, 40), kernel = Marshal.ReadInt64(p, 48);
                ushort nameLen = (ushort)Marshal.ReadInt16(p, 56);
                IntPtr namePtr = Marshal.ReadIntPtr(p, 64);
                int pid = (int)Marshal.ReadInt64(p, 80);
                int parent = (int)Marshal.ReadInt64(p, 88);
                int nHandles = Marshal.ReadInt32(p, 96);
                int session = Marshal.ReadInt32(p, 100);
                long io = Marshal.ReadInt64(p, 232) + Marshal.ReadInt64(p, 240);   // read + write transfer bytes
                string name = pid == 0 ? "System Idle Process" : namePtr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(namePtr, nameLen / 2) ?? "";
                threads += nThreads; handles += nHandles;
                seen.Add(pid);

                if (pid != 0)
                {
                    double cpu = 0, disk = 0;
                    long cpuTime = user + kernel;
                    if (lastProc.TryGetValue(pid, out var prev) && prev.CreateTime == create && elapsed > TimeSpan.Zero)
                    {
                        cpu = CpuMath.ProcessPercent(TimeSpan.FromTicks(prev.CpuTime), TimeSpan.FromTicks(cpuTime), elapsed, logical);
                        disk = Math.Max(0, io - prev.IoBytes) / elapsed.TotalSeconds;
                    }
                    lastProc[pid] = (create, cpuTime, io);
                    var (path, display) = Info(pid, create, name);
                    list.Add(new ProcessSample
                    {
                        Pid = pid, ParentPid = parent, Name = name, DisplayName = display, Path = path,
                        Kind = Classify(pid, name, path, session, windows.Contains(pid)),
                        Cpu = cpu, Memory = privateWs, DiskPerSec = disk, Gpu = gpu.GetValueOrDefault(pid),
                        Threads = nThreads, Handles = nHandles, HasWindow = windows.Contains(pid), User = session.ToString(),
                        CreateTime = create,
                    });
                }
                if (next == 0) break;
                p += next;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        foreach (var gone in lastProc.Keys.Where(k => !seen.Contains(k)).ToList()) lastProc.Remove(gone);
        foreach (var gone in procInfo.Keys.Where(k => !seen.Contains(k.Item1)).ToList()) procInfo.Remove(gone);
        return list;
    }

    (string Path, string Display) Info(int pid, long create, string name)
    {
        if (procInfo.TryGetValue((pid, create), out var i)) return i;
        string path = Native.ImagePath(pid) ?? "";
        string display = "";
        if (path.Length > 0)
        {
            try { display = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; } catch { }
        }
        if (display.Length == 0) display = Path.GetFileNameWithoutExtension(name);
        procInfo[(pid, create)] = (path, display);
        return (path, display);
    }

    static readonly HashSet<string> CoreWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
        "svchost.exe", "dwm.exe", "fontdrvhost.exe", "Memory Compression", "LsaIso.exe", "spoolsv.exe", "Secure System",
    };

    ProcessKind Classify(int pid, string name, string path, int session, bool hasWindow)
    {
        if (hasWindow) return ProcessKind.App;
        if (pid == 4 || CoreWindows.Contains(name)) return ProcessKind.Windows;
        if (path.Length == 0 && session == 0) return ProcessKind.Windows;
        if (path.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase) && session == 0) return ProcessKind.Windows;
        return ProcessKind.Background;
    }

    // ---- Memory -----------------------------------------------------------------

    static MemoryStats Memory(List<ProcessSample> procs)
    {
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        Native.GlobalMemoryStatusEx(ref m);
        Native.GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<Native.PERFORMANCE_INFORMATION>());
        long page = (long)pi.PageSize;
        var compression = procs.FirstOrDefault(p => p.Name == "Memory Compression");
        return new MemoryStats
        {
            Total = (long)m.ullTotalPhys, Available = (long)m.ullAvailPhys,
            Committed = (long)pi.CommitTotal * page, CommitLimit = (long)pi.CommitLimit * page,
            Cached = (long)pi.SystemCache * page, PagedPool = (long)pi.KernelPaged * page, NonPagedPool = (long)pi.KernelNonpaged * page,
            Compressed = compression?.Memory ?? 0,
        };
    }

    // ---- Disks ------------------------------------------------------------------

    List<DiskStats> Disks()
    {
        var idle = pdh.Values(@"\PhysicalDisk(*)\% Idle Time").Where(v => v.Instance != "_Total").ToList();
        var read = pdh.Values(@"\PhysicalDisk(*)\Disk Read Bytes/sec").ToDictionary(v => v.Instance, v => v.Value);
        var write = pdh.Values(@"\PhysicalDisk(*)\Disk Write Bytes/sec").ToDictionary(v => v.Instance, v => v.Value);
        var resp = pdh.Values(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer").ToDictionary(v => v.Instance, v => v.Value);
        string sysDrive = Path.GetPathRoot(windowsDir)?.TrimEnd('\\') ?? "C:";
        var list = new List<DiskStats>();
        foreach (var (inst, idlePct) in idle.OrderBy(v => v.Instance))
        {
            // "0 C: D:" → disk 0 holding C: and D:
            var parts = inst.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int.TryParse(parts.FirstOrDefault(), out int index);
            var letters = parts.Skip(1).ToList();
            long cap = 0, free = 0;
            foreach (var l in letters)
            {
                try { var d = new DriveInfo(l); if (d.IsReady) { cap += d.TotalSize; free += d.AvailableFreeSpace; } } catch { }
            }
            list.Add(new DiskStats
            {
                Name = $"Disk {index}" + (letters.Count > 0 ? $" ({string.Join(" ", letters)})" : ""),
                Model = diskModels.GetValueOrDefault(index, ""), ActivePercent = Math.Clamp(100 - idlePct, 0, 100),
                ReadPerSec = read.GetValueOrDefault(inst), WritePerSec = write.GetValueOrDefault(inst),
                ResponseMs = resp.GetValueOrDefault(inst) * 1000, Capacity = cap, Free = free,
                IsSystem = letters.Any(l => l.Equals(sysDrive, StringComparison.OrdinalIgnoreCase)),
            });
        }
        return list;
    }

    // ---- Network ----------------------------------------------------------------

    List<NetworkStats> Networks(TimeSpan elapsed)
    {
        var list = new List<NetworkStats>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel && !IsVpn(n)) continue;
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            if (n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !IsVpn(n)
                || n.Description.Contains("Filter", StringComparison.OrdinalIgnoreCase)) continue;
            long rx = 0, tx = 0;
            try { var s = n.GetIPStatistics(); rx = s.BytesReceived; tx = s.BytesSent; } catch { }
            double inRate = 0, outRate = 0;
            if (lastNet.TryGetValue(n.Id, out var prev) && elapsed > TimeSpan.Zero)
            {
                inRate = Math.Max(0, rx - prev.In) / elapsed.TotalSeconds;
                outRate = Math.Max(0, tx - prev.Out) / elapsed.TotalSeconds;
            }
            lastNet[n.Id] = (rx, tx);
            var props = n.GetIPProperties();
            var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString() ?? "";
            var v6 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal)?.Address.ToString() ?? "";
            string kind = IsVpn(n) ? "VPN" : n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
            list.Add(new NetworkStats
            {
                Id = n.Id, Name = n.Name, Description = n.Description, Kind = kind, InPerSec = inRate, OutPerSec = outRate,
                SpeedBitsPerSec = n.Speed, IPv4 = v4, IPv6 = v6, Up = true,
            });
        }
        return list.OrderBy(n => n.Kind == "VPN" ? 2 : n.Kind == "Wi-Fi" ? 1 : 0).ThenBy(n => n.Name).ToList();
    }

    static bool IsVpn(NetworkInterface n) =>
        n.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
        || n.Description.Contains("VPN", StringComparison.OrdinalIgnoreCase)
        || n.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
        || n.Description.Contains("TAP-", StringComparison.OrdinalIgnoreCase);

    // ---- GPU ------------------------------------------------------------------------

    List<GpuStats> Gpus(List<(string Instance, double Value)> engines)
    {
        var perEngine = GpuInstance.PerEngine(engines);
        var dedicated = pdh.Values(@"\GPU Adapter Memory(*)\Dedicated Usage").Sum(v => v.Value);
        var shared = pdh.Values(@"\GPU Adapter Memory(*)\Shared Usage").Sum(v => v.Value);
        var (name, total) = gpuNames.Count > 0 ? gpuNames[0] : ("GPU", 0L);
        return new List<GpuStats>
        {
            new()
            {
                Name = name, Engines = perEngine, Utilization = perEngine.Count > 0 ? perEngine.Values.Max() : 0,
                DedicatedUsed = (long)dedicated, DedicatedTotal = total, SharedUsed = (long)shared,
            },
        };
    }

    // ---- Battery ------------------------------------------------------------------

    static BatteryStats Battery()
    {
        if (!Native.GetSystemPowerStatus(out var s) || (s.BatteryFlag & 128) != 0 || s.BatteryFlag == 255) return new BatteryStats();
        return new BatteryStats
        {
            HasBattery = true, Percent = s.BatteryLifePercent > 100 ? 0 : s.BatteryLifePercent, OnPower = s.ACLineStatus == 1,
            Charging = (s.BatteryFlag & 8) != 0, Remaining = s.BatteryLifeTime > 0 ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null,
        };
    }
}
