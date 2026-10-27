namespace Apex.Core;

/// Plain words for Performance › Memory (compression, page file, reads from
/// disk). Pure: tested.
public static class MemoryText
{
    public static string Compression(MemoryStats m) => m.CompressionOn
        ? (m.Compressed > 0 ? $"On · {Fmt.Memory(m.Compressed)} compressed" : "On · nothing compressed now")
        : "Off";

    public static string CompressionHelp(MemoryStats m) => m.CompressionOn
        ? "When memory runs short, Windows squeezes what apps aren't using right now instead of writing it to disk. The figure is the memory the compressed data takes; reading it back is far faster than from the page file."
        : "Windows writes memory apps aren't using to the page file on disk instead of compressing it. Turning compression on (it's on by default on Windows 10 and 11) keeps more in memory; it takes effect after a restart.";

    public static string PageFile(MemoryStats m) => m.PageFileSize <= 0
        ? "None"
        : $"{Fmt.Memory(m.PageFileUsed)} of {Fmt.Memory(m.PageFileSize)}";

    /// Reads from the page file: a few now and then are normal; many every
    /// second means apps are waiting on the disk for their memory.
    public static string ReadsFromDisk(MemoryStats m, int pageSize = 4096)
    {
        double bytes = m.PagesInPerSec * pageSize;
        if (m.PagesInPerSec < 1) return "None";
        return $"{Fmt.Rate(bytes)}" + (bytes >= 20_000_000 ? " · a lot: apps wait on the disk" : "");
    }

    /// PowerShell to turn compression on or off (needs administrator; applies after a restart).
    public static string ToggleCommand(bool on) => (on ? "Enable" : "Disable") + "-MMAgent -MemoryCompression";
}
