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
