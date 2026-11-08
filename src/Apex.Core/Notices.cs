using System.Text.Json;
using System.Text.RegularExpressions;

namespace Apex.Core;

/// Telling people a newer Apex is out. Pure parts; the check is in the app.
public static class AppUpdate
{
    public const string Page = "https://github.com/sudo-user101/Apex-Activity-Manager-for-Windows/releases/latest";
    public const string Newest = "https://api.github.com/repos/sudo-user101/Apex-Activity-Manager-for-Windows/releases/latest";

    /// The version in a GitHub release answer ("v1.7.0" → "1.7.0"), or null.
    public static string? VersionFrom(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            if (!d.RootElement.TryGetProperty("tag_name", out var t) || t.ValueKind != JsonValueKind.String) return null;
            var v = t.GetString()!.TrimStart('v');
            return Parts(v) == null ? null : v;
        }
        catch (JsonException) { return null; }
    }

    /// By number, so 1.10.0 is newer than 1.9.0.
    public static bool IsNewer(string a, string b)
    {
        var x = Parts(a); var y = Parts(b);
        if (x == null || y == null) return false;
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int l = i < x.Length ? x[i] : 0, r = i < y.Length ? y[i] : 0;
            if (l != r) return l > r;
        }
        return false;
    }

    static int[]? Parts(string v)
    {
        var p = v.Split('.');
        var o = new int[p.Length];
        for (int i = 0; i < p.Length; i++) if (!int.TryParse(p[i], out o[i]) || o[i] < 0) return null;
        return p.Length > 0 ? o : null;
    }
}

/// After a crash, offering to report it. Apex writes its own crash note
/// (App.xaml.cs); on the next launch it offers a GitHub issue the person reads
/// before sending. Nothing is sent otherwise, and the text has no paths or names.
public static class CrashReport
{
    public const string NewIssue = "https://github.com/sudo-user101/Apex-Activity-Manager-for-Windows/issues/new";

    /// The newest note in the log: its time and text. Notes are "time\nexception\n\n".
    public static (DateTime When, string Text)? Latest(string log)
    {
        (DateTime, string)? last = null;
        foreach (var block in log.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var nl = block.IndexOf('\n');
            if (nl < 0) continue;
            if (DateTime.TryParse(block[..nl], null, System.Globalization.DateTimeStyles.RoundtripKind, out var when))
                last = (when, block[(nl + 1)..].Trim());
        }
        return last;
    }

    static readonly Regex DrivePath = new(@"[A-Za-z]:\\[^\s""'<>|]*", RegexOptions.Compiled);
    static readonly Regex SharePath = new(@"\\\\[^\s""'<>|]+", RegexOptions.Compiled);
    static readonly Regex SourceRef = new(@" in [^\r\n]*:line \d+", RegexOptions.Compiled);

    /// Only what finds the bug: the error and where in Apex. No paths, no user name.
    public static string Clean(string text, string userName)
    {
        var s = SourceRef.Replace(text, "");
        s = DrivePath.Replace(s, "<path>");
        s = SharePath.Replace(s, "<path>");
        if (userName.Length > 1) s = Regex.Replace(s, Regex.Escape(userName), "<user>", RegexOptions.IgnoreCase);
        var lines = s.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(16);
        return string.Join("\n", lines);
    }

    public static string IssueUrl(string version, string windows, string cleaned)
    {
        var first = cleaned.Split('\n')[0];
        var title = "Crash in " + version + ": " + (first.Length > 80 ? first[..80] : first);
        var body = "**What were you doing when Apex quit?**\n\n\n---\nApex " + version + "\n" + windows + "\n```\n" + cleaned + "\n```";
        return NewIssue + "?title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body);
    }
}
