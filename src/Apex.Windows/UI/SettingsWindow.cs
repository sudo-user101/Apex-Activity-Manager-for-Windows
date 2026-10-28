using Apex.Core;
using Apex.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.UI;

namespace Apex.UI;

/// Apex › Settings, as on the Mac: sections on the left, the chosen one on the
/// right, one fixed size so switching never resizes the window. Utilities and
/// Windows Tweaks are a list and a page, like Mail's accounts. Everything is
/// read live, so a change made elsewhere in Apex shows here too.
internal sealed class SettingsWindow : Window
{
    static SettingsWindow? open;
    public const int Width = 940, Height = 640;

    readonly NavigationView nav = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Left, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsSettingsVisible = false, IsPaneToggleButtonVisible = false, OpenPaneLength = 210,
    };
    static readonly (string Key, string Title, string Glyph)[] Sections =
    {
        ("general", "General", ""), ("utilities", "Utilities", ""), ("tweaks", "Windows Tweaks", ""),
        ("alerts", "Alerts", "\uEA8F"), ("history", "History", "\uE81C"),
        ("privacy", "Privacy", ""), ("permissions", "Permissions", ""),
    };

    /// Opens Settings (or brings it forward), on a section if given.
    public static void Show(string? section = null)
    {
        if (open == null) { open = new SettingsWindow(); open.Closed += (_, _) => open = null; }
        if (section != null) open.Go(section);
        open.Activate();
    }

    SettingsWindow()
    {
        Title = "Apex Settings";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Width, Height));
        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsResizable = false; p.IsMaximizable = false; }
        foreach (var (key, title, glyph) in Sections)
            nav.MenuItems.Add(new NavigationViewItem { Content = title, Tag = key, Icon = new FontIcon { Glyph = glyph } });
        nav.SelectionChanged += (_, e) => { if (e.SelectedItem is NavigationViewItem i) nav.Content = Page((string)i.Tag); };
        Content = nav;
        // --settings=tweaks opens on that section (the build's screenshots).
        var arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--settings="))?[11..];
        Go(arg ?? Settings.Get("settingsSection", "general"));
    }

    void Go(string key)
    {
        var item = nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == key) ?? (NavigationViewItem)nav.MenuItems[0];
        nav.SelectedItem = item;
        Settings.Set("settingsSection", (string)item.Tag);
    }

    UIElement Page(string key) => key switch
    {
        "utilities" => UtilitiesPage(),
        "tweaks" => TweaksPage(),
        "privacy" => Scrolling(PrivacyPage()),
        "alerts" => Scrolling(AlertsPage()),
        "history" => Scrolling(HistorySettingsPage()),
        "permissions" => Scrolling(PermissionsPage()),
        _ => Scrolling(GeneralPage()),
    };

    static UIElement Scrolling(UIElement e) => new ScrollViewer { Content = e, Padding = new Thickness(28, 20, 28, 20) };

    // ---- Small building blocks ----------------------------------------------------------

    static StackPanel Stack() => new() { Spacing = 14, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };

    static UIElement Section(string title, params UIElement[] rows)
    {
        var p = new StackPanel { Spacing = 8 };
        if (title.Length > 0) p.Children.Add(Theme.Text(title, 14, bold: true));
        var inner = new StackPanel { Spacing = 12 };
        foreach (var r in rows) inner.Children.Add(r);
        p.Children.Add(Theme.Card(inner, 14));
        return p;
    }

    /// A row: a label with an optional caption, and a control on the right.
    static UIElement Line(string label, string caption, UIElement control)
    {
        var g = new Grid { ColumnSpacing = 16 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(Theme.Text(label, 14));
        if (caption.Length > 0) { var c = Theme.Text(caption, 12, Theme.Secondary); c.TextWrapping = TextWrapping.Wrap; t.Children.Add(c); }
        g.Children.Add(t);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn((FrameworkElement)control, 1); g.Children.Add(control);
        return g;
    }

    static ToggleSwitch Switch(bool on, Action<bool> changed)
    {
        var t = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0 };
        t.Toggled += (_, _) => changed(t.IsOn);
        return t;
    }

    /// An icon on a rounded square of its colour, like Windows Settings.
    static Border Badge(string glyph, Color color, double size)
    {
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.24), Background = new SolidColorBrush(color),
            Child = new FontIcon { Glyph = glyph, FontSize = size * 0.5, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
        };
    }

    /// The list-and-page layout (Utilities, Windows Tweaks).
    static Grid ListAndPage(IList<(string Title, string Glyph, Color Color, Func<string> Status)> items, int selected,
                            Func<int, UIElement> page, Action<int> chosen, UIElement? footer = null)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new Grid { Padding = new Thickness(8, 12, 8, 8), RowSpacing = 8 };
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single };
        var statuses = new List<TextBlock>();
        foreach (var (title, glyph, color, status) in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(0, 4, 0, 4) };
            row.Children.Add(Badge(glyph, color, 28));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Theme.Text(title, 14));
            var st = Theme.Text(status(), 12, Theme.Secondary);
            statuses.Add(st);
            text.Children.Add(st);
            row.Children.Add(text);
            list.Items.Add(row);
        }
        left.Children.Add(list);
        if (footer != null) { Grid.SetRow((FrameworkElement)footer, 1); left.Children.Add(footer); }
        g.Children.Add(left);
        var right = new ScrollViewer { Padding = new Thickness(24, 18, 24, 18) };
        Grid.SetColumn(right, 1); g.Children.Add(right);
        void Refresh() { for (int i = 0; i < items.Count; i++) statuses[i].Text = items[i].Status(); }
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            chosen(list.SelectedIndex);
            right.Content = page(list.SelectedIndex);
        };
        list.SelectedIndex = Math.Clamp(selected, 0, items.Count - 1);
        g.Tag = (Action)Refresh;           // pages call it after a change
        return g;
    }

    static UIElement Header(string glyph, Color color, string title, string blurb, UIElement? trailing = null)
    {
        var h = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 0, 0, 12) };
        h.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        h.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        h.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        h.Children.Add(Badge(glyph, color, 44));
        var t = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(Theme.Text(title, 22, bold: true));
        var b = Theme.Text(blurb, 13, Theme.Secondary); b.TextWrapping = TextWrapping.Wrap; t.Children.Add(b);
        Grid.SetColumn(t, 1); h.Children.Add(t);
        if (trailing is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(fe, 2); h.Children.Add(fe); }
        return h;
    }

    // ---- General ------------------------------------------------------------------------

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static bool StartsAtSignIn
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Apex Activity Manager") != null; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Apex Activity Manager", $"\"{Environment.ProcessPath}\" --background");
            else k.DeleteValue("Apex Activity Manager", throwOnMissingValue: false);
        }
    }

    UIElement GeneralPage()
    {
        var s = Stack();
        var speed = new ComboBox { MinWidth = 200 };
        var speeds = new[] { ("High (½ second)", 0.5), ("Normal (1 second)", 1.0), ("Low (4 seconds)", 4.0) };
        foreach (var (label, _) in speeds) speed.Items.Add(label);
        speed.SelectedIndex = Math.Max(0, Array.FindIndex(speeds, x => x.Item2 == Settings.Get("updateSeconds", 1.0)));
        speed.SelectionChanged += (_, _) => MainWindow.SetUpdateSeconds(speeds[speed.SelectedIndex].Item2);
        s.Children.Add(Section("Updates",
            Line("Update speed", "How often the numbers refresh. Less often uses less energy.", speed)));
        s.Children.Add(Section("Starting and closing",
            Line("Start Apex when you sign in", "It starts in the notification area, out of the way.", Switch(StartsAtSignIn, on => StartsAtSignIn = on)),
            Line("Keep running in the notification area", "Closing the window keeps Apex by the clock; Quit is in its icon’s menu.",
                 Switch(Settings.Get("keepInTray", true), on => Settings.Set("keepInTray", on))),
            Line("Always on top", "The Apex window stays above other windows.",
                 Switch(Settings.Get("alwaysOnTop", false), on => MainWindow.SetAlwaysOnTop(on)))));
        return s;
    }

    // ---- Utilities ----------------------------------------------------------------------

    UIElement UtilitiesPage()
    {
        Grid? layout = null;
        void Changed() => (layout?.Tag as Action)?.Invoke();
        var items = new List<(string, string, Color, Func<string>)>
        {
            ("Clipboard History", "", Color.FromArgb(255, 255, 149, 0), () => ClipboardHistory.Shared.Enabled ? "On" : "Off"),
            ("Stay Awake", "", Color.FromArgb(255, 162, 132, 94), () => StayAwake.IsOn ? (StayAwake.Until is { } u ? $"Until {u:t}" : "On until turned off") : "Off"),
        };
        UIElement Page(int i)
        {
            var p = new StackPanel { Spacing = 14 };
            if (i == 0)
            {
                p.Children.Add(Header("", items[0].Item3, "Clipboard History", "Your last 50 copies, in the notification area panel.",
                    Switch(ClipboardHistory.Shared.Enabled, on => { ClipboardHistory.Shared.Enabled = on; Changed(); })));
                var clear = new Button { Content = "Clear Clipboard History" };
                clear.Click += (_, _) => ClipboardHistory.Shared.Clear();
                p.Children.Add(Section("", Line("Kept in memory only", "Copies from password managers are never kept. Windows’ own history (Win+V) is separate.", clear)));
            }
            else
            {
                p.Children.Add(Header("", items[1].Item3, "Stay Awake", "Keeps this PC from sleeping for a while."));
                var length = new ComboBox { MinWidth = 160 };
                var lengths = new (string, TimeSpan?)[] { ("15 minutes", TimeSpan.FromMinutes(15)), ("1 hour", TimeSpan.FromHours(1)), ("2 hours", TimeSpan.FromHours(2)), ("5 hours", TimeSpan.FromHours(5)), ("Until turned off", null) };
                foreach (var (l, _) in lengths) length.Items.Add(l);
                length.SelectedIndex = 1;
                var display = new CheckBox { Content = "Keep the display on too", IsChecked = StayAwake.KeepDisplayOn };
                var go = new Button { Content = StayAwake.IsOn ? "Turn Off" : "Start", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                go.Click += (_, _) =>
                {
                    if (StayAwake.IsOn) StayAwake.Stop(); else StayAwake.Start(lengths[length.SelectedIndex].Item2, display.IsChecked == true);
                    go.Content = StayAwake.IsOn ? "Turn Off" : "Start";
                    Changed();
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                row.Children.Add(length); row.Children.Add(go);
                p.Children.Add(Section("", Line("Keep this PC awake", "", row), display));
            }
            return p;
        }
        layout = ListAndPage(items, Settings.Get("settingsUtility", 0), Page, i => Settings.Set("settingsUtility", i));
        return layout;
    }

    // ---- Windows Tweaks -----------------------------------------------------------------

    static (string Glyph, Color Color) GroupLook(string g) => g switch
    {
        "File Explorer" => ("", Color.FromArgb(255, 242, 178, 0)),
        "Taskbar" => ("", Color.FromArgb(255, 0, 120, 212)),
        "Start & search" => ("", Color.FromArgb(255, 106, 90, 205)),
        "Windows" => ("", Color.FromArgb(255, 0, 153, 153)),
        "Mouse" => ("", Color.FromArgb(255, 232, 17, 123)),
        "Privacy" => ("", Color.FromArgb(255, 96, 125, 139)),
        _ => ("", Color.FromArgb(255, 16, 124, 16)),
    };

    UIElement TweaksPage()
    {
        Grid? layout = null;
        void Changed() => (layout?.Tag as Action)?.Invoke();
        int ChangedIn(string g) => WindowsTweaks.All.Count(t => t.Group == g && Tweaks.IsOn(t));
        var items = WindowsTweaks.Groups.Select(g =>
        {
            var (glyph, color) = GroupLook(g);
            return (g, glyph, color, (Func<string>)(() => ChangedIn(g) is var n && n == 0 ? "As Windows had it" : $"{n} changed"));
        }).ToList();
        UIElement Page(int i)
        {
            var g = WindowsTweaks.Groups[i];
            var (glyph, color) = GroupLook(g);
            var p = new StackPanel { Spacing = 14 };
            p.Children.Add(Header(glyph, color, g, "Settings Windows has but buries or doesn’t show. Only for you, no administrator; turning one off puts back what you had."));
            var rows = new List<UIElement>();
            foreach (var t in WindowsTweaks.All.Where(t => t.Group == g))
            {
                var tweak = t;
                var problem = Theme.Text("", 12, Theme.Solid(Theme.Yellow)); problem.TextWrapping = TextWrapping.Wrap;
                var sw = Switch(Tweaks.IsOn(t), on =>
                {
                    var why = Tweaks.Set(tweak, on);
                    problem.Text = why ?? "";
                    Changed();
                });
                var caption = string.Join(" ", new[] { t.Detail, t.Note }.Where(x => x.Length > 0));
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(Line(t.Title, caption, sw));
                row.Children.Add(problem);
                rows.Add(row);
            }
            p.Children.Add(Section("", rows.ToArray()));
            return p;
        }
        var undo = new Button { Content = "Undo All Changes…", HorizontalAlignment = HorizontalAlignment.Stretch };
        undo.Click += async (_, _) =>
        {
            var d = new ContentDialog
            {
                Title = "Undo every change made here?", Content = "Each setting goes back to what it was before Apex changed it. File Explorer and the taskbar may restart.",
                PrimaryButtonText = "Undo All Changes", CloseButtonText = "Cancel", XamlRoot = undo.XamlRoot,
            };
            if (await d.ShowAsync() == ContentDialogResult.Primary) { Tweaks.UndoAll(); Changed(); nav.Content = TweaksPage(); }
        };
        layout = ListAndPage(items, Settings.Get("settingsTweakGroup", 0), Page, i => Settings.Set("settingsTweakGroup", i), undo);
        return layout;
    }

    // ---- Alerts and History -------------------------------------------------------------

    static void Change(Func<DetectionSettings, DetectionSettings> f) => History.SaveSettings(f(History.Engine.Settings));

    UIElement AlertsPage()
    {
        var s = Stack();
        var d = History.Engine.Settings;
        s.Children.Add(Section("",
            Line("Alerts", "A Windows notification when a problem lasts. Shorter spikes are only recorded (App history › Events).",
                 Switch(History.AlertsOn, on => History.AlertsOn = on))));
        var cpu = new ComboBox { MinWidth = 110 };
        var levels = new[] { 50.0, 80, 100, 150, 200 };
        foreach (var l in levels) cpu.Items.Add($"{l:0}%");
        cpu.SelectedIndex = Math.Max(0, Array.IndexOf(levels, d.CpuThreshold));
        cpu.SelectionChanged += (_, _) => Change(x => x with { CpuThreshold = levels[cpu.SelectedIndex] });
        var mins = new ComboBox { MinWidth = 110 };
        var spans = new[] { 1.0, 3, 5, 10 };
        foreach (var m in spans) mins.Items.Add($"{m:0} min");
        mins.SelectedIndex = Math.Max(0, Array.IndexOf(spans, d.CpuMinutes));
        mins.SelectionChanged += (_, _) => Change(x => x with { CpuMinutes = spans[mins.SelectedIndex] });
        var cpuRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        cpuRow.Children.Add(cpu); cpuRow.Children.Add(Theme.Text("for", 13, Theme.Secondary)); cpuRow.Children.Add(mins);
        s.Children.Add(Section("Notify me",
            Line("An app keeps the CPU busy", "100% is one whole core (as on the Mac). Most apps use far less.",
                 Switch(d.AppCpu, on => Change(x => x with { AppCpu = on }))),
            Line("…over", "", cpuRow),
            Line("Memory runs critically short", "", Switch(d.Memory, on => Change(x => x with { Memory = on }))),
            Line("An app’s memory keeps growing, or the page file grows fast", "A steady climb for half an hour or more often means a memory leak.",
                 Switch(d.MemoryGrowth, on => Change(x => x with { MemoryGrowth = on }))),
            Line("Your disk is almost full or filling up fast", "", Switch(d.Disk, on => Change(x => x with { Disk = on }))),
            Line("Your PC is running hot", "Only where this PC reports heat to Windows; many don’t.",
                 Switch(d.Heat, on => Change(x => x with { Heat = on })))));
        return s;
    }

    UIElement HistorySettingsPage()
    {
        var s = Stack();
        var d = History.Engine.Settings;
        s.Children.Add(Section("Record",
            Line("Short spikes", "Bursts that settle before they’d alert, so you can see what happened.",
                 Switch(d.RecordSpikes, on => Change(x => x with { RecordSpikes = on }))),
            Line("Heavy disk writing", "Over 100 MB/s for 2 minutes or more.", Switch(d.DiskWrites, on => Change(x => x with { DiskWrites = on }))),
            Line("Heavy network traffic", "Over 10 MB/s for 2 minutes or more.", Switch(d.Network, on => Change(x => x with { Network = on })))));
        var clearUsage = new Button { Content = "Clear App Usage" };
        clearUsage.Click += (_, _) => History.ClearUsage();
        var clearEvents = new Button { Content = "Clear Events" };
        clearEvents.Click += (_, _) => History.ClearEvents();
        s.Children.Add(Section("Kept on this PC",
            Line("App usage", $"What each app used, per day, for {AppHistory.KeepDays} days.", clearUsage),
            Line("Events", $"The last {History.KeepEvents} alerts and spikes.", clearEvents)));
        return s;
    }

    // ---- Privacy and Permissions --------------------------------------------------------

    UIElement PrivacyPage()
    {
        var s = Stack();
        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) => ClipboardHistory.Shared.Clear();
        var signOut = new Button { Content = "Sign Out", IsEnabled = LumenAccount.HasKey };
        signOut.Click += (_, _) => { LumenAccount.SignOut(); signOut.IsEnabled = false; };
        s.Children.Add(Section("What Apex keeps",
            Line("Clipboard history", "In memory only, gone when Apex quits. Copies from password managers are never kept.", clear),
            Line("Lumen AI key", "In Windows’ Credential Manager, never in Apex’s settings file.", signOut),
            Line("Settings", "In your user folder (AppData › Local › Apex). Nothing is sent anywhere.", new TextBlock())));
        s.Children.Add(Section("What leaves this PC",
            Line("Nothing, unless you ask", "Apex Agent and Local AI answer on this PC. Lumen AI, the speed test and the public IP lookup only go out when you use them.", new TextBlock())));
        return s;
    }

    UIElement PermissionsPage()
    {
        var s = Stack();
        bool admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        var restart = new Button { Content = admin ? "Running as administrator" : "Restart as Administrator", IsEnabled = !admin };
        restart.Click += (_, _) => MainWindow.RestartAsAdmin();
        s.Children.Add(Section("Administrator",
            Line("Apps and services of other users and Windows itself", "Ending or changing them, and some cleaning, need administrator. Windows asks each time otherwise.", restart)));
        s.Children.Add(Section("Frame rates",
            Line("Reading games’ frame rates", FrameMonitor.Status == FrameMonitor.State.Running ? "Allowed: Apex can read frame rates." : "Windows needs Apex to be in the Performance Log Users group, or to run as administrator. The Gaming page asks when you first use it.",
                 new TextBlock())));
        return s;
    }
}
