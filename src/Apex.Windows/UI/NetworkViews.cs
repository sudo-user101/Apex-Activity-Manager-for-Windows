using Apex.Core;
using Apex.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Apex.UI;

/// The Network page's extra panels, as on the Mac: connections grouped by
/// app (with Pause), ports in use (Free Port), the public IP, a speed test,
/// and Network Doctor.
internal sealed class NetworkPanels
{
    public readonly StackPanel View = new() { Spacing = 14 };
    readonly StackPanel traffic = new() { Spacing = 2 };
    readonly StackPanel ports = new() { Spacing = 2 };
    readonly TextBlock trafficNote = Theme.Text("", 12, Theme.Secondary);
    readonly ToggleButton pause = new() { Content = "Pause" };
    readonly ToggleSwitch hideLocal = new() { OnContent = "Hide this PC’s own", OffContent = "Hide this PC’s own", IsOn = true };
    DateTime lastRead;
    DateTime? pausedAt;

    public NetworkPanels()
    {
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Theme.Text("Traffic: connections by app", 16, null, bold: true));
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        controls.Children.Add(hideLocal); controls.Children.Add(pause);
        Grid.SetColumn(controls, 1); head.Children.Add(controls);
        var t = new StackPanel { Spacing = 8 };
        t.Children.Add(head); t.Children.Add(trafficNote);
        t.Children.Add(new ScrollViewer { Content = traffic, MaxHeight = 320 });
        View.Children.Add(Theme.Card(t));
        pause.Click += (_, _) => { pausedAt = pause.IsChecked == true ? DateTime.Now : null; Refresh(force: true); };
        hideLocal.Toggled += (_, _) => Refresh(force: true);

        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(Theme.Text("Ports in use", 16, null, bold: true));
        p.Children.Add(Theme.Text("Apps on this PC waiting for connections. Free Port ends the app that holds it.", 12, Theme.Secondary));
        p.Children.Add(new ScrollViewer { Content = ports, MaxHeight = 240 });
        View.Children.Add(Theme.Card(p));
    }

    /// Every 2 s while the page is on screen (and not paused).
    public void Refresh(bool force = false)
    {
        if (!force && (pausedAt != null || DateTime.Now - lastRead < TimeSpan.FromSeconds(2))) return;
        lastRead = DateTime.Now;
        var procs = AppState.Latest.Processes.ToDictionary(p => p.Pid, p => p);
        _ = Task.Run(NetworkTools.Connections).ContinueWith(task => MainWindow.Ui(() =>
        {
            if (task.Result is not { } all) return;
            string Name(int pid) => procs.TryGetValue(pid, out var s) ? (s.DisplayName.Length > 0 ? s.DisplayName : s.Name) : pid == 4 ? "System" : $"Process {pid}";

            // Connections, grouped by app.
            var open = all.Where(c => !c.Listening && c.Remote.Length > 0 && !(hideLocal.IsOn && c.LocalOnly)).ToList();
            traffic.Children.Clear();
            foreach (var g in open.GroupBy(c => c.Pid).OrderByDescending(g => g.Count()).Take(25))
            {
                var exp = new Expander { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                exp.Header = $"{Name(g.Key)}  ·  {g.Count()} connection{(g.Count() == 1 ? "" : "s")}";
                var inner = new StackPanel { Spacing = 2 };
                foreach (var c in g.OrderBy(c => c.Remote).Take(30))
                    inner.Children.Add(Theme.Text($"{c.Protocol}  {c.Remote}:{c.RemotePort}  ·  {c.State}", 12, Theme.Secondary));
                exp.Content = inner;
                traffic.Children.Add(exp);
            }
            if (open.Count == 0) traffic.Children.Add(Theme.Text("No connections right now.", 13, Theme.Secondary));
            trafficNote.Text = pausedAt is { } at ? $"Paused at {at:T} · the list stays still" : $"Live · {open.Count} connections from {open.Select(c => c.Pid).Distinct().Count()} apps";

            // Listening ports.
            ports.Children.Clear();
            foreach (var c in all.Where(c => c.Listening && c.Pid > 4).GroupBy(c => (c.Protocol, c.LocalPort)).Select(g => g.First()).OrderBy(c => c.LocalPort).Take(60))
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Theme.Text($"{c.Protocol} {c.LocalPort}", 13, null, bold: true));
                var who = Theme.Text($"{Name(c.Pid)}  ·  {(c.Local is "0.0.0.0" or "::" ? "any network" : c.Local)}", 13, Theme.Secondary);
                Grid.SetColumn(who, 1); row.Children.Add(who);
                if (procs.TryGetValue(c.Pid, out var owner) && owner.Kind != ProcessKind.Windows)
                {
                    var free = new Button { Content = "Free Port" };
                    var target = owner.Identity; var name = Name(c.Pid);
                    free.Click += (_, _) =>
                    {
                        var r = ProcessActions.End(new[] { target });
                        if (r.Message(new Dictionary<ProcessIdentity, string> { [target] = name }, "end") is { } m) _ = MainWindow.ShowMessage("Free Port", m);
                        Refresh(force: true);
                    };
                    Grid.SetColumn(free, 2); row.Children.Add(free);
                }
                ports.Children.Add(row);
            }
            if (ports.Children.Count == 0) ports.Children.Add(Theme.Text("No apps are waiting for connections.", 13, Theme.Secondary));
        }));
    }
}

