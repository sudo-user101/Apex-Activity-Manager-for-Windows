using Apex.Core;

namespace Apex.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "apex-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string rel, int bytes)
    {
        var p = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class DiskTreeTests
{
    [Fact] public void TotalsLargestAndContents()
    {
        using var t = new TempDir();
        t.File("Photos/a.jpg", 5000); t.File("Photos/2024/b.jpg", 3000); t.File("top.txt", 10); t.File("Docs/c.pdf", 7000);
        var tree = DiskTree.Scan(t.Path);
        Assert.Equal(15010, tree.Top.Size);
        Assert.Equal(4, tree.FileCount);
        var photos = tree.Find(System.IO.Path.Combine(t.Path, "Photos"))!;
        Assert.Equal(8000, photos.Size);
        Assert.Equal(new[] { "Photos", "Docs", "top.txt" }, DiskTree.Contents(tree.Top).Select(c => System.IO.Path.GetFileName(c.Path)).ToArray());
        Assert.Equal(new[] { "c.pdf", "a.jpg", "b.jpg", "top.txt" }, tree.Largest().Select(f => f.Name).ToArray());
    }

    [Fact] public void RemoveShrinksTheFoldersAbove()
    {
        using var t = new TempDir();
        var b = t.File("Photos/2024/b.jpg", 3000); t.File("Photos/a.jpg", 5000); t.File("Docs/c.pdf", 7000);
        var tree = DiskTree.Scan(t.Path);
        Assert.True(tree.Remove(b));
        Assert.Equal(12000, tree.Top.Size);
        Assert.Equal(5000, tree.Find(System.IO.Path.Combine(t.Path, "Photos"))!.Size);
        Assert.DoesNotContain(tree.Largest(), f => f.Name == "b.jpg");
        Assert.True(tree.Remove(System.IO.Path.Combine(t.Path, "Docs")));
        Assert.Equal(5000, tree.Top.Size);
        Assert.Null(tree.Find(System.IO.Path.Combine(t.Path, "Docs")));
        Assert.Equal(1, tree.FileCount);
        Assert.False(tree.Remove(System.IO.Path.Combine(t.Path, "nothing")));
    }

    [Fact] public void BuiltAFolderAtATimeMatchesOneWalk()
    {
        using var t = new TempDir();
        t.File("Photos/a.jpg", 5000); t.File("Photos/2024/b.jpg", 3000); t.File("top.txt", 10); t.File("Docs/c.pdf", 7000);
        var whole = DiskTree.Scan(t.Path);
        var built = new DiskTree(t.Path);
        built.AddRootFile(new DiskTree.FileEntry(System.IO.Path.Combine(t.Path, "top.txt"), 10, false));
        foreach (var d in new[] { "Docs", "Photos" }) built.Graft(DiskTree.Scan(System.IO.Path.Combine(t.Path, d)));
        Assert.Equal(whole.Top.Size, built.Top.Size);
        Assert.Equal(whole.FileCount, built.FileCount);
        Assert.Equal(8000, built.Find(System.IO.Path.Combine(t.Path, "Photos"))!.Size);
        Assert.Equal(3000, built.Find(System.IO.Path.Combine(t.Path, "Photos", "2024"))!.Size);
        Assert.Equal(whole.Largest().Select(f => f.Name), built.Largest().Select(f => f.Name));
        Assert.Equal(new[] { "Docs", "Photos", "top.txt" }.OrderBy(x => x), DiskTree.Contents(built.Top).Select(c => System.IO.Path.GetFileName(c.Path)).OrderBy(x => x));
        Assert.True(built.Remove(System.IO.Path.Combine(t.Path, "Photos", "a.jpg")));
        Assert.Equal(10010, built.Top.Size);
    }

    [Fact] public void LinksAreNotFollowed()
    {
        using var t = new TempDir();
        t.File("Real/x.bin", 4000);
        Directory.CreateSymbolicLink(System.IO.Path.Combine(t.Path, "Loop"), t.Path);           // would loop forever
        Directory.CreateSymbolicLink(System.IO.Path.Combine(t.Path, "Again"), System.IO.Path.Combine(t.Path, "Real"));
        var tree = DiskTree.Scan(t.Path);
        Assert.Equal(4000, tree.Top.Size);
        Assert.Equal(1, tree.FileCount);
    }

    [Fact] public void OnlineOnlyAttributes()
    {
        Assert.True(DiskTree.IsOnlineOnly(0x400000 | 0x20));
        Assert.True(DiskTree.IsOnlineOnly(0x1000));
        Assert.True(DiskTree.IsOnlineOnly(0x40000));
        Assert.False(DiskTree.IsOnlineOnly(0x20 | 0x80000));   // archive + pinned (always kept on this PC)
    }

    [Fact] public void Stops()
    {
        using var t = new TempDir();
        for (int i = 0; i < 5; i++) t.File($"d{i}/f", 1);
        var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.True(DiskTree.Scan(t.Path, cts.Token).Stopped);
    }
}

public class InPlaceCompressTests
{
    [Fact] public void FinalNames()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "x");
        string In(string name) => System.IO.Path.Combine(dir, name);
        Assert.Equal(In("IMG.jpeg"), InPlaceCompress.FinalPath(In("IMG.jpeg"), _ => true));
        Assert.Equal(In("Shot.jpg"), InPlaceCompress.FinalPath(In("Shot.png"), _ => false));
        var taken = new HashSet<string> { In("Shot.jpg"), In("Shot 2.jpg") };
        Assert.Equal(In("Shot 3.jpg"), InPlaceCompress.FinalPath(In("Shot.png"), taken.Contains));
    }

    [Fact] public void PlanFindsPhotosOnly()
    {
        using var t = new TempDir();
        t.File("a.png", 10); t.File("b.JPG", 10); t.File("notes.txt", 10); t.File("sub/c.bmp", 10); t.File("sub/deeper/d.png", 10);
        t.File(".apex-123.jpg", 10);
        var (photos, online) = InPlaceCompress.Plan(t.Path);
        Assert.Equal(new[] { "a.png", "c.bmp" }, photos.Select(p => System.IO.Path.GetFileName(p.Path)).OrderBy(x => x).ToArray());
        Assert.Equal(0, online);
    }

    [Fact] public void SwapInNewNameAndSameName()
    {
        using var t = new TempDir();
        var bin = Directory.CreateDirectory(System.IO.Path.Combine(t.Path, "bin")).FullName;
        bool Recycle(string p) { System.IO.File.Move(p, System.IO.Path.Combine(bin, System.IO.Path.GetFileName(p)), true); return true; }
        var png = t.File("Shot.png", 1000); var pngCopy = t.File(".apex-1.jpg", 300);
        var jpg = t.File("IMG.jpg", 2000); var jpgCopy = t.File(".apex-2.jpg", 900);
        var copies = new[]
        {
            new InPlaceCompress.Copy(png, pngCopy, InPlaceCompress.FinalPath(png), 1000, 300),
            new InPlaceCompress.Copy(jpg, jpgCopy, InPlaceCompress.FinalPath(jpg), 2000, 900),
        };
        Assert.Empty(InPlaceCompress.SwapIn(copies, Recycle));
        Assert.False(System.IO.File.Exists(png));
        Assert.Equal(300, new FileInfo(System.IO.Path.Combine(t.Path, "Shot.jpg")).Length);
        Assert.Equal(900, new FileInfo(jpg).Length);
        Assert.Equal(new[] { "IMG.jpg", "Shot.png" }, Directory.GetFiles(bin).Select(System.IO.Path.GetFileName).OrderBy(x => x).ToArray());
        Assert.Empty(Directory.GetDirectories(t.Path).Where(d => d != bin));      // the hidden holder is gone
    }

    [Fact] public void SwapInPutsBackWhenTheBinRefuses()
    {
        using var t = new TempDir();
        var jpg = t.File("IMG.jpg", 2000); var copy = t.File(".apex-2.jpg", 900);
        var png = t.File("Shot.png", 1000); var pngCopy = t.File(".apex-1.jpg", 300);
        var failed = InPlaceCompress.SwapIn(new[]
        {
            new InPlaceCompress.Copy(jpg, copy, jpg, 2000, 900),
            new InPlaceCompress.Copy(png, pngCopy, InPlaceCompress.FinalPath(png), 1000, 300),
        }, _ => false);
        Assert.Equal(new[] { "IMG.jpg", "Shot.png" }, failed.ToArray());
        Assert.Equal(2000, new FileInfo(jpg).Length);
        Assert.Equal(1000, new FileInfo(png).Length);
        Assert.True(System.IO.File.Exists(copy) && System.IO.File.Exists(pngCopy));
        Assert.False(System.IO.File.Exists(System.IO.Path.Combine(t.Path, "Shot.jpg")));
    }
}

