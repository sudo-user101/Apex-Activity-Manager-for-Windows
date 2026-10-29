namespace Apex.Core;

/// Drive health, as on the Mac's Drive Doctor: what Windows' storage service
/// reports for each physical drive (health status, and where Windows allows
/// it, wear, temperature, errors and power-on hours), in plain words. The
/// verdict rules are here (tested); the Windows app reads the numbers.
public static class DriveHealth
{
    public sealed record Drive(string Name, string Media, string Bus, long Size, string Health, string Operational,
                               int? WearPercent, int? TemperatureC, long? ReadErrors, long? WriteErrors, long? PowerOnHours);

    public sealed record Verdict(Severity Level, string Title, string Detail, List<string> Facts);

    /// Plain words for one drive. Pure: tested.
    public static Verdict Judge(Drive d)
    {
        var facts = new List<string>();
        if (d.WearPercent is { } w) facts.Add($"Wear: {w}% of its rated life used");
        if (d.TemperatureC is { } t and > 0) facts.Add($"Temperature: {t} °C");
        if (d.PowerOnHours is { } h and > 0) facts.Add($"Powered on for {h:N0} hours (about {h / 24 / 365.0:0.0} years)");
        long errors = (d.ReadErrors ?? 0) + (d.WriteErrors ?? 0);
        if (d.ReadErrors != null || d.WriteErrors != null) facts.Add($"Read/write errors: {errors:N0}");
        if (d.WearPercent == null && d.ReadErrors == null) facts.Add("Wear and error counts need Apex to run as administrator (Settings › Permissions).");

        if (!d.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase) && d.Health.Length > 0)
            return new(d.Health.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase) ? Severity.Critical : Severity.Warning,
                $"Windows reports this drive as {d.Health.ToLowerInvariant()}",
                "Back up what’s on it now. A drive Windows flags can fail without more warning.", facts);
        if (d.WearPercent is >= 90)
            return new(Severity.Warning, "This drive is near the end of its rated life", $"{d.WearPercent}% of its rated writes are used. Back it up and plan to replace it.", facts);
        if (errors > 0)
            return new(Severity.Warning, "This drive has reported errors", $"{errors:N0} read or write errors. Back up, and run Drive Doctor’s check of the disk.", facts);
        if (d.TemperatureC is >= 70)
            return new(Severity.Notice, "This drive is running hot", $"{d.TemperatureC} °C. Make sure air can reach it; heat shortens a drive’s life.", facts);
        return new(Severity.Ok, "Healthy", d.Health.Length > 0 ? "Windows reports no problems with this drive." : "Windows didn’t report a health status for this drive.", facts);
    }
}
