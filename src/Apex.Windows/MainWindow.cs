using System.Diagnostics;
using Apex.Core;
using Apex.Services;
using Apex.UI;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Apex;

/// The one window, laid out like Task Manager (navigation on the left, search
/// in the title bar, each page's title and actions on top), styled like
/// macOS (translucent sidebar, rounded pills and cards, large bold titles).
public sealed class MainWindow : Window
{
    static MainWindow? current;
    readonly Grid titleBar = new() { Height = 52 };
    readonly Sidebar tabs;
    readonly TextBox search = new()
    {
        PlaceholderText = "Type a name, publisher or PID to search", Width = 420, CornerRadius = new CornerRadius(8),
        VerticalAlignment = VerticalAlignment.Center,
    };
    readonly ContentControl host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly IPage[] pages = { new ProcessesPage(), new PerformancePage(), new AssistantPage(), new GamingPage(), new StartupPage(), new UsersPage(), new ServicesPage() };
    /// Page keys, for the Assistant's buttons and --page.
    static readonly string[] Keys = { "processes", "performance", "assistant", "gaming", "startup", "users", "services" };
    static readonly (string Name, string Glyph)[] Pages =
    {
        ("Processes", ""), ("Performance", ""), ("Assistant", "\uE99A"), ("Gaming", "\uE7FC"), ("Startup apps", ""), ("Users", ""), ("Services", ""),
    };
    readonly ColumnDefinition rightInset = new() { Width = new GridLength(140) };
    readonly Sampler sampler = new();
    int page;
    Tray? tray;
    TrayPanel? trayPanel;
    bool quitting;