public class MemoryTextTests
{
    [Fact] public void Words()
    {
        var on = new MemoryStats { CompressionOn = true, Compressed = 512L * 1024 * 1024 };
        Assert.StartsWith("On · 512", MemoryText.Compression(on));
        Assert.Equal("Off", MemoryText.Compression(new MemoryStats()));
        Assert.Equal("None", MemoryText.PageFile(new MemoryStats()));
        Assert.Contains(" of ", MemoryText.PageFile(new MemoryStats { PageFileUsed = 1L << 30, PageFileSize = 4L << 30 }));
        Assert.Equal("None", MemoryText.ReadsFromDisk(new MemoryStats { PagesInPerSec = 0.2 }));
        Assert.Contains("apps wait on the disk", MemoryText.ReadsFromDisk(new MemoryStats { PagesInPerSec = 10_000 }));
        Assert.Equal("Enable-MMAgent -MemoryCompression", MemoryText.ToggleCommand(true));
    }
}

public class LumenTests
{
    [Fact] public void NotLiveUntilCyberNetLabsOpensIt()
    {
        Environment.SetEnvironmentVariable("APEX_LUMEN_BASE", null);
        Assert.False(Lumen.IsLive);
        Environment.SetEnvironmentVariable("APEX_LUMEN_BASE", "http://127.0.0.1:9/v1");
        Assert.True(Lumen.IsLive);
        Environment.SetEnvironmentVariable("APEX_LUMEN_BASE", null);
    }
    [Fact] public void Words()
    {
        Assert.Equal("Monthly Plan", Lumen.Title(Lumen.Access.Plan));
        Assert.Contains("didn’t accept that key", Lumen.Problem(401));
        Assert.Contains("run out", Lumen.Problem(402));
        Assert.Contains("(418)", Lumen.Problem(418));
    }
}

