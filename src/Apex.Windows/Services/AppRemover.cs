using System.Diagnostics;
using System.Text.Json;
using Apex.Core;
using Apex.UI;
using Microsoft.Win32;

namespace Apex.Services;

/// Storage › Remove apps, as on the Mac: every installed program (Windows'
/// uninstall lists for all users and for you, 64- and 32-bit, and Microsoft
/// Store apps). Removing one closes it first, runs its own uninstaller (the
/// one Settings › Apps runs), then offers what it left behind.
internal static class AppRemover
{
    const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledApps.App> List()
    {
        var all = new List<InstalledApps.App>();
        void Read(RegistryKey hive, RegistryView view, bool allUsers)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view).OpenSubKey(Uninstall);
                if (root == null) return;
                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(name);
                        if (k == null) continue;
                        var values = k.GetValueNames().ToDictionary(n => n, n => k.GetValue(n));
                        if (InstalledApps.FromEntry(name, values, allUsers) is { } app) all.Add(app);
                    }
                    catch { }
                }
            }
            catch { }
        }
        Read(Registry.LocalMachine, RegistryView.Registry64, true);
        Read(Registry.LocalMachine, RegistryView.Registry32, true);
        Read(Registry.CurrentUser, RegistryView.Default, false);
        all.AddRange(StoreApps());
        return InstalledApps.Distinct(all);
    }

    /// Store apps you installed (not frameworks or ones Windows won't remove).
    static List<InstalledApps.App> StoreApps()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -eq 'Store' } | " +
                "Select-Object Name, PackageFullName, InstallLocation, Version, @{n='Pub';e={$_.PublisherId}} | ConvertTo-Json -Compress\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20_000);
            if (string.IsNullOrWhiteSpace(json)) return new();
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            var items = node is System.Text.Json.Nodes.JsonArray a ? a.ToList() : new List<System.Text.Json.Nodes.JsonNode?> { node };
            return items.Where(i => i != null).Select(i =>
            {
                string name = i!["Name"]?.ToString() ?? "";
                return new InstalledApps.App(i["PackageFullName"]?.ToString() ?? name, Friendly(name), "Microsoft Store", i["Version"]?.ToString() ?? "",
                    i["InstallLocation"]?.ToString() ?? "", "", "", 0, null, "", false, InstalledApps.Source.Store);
            }).ToList();
        }
        catch { return new(); }
    }

    /// "SpotifyAB.SpotifyMusic" → "Spotify Music".
    static string Friendly(string package)
    {
        var last = package.Contains('.') ? package[(package.LastIndexOf('.') + 1)..] : package;
        return System.Text.RegularExpressions.Regex.Replace(last, "(?<=[a-z])(?=[A-Z])", " ");
    }

    /// Closes everything running from the app's folder (each by identity), then
    /// runs its uninstaller and waits for it. True when Windows no longer lists it.
    public static async Task<(bool Removed, string Message)> Remove(InstalledApps.App app)
    {
        if (app.InstallLocation.Length > 3)
        {
            var targets = AppState.Latest.Processes.Where(p => p.Path.StartsWith(app.InstallLocation, StringComparison.OrdinalIgnoreCase)).Select(p => p.Identity).ToList();
            if (targets.Count > 0) { ProcessActions.End(targets); await Task.Delay(800); }
        }
        if (app.Kind == InstalledApps.Source.Store)
        {
            var ok = await Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"Remove-AppxPackage -Package '{app.Id}'\"", shell: false);
            return (ok && !StoreApps().Any(a => a.Id == app.Id), ok ? "" : "Windows didn’t remove it.");
        }
        var (exe, args) = Split(app.UninstallCommand.Length > 0 ? app.UninstallCommand : app.QuietUninstallCommand);   // its own uninstaller, as Settings › Apps runs it
        if (exe.Length == 0) return (false, "It has no uninstaller.");
        // MSI installs: /I opens the installer's repair screen; /X uninstalls.
        if (Path.GetFileName(exe).Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase)) args = System.Text.RegularExpressions.Regex.Replace(args, @"/I(\{)", "/X$1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool ran = await Run(exe, args, shell: true);
        await Task.Delay(1500);
        bool gone = !List().Any(a => a.Name == app.Name && a.Version == app.Version);
        return (gone, gone ? "" : ran ? "Its uninstaller closed but the app is still listed (it may have been cancelled, or it finishes after a restart)."
                                      : "The uninstaller didn’t start (permission was declined).");
    }

    /// "\"C:\\x\\uninstall.exe\" /S" → (path, args); also unquoted paths with spaces.
    public static (string Exe, string Args) Split(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"')) { int end = command.IndexOf('"', 1); return end < 0 ? (command.Trim('"'), "") : (command[1..end], command[(end + 1)..].Trim()); }
        int exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd < 0 ? (command, "") : (command[..(exeEnd + 4)], command[(exeEnd + 4)..].Trim());
    }

    static async Task<bool> Run(string exe, string args, bool shell)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = shell, CreateNoWindow = !shell })!;
            await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromMinutes(30)).Token);
            return true;
        }
        catch { return false; }       // declined or not found
    }

    public sealed record Leftover(string Path, string Where, long Size);

    /// What it left behind: its folders in AppData and ProgramData (and its
    /// install folder, if the uninstaller didn't remove it). Nothing shared.
    public static List<Leftover> Leftovers(InstalledApps.App app)
    {
        var found = new List<Leftover>();
        void Look(string root, string where)
        {
            try
            {
                foreach (var d in new DirectoryInfo(root).EnumerateDirectories())
                {
                    if (InstalledApps.BelongsTo(app, d.Name)) { found.Add(new(d.FullName, where, Size(d.FullName))); continue; }
                    // One level under the publisher's own folder.
                    if (app.Publisher.Length > 0 && !InstalledApps.Shared.Contains(d.Name))
                        try
                        {
                            foreach (var sub in d.EnumerateDirectories())
                                if (InstalledApps.BelongsTo(app, sub.Name, d.Name)) found.Add(new(sub.FullName, where, Size(sub.FullName)));
                        }
                        catch { }
                }
            }
            catch { }
        }
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Look(roaming, "Your settings (AppData › Roaming)");
        Look(local, "Its data and caches (AppData › Local)");
        Look(Path.Combine(local, "Programs"), "Its program files");
        Look(programData, "Shared data (ProgramData)");
        if (app.InstallLocation.Length > 3 && Directory.Exists(app.InstallLocation) && !found.Any(f => f.Path.Equals(app.InstallLocation, StringComparison.OrdinalIgnoreCase)))
            found.Add(new(app.InstallLocation, "Its install folder, still there", Size(app.InstallLocation)));
        return found.DistinctBy(f => f.Path.ToLowerInvariant()).ToList();
    }

    static long Size(string dir) { try { return DiskTree.Scan(dir).Top.Size; } catch { return 0; } }
}
