using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Apex.Core;

namespace Apex.Services;

/// The Network page's tools, as on the Mac: every connection and listening
/// port with the app that owns it, the public IP, an internet speed test, and
/// the measurements for Network Doctor.
internal static class NetworkTools
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // ---- Connections (GetExtendedTcpTable / GetExtendedUdpTable, owner PID) ----

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);
    const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;

    /// Asks for the size, then fills a buffer (retrying if the table grew meanwhile).
    static IntPtr Table(Func<IntPtr, int, (uint Result, int Size)> call)
    {
        int size = call(IntPtr.Zero, 0).Size;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            size += 4096;
            var buf = Marshal.AllocHGlobal(size);
            var (ret, needed) = call(buf, size);
            if (ret == 0) return buf;
            Marshal.FreeHGlobal(buf);
            size = needed;
        }
        return IntPtr.Zero;
    }

    /// Every TCP and UDP endpoint (IPv4 and IPv6) with its owning process.
    public static List<Connection> Connections()
    {
        var list = new List<Connection>();
        foreach (int af in new[] { AF_INET, AF_INET6 })
        {
            var tcp = Table((buf, size) => { int s = size; uint r = GetExtendedTcpTable(buf, ref s, false, af, TCP_TABLE_OWNER_PID_ALL, 0); return (r, s); });
            if (tcp != IntPtr.Zero)
            {
                try
                {
                    int n = Marshal.ReadInt32(tcp);
                    for (int i = 0; i < n; i++)
                    {
                        if (af == AF_INET)
                        {
                            var row = tcp + 4 + i * 24;      // MIB_TCPROW_OWNER_PID
                            int state = Marshal.ReadInt32(row);
                            var local = new IPAddress((uint)Marshal.ReadInt32(row, 4));
                            int lport = Port(Marshal.ReadInt32(row, 8));
                            var remote = new IPAddress((uint)Marshal.ReadInt32(row, 12));
                            int rport = Port(Marshal.ReadInt32(row, 16));
                            int pid = Marshal.ReadInt32(row, 20);
                            list.Add(new Connection(pid, "TCP", local.ToString(), lport, state == 2 ? "" : remote.ToString(), rport, NetParse.TcpState(state)));
                        }
                        else
                        {
                            var row = tcp + 4 + i * 56;      // MIB_TCP6ROW_OWNER_PID
                            var local = new IPAddress(Bytes(row, 0, 16), Marshal.ReadInt32(row, 16));
                            int lport = Port(Marshal.ReadInt32(row, 20));
                            var remote = new IPAddress(Bytes(row, 24, 16), Marshal.ReadInt32(row, 40));
                            int rport = Port(Marshal.ReadInt32(row, 44));
                            int state = Marshal.ReadInt32(row, 48);
                            int pid = Marshal.ReadInt32(row, 52);
                            list.Add(new Connection(pid, "TCP", local.ToString(), lport, state == 2 ? "" : remote.ToString(), rport, NetParse.TcpState(state)));
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(tcp); }
            }
            var udp = Table((buf, size) => { int s = size; uint r = GetExtendedUdpTable(buf, ref s, false, af, UDP_TABLE_OWNER_PID, 0); return (r, s); });
            if (udp != IntPtr.Zero)
            {
                try
                {
                    int n = Marshal.ReadInt32(udp);
                    for (int i = 0; i < n; i++)
                    {
                        if (af == AF_INET)
                        {
                            var row = udp + 4 + i * 12;      // MIB_UDPROW_OWNER_PID
                            list.Add(new Connection(Marshal.ReadInt32(row, 8), "UDP", new IPAddress((uint)Marshal.ReadInt32(row)).ToString(), Port(Marshal.ReadInt32(row, 4)), "", 0, "Listening"));
                        }
                        else
                        {
                            var row = udp + 4 + i * 28;      // MIB_UDP6ROW_OWNER_PID
                            list.Add(new Connection(Marshal.ReadInt32(row, 24), "UDP", new IPAddress(Bytes(row, 0, 16), Marshal.ReadInt32(row, 16)).ToString(), Port(Marshal.ReadInt32(row, 20)), "", 0, "Listening"));
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(udp); }
            }
        }
        return list;
    }

    static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
    static byte[] Bytes(IntPtr p, int offset, int n) { var b = new byte[n]; Marshal.Copy(p + offset, b, 0, n); return b; }

    // ---- Public IP and speed -------------------------------------------------------

    /// The address websites see (looked up with ipify, as on the Mac; nothing else is sent).
    public static async Task<string?> PublicIp()
    {
        try { return (await http.GetStringAsync("https://api.ipify.org")).Trim(); } catch { return null; }
    }

    public sealed record Speed(double DownBitsPerSec, double UpBitsPerSec, DateTime Tested);

    /// About 15 s: downloads and uploads test data from Cloudflare's speed-test servers.
    public static async Task<Speed?> SpeedTest(IProgress<string>? progress = null)
    {
        try
        {
            progress?.Report("Testing download…");
            var sw = Stopwatch.StartNew();
            long got = 0;
            using (var res = await http.GetAsync("https://speed.cloudflare.com/__down?bytes=50000000", HttpCompletionOption.ResponseHeadersRead))
            using (var s = await res.Content.ReadAsStreamAsync())
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await s.ReadAsync(buf)) > 0 && sw.Elapsed < TimeSpan.FromSeconds(10)) got += n;
            }
            double down = got * 8 / Math.Max(0.1, sw.Elapsed.TotalSeconds);
            progress?.Report("Testing upload…");
            var data = new byte[10_000_000];
            sw.Restart();
            using (var content = new ByteArrayContent(data)) await http.PostAsync("https://speed.cloudflare.com/__up", content);
            double up = data.Length * 8 / Math.Max(0.1, sw.Elapsed.TotalSeconds);
            return new Speed(down, up, DateTime.Now);
        }
        catch { return null; }
    }

    // ---- Network Doctor measurements ------------------------------------------------

    static async Task<NetworkDoctor.Ping> PingHost(string host, int count = 5)
    {
        using var p = new Ping();
        int got = 0; var times = new List<long>();
        for (int i = 0; i < count; i++)
        {
            try { var r = await p.SendPingAsync(host, 1500); if (r.Status == IPStatus.Success) { got++; times.Add(r.RoundtripTime); } } catch { }
        }
        return new NetworkDoctor.Ping(count, got, times.Count > 0 ? times.Average() : null);
    }

    /// Measures the connection with this adapter id (about 10 s). Nothing is changed.
    public static async Task<NetworkDoctor.Input> Probe(string adapterId, string kind, double? lastSpeed, IProgress<string>? progress = null)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == adapterId);
        var props = nic?.GetIPProperties();
        var address = props?.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";
        var gateway = props?.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "";
        bool up = nic?.OperationalStatus == OperationalStatus.Up;
        if (!up) return new NetworkDoctor.Input { Name = nic?.Name ?? kind, Kind = kind, IsUp = false };

        progress?.Report("Checking the router…");
        var router = gateway.Length > 0 ? await PingHost(gateway) : null;
        progress?.Report("Checking the internet…");
        var internet = await PingHost("1.1.1.1");
        bool? https = null, portal = null;
        try
        {
            var ncsi = await http.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt");   // Windows' own sign-in-page test
            portal = ncsi.Trim() != "Microsoft Connect Test";
        }
        catch { }
        try { using var r = await http.GetAsync("https://www.microsoft.com", HttpCompletionOption.ResponseHeadersRead); https = true; }
        catch { https = false; }
        progress?.Report("Checking website names…");
        bool? dnsOk; double? dnsMs = null;
        var sw = Stopwatch.StartNew();
        try { await Dns.GetHostAddressesAsync("www.microsoft.com"); dnsOk = true; dnsMs = sw.Elapsed.TotalMilliseconds; } catch { dnsOk = false; }
        int? signal = null;
        if (kind == "Wi-Fi")
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("netsh.exe", "wlan show interfaces") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
                signal = NetParse.WlanSignal(await p.StandardOutput.ReadToEndAsync());
            }
            catch { }
        }
        return new NetworkDoctor.Input
        {
            Name = nic?.Name ?? kind, Kind = kind, IsUp = true, Address = address, Gateway = gateway, SignalPercent = signal,
            Router = router, Internet = internet, HttpsOk = https, DnsOk = dnsOk, DnsMs = dnsMs, SignInPage = portal, DownloadBitsPerSec = lastSpeed,
        };
    }
}
