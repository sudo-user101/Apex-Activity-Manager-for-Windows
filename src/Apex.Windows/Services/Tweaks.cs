using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Apex.Core;
using Microsoft.Win32;

namespace Apex.Services;

/// Applies Settings › Windows Tweaks to this user's registry (HKEY_CURRENT_USER,
/// no administrator). Before a value is first changed, what was there is
/// remembered (in Apex's settings), and turning the tweak off writes exactly
/// that back, or removes the value if there was none.
internal static class Tweaks
{
    const string Remembered = "tweakPrevious";

    static Dictionary<string, string?> Load()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string?>>(Settings.Get(Remembered, "{}")) ?? new(); }
        catch { return new(); }
    }
    static void Save(Dictionary<string, string?> d) => Settings.Set(Remembered, JsonSerializer.Serialize(d));

    /// The value as text, or null when it isn't there ("" for a KeyPresent key that exists).
    public static string? Read(WindowsTweaks.Value v)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(v.Key);
            if (v.Kind == WindowsTweaks.Kind.KeyPresent) return k == null ? null : "";
            return k?.GetValue(v.Name) switch { null => null, int i => i.ToString(), string s => s, var o => o.ToString() };
        }
        catch { return null; }
    }

    public static bool IsOn(WindowsTweaks.Tweak t) => WindowsTweaks.IsOn(t, Read);

    /// Turns a tweak on or off. Returns a problem in plain words, or null.
    public static string? Set(WindowsTweaks.Tweak t, bool on)
    {
        var remembered = Load();
        try
        {
            foreach (var v in t.Values)
            {
                if (on)
                {
                    if (!remembered.ContainsKey(WindowsTweaks.Id(v))) remembered[WindowsTweaks.Id(v)] = Read(v);
                    Write(v, v.On);
                }
                else
                {
                    Write(v, WindowsTweaks.Previous(v, remembered));
                    remembered.Remove(WindowsTweaks.Id(v));
                }
            }
        }
        catch (UnauthorizedAccessException) { return "Windows doesn’t allow changing this one from here (it’s locked on this PC)."; }
        catch (Exception e) { return e.Message; }
        finally { Save(remembered); }
        Announce();
        if (t.Restart == WindowsTweaks.Restart.Explorer) RestartExplorer();
        return null;
    }

    /// Every tweak back to what it was before Apex changed it.
    public static void UndoAll()
    {
        foreach (var t in WindowsTweaks.All) if (t.Values.Any(v => Load().ContainsKey(WindowsTweaks.Id(v)))) Set(t, false);
    }

    static void Write(WindowsTweaks.Value v, string? value)
    {
        if (v.Kind == WindowsTweaks.Kind.KeyPresent)
        {
            if (value == null) Registry.CurrentUser.DeleteSubKeyTree(v.Key, throwOnMissingSubKey: false);
            else { using var k = Registry.CurrentUser.CreateSubKey(v.Key); k.SetValue(v.Name, value, RegistryValueKind.String); }
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(v.Key);
        if (value == null) key.DeleteValue(v.Name, throwOnMissingValue: false);
        else if (v.Kind == WindowsTweaks.Kind.Dword) key.SetValue(v.Name, int.Parse(value), RegistryValueKind.DWord);
        else key.SetValue(v.Name, value, RegistryValueKind.String);
    }

    // Tells running apps and the taskbar that settings changed (many tweaks apply at once).
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
    static void Announce()
    {
        foreach (var area in new[] { "TraySettings", "Policy", "ImmersiveColorSet" })
            SendMessageTimeout((IntPtr)0xffff, 0x001A /* WM_SETTINGCHANGE */, UIntPtr.Zero, area, 0x0002 /* ABORTIFHUNG */, 500, out _);
    }

    /// Restarts File Explorer (the taskbar and folder windows), as the tweak needs.
    public static void RestartExplorer()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            // Windows usually starts it again by itself; make sure.
            Thread.Sleep(1500);
            if (Process.GetProcessesByName("explorer").Length == 0) Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch { }
    }
}
