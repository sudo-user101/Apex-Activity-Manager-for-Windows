using Apex.Core;

namespace Apex.Core.Tests;

public class FmtTests
{
    [Fact] public void Bytes() { Assert.Equal("1.5 MB", Fmt.Bytes(1_536_000).Replace(',', '.')); Assert.Equal("999 bytes", Fmt.Bytes(999)); }
    [Fact] public void Memory() => Assert.Equal("8.0 GB", Fmt.Memory(8L * 1024 * 1024 * 1024).Replace(',', '.'));
    [Fact] public void Rates() { Assert.Equal("0 KB/s", Fmt.Rate(500)); Assert.Equal("12.4 Mbps", Fmt.Bits(1_550_000).Replace(',', '.')); }
    [Fact] public void Times()
    {
        Assert.Equal("1:02:05", Fmt.Clock(TimeSpan.FromSeconds(3725)));
        Assert.Equal("1:15", Fmt.Clock(TimeSpan.FromSeconds(75)));
        Assert.Equal("3 min 20 s", Fmt.Duration(200));
        Assert.Equal("2:03:04:05", Fmt.UpTime(new TimeSpan(2, 3, 4, 5)));
    }
}

public class PressureTests
{
    static MemoryStats M(long avail, long commit = 0, long limit = 100) => new() { Total = 100, Available = avail, Committed = commit, CommitLimit = limit };
    [Fact] public void Levels()
    {
        Assert.Equal(PressureLevel.Normal, M(40).Pressure);
        Assert.Equal(PressureLevel.Elevated, M(10).Pressure);
        Assert.Equal(PressureLevel.Critical, M(3).Pressure);
        Assert.Equal(PressureLevel.Critical, M(40, commit: 97).Pressure);
        Assert.Equal(PressureLevel.Elevated, M(40, commit: 90).Pressure);
    }
}

public class SeriesTests
{
    [Fact] public void DropsOldPoints()
    {
        var s = new TimeSeries(TimeSpan.FromSeconds(60));
        var t = new DateTime(2026, 1, 1);
        for (int i = 0; i <= 90; i += 10) s.Add(t.AddSeconds(i), i);
        Assert.Equal(7, s.Count);                 // 30…90
        Assert.Equal(90, s.Last);
        var pts = s.Normalised(t.AddSeconds(90)).ToList();
        Assert.Equal(0, pts[0].X, 3); Assert.Equal(1, pts[^1].X, 3);
    }
}

public class CpuTests
{
    [Fact] public void ProcessShareOfWholePC() =>
        Assert.Equal(25, CpuMath.ProcessPercent(TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 8), 3);
    [Fact] public void SystemBusy() => Assert.Equal(75, CpuMath.SystemPercent(0, 0, 0, 25, 60, 40), 3);
    [Fact] public void NoTimeNoNumber() => Assert.Equal(0, CpuMath.ProcessPercent(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.Zero, 8));
}

