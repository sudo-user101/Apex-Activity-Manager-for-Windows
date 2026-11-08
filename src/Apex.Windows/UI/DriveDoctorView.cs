using System.Diagnostics;
using System.Security.Principal;
using Apex.Core;
using Apex.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Apex.UI;

/// Drive Doctor, as on the Mac: reads what Windows reports about one drive
/// (nothing is changed), then plain findings with safe steps and buttons.
internal static class DriveDoctorView
{
    public static async Task Show(FrameworkElement anchor, string diskName)
    {
        var body = new StackPanel { Spacing = 10, Width = 640 };
        var progress = Theme.Text("Checking this drive…", 14, Theme.Secondary);
        body.Children.Add(progress);
        body.Children.Add(new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left });
        var d = new ContentDialog { Title = "Drive Doctor · " + diskName, Content = new ScrollViewer { Content = body, MaxHeight = 560 }, CloseButtonText = "Done", XamlRoot = anchor.XamlRoot };
        d.Resources["ContentDialogMaxWidth"] = 760.0;
        var shown = d.ShowAsync();

        var letter = DriveDoctor.FirstLetter(diskName);
        var input = await Task.Run(() =>
        {
            DriveHealth.Drive? health = null;
            try { if (Index(diskName) is { } i && DriveHealthReader.Read().TryGetValue(i, out var h)) health = h; } catch { }
            var disk = AppState.Latest.Disks.FirstOrDefault(x => x.Name == diskName);
            return new DriveDoctor.Input
            {
                Health = health, Capacity = disk?.Capacity ?? 0, Free = disk?.Free ?? 0, IsSystem = disk?.IsSystem ?? false,
                Letter = letter, UnexpectedShutdowns = UnexpectedShutdowns(),
                IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            };
        });
        body.Children.Clear();
        foreach (var f in DriveDoctor.Diagnose(input)) body.Children.Add(FindingCard.Make(f, key => Action(key, anchor, letter, d)));
        try { await shown; } catch { }
    }

    static (string, Action)? Action(string key, FrameworkElement anchor, string letter, ContentDialog d) => key switch
    {
        DriveDoctor.Clean => ("Free Up Space…", () => { d.Hide(); _ = StorageView.Show(anchor); }),
        DriveDoctor.CheckDisk => ($"Check {letter} for Damage…", () => RunCheckDisk(letter)),
        DriveDoctor.Permissions => ("Administrator Settings…", () => SettingsWindow.Show("permissions")),
        DriveDoctor.Backup => ("Windows Backup Settings", () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(DriveDoctor.Backup))),
        DriveDoctor.StorageSense => ("Storage Sense Settings", () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(DriveDoctor.StorageSense))),
        _ => null,
    };

    /// Windows' own read-only scan (`chkdsk X: /scan` changes nothing), in a
    /// window the person can watch. Needs administrator: Windows asks.
    static void RunCheckDisk(string letter)
    {
        if (letter.Length != 2 || !char.IsLetter(letter[0]) || letter[1] != ':') return;
        try
        {
            var p = new ProcessStartInfo("cmd.exe") { UseShellExecute = true, Verb = "runas" };
            p.ArgumentList.Add("/k"); p.ArgumentList.Add("chkdsk"); p.ArgumentList.Add(letter); p.ArgumentList.Add("/scan");
            Process.Start(p);
        }
        catch (System.ComponentModel.Win32Exception) { }   // the person said No
    }

    /// Kernel-Power event 41 in the last 30 days (the System log, readable without administrator).
    static int? UnexpectedShutdowns()
    {
        try
        {
            var p = new ProcessStartInfo("wevtutil.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "qe", "System", "/q:*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]", "/f:xml", "/c:200" })
                p.ArgumentList.Add(a);
            using var proc = Process.Start(p);
            if (proc == null) return null;
            var xml = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(10000)) { try { proc.Kill(); } catch { } return null; }
            return proc.ExitCode == 0 ? DriveDoctor.CountEvents(xml) : null;
        }
        catch { return null; }
    }

    /// "Disk 0 (C:)" → 0.
    static int? Index(string name) =>
        name.StartsWith("Disk ") && int.TryParse(new string(name.AsSpan(5).ToArray().TakeWhile(char.IsDigit).ToArray()), out var i) ? i : null;
}
