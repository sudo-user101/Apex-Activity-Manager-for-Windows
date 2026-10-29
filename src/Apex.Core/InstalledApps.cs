namespace Apex.Core;

/// Storage › Remove apps: the programs installed on this PC (from Windows'
/// uninstall list, and Microsoft Store apps), and what an app leaves behind
/// after its own uninstaller ran. The rules are here (tested); the Windows app
/// reads the registry and the folders.
public static class InstalledApps
{
    public enum Source { Desktop, Store }

    public sealed record App(string Id, string Name, string Publisher, string Version, string InstallLocation, string UninstallCommand,
                             string QuietUninstallCommand, long SizeBytes, DateTime? Installed, string Icon, bool AllUsers, Source Kind = Source.Desktop)
    {
        public bool CanUninstall => Kind == Source.Store || UninstallCommand.Length > 0;
    }

    /// One uninstall entry's values (as read from the registry) → an app, or
    /// null for what isn't an app you'd remove (Windows components, updates,
    /// entries without a name or a way to uninstall). Pure: tested.
    public static App? FromEntry(string keyName, IReadOnlyDictionary<string, object?> v, bool allUsers)
    {
        string S(string k) => v.TryGetValue(k, out var o) && o != null ? o.ToString()!.Trim() : "";
        var name = S("DisplayName");
        if (name.Length == 0) return null;
        if (S("SystemComponent") == "1") return null;                         // part of Windows
        if (S("ParentKeyName").Length > 0 || S("ReleaseType") is "Update" or "Hotfix" or "Security Update") return null;   // an update of something else
        if (name.StartsWith("Microsoft Visual C++", StringComparison.OrdinalIgnoreCase) && name.Contains("Redistributable")) { }   // shared runtimes stay listed (they can be removed, but say so)
        var uninstall = S("UninstallString");
        if (uninstall.Length == 0 && S("QuietUninstallString").Length == 0) return null;
        long size = long.TryParse(S("EstimatedSize"), out var kb) ? kb * 1024 : 0;
        DateTime? installed = DateTime.TryParseExact(S("InstallDate"), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        var icon = S("DisplayIcon").Split(',')[0].Trim('"');
        return new App(keyName, name, S("Publisher"), S("DisplayVersion"), S("InstallLocation").Trim('"'), uninstall, S("QuietUninstallString"),
                       size, installed, icon, allUsers);
    }

    /// Several entries for the same program (32- and 64-bit lists, per user and all users) shown once. Pure: tested.
    public static List<App> Distinct(IEnumerable<App> apps) =>
        apps.GroupBy(a => (a.Name.ToLowerInvariant(), a.Version))
            .Select(g => g.OrderByDescending(a => a.InstallLocation.Length).First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// Folder names never offered as leftovers: shared by many apps, or Windows'.
    public static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Packages", "Temp", "Programs", "Common Files", "Google", "Adobe", "Mozilla", "Apple", "Intel", "NVIDIA",
        "NVIDIA Corporation", "AMD", "Package Cache", "Comms", "ConnectedDevicesPlatform", "D3DSCache", "CrashDumps", "Publishers",
        "Microsoft Corporation", "Oracle", "Java", "Steam", "Epic Games",
    };

    /// Is a folder (in AppData / ProgramData) this app's? Its name is the
    /// app's name or its first word(s), or the publisher's folder holding a
    /// folder named for the app. Never a shared folder. Pure: tested.
    public static bool BelongsTo(App app, string folderName, string? parentFolderName = null)
    {
        if (Shared.Contains(folderName)) return false;
        static string Clean(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var f = Clean(folderName);
        if (f.Length < 3) return false;
        var name = Clean(app.Name);
        // The name without its version and notes: "7-Zip 24.08 (x64)" → "7-Zip".
        static bool VersionOrNote(string w) => w.StartsWith('(') || w.All(c => char.IsDigit(c) || c == '.')
                                              || (w.Length > 1 && (w[0] == 'v' || w[0] == 'V') && w[1..].All(c => char.IsDigit(c) || c == '.'));
        var shortName = Clean(string.Join(" ", app.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeWhile(w => !VersionOrNote(w))));
        if (f == name || f == shortName) return true;
        // "Publisher\App": a folder named for the app under the publisher's own folder.
        if (parentFolderName != null && !Shared.Contains(parentFolderName) && app.Publisher.Length > 0
            && Clean(parentFolderName) == Clean(app.Publisher) && (f == name || f == shortName || (shortName.Length >= 4 && f.StartsWith(shortName))))
            return true;
        return false;
    }
}