public class GpuTests
{
    [Fact] public void ParsesInstanceNames()
    {
        var p = GpuInstance.Parse("pid_1234_luid_0x00000000_0x0000C9F3_phys_0_eng_0_engtype_3D");
        Assert.Equal(1234, p?.Pid); Assert.Equal("3D", p?.EngineType);
        Assert.Null(GpuInstance.Parse("_Total"));
    }
    [Fact] public void BusiestEnginePerProcess()
    {
        var r = new[] {
            ("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 30.0),
            ("pid_1_luid_0x0_0x1_phys_0_eng_1_engtype_3D", 10.0),
            ("pid_1_luid_0x0_0x1_phys_0_eng_2_engtype_VideoDecode", 15.0),
            ("pid_2_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 5.0) };
        var per = GpuInstance.PerProcess(r);
        Assert.Equal(40, per[1]); Assert.Equal(5, per[2]);
        Assert.Equal(45, GpuInstance.PerEngine(r)["3D"]);
    }
}

public class ProcessTreeTests
{
    static ProcessSample P(int pid, string name, ProcessKind k, double cpu, string path = "", bool window = false, int parent = 0) =>
        new() { Pid = pid, Name = name, Kind = k, Cpu = cpu, Path = path, HasWindow = window, ParentPid = parent, Memory = 100 };

    [Fact] public void SectionsProgramsAndProcesses()
    {
        var procs = new[] {
            P(10, "msedge.exe", ProcessKind.App, 5, @"C:\Edge\msedge.exe", window: true),
            P(11, "msedge.exe", ProcessKind.App, 3, @"C:\Edge\msedge.exe", parent: 10),
            P(20, "notepad.exe", ProcessKind.App, 1, @"C:\Windows\notepad.exe", window: true),
            P(30, "OneDrive.exe", ProcessKind.Background, 0.5, @"C:\OD\OneDrive.exe"),
            P(4, "System", ProcessKind.Windows, 2),
        };
        var rows = ProcessTree.Build(procs);
        Assert.Equal(new[] { "Apps (2)", "Background processes (1)", "Windows processes (1)" }, rows.Select(r => r.Name));
        var edge = rows[0].Children[0];
        Assert.Equal("msedge.exe (2)", edge.Name);
        Assert.Equal(8, edge.Cpu); Assert.Equal(2, edge.Children.Count); Assert.Null(edge.Pid);
        var flat = ProcessTree.Flatten(rows, new HashSet<string> { edge.Id });
        Assert.Equal(1 + 2 + 2 + 1 + 1 + 1 + 1, flat.Count);
        Assert.Equal(2, flat[2].Depth);
    }

    [Fact] public void SearchAndSort()
    {
        var procs = new[] { P(1, "a.exe", ProcessKind.App, 1, "a"), P(2, "b.exe", ProcessKind.App, 9, "b") };
        Assert.Equal("b.exe", ProcessTree.Build(procs)[0].Children[0].Name);
        Assert.Equal("a.exe", ProcessTree.Build(procs, sort: ProcessSortKey.Name, ascending: true)[0].Children[0].Name);
        Assert.Single(ProcessTree.Build(procs, search: "2")[0].Children);
    }
}

public class StartupTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --min", "C:\\Program Files\\App\\app.exe")]
    [InlineData("C:\\Tools\\tool.exe -silent", "C:\\Tools\\tool.exe")]
    [InlineData("C:\\Program Files\\X\\x.EXE /background", "C:\\Program Files\\X\\x.EXE")]
    [InlineData("rundll32 shell32.dll,Control_RunDLL", "rundll32")]
    public void ExecutablePath(string cmd, string exe) => Assert.Equal(exe, StartupParsing.ExecutablePath(cmd));

    [Fact] public void ApprovedFlags()
    {
        Assert.True(StartupParsing.IsEnabled(null));
        Assert.True(StartupParsing.IsEnabled(new byte[] { 2, 0, 0, 0 }));
        Assert.False(StartupParsing.IsEnabled(new byte[] { 3, 0, 0, 0 }));
        Assert.False(StartupParsing.IsEnabled(StartupParsing.ApprovedValue(false, DateTime.UtcNow)));
        Assert.True(StartupParsing.IsEnabled(StartupParsing.ApprovedValue(true, DateTime.UtcNow)));
        Assert.Equal(12, StartupParsing.ApprovedValue(false, DateTime.UtcNow).Length);
    }
}

public class HeatTests
{
    [Fact] public void Levels() { Assert.Equal(0, Heat.Level(0, 10)); Assert.Equal(5, Heat.Level(9, 10)); Assert.Equal(3, Heat.Level(3, 10)); }
}

public class ProcessIdentityTests
{
    static ProcessSample P(int pid, long created, string path = "C:\\a\\a.exe") =>
        new() { Pid = pid, Name = "a.exe", Path = path, CreateTime = created, Kind = ProcessKind.Background };

    [Fact] public void RowsCarryTheExactProcesses()
    {
        var rows = ProcessTree.Build(new[] { P(10, 111), P(11, 222) });
        var program = rows[0].Children[0];
        Assert.Equal(new[] { new ProcessIdentity(10, 111), new ProcessIdentity(11, 222) }, program.Targets.OrderBy(t => t.Pid));
        Assert.All(program.Children, c => Assert.Single(c.Targets));
    }

    [Fact] public void IncompleteIdentity() { Assert.False(new ProcessIdentity(10, 0).IsComplete); Assert.True(new ProcessIdentity(10, 5).IsComplete); }

    [Fact] public void ReportExplainsEachRefusal()
    {
        ProcessIdentity a = new(10, 1), b = new(11, 2), c = new(12, 3), d = new(13, 4);
        var r = new ActionReport();
        r.Add(a, ActionResult.Reused); r.Add(b, ActionResult.Unverifiable); r.Add(c, ActionResult.NeedsAdmin); r.Add(d, ActionResult.Exited);
        var m = r.Message(new Dictionary<ProcessIdentity, string> { [a] = "Edge", [b] = "node", [c] = "svchost" }, "end")!;
        Assert.Contains("“Edge” had already closed", m);
        Assert.Contains("couldn’t confirm “node”", m);
        Assert.Contains("“svchost” belongs to another user", m);
        Assert.Equal(new[] { d }, r.Done);
        var ok = new ActionReport(); ok.Add(a, ActionResult.Done); ok.Add(b, ActionResult.Exited);
        Assert.Null(ok.Message(new Dictionary<ProcessIdentity, string>(), "end"));
    }
}

public class ClipboardTests
{
    [Fact] public void NewestFirstNoDuplicatesCapped()
    {
        var list = new List<ClipItem>();
        for (int i = 0; i < 60; i++) ClipList.Insert(list, ClipItem.FromText("t" + i), 50);
        Assert.Equal(50, list.Count);
        Assert.Equal("t59", list[0].Text);
        ClipList.Insert(list, ClipItem.FromText("t30"), 50);
        Assert.Equal("t30", list[0].Text);
        Assert.Equal(50, list.Count);
        Assert.Single(list, i => i.Text == "t30");
    }

    [Fact] public void TitlesAndSearch()
    {
        var files = ClipItem.FromFiles(new[] { @"C:\a\report.pdf", @"C:\a\b.png" });
        Assert.Equal("2 files", files.Title);
        Assert.True(files.Matches("report"));
        Assert.Equal("report.pdf", ClipItem.FromFiles(new[] { @"C:\a\report.pdf" }).Title);
        Assert.Equal("Picture · 10 × 20", ClipItem.FromPicture(new byte[100], 10, 20).Title);
        Assert.Equal("a b", ClipItem.FromText("a\nb").Title);
        Assert.False(ClipItem.FromText("hello").Matches("bye"));
    }
}

public class FrameTests
{
    const string Header = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange";

    [Fact] public void ParsesByColumnName()
    {
        var csv = new PresentMonCsv();
        Assert.Null(csv.Parse(Header));
        var f = csv.Parse("game.exe,4321,0x1,DXGI,0,0,1,Hardware: Independent Flip,12.5,NA,16.667,16.667")!.Value;
        Assert.Equal(("game.exe", 4321, "DXGI"), (f.App, f.Pid, f.Runtime));
        Assert.Equal(16.667, f.MsBetweenPresents, 3);
        Assert.Null(csv.Parse("game.exe,4321,0x1,DXGI,0,0,1,x,1,NA,NA,NA"));     // no frame time
        Assert.Null(new PresentMonCsv().Parse("game.exe,1,0x1,DXGI"));             // no header yet
    }

    [Fact] public void FpsOnePercentLowAndPruning()
    {
        var h = new FrameHistory();
        double t = 0;
        for (int i = 0; i < 600; i++) { t += i % 100 == 0 ? 0.05 : 1 / 60.0; h.Add(new FrameSample("g", 7, i % 100 == 0 ? 50 : 1000 / 60.0, "DXGI"), t); }
        var s = h.Stats(7, t);
        Assert.InRange(s.Fps, 57, 61);              // the last 2 s include one 50 ms hitch
        Assert.InRange(s.OnePercentLow, 19, 21);     // the 50 ms hitches
        Assert.True(s.JitterMs > 0);
        h.Prune(t + 10);
        Assert.Empty(h.Apps);
    }

    [Fact] public void SaysWhatHoldsItBack()
    {
        Assert.Equal(Limit.None, Bottleneck.Diagnose(60, 60, 99, 99, 50, PressureLevel.Critical).Limit);
        Assert.Equal(Limit.Memory, Bottleneck.Diagnose(40, 60, 50, 50, 1, PressureLevel.Critical).Limit);
        Assert.Equal(Limit.Background, Bottleneck.Diagnose(40, 60, 50, 50, 30, PressureLevel.Normal).Limit);
        Assert.Equal(Limit.Gpu, Bottleneck.Diagnose(40, 60, 99, 50, 2, PressureLevel.Normal).Limit);
        Assert.Equal(Limit.Cpu, Bottleneck.Diagnose(40, 60, 70, 98, 2, PressureLevel.Normal).Limit);
        Assert.Equal(Limit.Unknown, Bottleneck.Diagnose(40, 60, 50, 50, 2, PressureLevel.Normal).Limit);
    }

    [Fact] public void GameModeQuietsOtherAppsOnly()
    {
        ProcessSample App(int pid, string path, ProcessKind k = ProcessKind.App) => new() { Pid = pid, Path = path, Name = path[(path.LastIndexOf('\\') + 1)..], Kind = k };
        var procs = new[] { App(1, @"C:\Games\game.exe"), App(2, @"C:\b\browser.exe"), App(3, @"C:\b\browser.exe"),
                            App(4, @"C:\v\voice.exe"), App(5, @"C:\a\apex.exe"), App(6, @"C:\w\svc.exe", ProcessKind.Background) };
        var quiet = GameModePlan.ToQuiet(procs, @"C:\Games\game.exe", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "voice.exe" }, self: 5);
        Assert.Equal(new[] { @"C:\b\browser.exe" }, quiet.Select(q => q.Path));
    }
}

public class PowerPlanTests
{
    [Fact] public void ParsesPowercfg()
    {
        var text = "Existing Power Schemes (* Active)\n-----------------------------------\nPower Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *\nPower Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)\nPower Scheme GUID: a1841308-3541-4fab-bc81-f71556f20b4a  (Power saver)\n";
        var plans = PowerPlanList.Parse(text);
        Assert.Equal(3, plans.Count);
        Assert.True(plans[0].Active);
        Assert.Equal(PowerPlanList.HighPerformance, PowerPlanList.Fastest(plans)!.Id);
        Assert.Null(PowerPlanList.Fastest(plans.Where(p => p.Id != PowerPlanList.HighPerformance)));
    }
}

public class ApexAgentTests
{
    static ProcessIdentity[] Id(int pid) => new[] { new ProcessIdentity(pid, 1000 + pid) };
    static ApexAgent.Readings R() => new()
    {
        Apps = new() { new("Google Chrome", @"C:\c\chrome.exe", Id(10), 20), new("Microsoft Word", @"C:\w\winword.exe", Id(11)),
                       new("Spotify", @"C:\s\spotify.exe", Id(12)), new("Galaxy Racer", @"C:\g\racer.exe", Id(13), 40, 0, 72) },
        Processes = new() { new("node", @"C:\n\node.exe", Id(50), 88), new("python", @"C:\p\python.exe", Id(51), 12) },
        Cpu = 30, MemoryUsed = 8L << 30, MemoryTotal = 16L << 30, DiskFree = 100L << 30, DiskTotal = 500L << 30,
    };

