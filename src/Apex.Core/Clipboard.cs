namespace Apex.Core;

public enum ClipKind { Text, Files, Picture }

/// One thing that was copied.
public sealed class ClipItem
{
    public ClipKind Kind { get; init; }
    public string Text { get; init; } = "";
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
    /// A picture as Windows keeps it on the clipboard (a DIB), to copy back exactly.
    public byte[]? Dib { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public DateTime Copied { get; init; } = DateTime.Now;

    /// One line for the list.
    public string Title => Kind switch
    {
        ClipKind.Files => Files.Count == 1 ? FileName(Files[0]) : $"{Files.Count} files",
        ClipKind.Picture => $"Picture · {Width} × {Height}",
        _ => Text.Length > 200 ? Text[..200].ReplaceLineEndings(" ") + "…" : Text.ReplaceLineEndings(" "),
    };

    /// Same content (copying it again moves it to the top instead of adding it twice).
    public string Key => Kind switch
    {
        ClipKind.Files => "f:" + string.Join("|", Files),
        ClipKind.Picture => $"p:{Width}x{Height}:{Dib?.Length}:{(Dib == null ? 0 : Hash(Dib))}",
        _ => "t:" + Text,
    };

    /// The last part of a Windows path (here rather than Path.GetFileName, so tests behave the same on any OS).
    static string FileName(string path) => path[(path.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];

    static int Hash(byte[] b)
    {
        unchecked
        {
            int h = 17;
            for (int i = 0; i < b.Length; i += Math.Max(1, b.Length / 4096)) h = h * 31 + b[i];
            return h;
        }
    }

    public static ClipItem FromText(string text) => new() { Kind = ClipKind.Text, Text = text };
    public static ClipItem FromFiles(IReadOnlyList<string> files) => new() { Kind = ClipKind.Files, Files = files };
    public static ClipItem FromPicture(byte[] dib, int width, int height) => new() { Kind = ClipKind.Picture, Dib = dib, Width = width, Height = height };

    public bool Matches(string search) => search.Length == 0
        || (Kind == ClipKind.Text && Text.Contains(search, StringComparison.OrdinalIgnoreCase))
        || (Kind == ClipKind.Files && Files.Any(f => f.Contains(search, StringComparison.OrdinalIgnoreCase)))
        || (Kind == ClipKind.Picture && "picture".Contains(search, StringComparison.OrdinalIgnoreCase));
}

public static class ClipList
{
    /// Newest first; the same content moves to the top; at most `max` kept. Pure: tested.
    public static void Insert(List<ClipItem> items, ClipItem item, int max)
    {
        items.RemoveAll(i => i.Key == item.Key);
        items.Insert(0, item);
        if (items.Count > max) items.RemoveRange(max, items.Count - max);
    }
}
