using System.Text.Json.Nodes;

namespace Apex.Core;

/// VirusTotal with the person's own free key, as on the Mac. A lookup sends
/// only the file's SHA-256; the file itself is sent only after the person
/// agrees (VirusTotal shares uploads with security companies and paying
/// customers). The free key must not be used in commercial products. Pure parts.
public static class VirusTotal
{
    public const string Base = "https://www.virustotal.com/api/v3";
    public const string KeyPage = "https://www.virustotal.com/gui/my-apikey";
    public const long DirectLimit = 32_000_000, UploadLimit = 650_000_000;
    public static string ReportPage(string sha256) => "https://www.virustotal.com/gui/file/" + sha256;

    /// What the scanners said. Scanners that timed out or can't read this kind
    /// of file aren't counted, so "0 of 60" only counts real answers.
    public sealed record Report(int Malicious, int Suspicious, int Scanners, DateTimeOffset? Scanned)
    {
        public int Flagged => Malicious + Suspicious;
    }

    static int N(JsonNode? stats, string k) => stats?[k] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    public static Report Make(JsonNode? stats, long? unixDate)
    {
        int m = N(stats, "malicious"), s = N(stats, "suspicious");
        return new(m, s, m + s + N(stats, "harmless") + N(stats, "undetected"),
                   unixDate is > 0 ? DateTimeOffset.FromUnixTimeSeconds(unixDate.Value) : null);
    }

    static long? Date(JsonNode? a, string key) => a?[key] is JsonValue v && v.TryGetValue<long>(out var d) ? d : null;

    /// GET /files/{sha256} answer → report.
    public static Report? FromLookup(string json)
    {
        try
        {
            var a = JsonNode.Parse(json)?["data"]?["attributes"];
            return a == null ? null : Make(a["last_analysis_stats"], Date(a, "last_analysis_date"));
        }
        catch { return null; }
    }

    /// GET /analyses/{id} answer → report once finished, null while it runs.
    public static Report? FromAnalysis(string json)
    {
        try
        {
            var a = JsonNode.Parse(json)?["data"]?["attributes"];
            return a?["status"]?.GetValue<string>() == "completed" ? Make(a["stats"], Date(a, "date")) : null;
        }
        catch { return null; }
    }

    /// The id in an upload answer.
    public static string? IdFrom(string json)
    {
        try { return JsonNode.Parse(json)?["data"]?["id"]?.GetValue<string>(); } catch { return null; }
    }

    /// Files over 32 MB go to an address VirusTotal hands out. The key goes
    /// there too, so only ever to VirusTotal's own servers over HTTPS.
    public static bool IsSafeUploadAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https"
        && (u.Host == "virustotal.com" || u.Host.EndsWith(".virustotal.com", StringComparison.Ordinal));

    public static string Problem(int status) => status switch
    {
        401 => "VirusTotal didn’t accept this key.",
        429 => "You’ve reached VirusTotal’s limit for a free key: 4 checks a minute, 500 a day. Try again soon.",
        _ => $"VirusTotal had a problem (error {status}). Try again soon.",
    };

    public static string Words(Report r) =>
        r.Scanners == 0 ? "VirusTotal has this file, but no scanner has given a result."
        : r.Flagged == 0 ? $"None of {r.Scanners} scanners flagged this file. That’s a good sign, not proof that it’s safe."
        : r.Flagged <= 2 ? $"{r.Flagged} of {r.Scanners} scanners flagged this file. One or two flags can be false alarms: read the full report."
        : $"{r.Flagged} of {r.Scanners} scanners flagged this file.";
}