    static ApexAgent.QuitApps? Quit(string q) => ApexAgent.Ask(q, R()).Actions.OfType<ApexAgent.QuitApps>().FirstOrDefault();

    [Fact] public void QuitCommandsCarryExactProcesses()
    {
        Assert.Equal(Id(10), Quit("close chrome")!.Targets);
        Assert.Equal(4, Quit("close all my apps")!.Names.Length);
        Assert.Equal(new[] { "Google Chrome", "Microsoft Word", "Galaxy Racer" }, Quit("close everything except spotify")!.Names);
        Assert.Null(Quit("how do I close an app"));
        Assert.Contains("can’t find", ApexAgent.Ask("close photoshop", R()).Text);
    }

    [Fact] public void StopProcesses()
    {
        var a = ApexAgent.Ask("stop process node", R()).Actions.OfType<ApexAgent.StopProcesses>().Single();
        Assert.Equal(Id(50), a.Targets);
    }

    [Fact] public void GameModeWish()
    {
        var a = ApexAgent.Ask("I want a steady 60 fps in galaxy racer", R()).Actions.OfType<ApexAgent.StartGameMode>().Single();
        Assert.Equal((@"C:\g\racer.exe", 60.0), (a.GamePath, a.TargetFps));
        var b = ApexAgent.Ask("game mode 144", R()).Actions.OfType<ApexAgent.StartGameMode>().Single();
        Assert.Equal(("Galaxy Racer", 144.0), (b.GameName, b.TargetFps));          // the app drawing frames
        Assert.Empty(ApexAgent.Ask("close the game", R()).Actions.OfType<ApexAgent.StartGameMode>());
        var none = new ApexAgent.Readings();
        Assert.Contains("Start the game first", ApexAgent.Ask("steady 60 fps please", none).Text);
    }

