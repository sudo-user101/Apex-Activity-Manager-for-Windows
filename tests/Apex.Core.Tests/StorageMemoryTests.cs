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
