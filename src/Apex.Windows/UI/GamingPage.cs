using Apex.Core;
using Apex.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Apex.UI;

/// Gaming: live frame rates for every app that's drawing, and Game mode
/// (quiet other apps, fastest power plan, game at high priority, watch the
/// frame rate against a target and say what holds it back).
internal sealed class GamingPage : IPage
{
    public UIElement View => scroll;
    readonly ScrollViewer scroll = new();
    readonly StackPanel root = new() { Padding = new Thickness(28, 16, 28, 24), Spacing = 14 };
    readonly TextBlock subtitle = Theme.Text("Frame rates and Game mode", 14, Theme.Secondary);
    readonly TextBlock monitorStatus = Theme.Text("", 13, Theme.Secondary);
    readonly Button allow = new() { Content = "Allow reading frame rates", Visibility = Visibility.Collapsed };
    readonly StackPanel drawing = new() { Spacing = 2 };

    // Game mode: set-up and live.
    readonly StackPanel setup = new() { Spacing = 10 };
    readonly ComboBox game = new() { PlaceholderText = "Choose the game", MinWidth = 320 };
    readonly ComboBox target = new() { MinWidth = 120 };
    readonly StackPanel quietList = new() { Spacing = 2 };
    readonly Button start = new() { Content = "Start Game mode" };
    readonly StackPanel live = new() { Spacing = 10, Visibility = Visibility.Collapsed };
    readonly TextBlock liveTitle = Theme.Text("", 18, null, bold: true);
    readonly TextBlock fps = Theme.Text("", 40, null, bold: true), low = Theme.Text("", 14, Theme.Secondary), advice = Theme.Text("", 14);
    readonly Graph graph = new(Theme.Green) { Height = 160 };
    readonly Button stop = new() { Content = "Stop Game mode" };
    List<ApexAgent.Item> gameChoices = new();
    bool visible;

    public GamingPage()
    {
        scroll.Content = root;
        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(Theme.Title("Gaming")); head.Children.Add(subtitle);
        root.Children.Add(head);

        // Frame rate now.
        var frames = new StackPanel { Spacing = 8 };
        frames.Children.Add(Theme.Text("Apps drawing right now", 13, Theme.Secondary));
        frames.Children.Add(drawing);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        monitorStatus.TextWrapping = TextWrapping.Wrap; monitorStatus.MaxWidth = 640;
        status.Children.Add(monitorStatus); status.Children.Add(allow);
        frames.Children.Add(status);
        root.Children.Add(Theme.Card(frames));
        allow.Click += async (_, _) =>
        {
            bool ok = await Task.Run(FrameMonitor.GrantPermission);
            await MainWindow.ShowMessage("Frame rates", ok
                ? "Done. Windows applies it after you sign out and in again (or restart). Until then, Apex can read frame rates when it runs as administrator."
                : "Windows didn’t make the change. You can also run Apex as administrator (⋯ › Restart as administrator).");
        };

        // Game mode set-up.
        foreach (var t in new[] { 30, 60, 75, 90, 120, 144, 165, 240 }) target.Items.Add($"{t} FPS");
        target.SelectedIndex = 1;
        var pick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        pick.Children.Add(game); pick.Children.Add(target);
        setup.Children.Add(Theme.Text("Game mode", 18, null, bold: true));
        var explain = Theme.Text("Apex quiets the apps below (Efficiency mode), switches Windows to its fastest power plan and gives the game high priority, then watches the frame rate. Everything goes back when you stop or close the game. No app can promise a frame rate: the game and the hardware decide; Apex shows how close it gets and why.", 13, Theme.Secondary);
        explain.TextWrapping = TextWrapping.Wrap;
        setup.Children.Add(explain);
        setup.Children.Add(pick);
        setup.Children.Add(Theme.Text("Quiet while you play (untick what should keep running, like voice chat):", 13, Theme.Secondary));
        setup.Children.Add(new ScrollViewer { Content = quietList, MaxHeight = 200 });
        setup.Children.Add(start);
        game.SelectionChanged += (_, _) => FillQuiet();
        start.Click += (_, _) => Start();

        live.Children.Add(liveTitle);
        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        numbers.Children.Add(fps); numbers.Children.Add(low);
        live.Children.Add(numbers);
        live.Children.Add(graph);
        advice.TextWrapping = TextWrapping.Wrap;
        live.Children.Add(advice);
        live.Children.Add(stop);
        graph.SetSeries((GameMode.Fps, Theme.Green, true));
        stop.Click += (_, _) => { GameMode.Stop(); Refresh(); };

        var gm = new StackPanel { Spacing = 10 };
        gm.Children.Add(setup); gm.Children.Add(live);
        root.Children.Add(Theme.Card(gm));

        FrameMonitor.Changed += () => MainWindow.Ui(() => { if (visible) Refresh(); });
        GameMode.Changed += () => MainWindow.Ui(() => { if (visible) Refresh(); });
    }

    public void Shown()
    {
        if (!visible) FrameMonitor.Acquire();
        visible = true;
        FillGames();
        Refresh();
    }

    public void Hidden()
    {
        if (visible) FrameMonitor.Release();
        visible = false;
    }