    [Fact] public void Topics()
    {
        Assert.Equal("slow", ApexAgent.TopicOf("why is my pc so laggy"));
        Assert.Equal("storage", ApexAgent.TopicOf("my drive is full"));
        Assert.Equal("wifi", ApexAgent.TopicOf("internet keeps disconnecting"));
        Assert.Equal("games", ApexAgent.TopicOf("my game stutters"));
        Assert.True(ApexAgent.Ask("what's the meaning of life", R()).Unknown);
    }
}

public class CleanerTests
{
    [Fact] public void OnlyOldFilesInsideTheRootsAndNothingElse()
    {
        var root = Path.Combine(Path.GetTempPath(), "apex-clean-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "apex-keep-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllText(Path.Combine(root, "old.tmp"), new string('x', 100));
            File.WriteAllText(Path.Combine(root, "sub", "old2.tmp"), new string('x', 50));
            File.WriteAllText(Path.Combine(root, "new.tmp"), "fresh");
            File.WriteAllText(outside, "mine");
            foreach (var f in new[] { "old.tmp", "sub/old2.tmp" }) File.SetLastWriteTime(Path.Combine(root, f), DateTime.Now.AddDays(-3));
            var c = new CleanCategory("t", "Temp", "", new[] { root }, TimeSpan.FromDays(1));
            var items = Cleaner.Scan(c, DateTime.Now);
            Assert.Equal(2, items.Count);
            Assert.Equal(150, items.Sum(i => i.Size));
            var r = Cleaner.Clean(c, items.Append(new CleanItem(outside, 4)));
            Assert.Equal((150L, 2, 1), (r.Freed, r.Removed, r.Skipped));      // the file outside the roots is refused
            Assert.True(File.Exists(Path.Combine(root, "new.tmp")));
            Assert.True(File.Exists(outside));
            Assert.False(Directory.Exists(Path.Combine(root, "sub")), "empty folder tidied");
            Assert.True(Directory.Exists(root), "the root itself stays");
        }
        finally { try { Directory.Delete(root, true); } catch { } try { File.Delete(outside); } catch { } }
    }
}

public class NetworkDoctorTests
{
    static NetworkDoctor.Input Ok() => new() { Address = "192.168.1.20", Gateway = "192.168.1.1", SignalPercent = 90,
        Router = new(5, 5, 3), Internet = new(5, 5, 20), HttpsOk = true, DnsOk = true, DnsMs = 20, SignInPage = false };

