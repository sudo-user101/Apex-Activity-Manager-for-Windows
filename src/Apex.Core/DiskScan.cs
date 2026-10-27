using System.IO.Enumeration;

namespace Apex.Core;

/// Storage › Analyze: one walk of a drive or folder, kept as folder totals
/// (so moving between folders is instant) and the largest files. Online-only
/// files (OneDrive and other cloud placeholders) are counted from their
/// attributes, so nothing is downloaded. Folders that are links elsewhere
/// (junctions, symbolic links) are skipped: nothing is counted twice and the
/// walk can't loop.
public sealed class DiskTree
{
    public sealed class Folder
    {
        public string Path { get; init; } = "";
        public long Size;               // everything under it, online-only included
        public long OnlineOnly;         // of which only in the cloud
        public long Files;
        public readonly List<Folder> Children = new();
        /// Files directly in this folder (sizes), for listing it.
        public readonly List<FileEntry> Own = new();
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
    }

    public sealed record FileEntry(string Path, long Size, bool OnlineOnly)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }

    public string Root { get; }
    public Folder Top { get; }
    public long FileCount { get; private set; }
    /// Folders Windows wouldn't let Apex read (other users', the system's).
    public int Unreadable { get; private set; }
    public bool Stopped { get; private set; }
    readonly Dictionary<string, Folder> folders = new(StringComparer.OrdinalIgnoreCase);
    readonly PriorityQueue<FileEntry, long> largest = new();
    public const int LargestKept = 100;
    /// Files kept per folder for listing it (the biggest); the rest are summed.
    public const int OwnKept = 200;

    public DiskTree(string root)
    {
        Root = root;
        Top = new Folder { Path = root };
        folders[root] = Top;
    }

    public Folder? Find(string path) => folders.GetValueOrDefault(path);

    /// The biggest files anywhere under the root, biggest first.
    public List<FileEntry> Largest() => largest.UnorderedItems.Select(x => x.Element).OrderByDescending(f => f.Size).ToList();

    // Windows file attributes that mean "the contents are in the cloud".
    public const int Offline = 0x1000, RecallOnOpen = 0x40000, RecallOnDataAccess = 0x400000;
    public static bool IsOnlineOnly(int attributes) => (attributes & (Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    /// Walks `root`, a level of folders at a time, the folders of each level
    /// read in parallel (each folder by one thread, so its own totals need no
    /// locks). `progress` gets the files counted so far, now and then.
    public static DiskTree Scan(string root, CancellationToken token = default, Action<long>? progress = null)
    {
        var t = new DiskTree(root);
        var opts = new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        var levels = new List<List<Folder>>();
        var level = new List<Folder> { t.Top };
        long files = 0, lastReport = 0;
        int unreadable = 0;
        var heaps = new System.Collections.Concurrent.ConcurrentBag<PriorityQueue<FileEntry, long>>();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) };
        while (level.Count > 0)
        {
            if (token.IsCancellationRequested) { t.Stopped = true; break; }
            levels.Add(level);
            var next = new System.Collections.Concurrent.ConcurrentBag<Folder>();
            Parallel.ForEach(level, parallel,
                () => new PriorityQueue<FileEntry, long>(),
                (f, state, heap) =>
                {
                    if (token.IsCancellationRequested) { state.Stop(); return heap; }
                    try
                    {
                        var e = new FileSystemEnumerable<(string Path, long Size, int Attr, bool Dir)>(f.Path,
                            (ref FileSystemEntry x) => (x.ToFullPath(), x.IsDirectory ? 0 : x.Length, (int)x.Attributes, x.IsDirectory), opts);
                        foreach (var x in e)
                        {
                            if (x.Dir)
                            {
                                // A link to somewhere else (junction, symbolic link): not followed. Cloud
                                // folders are reparse points too, but not links, so they are walked.
                                if ((x.Attr & (int)FileAttributes.ReparsePoint) != 0 && IsLink(x.Path)) continue;
                                var child = new Folder { Path = x.Path };
                                f.Children.Add(child);
                                next.Add(child);
                            }
                            else
                            {
                                var fe = new FileEntry(x.Path, x.Size, IsOnlineOnly(x.Attr));
                                AddOwn(f, fe);
                                if (heap.Count < LargestKept) heap.Enqueue(fe, fe.Size);
                                else if (fe.Size > heap.Peek().Size) { heap.Dequeue(); heap.Enqueue(fe, fe.Size); }
                                long n = Interlocked.Increment(ref files);
                                if (progress != null && n - Interlocked.Read(ref lastReport) >= 20_000) { Interlocked.Exchange(ref lastReport, n); progress(n); }
                            }
                        }
                    }
                    catch (UnauthorizedAccessException) { Interlocked.Increment(ref unreadable); }
                    catch (IOException) { Interlocked.Increment(ref unreadable); }
                    return heap;
                },
                heap => heaps.Add(heap));
            level = next.ToList();
        }
        t.FileCount = files;
        t.Unreadable = unreadable;
        foreach (var heap in heaps)
            foreach (var (e, size) in heap.UnorderedItems)
            {
                if (t.largest.Count < LargestKept) t.largest.Enqueue(e, size);
                else if (size > t.largest.Peek().Size) { t.largest.Dequeue(); t.largest.Enqueue(e, size); }
            }
        // Totals from the deepest level up; the index for Find.
        for (int i = levels.Count - 1; i >= 0; i--)
            foreach (var f in levels[i])
            {
                t.folders[f.Path] = f;
                foreach (var c in f.Children) { f.Size += c.Size; f.OnlineOnly += c.OnlineOnly; f.Files += c.Files; }
                f.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
                f.Own.Sort((a, b) => b.Size.CompareTo(a.Size));
            }
        progress?.Invoke(t.FileCount);
        return t;
    }

    /// One file directly in `f` (called only by the thread reading `f`).
    static void AddOwn(Folder f, FileEntry e)
    {
        f.Size += e.Size; f.Files++;
        if (e.OnlineOnly) f.OnlineOnly += e.Size;
        f.Own.Add(e);
        if (f.Own.Count > OwnKept * 2) { f.Own.Sort((a, b) => b.Size.CompareTo(a.Size)); f.Own.RemoveRange(OwnKept, f.Own.Count - OwnKept); }
    }

    static bool IsLink(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget != null; }
        catch { return true; }     // can't tell: safer not to follow
    }

    /// After something here went to the Recycle Bin: it's taken out, and the
    /// folders above it shrink by its size. False when it isn't in the tree.
    public bool Remove(string path)
    {
        try { path = Path.GetFullPath(path); } catch { return false; }
        var parent = Find(Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? "");
        if (parent == null) return false;
        long size, online, files;
        if (Find(path) is { } folder && parent.Children.Remove(folder))
        {
            (size, online, files) = (folder.Size, folder.OnlineOnly, folder.Files);
            foreach (var key in folders.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) || k.StartsWith(path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
                folders.Remove(key);
        }
        else if (parent.Own.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } file)
        {
            parent.Own.Remove(file);
            (size, online, files) = (file.Size, file.OnlineOnly ? file.Size : 0, 1);
        }
        else return false;
        for (var f = parent; f != null; f = f == Top ? null : Find(Path.GetDirectoryName(f.Path.TrimEnd('\\', '/')) ?? ""))
        {
            f.Size -= size; f.OnlineOnly -= online; f.Files -= files;
            if (f == Top) break;
        }
        FileCount -= files;
        var under = path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var keep = largest.UnorderedItems.Select(x => x.Element)
            .Where(e => !e.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && !e.Path.StartsWith(under, StringComparison.OrdinalIgnoreCase)).ToList();
        largest.Clear();
        foreach (var e in keep) largest.Enqueue(e, e.Size);
        return true;
    }

    /// What's in a folder, biggest first: its subfolders and its own files together.
    public static List<(string Path, long Size, long OnlineOnly, bool IsFolder)> Contents(Folder f) =>
        f.Children.Select(c => (c.Path, c.Size, c.OnlineOnly, true))
            .Concat(f.Own.Select(o => (o.Path, o.Size, o.OnlineOnly ? o.Size : 0L, false)))
            .OrderByDescending(x => x.Item2).ToList();
}