    public void Update(Snapshot s) => Refresh();

    void FillGames()
    {
        var r = AgentBridge.Readings();
        gameChoices = r.Apps.OrderByDescending(a => a.Fps).ThenByDescending(a => a.Cpu).ToList();
        var keep = game.SelectedIndex >= 0 && game.SelectedIndex < game.Items.Count ? game.SelectedItem as string : null;
        game.Items.Clear();
        foreach (var a in gameChoices) game.Items.Add(a.Name + (a.Fps > 0 ? $"  ·  {a.Fps:0} FPS" : ""));
        if (gameChoices.Count > 0) game.SelectedIndex = Math.Max(0, keep == null ? 0 : game.Items.IndexOf(keep));
        FillQuiet();
    }

    void FillQuiet()
    {
        quietList.Children.Clear();
        if (game.SelectedIndex < 0 || game.SelectedIndex >= gameChoices.Count) { start.IsEnabled = false; return; }
        start.IsEnabled = true;
        var g = gameChoices[game.SelectedIndex];
        foreach (var (path, name) in GameModePlan.ToQuiet(AppState.Latest.Processes, g.Path, new HashSet<string>(), Environment.ProcessId))
        {
            var file = path[(path.LastIndexOf('\\') + 1)..];
            var box = new CheckBox { Content = name, IsChecked = !GameMode.Keep.Contains(file), Tag = path };
            box.Click += (_, _) => { if (box.IsChecked == true) GameMode.Keep.Remove(file); else GameMode.Keep.Add(file); GameMode.SaveKeep(); };
            quietList.Children.Add(box);
        }
        if (quietList.Children.Count == 0) quietList.Children.Add(Theme.Text("No other apps are open.", 13, Theme.Secondary));
    }

    void Start()
    {
        if (game.SelectedIndex < 0 || game.SelectedIndex >= gameChoices.Count) return;
        var g = gameChoices[game.SelectedIndex];
        double t = double.Parse(((string)target.SelectedItem).Split(' ')[0]);
        var quiet = quietList.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (string)b.Tag);
        var result = GameMode.Start(g.Path, g.Name, t, quiet);
        _ = MainWindow.ShowMessage("Game mode", result);
        Refresh();
    }

    void Refresh()
    {
        // Frame rates now.
        monitorStatus.Text = FrameMonitor.Status switch
        {
            FrameMonitor.State.Running => "Live, from Windows’ graphics events (via Intel’s PresentMon).",
            FrameMonitor.State.Starting => "Starting…",
            FrameMonitor.State.Off => "",
            _ => FrameMonitor.Detail,
        };
        allow.Visibility = FrameMonitor.Status == FrameMonitor.State.NeedsPermission ? Visibility.Visible : Visibility.Collapsed;
        drawing.Children.Clear();
        var procs = AppState.Latest.Processes.ToDictionary(p => p.Pid, p => p.DisplayName.Length > 0 ? p.DisplayName : p.Name);
        var apps = FrameMonitor.Apps().Where(a => a.Pid != Environment.ProcessId).Take(8).ToList();
        foreach (var (pid, app, st) in apps)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.Children.Add(Theme.Text(procs.GetValueOrDefault(pid, app), 14));
            var f = Theme.Text($"{st.Fps:0} FPS", 14, null, bold: true); Grid.SetColumn(f, 1); row.Children.Add(f);
            var l = Theme.Text($"1% low {st.OnePercentLow:0} · {st.FrameTimeMs:0.0} ms", 13, Theme.Secondary); Grid.SetColumn(l, 2); row.Children.Add(l);
            drawing.Children.Add(row);
        }
        if (apps.Count == 0)
            drawing.Children.Add(Theme.Text(FrameMonitor.Status == FrameMonitor.State.Running ? "Nothing is drawing frames right now. Start a game." : "—", 13, Theme.Secondary));

        // Game mode.
        if (GameMode.Active is { } s)
        {
            setup.Visibility = Visibility.Collapsed; live.Visibility = Visibility.Visible;
            liveTitle.Text = $"{s.GameName} · target {s.TargetFps:0} FPS";
            var st = GameMode.Latest;
            fps.Text = st.Fps > 0 ? $"{st.Fps:0} FPS" : "–";
            fps.Foreground = Theme.Solid(st.Fps <= 0 ? Theme.Yellow : st.Fps >= s.TargetFps * 0.97 ? Theme.Green : st.Fps >= s.TargetFps * 0.8 ? Theme.Yellow : Theme.Red);
            low.Text = st.Fps > 0 ? $"1% low {st.OnePercentLow:0} FPS · frame time {st.FrameTimeMs:0.0} ms" : "";
            graph.Max = Math.Max(s.TargetFps * 1.5, GameMode.Fps.Max * 1.1);
            graph.Redraw();
            advice.Text = GameMode.Diagnosis.Advice;
            subtitle.Text = $"Game mode is on for {s.GameName}";
        }
        else
        {
            setup.Visibility = Visibility.Visible; live.Visibility = Visibility.Collapsed;
            subtitle.Text = "Frame rates and Game mode";
        }
    }
}
