using Apex.Core;
using Apex.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Apex.UI;

/// App History, as on the Mac: **App usage** (which apps used the most CPU
/// time, disk and memory, today or over 7 or 30 days) and **Events** (alerts
/// and spikes, each with why it fired and the numbers behind it).
internal sealed class HistoryPage : IPage
{
    public UIElement View => root;
    readonly Grid root = new() { Padding = new Thickness(28, 16, 28, 16), RowSpacing = 12 };
    readonly TextBlock subtitle = Theme.Text("", 14, Theme.Secondary);
    readonly RadioButtons mode = new() { MaxColumns = 2 };
    readonly ComboBox range = new() { MinWidth = 140 };
    readonly TextBox search = new() { PlaceholderText = "Search apps", Width = 220, CornerRadius = new CornerRadius(8) };
    readonly ComboBox kinds = new() { MinWidth = 160 };
    readonly Button clear = new() { Content = "Clear…" };
    readonly LazyRows rows = new();
    static readonly int[] Ranges = { 1, 7, 30 };
    DateTime lastFill;

    public HistoryPage()
    {
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(Theme.Title("App history"));
        titles.Children.Add(subtitle);
        head.Children.Add(titles);
        mode.Items.Add("App usage"); mode.Items.Add("Events");
        mode.SelectedIndex = Settings.Get("historyMode", 0);
        mode.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(mode, 1); head.Children.Add(mode);
        root.Children.Add(head);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var r in new[] { "Today", "Last 7 days", "Last 30 days" }) range.Items.Add(r);
        range.SelectedIndex = Settings.Get("historyRange", 0);
        foreach (var k in new[] { "All events", "Alerts only", "Spikes only" }) kinds.Items.Add(k);
        kinds.SelectedIndex = 0;
        bar.Children.Add(range); bar.Children.Add(search); bar.Children.Add(kinds); bar.Children.Add(clear);
        Grid.SetRow(bar, 1); root.Children.Add(bar);

        var card = Theme.Card(new ScrollViewer { Content = rows.View }, 0);
        Grid.SetRow(card, 2); root.Children.Add(card);

