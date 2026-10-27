namespace Apex.Core;

/// Analyze › Compress in place (e.g. the Screenshots folder), as on the Mac:
/// smaller copies are written next to each photo first; only when you say
/// Done does each copy take its original's place, and the original goes to
/// the Recycle Bin. A photo is never left missing. The encoding itself is
/// Windows' (in the app); the planning and the swapping are here, tested.
public static class InPlaceCompress
{
    public sealed record Photo(string Path, long Size);
    /// A smaller copy written next to its original, not swapped in yet.
    public sealed record Copy(string Original, string CopyPath, string FinalPath, long Before, long After);

    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".bmp", ".tif", ".tiff" };
    public const string TempPrefix = ".apex-";

    /// JPEG photos counted by `Plan` but left as they are (see `OutputExtension`).
    public static bool IsJpeg(string path) => Path.GetExtension(path) is var e && (e.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || e.Equals(".jpeg", StringComparison.OrdinalIgnoreCase));

    /// Photos in `folder` (and one level of subfolders), online-only ones left
    /// out (compressing would download them) and counted. At most 10,000.
    public static (List<Photo> Photos, int OnlineOnly) Plan(string folder)
    {
        var photos = new List<Photo>();
        int online = 0;
        void Look(string dir, int depth)
        {
            try
            {
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
                {
                    if (!Extensions.Contains(f.Extension) || f.Name.StartsWith(TempPrefix)) continue;
                    if (DiskTree.IsOnlineOnly((int)f.Attributes)) { online++; continue; }
                    photos.Add(new Photo(f.FullName, f.Length));
                    if (photos.Count >= 10_000) return;
                }
                if (depth < 1)
                    foreach (var d in new DirectoryInfo(dir).EnumerateDirectories())
                        if (!d.Attributes.HasFlag(FileAttributes.ReparsePoint) || d.LinkTarget == null) Look(d.FullName, depth + 1);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        Look(folder, 0);
        return (photos, online);
    }

    /// The format a photo becomes: JPEG. Screenshots and scans (PNG, BMP,
    /// TIFF) shrink most. JPEGs are left as they are: re-saving one would lose
    /// quality and its details (date taken, place), and Windows doesn't always
    /// have the HEIF codec the Mac uses instead.
    public const string OutputExtension = ".jpg";

    /// Where the smaller copy ends up: the same name, with .jpg when the format
    /// changes, never over another file ("Shot 2.jpg"). Pure: tested.
    public static string FinalPath(string original, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var ext = Path.GetExtension(original);
        if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return original;
        var dir = Path.GetDirectoryName(original) ?? "";
        var stem = Path.GetFileNameWithoutExtension(original);
        var candidate = Path.Combine(dir, stem + OutputExtension);
        for (int n = 2; exists(candidate); n++) candidate = Path.Combine(dir, $"{stem} {n}{OutputExtension}");
        return candidate;
    }

    /// A hidden name next to the original for its smaller copy.
    public static string TempPath(string original) =>
        Path.Combine(Path.GetDirectoryName(original) ?? "", TempPrefix + Guid.NewGuid().ToString("N") + OutputExtension);

    /// Done: each copy takes its original's place; the original goes to the
    /// Recycle Bin (`recycle`). A new name is put in place before the original
    /// goes; for the same name the original is set aside (in a hidden folder,
    /// keeping its name, so it's recognisable in the bin), the copy moved in,
    /// then the original recycled; any failure puts everything back. Returns
    /// the names left as they were.
    public static List<string> SwapIn(IEnumerable<Copy> copies, Func<string, bool> recycle)
    {
        var failed = new List<string>();
        foreach (var c in copies)
        {
            try
            {
                if (!string.Equals(c.FinalPath, c.Original, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(c.CopyPath, c.FinalPath);                       // the new file first…
                    if (!recycle(c.Original)) { File.Move(c.FinalPath, c.CopyPath); throw new IOException("not recycled"); }   // …then the old one
                }
                else
                {
                    // Same name: keep the original safe beside it, put the copy in, then recycle the original.
                    var holder = Path.Combine(Path.GetDirectoryName(c.Original) ?? "", TempPrefix + "originals-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(holder);
                    var aside = Path.Combine(holder, Path.GetFileName(c.Original));
                    try
                    {
                        File.Move(c.Original, aside);
                        try { File.Move(c.CopyPath, c.FinalPath); }
                        catch { File.Move(aside, c.Original); throw; }
                        if (!recycle(aside)) { File.Move(c.FinalPath, c.CopyPath); File.Move(aside, c.Original); throw new IOException("not recycled"); }
                    }
                    finally { try { Directory.Delete(holder); } catch { } }
                }
            }
            catch { failed.Add(Path.GetFileName(c.Original)); }
        }
        return failed;
    }

    /// Cancel: the copies are removed, the originals untouched.
    public static void Discard(IEnumerable<Copy> copies)
    {
        foreach (var c in copies) try { File.Delete(c.CopyPath); } catch { }
    }
}
