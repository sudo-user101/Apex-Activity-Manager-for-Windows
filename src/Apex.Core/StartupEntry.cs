namespace Apex.Core;

/// Something Windows starts by itself when you sign in.
public sealed record StartupEntry
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Path { get; init; } = "";
    public string Publisher { get; init; } = "";
    /// Where it's registered: "Registry (you)", "Registry (all users)", "Startup folder"…
    public string Source { get; init; } = "";
    public bool Enabled { get; init; } = true;
    /// Changing it needs administrator rights (all-users entries).
    public bool NeedsAdmin { get; init; }
    /// Internal: the registry hive / key or folder it lives in.
    public string Location { get; init; } = "";
}

/// Reading what Windows stores about startup items. Pure: tested.
public static class StartupParsing
{
    /// The program in a startup command: `"C:\A B\app.exe" --x` → `C:\A B\app.exe`;
    /// `C:\Tools\app.exe -silent` → `C:\Tools\app.exe`.
    public static string ExecutablePath(string command)
    {
        var c = command.Trim();
        if (c.Length == 0) return "";
        if (c[0] == '"')
        {
            int end = c.IndexOf('"', 1);
            return end > 1 ? c.Substring(1, end - 1) : c.Trim('"');
        }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return c[..(exe + 4)];
        int space = c.IndexOf(' ');
        return space > 0 ? c[..space] : c;
    }

    /// Windows keeps on/off for startup items in "StartupApproved" as a
    /// 12-byte value: first byte even (02, 06) = on, odd (03, 07) = off.
    /// No value at all means on.
    public static bool IsEnabled(byte[]? approved) => approved is not { Length: > 0 } || approved[0] % 2 == 0;

    /// The value to write for on / off (off carries the time it was switched off).
    public static byte[] ApprovedValue(bool enabled, DateTime nowUtc)
    {
        var v = new byte[12];
        v[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(nowUtc.ToFileTimeUtc()).CopyTo(v, 4);
        return v;
    }
}
