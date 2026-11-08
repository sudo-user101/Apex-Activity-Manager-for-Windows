namespace Apex.Core;

/// Drive Doctor, as on the Mac: what's wrong with a drive, why it matters,
/// and safe things to do, from what Windows reports. Nothing here changes
/// anything; the actions are offered as buttons. Pure: tested.
public static class DriveDoctor
{
    public sealed class Input
    {
        public DriveHealth.Drive? Health { get; init; }
        public long Capacity { get; init; }
        public long Free { get; init; }
        public bool IsSystem { get; init; }
        /// "C:" for Check Disk, or "" when the drive has no letter.
        public string Letter { get; init; } = "";
        /// Unexpected shutdowns in the last 30 days (Kernel-Power event 41), null if unknown.
        public int? UnexpectedShutdowns { get; init; }
        public bool IsAdmin { get; init; }
    }

    // Action keys the app turns into buttons.
    public const string Clean = "clean", CheckDisk = "chkdsk", Backup = "ms-settings:backup",
        StorageSense = "ms-settings:storagesense", Permissions = "permissions";

    static readonly string BackUp = "Back up anything important on this drive now (Settings › Accounts › Windows Backup, or copy it to another drive)";

    public static List<Finding> Diagnose(Input i)
    {
        var f = new List<Finding>();
        var d = i.Health;
        var health = d?.Health ?? "";
        var op = d?.Operational ?? "";
        bool failing = health.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase)
            || op is "Predictive failure" or "Failed media" or "Error" or "Lost communication";
        if (failing)
            f.Add(new("health.critical", "Windows reports this drive is failing",
                "A drive Windows flags can stop working without more warning, and what's on it can be lost.",
                Severity.Critical, Ev(("Health", health), ("Status", op)), new[] { "The drive is wearing out or damaged" },
                new[] { BackUp, "Replace the drive. Keep using it only for things you have copies of." }, Backup));
        else if (health.Equals("Warning", StringComparison.OrdinalIgnoreCase) || op == "Degraded")
            f.Add(new("health.warning", "Windows has a warning about this drive",
                "It still works, but Windows has seen something that often comes before a failure.",
                Severity.Warning, Ev(("Health", health), ("Status", op)), new[] { "Early signs of wear or damage" },
                new[] { BackUp, "Check it again in a week; replace it if the warning stays." }, Backup));

        if (d?.WearPercent is { } w && w >= 80)
            f.Add(new("wear", $"{w}% of its rated life is used",
                "Drives that store data in flash memory (SSDs) can only be written a certain amount. Near the end, they can turn read-only or fail.",
                w >= 90 ? Severity.Warning : Severity.Notice, Ev(("Wear", $"{w}%")), new[] { "Years of writing, or heavy use of the page file" },
                new[] { BackUp, "Plan to replace it", "Close memory-hungry apps so Windows writes less to the page file" }, Backup));

        long errors = (d?.ReadErrors ?? 0) + (d?.WriteErrors ?? 0);
        if (errors > 0)
            f.Add(new("errors", "The drive has reported read or write errors",
                "Errors can mean damaged areas on the drive or a bad connection. Files may not read back correctly.",
                Severity.Warning, Ev(("Read errors", $"{d?.ReadErrors ?? 0:N0}"), ("Write errors", $"{d?.WriteErrors ?? 0:N0}")),
                new[] { "Damaged areas on the drive", "A loose or faulty cable (external and desktop drives)" },
                new[] { BackUp, "Check the file system for damage (Check Disk, read-only scan)", "For an external drive: try another cable and port" },
                i.Letter.Length > 0 ? CheckDisk : ""));

        if (d?.TemperatureC is { } t && t >= 70)
            f.Add(new("heat", $"The drive is running hot ({t} °C)",
                "Heat shortens a drive's life, and many drives slow themselves down to cool off.",
                Severity.Notice, Ev(("Temperature", $"{t} °C")), new[] { "Blocked vents or dust", "A long, heavy copy" },
                new[] { "Keep the PC's vents clear and let it cool", "For a laptop, use it on a hard surface" }));

        if (i.Capacity > 0)
        {
            double share = (double)i.Free / i.Capacity;
            bool tight = share < 0.10 || i.Free < 10_000_000_000L && i.Capacity > 64_000_000_000L;
            if (tight)
                f.Add(new("space.low", "The drive is almost full",
                    i.IsSystem ? "Windows needs free space for updates, the page file and temporary files. When it runs out, apps slow down and updates fail."
                               : "When a drive is full, saving and copying fail, and it can slow down.",
                    share < 0.05 ? Severity.Warning : Severity.Notice,
                    Ev(("Free", Fmt.Bytes(i.Free)), ("Of", Fmt.Bytes(i.Capacity)), ("Free share", $"{share * 100:0}%")),
                    new[] { "Large files, downloads and old installers", "Temporary files and the Recycle Bin" },
                    new[] { "Free up space: clean leftovers, or see what takes the space", "Turn on Storage Sense so Windows cleans up by itself" }, Clean));
        }

        if (i.UnexpectedShutdowns is { } n && n >= 3)
            f.Add(new("shutdowns", $"This PC shut down unexpectedly {n} times in 30 days",
                "Losing power while the drive is writing can damage files.",
                Severity.Notice, Ev(("Unexpected shutdowns", $"{n}")), new[] { "Holding the power button", "A flat battery or a power cut", "A crash" },
                new[] { "Use Shut down from the Start menu where possible", "Check the file system for damage (Check Disk, read-only scan)" },
                i.Letter.Length > 0 && i.IsSystem ? CheckDisk : ""));

        if (d != null && d.WearPercent == null && d.ReadErrors == null && !i.IsAdmin)
            f.Add(new("admin", "Wear and error counts aren't available yet",
                "Windows only shows these to apps running as administrator.",
                Severity.Info, Ev(), Array.Empty<string>(), new[] { "Restart Apex as administrator to see them" }, Permissions));

        if (!f.Any(x => x.Severity >= Severity.Notice))
            f.Insert(0, new("healthy", "No problems found",
                d == null ? "Windows didn't report this drive's health, and nothing else looks wrong." : "Windows reports no problems with this drive, and it has room to spare.",
                Severity.Ok, Ev(), Array.Empty<string>(), Array.Empty<string>()));
        // Worst first; "No problems found" leads when it's there, notes after it.
        return f.OrderByDescending(x => x.Id == "healthy" ? int.MaxValue : (int)x.Severity).ToList();
    }

    static IReadOnlyList<(string, string)> Ev(params (string, string)[] e) => e.Where(x => x.Item2.Length > 0).ToList();

    /// "Disk 0 (C: D:)" → "C:". Pure: tested.
    public static string FirstLetter(string diskName)
    {
        var open = diskName.IndexOf('(');
        if (open < 0) return "";
        var rest = diskName[(open + 1)..];
        return rest.Length >= 2 && char.IsLetter(rest[0]) && rest[1] == ':' ? char.ToUpperInvariant(rest[0]) + ":" : "";
    }

    /// Counts events in `wevtutil qe … /f:xml` output.
    public static int CountEvents(string xml) =>
        xml.Split("<Event ", StringSplitOptions.None).Length - 1;
}