    [Fact] public void FromThePcOutwards()
    {
        Assert.Equal("net.ok", NetworkDoctor.Diagnose(Ok()).Single().Id);
        Assert.Equal("net.down", NetworkDoctor.Diagnose(new() { IsUp = false }).Single().Id);
        Assert.Contains(NetworkDoctor.Diagnose(new() { Address = "169.254.3.4", Gateway = "" }), f => f.Id == "net.dhcp");
        var noInternet = new NetworkDoctor.Input { Address = "192.168.1.20", Gateway = "192.168.1.1", Router = new(5, 5, 3), Internet = new(5, 0, null), HttpsOk = false, SignInPage = false };
        Assert.Equal("net.internet", NetworkDoctor.Diagnose(noInternet)[0].Id);
        var dns = new NetworkDoctor.Input { Address = "192.168.1.20", Gateway = "192.168.1.1", Router = new(5, 5, 3), Internet = new(5, 5, 20), DnsOk = false };
        Assert.Contains(NetworkDoctor.Diagnose(dns), f => f.Id == "net.dns");
        var weak = new NetworkDoctor.Input { Address = "10.0.0.5", Gateway = "10.0.0.1", SignalPercent = 25, Router = new(5, 5, 4) };
        Assert.Contains(NetworkDoctor.Diagnose(weak), f => f.Id == "net.signal" && f.Severity == Severity.Warning);
        var portal = new NetworkDoctor.Input { Address = "10.0.0.5", Gateway = "10.0.0.1", Router = new(5, 5, 4), HttpsOk = false, SignInPage = true };
        var pf = NetworkDoctor.Diagnose(portal);
        Assert.Contains(pf, f => f.Id == "net.portal");
        Assert.DoesNotContain(pf, f => f.Id == "net.internet");
    }
}

public class CleanerInsideTests
{
    [Fact] public void InsideMeansInside()
    {
        Assert.True(Cleaner.Inside(@"C:\Temp\a.txt", @"C:\Temp"));
        Assert.True(Cleaner.Inside(@"C:\Temp\a.txt", @"C:\Temp\"));
        Assert.False(Cleaner.Inside(@"C:\Temp2\a.txt", @"C:\Temp"));
        Assert.False(Cleaner.Inside(@"C:\Temp", @"C:\Temp"));
    }
}

public class NetParseTests
{
    [Fact] public void WlanSignal()
    {
        var text = "There is 1 interface on the system:\n\n    Name                   : Wi-Fi\n    State                  : connected\n    SSID                   : Home\n    Signal                 : 86%\n";
        Assert.Equal(86, NetParse.WlanSignal(text));
        Assert.Null(NetParse.WlanSignal("There is no wireless interface on the system."));
        Assert.Equal("Listening", NetParse.TcpState(2));
        Assert.True(new Connection(1, "TCP", "0.0.0.0", 80, "", 0, "Listening").Listening);
        Assert.True(new Connection(1, "TCP", "127.0.0.1", 5000, "127.0.0.1", 6000, "Connected").LocalOnly);
    }
}