public class WindowsTweaksTests
{
    [Fact] public void EveryTweakIsInAKnownGroupAndHasAUniqueId()
    {
        Assert.Equal(WindowsTweaks.All.Length, WindowsTweaks.All.Select(t => t.Id).Distinct().Count());
        Assert.All(WindowsTweaks.All, t => Assert.Contains(t.Group, WindowsTweaks.Groups));
        Assert.All(WindowsTweaks.All, t => Assert.DoesNotContain(t.Values, v => v.Key.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase)));   // under HKCU only
    }

    [Fact] public void OnOnlyWhenEveryValueMatches()
    {
        var accel = WindowsTweaks.All.First(t => t.Id == "noaccel");
        var reg = new Dictionary<string, string?> { ["MouseSpeed"] = "0", ["MouseThreshold1"] = "0", ["MouseThreshold2"] = "6" };
        Assert.False(WindowsTweaks.IsOn(accel, v => reg.GetValueOrDefault(v.Name)));
        reg["MouseThreshold2"] = "0";
        Assert.True(WindowsTweaks.IsOn(accel, v => reg.GetValueOrDefault(v.Name)));
    }

    [Fact] public void TurningOffPutsBackWhatWasThere()
    {
        var v = WindowsTweaks.All.First(t => t.Id == "ext").Values[0];
        Assert.Null(WindowsTweaks.Previous(v, null));
        Assert.Equal("1", WindowsTweaks.Previous(v, new Dictionary<string, string?> { [WindowsTweaks.Id(v)] = "1" }));
        Assert.Null(WindowsTweaks.Previous(v, new Dictionary<string, string?> { [WindowsTweaks.Id(v)] = null }));
    }
}

public class DetectionTests
{
    static readonly DateTime T0 = new(2026, 10, 28, 12, 0, 0);
    static DetectionSample S(int seconds, double cpu = 0, PressureLevel p = PressureLevel.Normal, long free = 100_000_000_000, long pageFile = 0, long mem = 0) => new()
    {
        Date = T0.AddSeconds(seconds), Pressure = p, DiskAvailable = free, DiskCapacity = 500_000_000_000, PageFileUsed = pageFile,
        Apps = new[] { new AppLoad("app:chrome", "Chrome", cpu, Memory: mem, Pids: new[] { 42 }) },
    };

    [Fact] public void AppCpuAlertsAfterThreeMinutesOnce()
    {
        var e = new DetectionEngine();
        var fired = new List<DetectionEvent>();
        for (int t = 0; t <= 200; t += 2) fired.AddRange(e.Evaluate(S(t, cpu: 95)));
        var alert = Assert.Single(fired);
        Assert.Equal(Rule.AppCpu, alert.Rule);
        Assert.True(alert.Notify);
        Assert.Equal("Chrome is using a lot of CPU", alert.Title);
        Assert.Contains(alert.Evidence, x => x.Label == "Lasted");
    }

