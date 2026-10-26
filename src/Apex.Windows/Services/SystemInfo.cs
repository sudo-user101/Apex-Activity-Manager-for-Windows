using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Apex.Core;
using Microsoft.Win32;

namespace Apex.Services;

/// Startup apps: the registry Run keys (yours and all users') and the Startup
/// folders, with on / off as Windows' own Startup settings store it.
internal static class StartupItems
{
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ApprovedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public static List<StartupEntry> List()
    {
        var list = new List<StartupEntry>();
        void FromKey(RegistryKey hive, string key, string approvedKey, string source, bool admin)
        {
            using var k = hive.OpenSubKey(key);
            using var a = hive.OpenSubKey(approvedKey);
            if (k == null) return;
            foreach (var name in k.GetValueNames())
            {
                var cmd = k.GetValue(name)?.ToString() ?? "";
                var path = Environment.ExpandEnvironmentVariables(StartupParsing.ExecutablePath(cmd));
                list.Add(new StartupEntry
                {
                    Name = name, Command = cmd, Path = path, Publisher = Publisher(path), Source = source,
                    Enabled = StartupParsing.IsEnabled(a?.GetValue(name) as byte[]), NeedsAdmin = admin,
                    Location = (hive == Registry.LocalMachine ? "HKLM\\" : "HKCU\\") + approvedKey,
                });
            }
        }
        FromKey(Registry.CurrentUser, Run, ApprovedRun, "Registry (you)", false);
        FromKey(Registry.LocalMachine, Run, ApprovedRun, "Registry (all users)", true);
        FromKey(Registry.LocalMachine, Run32, ApprovedRun32, "Registry (all users, 32-bit)", true);

        void FromFolder(string folder, RegistryKey hive, string source, bool admin)
        {
            if (!Directory.Exists(folder)) return;
            using var a = hive.OpenSubKey(ApprovedFolder);
            foreach (var f in Directory.GetFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(f);
                list.Add(new StartupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(f), Command = f, Path = f, Publisher = Publisher(f), Source = source,
                    Enabled = StartupParsing.IsEnabled(a?.GetValue(name) as byte[]), NeedsAdmin = admin,
                    Location = (hive == Registry.LocalMachine ? "HKLM\\" : "HKCU\\") + ApprovedFolder + "|" + name,
                });
            }
        }
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "Startup folder (you)", false);
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "Startup folder (all users)", true);
        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string Publisher(string path)
    {
        try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() ?? "" : ""; } catch { return ""; }
    }

    /// Turns an entry on or off the way Windows' Settings › Startup does.
    /// All-users entries need administrator rights (Windows asks).
    public static bool SetEnabled(StartupEntry e, bool on)
    {
        var parts = e.Location.Split('|');
        var key = parts[0];
        var valueName = parts.Length > 1 ? parts[1] : e.Name;
        var data = StartupParsing.ApprovedValue(on, DateTime.UtcNow);
        if (!e.NeedsAdmin)
        {
            using var k = Registry.CurrentUser.CreateSubKey(key["HKCU\\".Length..]);
            k.SetValue(valueName, data, RegistryValueKind.Binary);
            return true;
        }
        var hex = string.Concat(data.Select(b => b.ToString("x2")));
        return Native.RunElevated("reg.exe", $"add \"{key}\" /v \"{valueName}\" /t REG_BINARY /d {hex} /f");
    }
}

/// Windows services, with start / stop / restart.
internal static class ServicesList
{
    public sealed record Item(string Name, string DisplayName, string Status, string StartType, string Description);

    public static List<Item> List()
    {
        var list = new List<Item>();
        foreach (var s in ServiceController.GetServices())
        {
            try
            {
                list.Add(new Item(s.ServiceName, s.DisplayName, s.Status switch
                {
                    ServiceControllerStatus.Running => "Running",
                    ServiceControllerStatus.Stopped => "Stopped",
                    ServiceControllerStatus.Paused => "Paused",
                    _ => "Changing",
                }, s.StartType switch
                {
                    ServiceStartMode.Automatic => "Automatic",
                    ServiceStartMode.Manual => "Manual",
                    ServiceStartMode.Disabled => "Disabled",
                    _ => "System",
                }, ""));
            }
            catch { }
            finally { s.Dispose(); }
        }
        return list.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// "start", "stop" or "restart". Tries directly, then asks for administrator rights.
    public static bool Control(string name, string action)
    {
        try
        {
            using var s = new ServiceController(name);
            if (action is "stop" or "restart") { s.Stop(); s.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
            if (action is "start" or "restart") { s.Start(); s.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20)); }
            return true;
        }
        catch (InvalidOperationException)
        {
            var cmd = action == "restart" ? $"/c net stop \"{name}\" & net start \"{name}\"" : $"/c net {action} \"{name}\"";
            return Native.RunElevated("cmd.exe", cmd);
        }
        catch { return false; }
    }
}

