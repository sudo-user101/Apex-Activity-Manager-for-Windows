using System.Diagnostics;
using Apex.Core;

namespace Apex.Services;

/// Asks GitHub once a day whether a newer Apex is out, and links to the
/// download. Nothing is installed, and nothing about this PC is sent.
internal static class UpdateCheck
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static System.Threading.Timer? timer;

    public enum Answer { UpToDate, Newer, Failed }

    /// A newer version to mention (null: none, or skipped). Raised on any thread.
    public static event Action<string?>? Changed;
    public static string? Available { get; private set; }

    public static bool Enabled
    {
        get => Settings.Get("checkForUpdates", true);
        set { Settings.Set("checkForUpdates", value); if (!value) Set(null); Start(); }
    }

    public static string Current => typeof(UpdateCheck).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static void Start()
    {
        timer?.Dispose(); timer = null;
        if (!Enabled) return;
        // Soon after launch, then every hour; it only asks once a day.
        timer = new System.Threading.Timer(_ => _ = IfDue(), null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(1));
    }

    static async Task IfDue()
    {
        var last = Settings.Get("updateCheckedAt", 0.0);
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - last >= 24 * 3600) await Check();
    }

    public static async Task<(Answer, string?)> Check()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, AppUpdate.Newest);
            req.Headers.UserAgent.ParseAdd("Apex-Activity-Manager");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var r = await http.SendAsync(req);
            if (!r.IsSuccessStatusCode) return (Answer.Failed, null);
            var v = AppUpdate.VersionFrom(await r.Content.ReadAsStringAsync());
            if (v == null) return (Answer.Failed, null);
            Settings.Set("updateCheckedAt", (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (!AppUpdate.IsNewer(v, Current)) { Set(null); return (Answer.UpToDate, v); }
            if (Enabled && Settings.Get("updateSkipped", "") != v) Set(v);
            return (Answer.Newer, v);
        }
        catch (Exception) { return (Answer.Failed, null); }
    }

    public static void Skip()
    {
        if (Available != null) Settings.Set("updateSkipped", Available);
        Set(null);
    }

    public static void OpenDownload() => Process.Start(new ProcessStartInfo(AppUpdate.Page) { UseShellExecute = true });

    /// Test switch only (--show-notices).
    public static void Pretend(string version) => Set(version);

    static void Set(string? v) { Available = v; Changed?.Invoke(v); }
}

/// After a crash: Apex's own note (App.xaml.cs) is read on the next launch,
/// and the person is offered a GitHub issue they read before sending.
internal static class CrashCheck
{
    static string LogFile => Path.Combine(Path.GetTempPath(), "apex-crash.txt");

    /// The cleaned report of a crash not mentioned before, or null.
    public static string? Pending()
    {
        // The first time, older notes aren't news.
        var seen = Settings.Get("crashesSeenUntil", -1.0);
        if (seen < 0) { MarkSeen(); return null; }
        try
        {
            if (!File.Exists(LogFile)) return null;
            if (CrashReport.Latest(File.ReadAllText(LogFile)) is not { } last) return null;
            if (new DateTimeOffset(last.When.ToUniversalTime()).ToUnixTimeSeconds() <= seen) return null;
            return CrashReport.Clean(last.Text, Environment.UserName);
        }
        catch (Exception) { return null; }
    }

    public static void Report(string cleaned)
    {
        var url = CrashReport.IssueUrl(UpdateCheck.Current, Environment.OSVersion.VersionString, cleaned);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        MarkSeen();
    }

    public static void MarkSeen() => Settings.Set("crashesSeenUntil", (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
}