/// Network Doctor: measures (about 10 s, nothing changed), then plain findings.
internal static class NetworkDoctorView
{
    public static async Task Show(FrameworkElement anchor, string adapterId, string kind)
    {
        var body = new StackPanel { Spacing = 10, Width = 640 };
        var progress = Theme.Text("Checking this PC’s connection…", 14, Theme.Secondary);
        body.Children.Add(progress);
        body.Children.Add(new ProgressRing { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Left });
        var d = new ContentDialog { Title = "Network Doctor", Content = new ScrollViewer { Content = body, MaxHeight = 560 }, CloseButtonText = "Done", XamlRoot = anchor.XamlRoot };
        d.Resources["ContentDialogMaxWidth"] = 760.0;
        var shown = d.ShowAsync();
        var input = await NetworkTools.Probe(adapterId, kind, NetworkState.LastSpeed?.DownBitsPerSec, new Progress<string>(t => progress.Text = t));
        body.Children.Clear();
        foreach (var f in NetworkDoctor.Diagnose(input)) body.Children.Add(Card(f));
        if (NetworkState.LastSpeed == null)
            body.Children.Add(Theme.Text("No speed test yet: use Speed test on the Network page to add one.", 12, Theme.Secondary));
        try { await shown; } catch { }
    }

    static UIElement Card(Finding f)
    {
        var p = new StackPanel { Spacing = 6 };
        var color = f.Severity switch { Severity.Critical => Theme.Red, Severity.Warning => Theme.Yellow, Severity.Ok => Theme.Green, _ => Theme.Gpu };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 10, Height = 10, Fill = Theme.Solid(color), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(Theme.Text(f.Title, 15, null, bold: true));
        p.Children.Add(title);
        var why = Theme.Text(f.Why, 13, Theme.Secondary); why.TextWrapping = TextWrapping.Wrap; p.Children.Add(why);
        if (f.Evidence.Count > 0) p.Children.Add(Theme.Text(string.Join("  ·  ", f.Evidence.Select(e => $"{e.Label}: {e.Value}")), 12, Theme.Secondary));
        if (f.Causes.Count > 0) { var c = Theme.Text("Likely: " + string.Join("; ", f.Causes), 12); c.TextWrapping = TextWrapping.Wrap; p.Children.Add(c); }
        int n = 1;
        foreach (var s in f.Steps) { var t = Theme.Text($"{n++}. {s}", 13); t.TextWrapping = TextWrapping.Wrap; p.Children.Add(t); }
        if (f.Action.Length > 0)
        {
            var b = new Button { Content = f.Action switch { "sign-in" => "Open the Sign-In Page", "wifi-settings" => "Wi-Fi Settings", _ => "Network Settings" } };
            b.Click += (_, _) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(f.Action switch
            {
                "sign-in" => "http://www.msftconnecttest.com/redirect", "wifi-settings" => "ms-settings:network-wifi", _ => "ms-settings:network-status",
            }));
            p.Children.Add(b);
        }
        return Theme.Card(p, 12);
    }
}

/// Results kept while Apex runs.
internal static class NetworkState
{
    public static NetworkTools.Speed? LastSpeed;
    public static string? PublicIp;
    public static bool ShowPublicIp = Settings.Get("showPublicIp", false);
}
