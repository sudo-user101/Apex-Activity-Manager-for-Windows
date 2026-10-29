using System.Diagnostics;
using Apex.Core;
using Apex.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Apex.UI;

/// Details about a program and its processes, as in the Mac's inspector: where
/// it lives and who signed it, its processes (ID, started, priority, threads),
/// what it's using now, and its network connections.
internal static class ProcessInspector
{
    public static async Task Show(FrameworkElement anchor, ProcessRow row)
    {
        var procs = AppState.Latest.Processes.Where(p => row.Targets.Contains(p.Identity)).ToList();
        var root = new StackPanel { Spacing = 14, Width = 600 };
        var path = procs.Select(p => p.Path).FirstOrDefault(p => p.Length > 0) ?? row.Path;

        // Who made it.
        var who = new StackPanel { Spacing = 4 };
        var signed = Theme.Text("Checking the signature…", 13, Theme.Secondary);
        who.Children.Add(Theme.Text(path.Length > 0 ? path : "Windows doesn’t say where this program is (it may be part of Windows itself).", 12, Theme.Secondary));
        who.Children.Add(signed);
        root.Children.Add(Theme.Card(who, 12));
        _ = Task.Run(() => Signature.Check(path)).ContinueWith(t => MainWindow.Ui(() =>
        {
            var r = t.Result;
            signed.Text = !r.Signed ? "Not signed, or signed through a Windows catalog (as many of Windows’ own files are)."
                        : r.Trusted ? $"Signed by {r.Signer}, and Windows trusts the signature."
                        : $"Signed by {r.Signer}, but Windows doesn’t trust this signature (expired, revoked, or the file was changed).";
        }));

        // What it's using.
        var use = new Grid { ColumnSpacing = 20 };
        string[] labels = { "CPU", "Memory", "Disk", "Processes", "Threads", "Handles" };
        string[] values =
        {
            Fmt.Percent(procs.Sum(p => p.Cpu)), Fmt.Memory(procs.Sum(p => p.Memory)), Fmt.Rate(procs.Sum(p => p.DiskPerSec)),
            procs.Count.ToString(), procs.Sum(p => p.Threads).ToString("N0"), procs.Sum(p => p.Handles).ToString("N0"),
        };
        for (int i = 0; i < labels.Length; i++)
        {
            use.ColumnDefinitions.Add(new ColumnDefinition());
            var st = Theme.Stat(labels[i], out var v, 16); v.Text = values[i];
            Grid.SetColumn(st, i); use.Children.Add(st);
        }
        root.Children.Add(Theme.Card(use, 12));

        // Each process.
        var list = new StackPanel { Spacing = 2 };
        foreach (var p in procs.OrderBy(p => p.Pid).Take(30))
        {
            var started = DateTime.FromFileTime(p.CreateTime);
            var line = $"{p.Name} · ID {p.Pid} · started {started:g}" + (p.User.Length > 0 ? $" · session {p.User}" : "")
                     + (ProcessActions.PriorityOf(p.Identity) is { Length: > 0 } pr ? $" · {pr} priority" : "") + (p.Suspended ? " · paused" : "");
            list.Children.Add(Theme.Text(line, 12, Theme.Secondary));
        }
        if (procs.Count > 30) list.Children.Add(Theme.Text($"and {procs.Count - 30} more", 12, Theme.Secondary));
        root.Children.Add(Theme.Card(list, 12));

        // Network connections.
        var net = Theme.Text("Looking for network connections…", 12, Theme.Secondary);
        net.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(Theme.Card(net, 12));
        var pids = procs.Select(p => p.Pid).ToHashSet();
        _ = Task.Run(NetworkTools.Connections).ContinueWith(t => MainWindow.Ui(() =>
        {
            var mine = (t.Result ?? new()).Where(c => pids.Contains(c.Pid)).ToList();
            var open = mine.Where(c => !c.Listening && c.Remote.Length > 0).Select(c => $"{c.Remote}:{c.RemotePort}").Distinct().Take(12).ToList();
            var listening = mine.Where(c => c.Listening).Select(c => $"{c.Protocol} {c.LocalPort}").Distinct().Take(8).ToList();
            net.Text = mine.Count == 0 ? "No network connections right now."
                : (open.Count > 0 ? $"Connected to {string.Join(", ", open)}" : "No outgoing connections")
                  + (listening.Count > 0 ? $". Listening on {string.Join(", ", listening)}." : ".");
        }));

        var d = new ContentDialog
        {
            Title = row.Name, Content = new ScrollViewer { Content = root, MaxHeight = 560 }, CloseButtonText = "Done",
            SecondaryButtonText = path.Length > 0 ? "Show in File Explorer" : "", XamlRoot = anchor.XamlRoot,
        };
        d.Resources["ContentDialogMaxWidth"] = 700.0;
        try { if (await d.ShowAsync() == ContentDialogResult.Secondary) ProcessActions.ShowInExplorer(path); } catch { }
    }
}
