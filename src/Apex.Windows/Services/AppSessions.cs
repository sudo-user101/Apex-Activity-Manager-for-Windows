using System.Diagnostics;
using System.Text.Json;
using Apex.Core;

namespace Apex.Services;

/// Apps closed to free memory (or from the tray's Sessions), remembered so
/// they can be opened again later. Closing asks each app politely (like
/// clicking its ×), so it can ask to save. Windows has no general way to
/// bring back an app's windows, so Reopen starts each program again; apps
/// that restore their own windows (browsers, Office) do so themselves.
internal static class AppSessions
{
    static readonly string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apex", "session.json");

    public sealed record Saved(string Name, string Path);

    public static List<Saved> Load()
    {
        try { return JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(file)) ?? new(); } catch { return new(); }
    }

    static void Store(List<Saved> list)
    {
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!); File.WriteAllText(file, JsonSerializer.Serialize(list)); } catch { }
    }

    /// Asks each program's windows to close, and remembers the programs. Only
    /// processes still the ones on screen are asked.
    public static int Close(IEnumerable<(string Name, string Path, ProcessIdentity[] Targets)> apps)
    {
        var saved = Load();
        int asked = 0;
        foreach (var (name, path, targets) in apps)
        {
            bool any = false;
            foreach (var t in targets.Where(t => ProcessActions.Check(t) == ActionResult.Done))
                foreach (var h in Native.AppWindows(t.Pid)) { Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); any = true; }
            if (!any) continue;
            asked++;
            if (!saved.Any(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) saved.Add(new Saved(name, path));
        }
        Store(saved);
        return asked;
    }

    /// Starts every remembered program again and forgets the session.
    public static int Reopen()
    {
        int opened = 0;
        foreach (var s in Load().Where(s => File.Exists(s.Path)))
        {
            try { Process.Start(new ProcessStartInfo(s.Path) { UseShellExecute = true }); opened++; } catch { }
        }
        Store(new());
        return opened;
    }

    public static void Forget() => Store(new());
}
