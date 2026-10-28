namespace Apex.Core;

/// Settings › Windows Tweaks (the Mac's "MacOSX Change"): Windows settings
/// that Settings hides or buries, as plain switches, for this user only (no
/// administrator). Turning one on remembers exactly what was there before;
/// turning it off puts that back. Shown live from the registry. The list and
/// its rules are here (tested); the Windows app reads and writes them.
public static class WindowsTweaks
{
    public enum Kind { Dword, String, KeyPresent }
    /// What has to restart before Windows uses it.
    public enum Restart { None, Explorer, SignOut }

    /// One registry value under HKEY_CURRENT_USER. `KeyPresent`: the key existing (with an empty default) is "on".
    public sealed record Value(string Key, string Name, Kind Kind, string On);

    public sealed record Tweak(string Id, string Group, string Title, string Detail, Restart Restart, params Value[] Values)
    {
        public string Note => Restart switch
        {
            Restart.Explorer => "File Explorer and the taskbar restart (open folder windows reopen).",
            Restart.SignOut => "Takes effect after you sign out and back in.",
            _ => "",
        };
    }

    const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    public static readonly string[] Groups = { "File Explorer", "Taskbar", "Start & search", "Windows", "Mouse", "Privacy", "Gaming" };

    public static readonly Tweak[] All =
    {
        new("ext", "File Explorer", "Show every file extension", "“report.pdf”, not just “report”, so you can tell file types apart.", Restart.Explorer,
            new Value(Adv, "HideFileExt", Kind.Dword, "0")),
        new("hidden", "File Explorer", "Show hidden files", "Files and folders marked hidden appear, faded.", Restart.Explorer,
            new Value(Adv, "Hidden", Kind.Dword, "1")),
        new("superhidden", "File Explorer", "Show protected system files", "Windows’ own files too. Leave them as they are.", Restart.Explorer,
            new Value(Adv, "ShowSuperHidden", Kind.Dword, "1")),
        new("thispc", "File Explorer", "Open File Explorer to This PC", "Your drives, instead of Home.", Restart.Explorer,
            new Value(Adv, "LaunchTo", Kind.Dword, "1")),
        new("fullpath", "File Explorer", "Show the full path in the title bar", "Where the folder is, not just its name.", Restart.Explorer,
            new Value(@"Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState", "FullPath", Kind.Dword, "1")),
        new("classicmenu", "File Explorer", "Classic right-click menu", "Every command at once, without “Show more options” (Windows 11).", Restart.Explorer,
            new Value(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", Kind.KeyPresent, "")),

        new("taskbarleft", "Taskbar", "Taskbar icons on the left", "Like earlier Windows, instead of centred (Windows 11).", Restart.None,
            new Value(Adv, "TaskbarAl", Kind.Dword, "0")),
        new("seconds", "Taskbar", "Show seconds on the clock", "Windows 11.", Restart.None,
            new Value(Adv, "ShowSecondsInSystemClock", Kind.Dword, "1")),
        new("taskview", "Taskbar", "Hide the Task View button", "", Restart.None,
            new Value(Adv, "ShowTaskViewButton", Kind.Dword, "0")),
        new("widgets", "Taskbar", "Hide the Widgets button", "Some Windows builds don’t allow this from here.", Restart.None,
            new Value(Adv, "TaskbarDa", Kind.Dword, "0")),
        new("endtask", "Taskbar", "“End task” when you right-click an app on the taskbar", "Windows 11 23H2 and later.", Restart.None,
            new Value(Adv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", Kind.Dword, "1")),

        new("nowebsearch", "Start & search", "No web results in Start search", "Only your apps, files and settings.", Restart.Explorer,
            new Value(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", Kind.Dword, "1")),
        new("norecent", "Start & search", "Don’t show recent files in Start and Jump Lists", "", Restart.Explorer,
            new Value(Adv, "Start_TrackDocs", Kind.Dword, "0")),

        new("noshake", "Windows", "Don’t minimize other windows when you shake one", "", Restart.None,
            new Value(Adv, "DisallowShaking", Kind.Dword, "1")),
        new("nosnapflyout", "Windows", "No snap layouts when you point at the maximize button", "Snapping by dragging still works.", Restart.None,
            new Value(Adv, "EnableSnapAssistFlyout", Kind.Dword, "0")),
        new("fastmenus", "Windows", "Menus open without a pause", "", Restart.SignOut,
            new Value(@"Control Panel\Desktop", "MenuShowDelay", Kind.String, "0")),

        new("noaccel", "Mouse", "Turn off pointer acceleration", "The pointer moves the same distance however fast you move the mouse (“Enhance pointer precision” off).", Restart.SignOut,
            new Value(@"Control Panel\Mouse", "MouseSpeed", Kind.String, "0"),
            new Value(@"Control Panel\Mouse", "MouseThreshold1", Kind.String, "0"),
            new Value(@"Control Panel\Mouse", "MouseThreshold2", Kind.String, "0")),

        new("noadid", "Privacy", "Turn off the advertising ID", "Apps can’t use it to show you personalised ads.", Restart.None,
            new Value(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", Kind.Dword, "0")),
        new("notips", "Privacy", "No tips and suggestions", "Windows stops suggesting apps and settings.", Restart.None,
            new Value(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", Kind.Dword, "0"),
            new Value(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", Kind.Dword, "0")),

        new("nogamedvr", "Gaming", "Turn off Game Bar background recording", "Frees a little performance in games.", Restart.None,
            new Value(@"System\GameConfigStore", "GameDVR_Enabled", Kind.Dword, "0"),
            new Value(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", Kind.Dword, "0")),
    };

    /// Does what's in the registry mean "on"? `read` gives the value as text, or null when absent
    /// (for KeyPresent: "" when the key exists). Pure: tested.
    public static bool IsOn(Tweak t, Func<Value, string?> read) => t.Values.All(v => read(v) == v.On);

    /// What turning it off writes back: what was there before it was turned on
    /// (null: remove it), or Windows' own value when nothing was remembered. Pure: tested.
    public static string? Previous(Value v, IReadOnlyDictionary<string, string?>? remembered)
    {
        var key = Id(v);
        if (remembered != null && remembered.TryGetValue(key, out var was)) return was;
        return null;                         // never set by the user: Windows' default is "absent"
    }

    public static string Id(Value v) => v.Key + "|" + v.Name;
}
