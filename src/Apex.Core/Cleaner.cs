namespace Apex.Core;

/// One kind of leftover Apex can clean, with where it lives and how old a
/// file must be before it counts (so nothing an app is using right now goes).
public sealed record CleanCategory(string Id, string Title, string Detail, IReadOnlyList<string> Roots, TimeSpan MinAge,
    bool NeedsAdmin = false, IReadOnlyList<string>? SkipWhileRunning = null, string Pattern = "*");

public sealed record CleanItem(string Path, long Size);

/// Finds and removes leftovers, as the Mac's Disk › Clean does. Only files
/// older than the category's age; never follows links or junctions; locked
/// files are skipped and counted. Pure apart from the file system: tested.
public static class Cleaner
{
    public static List<CleanItem> Scan(CleanCategory c, DateTime now)
    {
        var items = new List<CleanItem>();
        foreach (var root in c.Roots)
        {
            if (!Directory.Exists(root)) continue;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, c.Pattern))
                    {
                        try
                        {
                            var info = new FileInfo(f);
                            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                            if (now - info.LastWriteTime < c.MinAge) continue;
                            items.Add(new CleanItem(f, info.Length));
                        }
                        catch { }
                    }
                    if (c.Pattern == "*")
                        foreach (var d in Directory.EnumerateDirectories(dir))
                            if (!new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push(d);
                }
                catch { }       // a folder we can't read: skip it
            }
        }
        return items;
    }

    public sealed record Result(long Freed, int Removed, int Skipped);

    /// `path` is inside `root` (not merely starting with the same letters: C:\\Temp2 isn't in C:\\Temp).
    public static bool Inside(string path, string root)
    {
        var r = root.TrimEnd('\\', '/');
        return path.Length > r.Length && path.StartsWith(r, StringComparison.OrdinalIgnoreCase) && path[r.Length] is '\\' or '/';
    }

    /// Deletes the files (skipping any that are in use), then empty folders
    /// left under the category's roots (never the roots themselves).
    public static Result Clean(CleanCategory c, IEnumerable<CleanItem> items)
    {
        long freed = 0; int removed = 0, skipped = 0;
        foreach (var it in items)
        {
            if (!c.Roots.Any(r => Inside(it.Path, r))) { skipped++; continue; }   // never outside its roots
            try { File.Delete(it.Path); freed += it.Size; removed++; }
            catch { skipped++; }
        }
        foreach (var root in c.Roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                    try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
            catch { }
        }
        return new Result(freed, removed, skipped);
    }
}