        mode.SelectionChanged += (_, _) => { Settings.Set("historyMode", mode.SelectedIndex); Fill(); };
        range.SelectionChanged += (_, _) => { Settings.Set("historyRange", range.SelectedIndex); Fill(); };
        search.TextChanged += (_, _) => Fill();
        kinds.SelectionChanged += (_, _) => Fill();
        clear.Click += async (_, _) =>
        {
            bool events = mode.SelectedIndex == 1;
            var d = new ContentDialog
            {
                Title = events ? "Clear the event history?" : "Clear the app history?",
                Content = events ? "Every recorded alert and spike is removed." : "The record of what each app used is removed. Apex starts recording again straight away.",
                PrimaryButtonText = "Clear", CloseButtonText = "Cancel", XamlRoot = root.XamlRoot,
            };
            if (await d.ShowAsync() != ContentDialogResult.Primary) return;
            if (events) History.ClearEvents(); else History.ClearUsage();
            Fill();
        };
        History.Changed += () => MainWindow.Ui(() => { if (mode.SelectedIndex == 1) Fill(); });
    }

    public void Shown() => Fill();
    // App usage changes every second; the list is refreshed at most every 10 s (it's a summary, not a live view).
    public void Update(Snapshot s) { if (mode.SelectedIndex == 0 && DateTime.Now - lastFill > TimeSpan.FromSeconds(10)) Fill(); }

    void Fill()
    {
        lastFill = DateTime.Now;
        bool events = mode.SelectedIndex == 1;
        range.Visibility = search.Visibility = events ? Visibility.Collapsed : Visibility.Visible;
        kinds.Visibility = events ? Visibility.Visible : Visibility.Collapsed;
        rows.Clear();
        if (events) FillEvents(); else FillUsage();
    }

    void FillUsage()
    {
        int days = Ranges[Math.Clamp(range.SelectedIndex, 0, 2)];
        var q = search.Text.Trim();
        var list = History.Usage.Totals(days, DateTime.Now)
            .Where(x => q.Length == 0 || x.Usage.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        subtitle.Text = History.Usage.Since is { } since
            ? $"{list.Count} apps · recorded since {since:d}. Kept for {AppHistory.KeepDays} days, on this PC only."
            : "Apex records what each app uses while it runs. Nothing yet.";
        double maxCpu = Math.Max(1, list.Select(x => x.Usage.CpuSeconds).DefaultIfEmpty(1).Max());
        rows.Add(() => UsageHeader());
        foreach (var (_, u) in list.Take(300))
        {
            var usage = u;
            rows.Add(() => UsageRow(usage, maxCpu));
        }
        if (list.Count == 0) rows.Add(() => Empty("No app usage recorded for this period yet."));
    }

    static Grid Columns()
    {
        var g = new Grid { Padding = new Thickness(14, 6, 14, 6), ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var w in new[] { 120, 110, 110, 130 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        return g;
    }

    static UIElement UsageHeader()
    {
        var g = Columns();
        string[] t = { "App", "CPU time", "Disk", "Peak memory", "Last used" };
        for (int i = 0; i < t.Length; i++)
        {
            var tb = Theme.Text(t[i], 12, Theme.Secondary);
            tb.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            Grid.SetColumn(tb, i); g.Children.Add(tb);
        }
        return g;
    }

    static UIElement UsageRow(AppUsageDay u, double maxCpu)
    {
        var g = Columns();
        g.MinHeight = 40;
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var icon = new Image { Width = 20, Height = 20 };
        if (u.Path.Length > 0) _ = Icons.Load(u.Path, icon);
        name.Children.Add(icon);
        name.Children.Add(new TextBlock { Text = u.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(name);
        string[] vals = { CpuTime(u.CpuSeconds), Fmt.Bytes(u.DiskBytes), Fmt.Memory(u.PeakMemory), u.LastSeen == default ? "" : Ago(u.LastSeen) };
        for (int i = 0; i < vals.Length; i++)
        {
            var tb = Theme.Text(vals[i], 13, i == 0 ? Theme.Primary : Theme.Secondary);
            tb.HorizontalAlignment = HorizontalAlignment.Right; tb.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(tb, i + 1); g.Children.Add(tb);
        }
        g.Background = Theme.HeatBrush(Heat.Level(u.CpuSeconds, maxCpu), false);
        g.BorderBrush = Theme.Res("DividerStrokeColorDefaultBrush"); g.BorderThickness = new Thickness(0, 0, 0, 1);
        return g;
    }

    /// "1 h 20 min" of one core's time.
    public static string CpuTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{t.Minutes} min {t.Seconds} s" : $"{t.Seconds} s";
    }

    static string Ago(DateTime d)
    {
        var t = DateTime.Now - d;
        return t.TotalMinutes < 2 ? "Now" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min ago" : t.TotalDays < 1 ? $"{(int)t.TotalHours} h ago" : d.ToString("d MMM");
    }

    void FillEvents()
    {
        var list = History.Events.Where(e => kinds.SelectedIndex switch { 1 => e.Notify, 2 => e.Kind == EventKind.Spike, _ => true }).ToList();
        subtitle.Text = list.Count == 0 ? "Alerts and spikes appear here, each with why it fired." : $"{list.Count} events · the last {History.KeepEvents} are kept.";
        foreach (var e in list)
        {
            var ev = e;
            rows.Add(() => EventRow(ev));
        }
        if (list.Count == 0) rows.Add(() => Empty("Nothing has happened that needed recording."));
    }

    static UIElement EventRow(DetectionEvent e)
    {
        var color = e.Severity switch { Severity.Critical => Theme.Red, Severity.Warning => Theme.Yellow, _ => Theme.Cpu };
        var label = e.Kind == EventKind.Spike ? "Spike" : e.Notify ? "Alert" : "Recorded";
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Theme.Pill(label, color));
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = e.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var sum = Theme.Text(e.Summary, 12, Theme.Secondary); sum.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(sum);
        Grid.SetColumn(text, 1); head.Children.Add(text);
        var when = Theme.Text(e.Date.ToString("d MMM, HH:mm"), 12, Theme.Secondary);
        Grid.SetColumn(when, 2); head.Children.Add(when);

        var details = new StackPanel { Spacing = 6, Padding = new Thickness(0, 6, 0, 0) };
        var why = Theme.Text("Why: " + e.Reason, 12, Theme.Secondary); why.TextWrapping = TextWrapping.Wrap;
        details.Children.Add(why);
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < e.Evidence.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Theme.Text(e.Evidence[i].Label, 12, Theme.Secondary); Grid.SetRow(l, i); grid.Children.Add(l);
            var v = Theme.Text(e.Evidence[i].Value, 12); Grid.SetRow(v, i); Grid.SetColumn(v, 1); grid.Children.Add(v);
        }
        details.Children.Add(grid);
        var exp = new Expander { Header = head, Content = details, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        return new Border { Child = exp, Padding = new Thickness(8, 4, 8, 4) };
    }

    static UIElement Empty(string text)
    {
        var t = Theme.Text(text, 13, Theme.Secondary);
        t.Margin = new Thickness(18);
        return t;
    }
}
