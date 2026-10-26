using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Apex.Core;

namespace Apex.Services;

/// Disk › Free Up Space, the Windows counterpart of the Mac's Clean: the
/// leftovers Windows and apps make (temporary files, caches, crash reports,
/// update downloads), each explained, ticked by default only when it's always
/// safe. Locked files are skipped. The Windows-wide ones need administrator
/// rights: Apex runs itself elevated for just those (`--clean=…`).
internal static class StorageClean
{
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string WinDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static List<CleanCategory> Categories() => new()
    {
        new("temp", "Temporary files", "Files apps left in your temp folder (older than a day).", new[] { Path.GetTempPath() }, TimeSpan.FromDays(1)),
        new("thumbs", "Thumbnail cache", "Picture previews File Explorer rebuilds as needed.", new[] { Path.Combine(Local, @"Microsoft\Windows\Explorer") }, TimeSpan.Zero, Pattern: "thumbcache_*.db"),
        new("crash", "Crash dumps and error reports", "Saved after apps crashed; only useful to developers.", new[] { Path.Combine(Local, "CrashDumps"), Path.Combine(Local, @"Microsoft\Windows\WER") }, TimeSpan.Zero),
        new("browser", "Browser caches", "Saved copies of web pages; they reload as you browse. Skipped while the browser is open.",
            new[] { Path.Combine(Local, @"Microsoft\Edge\User Data\Default\Cache\Cache_Data"), Path.Combine(Local, @"Google\Chrome\User Data\Default\Cache\Cache_Data") },
            TimeSpan.Zero, SkipWhileRunning: new[] { "msedge", "chrome" }),
        new("shaders", "Graphics shader caches", "Built by games and the graphics driver. Games rebuild them, so the first launch afterwards can stutter a little (older than a week only).",
            new[] { Path.Combine(Local, "D3DSCache"), Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"), Path.Combine(Local, @"AMD\DxCache") }, TimeSpan.FromDays(7)),
        new("wintemp", "Windows temporary files", "Windows’ own temp folder (older than a day).", new[] { Path.Combine(WinDir, "Temp") }, TimeSpan.FromDays(1), NeedsAdmin: true),
        new("update", "Windows Update downloads", "Updates Windows has already installed (older than 10 days). Windows downloads again if it ever needs them.",
            new[] { Path.Combine(WinDir, @"SoftwareDistribution\Download") }, TimeSpan.FromDays(10), NeedsAdmin: true),
        new("delivery", "Delivery Optimization cache", "Update pieces Windows shares with other PCs.",
            new[] { Path.Combine(WinDir, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") }, TimeSpan.FromDays(3), NeedsAdmin: true),
        new("werall", "Windows error reports", "Reports Windows kept about problems on this PC.",
            new[] { Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue") }, TimeSpan.Zero, NeedsAdmin: true),
    };

    public static bool Blocked(CleanCategory c) => c.SkipWhileRunning?.Any(n => Process.GetProcessesByName(n).Length > 0) == true;

    // ---- Recycle Bin ------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct SHQUERYRBINFO { public int cbSize; public long i64Size, i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    public static (long Size, long Items) RecycleBin()
    {
        var i = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref i) == 0 ? (i.i64Size, i.i64NumItems) : (0, 0);
    }

    public static bool EmptyRecycleBin() => SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4) is 0 or unchecked((int)0x8000FFFF);   // no confirm, progress or sound

    // ---- Moving to the Recycle Bin (Analyze) --------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT { public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// Into the Recycle Bin, so it can be restored.
    public static bool ToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT { wFunc = 3 /* FO_DELETE */, pFrom = path + "\0\0", fFlags = 0x40 | 0x10 | 0x4 /* ALLOWUNDO | NOCONFIRMATION | SILENT */ };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    // ---- Cleaning (here, or elevated) -----------------------------------------------

    public sealed record Outcome(long Freed, int Removed, int Skipped);

    public static Outcome Clean(IEnumerable<CleanCategory> categories)
    {
        long freed = 0; int removed = 0, skipped = 0;
        foreach (var c in categories)
        {
            if (Blocked(c)) continue;
            var r = Cleaner.Clean(c, Cleaner.Scan(c, DateTime.Now));
            freed += r.Freed; removed += r.Removed; skipped += r.Skipped;
        }
        return new Outcome(freed, removed, skipped);
    }

    static string ResultFile => Path.Combine(Path.GetTempPath(), "apex-clean-result.json");

    /// `Apex.exe --clean=wintemp,update`: run elevated by Apex for the Windows-wide categories.
    public static int RunElevatedPart(string ids)
    {
        var wanted = ids.Split(',');
        var o = Clean(Categories().Where(c => c.NeedsAdmin && wanted.Contains(c.Id)));
        try { File.WriteAllText(ResultFile, JsonSerializer.Serialize(o)); } catch { }
        return 0;
    }

    /// Asks Windows for permission and cleans the admin categories; null if the user said no.
    public static async Task<Outcome?> CleanAsAdmin(IEnumerable<CleanCategory> categories)
    {
        var ids = string.Join(',', categories.Select(c => c.Id));
        if (ids.Length == 0) return new Outcome(0, 0, 0);
        try { File.Delete(ResultFile); } catch { }
        try
        {
            var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--clean={ids}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) return null;
            await p.WaitForExitAsync();
            // The elevated copy runs as the same user, so it writes to the same temp folder.
            return JsonSerializer.Deserialize<Outcome>(await File.ReadAllTextAsync(ResultFile));
        }
        catch { return null; }
    }
}