    [Fact] public void AShortBurnIsASpikeNeverNotified()
    {
        var e = new DetectionEngine();
        var all = new List<DetectionEvent>();
        for (int t = 0; t <= 60; t += 2) all.AddRange(e.Evaluate(S(t, cpu: 95)));
        for (int t = 62; t <= 100; t += 2) all.AddRange(e.Evaluate(S(t, cpu: 5)));
        var spike = Assert.Single(all);
        Assert.Equal(EventKind.Spike, spike.Kind);
        Assert.False(spike.Notify);
    }

    [Fact] public void BriefDipsDontBreakTheEpisode()
    {
        var e = new DetectionEngine();
        var all = new List<DetectionEvent>();
        for (int t = 0; t <= 200; t += 2) all.AddRange(e.Evaluate(S(t, cpu: t == 100 ? 10 : 95)));
        Assert.Contains(all, x => x.Kind == EventKind.Sustained && x.Rule == Rule.AppCpu);
    }

    [Fact] public void CriticalMemoryAfterThirtySeconds()
    {
        var e = new DetectionEngine();
        var all = new List<DetectionEvent>();
        for (int t = 0; t <= 40; t += 2) all.AddRange(e.Evaluate(S(t, p: PressureLevel.Critical)));
        var m = Assert.Single(all, x => x.Key == "memory:critical");
        Assert.Equal(Severity.Critical, m.Severity);
        Assert.Equal("Your PC is running out of memory", m.Title);
    }

    [Fact] public void LowDiskOncePerDay()
    {
        var e = new DetectionEngine();
        Assert.Single(e.Evaluate(S(0, free: 5_000_000_000)), x => x.Key == "disk:low");
        Assert.DoesNotContain(e.Evaluate(S(60, free: 5_000_000_000)), x => x.Key == "disk:low");
    }

    [Fact] public void SteadyGrowthIsFoundButASawtoothIsNot()
    {
        var steady = Enumerable.Range(0, 40).Select(i => (T0.AddMinutes(i), 400_000_000d + i * 30_000_000d)).ToList();
        Assert.NotNull(DetectionEngine.GrowthOf(steady));
        var saw = Enumerable.Range(0, 40).Select(i => (T0.AddMinutes(i), 400_000_000d + (i % 5) * 300_000_000d)).ToList();
        Assert.Null(DetectionEngine.GrowthOf(saw));
    }
}

public class AppHistoryTests
{
    static readonly DateTime T = new(2026, 10, 28, 10, 0, 0);

    [Fact] public void CountsCoreSecondsAndHelpersTogether()
    {
        var h = new AppHistory();
        var key = AppHistory.KeyFor(@"C:\Apps\Edge\msedge.exe", "msedge");
        for (int i = 0; i <= 10; i++)
            h.Record(T.AddSeconds(i), new[]
            {
                new AppHistory.Use(key, "Microsoft Edge", @"C:\Apps\Edge\msedge.exe", 0.5, 1000, 0, 100_000_000),
                new AppHistory.Use(key, "Microsoft Edge", @"C:\Apps\Edge\msedge.exe", 0.5, 0, 0, 50_000_000),     // a helper
            });
        var (k, u) = Assert.Single(h.Totals(1, T.AddSeconds(10)));
        Assert.Equal(key, k);
        Assert.Equal(10, u.CpuSeconds, 3);              // 1 core for 10 s
        Assert.Equal(10_000, u.DiskBytes, 3);
        Assert.Equal(150_000_000, u.PeakMemory);
    }

    [Fact] public void GapsAreNotCountedAndOldDaysGo()
    {
        var h = new AppHistory();
        var use = new[] { new AppHistory.Use("k", "A", "", 1, 0, 0, 1) };
        h.Record(T, use); h.Record(T.AddMinutes(10), use);          // a sleep gap
        Assert.Empty(h.Totals(1, T.AddMinutes(10)));
        h.Record(T.AddMinutes(10).AddSeconds(2), use);
        Assert.Equal(2, h.Totals(1, T.AddMinutes(11)).Single().Usage.CpuSeconds, 3);
        h.Prune(T.AddDays(40));
        Assert.Empty(h.Days);
    }

    [Fact] public void RoundTripsAsJson()
    {
        var h = new AppHistory();
        var use = new[] { new AppHistory.Use("k", "A", "", 1, 0, 0, 1) };
        h.Record(T, use); h.Record(T.AddSeconds(5), use);
        var back = AppHistory.FromJson(h.ToJson());
        Assert.Equal(5, back.Totals(1, T).Single().Usage.CpuSeconds, 3);
        Assert.Empty(AppHistory.FromJson("not json").Days);
    }
}