    public MainWindow()
    {
        current = this;
        Title = "Apex Activity Manager";
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        FitToScreen();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Apex.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        // Title bar, like Task Manager: icon + name | search (centred) | caption buttons.
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(rightInset);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Apex.png"))), Width = 20, Height = 20 });
        brand.Children.Add(new TextBlock { Text = "Apex Activity Manager", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        titleBar.Children.Add(brand);
        Grid.SetColumn(search, 1); titleBar.Children.Add(search);
        search.TextChanged += (_, _) =>
        {
            ((ProcessesPage)pages[0]).SetSearch(search.Text);
            if (search.Text.Length > 0 && page != 0) tabs!.Select(0);
        };

        tabs = new Sidebar(Pages);
        tabs.SelectionChanged += Show;
        var settings = Menu();
        tabs.SettingsClicked += anchor => settings.ShowAt(anchor);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumnSpan(titleBar, 2); root.Children.Add(titleBar);
        Grid.SetRow(tabs, 1); root.Children.Add(tabs);
        // Pages sit on a slightly raised layer with a rounded top-left corner, as Windows 11 apps do.
        var stage = new Border
        {
            Child = host, Background = Theme.Res("LayerFillColorDefaultBrush"), CornerRadius = new CornerRadius(10, 0, 0, 0),
            BorderBrush = Theme.CardStroke, BorderThickness = new Thickness(1, 1, 0, 0),
        };
        Grid.SetRow(stage, 1); Grid.SetColumn(stage, 1); root.Children.Add(stage);
        Content = root;
        SetTitleBar(titleBar);

        // Ctrl+1…5 switch pages, Ctrl+F searches.
        for (int i = 0; i < Pages.Length; i++)
        {
            int index = i;
            var acc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Number1 + i, Modifiers = Windows.System.VirtualKeyModifiers.Control };
            acc.Invoked += (_, e) => { tabs.Select(index); e.Handled = true; };
            root.KeyboardAccelerators.Add(acc);
        }
        var findAcc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        findAcc.Invoked += (_, e) => { search.Focus(FocusState.Keyboard); e.Handled = true; };
        root.KeyboardAccelerators.Add(findAcc);

        titleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        titleBar.Loaded += (_, _) => UpdateTitleBarRegions();
        // --page=N opens on that page (used for the build's screenshots).
        int start = Environment.GetCommandLineArgs().Select(a => a.StartsWith("--page=") && int.TryParse(a[7..], out var n) ? n : -1).FirstOrDefault(n => n >= 0, 0);
        tabs.Select(Math.Clamp(start, 0, Pages.Length - 1), notify: false);
        Show(tabs.Selected);

        sampler.Updated += s => Ui(() =>
        {
            AppState.Record(s);
            Efficiency.Keep(s.Processes);
            GameMode.Tick(s);
            if (AppWindow.IsVisible) pages[page].Update(s);
            trayPanel?.Update(s);
            tray?.SetTip($"Apex · CPU {Fmt.Percent(s.Cpu.Total)} · Memory {Fmt.Percent(s.Memory.Percent)}");
        });
        Efficiency.Start();
        ClipboardHistory.Shared.Start();
        StartTray();
        sampler.Start();
        // --perftest: time each heavy page's update (with its layout) on real readings, then quit (the build's measurements).
        if (Environment.GetCommandLineArgs().Contains("--perftest")) _ = PerfTest();
        // Closing the window keeps Apex in the notification area (like the Mac app in the menu bar),
        // unless that's switched off; Quit is in the tray icon's menu.
        AppWindow.Closing += (_, e) =>
        {
            if (quitting || tray == null || !Settings.Get("keepInTray", true)) return;
            e.Cancel = true;
            AppWindow.Hide();
        };
        Closed += (_, _) => { sampler.Dispose(); tray?.Dispose(); };
    }

    void StartTray()
    {
        // --tray=N opens the panel on tab N; --dialog=freeup opens Free Up Memory (both for the build's screenshots).
        var args = Environment.GetCommandLineArgs();
        var trayArg = args.FirstOrDefault(a => a.StartsWith("--tray="));
        if (trayArg != null && int.TryParse(trayArg[7..], out int t)) Settings.Set("trayTab", t);
        tray = new Tray();
        trayPanel = new TrayPanel();
        trayPanel.OpenApp += ShowWindow;
        tray.Clicked += () => trayPanel.Toggle();
        tray.OpenRequested += ShowWindow;
        tray.QuitRequested += Quit;
        if (trayArg != null)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => trayPanel.Toggle());
        if (args.Contains("--dialog=freeup") && Content is FrameworkElement root)
            root.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = FreeUpMemory.Show(root));
        if (args.Contains("--dialog=storage") && Content is FrameworkElement root2)
            root2.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = StorageView.Show(root2));
        if (args.Contains("--dialog=analyze") && Content is FrameworkElement root3)
            root3.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = StorageView.Show(root3, analyze: true));
        // After a few readings, so it shows this PC's real figures.
        if (args.Contains("--dialog=compression") && Content is FrameworkElement root4)
            root4.Loaded += async (_, _) => { await Task.Delay(4000); _ = MemoryCompression.Show(root4, AppState.Latest.Memory); };
    }

    async Task PerfTest()
    {
        var lines = new List<string>();
        await Task.Delay(6000);                                     // a few real readings first
        foreach (var i in new[] { 0, 1, 5, 6 })
        {
            tabs.Select(i, notify: false); Show(i);
            await Task.Delay(2500);                                 // its lists loaded
            var times = new List<double>();
            for (int n = 0; n < 40; n++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                pages[i].Update(AppState.Latest);
                (Content as FrameworkElement)?.UpdateLayout();
                times.Add(sw.Elapsed.TotalMilliseconds);
                await Task.Delay(30);
            }
            times.Sort();
            lines.Add($"PERF {Keys[i]}: average {times.Average():0.0} ms, median {times[times.Count / 2]:0.0} ms, worst {times[^1]:0.0} ms per update (with layout), {AppState.Latest.Processes.Count} processes");
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "apex-perftest.txt"), lines);
        Environment.Exit(0);
    }

    void ShowWindow()
    {
        AppWindow.Show();
        Activate();
        pages[page].Update(AppState.Latest);
    }

    void Quit()
    {
        quitting = true;
        StayAwake.Stop();
        GameMode.Stop();
        FrameMonitor.StopAll();
        Efficiency.RestoreAll(AppState.Latest.Processes);      // nothing stays slowed down after Apex quits
        tray?.Dispose(); tray = null;
        Application.Current.Exit();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

    /// A comfortable size for the display (scaled for its DPI), never bigger
    /// than the screen, centred. --maximize opens it maximised.
    void FitToScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        int w = (int)Math.Min(1280 * scale, area.Width * 0.94), h = (int)Math.Min(820 * scale, area.Height * 0.94);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        if (Environment.GetCommandLineArgs().Contains("--maximize") && AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
    }

    void Show(int index)
    {
        if (index != page) pages[page].Hidden();
        page = index;
        var v = pages[index].View;
        host.Content = v;
        Motion.FadeIn(v);
        pages[index].Shown();
    }

    /// The search box takes clicks; the rest of the title bar drags the window.
    void UpdateTitleBarRegions()
    {
        if (Content?.XamlRoot == null) return;
        double scale = Content.XamlRoot.RasterizationScale;
        rightInset.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
        var b = search.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, search.ActualWidth, search.ActualHeight));
        var rect = new RectInt32((int)(b.X * scale), (int)(b.Y * scale), (int)(b.Width * scale), (int)(b.Height * scale));
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, new[] { rect });
    }

    MenuFlyout Menu()
    {
        var m = new MenuFlyout();
        var speed = new MenuFlyoutSubItem { Text = "Update speed", Icon = new FontIcon { Glyph = "" } };
        foreach (var (label, secs) in new[] { ("High (½ second)", 0.5), ("Normal (1 second)", 1.0), ("Low (4 seconds)", 4.0) })
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "speed", IsChecked = secs == 1.0 };
            item.Click += (_, _) => sampler.Interval = TimeSpan.FromSeconds(secs);
            speed.Items.Add(item);
        }
        m.Items.Add(speed);
        var keep = new ToggleMenuFlyoutItem { Text = "Keep running in the notification area", IsChecked = Settings.Get("keepInTray", true) };
        keep.Click += (_, _) => Settings.Set("keepInTray", keep.IsChecked);
        m.Items.Add(keep);
        var top = new ToggleMenuFlyoutItem { Text = "Always on top", Icon = new FontIcon { Glyph = "" } };
        top.Click += (_, _) => { if (AppWindow.Presenter is OverlappedPresenter p) p.IsAlwaysOnTop = top.IsChecked; };
        m.Items.Add(top);
        m.Items.Add(new MenuFlyoutSeparator());
        var admin = new MenuFlyoutItem { Text = "Restart as administrator", Icon = new FontIcon { Glyph = "" } };
        admin.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
                Application.Current.Exit();
            }
            catch { }
        };
        m.Items.Add(admin);
        var about = new MenuFlyoutItem { Text = "About Apex", Icon = new FontIcon { Glyph = "" } };
        about.Click += (_, _) => _ = ShowMessage("Apex Activity Manager",
            $"Version {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\nBy CyberNet Labs · designed and built by sudo-user\n\nSee what your PC is doing, in plain words.");
        m.Items.Add(about);
        return m;
    }

    /// Opens a page by key ("gaming", "processes"…), bringing the window forward.
    public static void Navigate(string key)
    {
        if (current is not { } w) return;
        int i = Array.IndexOf(Keys, key);
        if (i < 0) return;
        w.ShowWindow();
        w.tabs.Select(i);
    }

    /// Runs on the UI thread.
    public static void Ui(Action a) => current?.DispatcherQueue.TryEnqueue(() => a());

    public static async Task ShowMessage(string title, string text)
    {
        if (current?.Content?.XamlRoot is not { } root) return;
        var d = new ContentDialog { Title = title, Content = text, CloseButtonText = "OK", XamlRoot = root, DefaultButton = ContentDialogButton.Close };
        try { await d.ShowAsync(); } catch { }
    }
}