/// Who's signed in (Users page).
internal static class Sessions
{
    public sealed record Session(int Id, string User, string State);

    public static List<Session> List()
    {
        var list = new List<Session>();
        if (!Native.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buf, out int count)) return list;
        try
        {
            int size = Marshal.SizeOf<Native.WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var s = Marshal.PtrToStructure<Native.WTS_SESSION_INFO>(buf + i * size);
                var user = Query(s.SessionId, Native.WTSUserName);
                if (string.IsNullOrEmpty(user)) continue;
                var domain = Query(s.SessionId, Native.WTSDomainName);
                list.Add(new Session(s.SessionId, string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}", s.State switch
                {
                    0 => "Active", 1 => "Connected", 4 => "Disconnected", 5 => "Idle", _ => "Signed in",
                }));
            }
        }
        finally { Native.WTSFreeMemory(buf); }
        return list;
    }

    static string Query(int id, int info)
    {
        if (!Native.WTSQuerySessionInformation(IntPtr.Zero, id, info, out var p, out _)) return "";
        try { return Marshal.PtrToStringUni(p) ?? ""; } finally { Native.WTSFreeMemory(p); }
    }
}

/// End task, Efficiency mode, Open file location.
internal static class ProcessActions
{
    /// Ends each process only if it's still the one that was chosen: a
    /// handle is opened, its start time compared with the one on screen, and
    /// the process ended **through that handle**. A handle always refers to
    /// the same process object, so a reused ID can't be hit. Never ID-only.
    public static ActionReport End(IEnumerable<ProcessIdentity> targets)
    {
        var report = new ActionReport();
        foreach (var t in targets) report.Add(t, EndOne(t, out var error), error);
        return report;
    }

    static ActionResult EndOne(ProcessIdentity t, out string? error)
    {
        error = null;
        if (t.Pid <= 4 || t.Pid == Environment.ProcessId) return ActionResult.Protected;   // Idle, System, Apex
        if (!t.IsComplete) return ActionResult.Unverifiable;
        var h = Native.OpenProcess(Native.PROCESS_TERMINATE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, t.Pid);
        if (h == IntPtr.Zero)
        {
            int e = Marshal.GetLastWin32Error();
            if (e == Native.ERROR_INVALID_PARAMETER) return ActionResult.Exited;          // no such process
            if (e != Native.ERROR_ACCESS_DENIED) { error = new System.ComponentModel.Win32Exception(e).Message; return ActionResult.Failed; }
            // Can't open it to end it: is the one running now still the chosen one?
            return Check(t) switch { ActionResult.Done => ActionResult.NeedsAdmin, var r => r };
        }
        try
        {
            if (!Native.GetProcessTimes(h, out long created, out _, out _, out _)) return ActionResult.Unverifiable;
            if (created != t.CreateTime) return ActionResult.Reused;
            if (Native.GetExitCodeProcess(h, out uint code) && code != Native.STILL_ACTIVE) return ActionResult.Exited;
            if (Native.TerminateProcess(h, 1)) return ActionResult.Done;
            int e = Marshal.GetLastWin32Error();
            if (e == Native.ERROR_ACCESS_DENIED) return ActionResult.NeedsAdmin;
            error = new System.ComponentModel.Win32Exception(e).Message;
            return ActionResult.Failed;
        }
        finally { Native.CloseHandle(h); }
    }

    /// Is the process running with this ID still the chosen one? Done = yes.
    public static ActionResult Check(ProcessIdentity t)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, t.Pid);
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == Native.ERROR_INVALID_PARAMETER ? ActionResult.Exited : ActionResult.Unverifiable;
        try
        {
            if (!Native.GetProcessTimes(h, out long created, out _, out _, out _)) return ActionResult.Unverifiable;
            if (created != t.CreateTime) return ActionResult.Reused;
            return Native.GetExitCodeProcess(h, out uint code) && code != Native.STILL_ACTIVE ? ActionResult.Exited : ActionResult.Done;
        }
        finally { Native.CloseHandle(h); }
    }

    public static void ShowInExplorer(string path)
    {
        if (path.Length > 0) Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
}
