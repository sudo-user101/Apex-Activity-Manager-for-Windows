using Apex.Core;
using Apex.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Apex.UI;

/// Processes: Apps, Background processes and Windows processes, each program
/// with its processes underneath; heat-shaded numbers like Task Manager;
/// End task and Efficiency mode.
internal sealed class ProcessesPage : IPage
{
    public UIElement View => root;
    readonly Grid root = new() { Padding = new Thickness(28, 16, 28, 16), RowSpacing = 12 };
    readonly TextBlock subtitle = Theme.Text("", 14, Theme.Secondary);
    string searchText = "";
    readonly Button endTask = new() { Content = "End task", IsEnabled = false };
    readonly Button efficiency = new() { IsEnabled = false };
    readonly Button restoreAll = new() { Content = "Restore all", Visibility = Visibility.Collapsed };
    // Only the rows on screen exist (virtualized, recycled as you scroll), like Task Manager.
    readonly ItemsRepeater repeater = new() { Layout = new StackLayout() };
    readonly ScrollViewer scroller = new() { Padding = new Thickness(0, 4, 0, 4) };
    readonly Stack<RowView> pool = new();
    DateTime lastScroll;
    readonly HashSet<string> expanded = new(), collapsedSections = new();
    readonly HeaderCell[] headers;
    ProcessSortKey sortKey = ProcessSortKey.Cpu;
    bool ascending;
    string? selectedId;
    /// The processes as they were when the row was clicked: End task acts on
    /// these, never on whatever has their IDs by then.
    ProcessIdentity[] selectedTargets = Array.Empty<ProcessIdentity>();
    readonly Dictionary<ProcessIdentity, string> selectedNames = new();
    List<ProcessRow> flat = new();

    public ProcessesPage()
    {
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header: big title + summary on the left, actions on the right (search is in the title bar).
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(Theme.Title("Processes"));
        titles.Children.Add(subtitle);
        header.Children.Add(titles);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(efficiency); actions.Children.Add(restoreAll); actions.Children.Add(endTask);
        Grid.SetColumn(actions, 1); header.Children.Add(actions);
        root.Children.Add(header);
        SetEfficiencyLabel(false);

        endTask.Click += (_, _) => EndSelected();
        efficiency.Click += (_, _) => ToggleEfficiency();
        restoreAll.Click += (_, _) => RestoreAll();

        // Column headers (Task Manager style: the total above each label).
        var cols = RowView.Columns();
        headers = new[]
        {
            new HeaderCell("Name", ProcessSortKey.Name, left: true), new HeaderCell("CPU", ProcessSortKey.Cpu),
            new HeaderCell("Memory", ProcessSortKey.Memory), new HeaderCell("Disk", ProcessSortKey.Disk), new HeaderCell("GPU", ProcessSortKey.Gpu),
        };
        var headerRow = new Grid { Padding = new Thickness(8, 0, 8, 4) };
        foreach (var c in cols) headerRow.ColumnDefinitions.Add(c);
        for (int i = 0; i < headers.Length; i++)
        {
            var h = headers[i];
            h.Clicked += k => { if (sortKey == k) ascending = !ascending; else { sortKey = k; ascending = k == ProcessSortKey.Name; } Refresh(); };
            Grid.SetColumn(h, i); headerRow.Children.Add(h);
        }
        Grid.SetRow(headerRow, 1); root.Children.Add(headerRow);

        repeater.ItemTemplate = new RowFactory(this);
        scroller.Content = repeater;
        // While you scroll, the order holds still; it catches up a moment after.
        scroller.ViewChanged += (_, e) => lastScroll = DateTime.UtcNow;
        var list = Theme.Card(scroller, 0);
        Grid.SetRow(list, 2); root.Children.Add(list);

        root.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Delete) EndSelected(); };
    }

    public void Shown() => Refresh();

    /// The title bar's search box.
    public void SetSearch(string text) { searchText = text; Refresh(); }
    public void Update(Snapshot s) => Refresh();

    void Refresh()
    {
        var s = AppState.Latest;
        subtitle.Text = $"CPU {Fmt.Percent(s.Cpu.Total)} · Memory {Fmt.Percent(s.Memory.Percent)} · {s.Processes.Count} processes";
        headers[1].Total = Fmt.Percent(s.Cpu.Total);
        headers[2].Total = Fmt.Percent(s.Memory.Percent);
        headers[3].Total = Fmt.Percent(s.Disks.Where(d => d.IsSystem).Select(d => d.ActivePercent).DefaultIfEmpty(0).Max());
        headers[4].Total = Fmt.Percent(s.Gpus.Select(g => g.Utilization).DefaultIfEmpty(0).Max());
        foreach (var h in headers) h.SetSort(sortKey, ascending);

        var tree = ProcessTree.Build(s.Processes, searchText, sortKey, ascending);
        var expandedNow = searchText.Length > 0 ? new HashSet<string>(tree.SelectMany(t => t.Children).Select(r => r.Id)) : expanded;
        expandedNowCache = expandedNow;
        var next = ProcessTree.Flatten(tree, expandedNow, collapsedSections);
        maxMem = Math.Max(1, s.Memory.Total * 0.25);
        dark = Theme.IsDark(root);

        bool scrolling = DateTime.UtcNow - lastScroll < TimeSpan.FromSeconds(1.2);
        bool sameOrder = next.Count == flat.Count && next.Select(r => r.Id).SequenceEqual(flat.Select(r => r.Id));
        if (scrolling && !sameOrder)
        {
            // Keep the rows where they are; refresh their numbers (rows that ended stay until the scroll stops).
            var byId = next.ToDictionary(r => r.Id);
            next = flat.Select(r => byId.TryGetValue(r.Id, out var n) ? n : r).ToList();
            sameOrder = true;
        }
        flat = next;
        if (sameOrder && repeater.ItemsSource != null)
        {
            // Same rows in the same places: update the ones on screen in place.
            for (int i = 0; i < flat.Count; i++)
                if (repeater.TryGetElement(i) is RowView v) Bind(v, flat[i]);
        }
        else repeater.ItemsSource = flat.ToList();          // a new order: the visible rows are re-made (recycled)
        if (selectedId != null && !flat.Any(r => r.Id == selectedId)) selectedId = null;
        UpdateButtons();
    }

    ProcessRow? Selected => flat.FirstOrDefault(r => r.Id == selectedId);
    HashSet<string> expandedNowCache = new();
    double maxMem = 1;
    const double maxDisk = 10_000_000;
    bool dark;

    /// Puts one process's numbers on a (recycled) row.
    void Bind(RowView v, ProcessRow r)
    {
        v.Id = r.Id;
        bool efficient = r.Path.Length > 0 && AppState.Efficient.Contains(r.Path);
        bool expandable = r.IsSection || r.Children.Count > 0;
        bool isOpen = r.IsSection ? !collapsedSections.Contains(r.Id) : expandedNowCache.Contains(r.Id);
        v.Set(r, expandable, isOpen, r.Id == selectedId, efficient, maxMem, maxDisk, dark);
    }

    /// Makes and recycles rows for the virtualized list.
    sealed class RowFactory(ProcessesPage page) : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            if (!page.pool.TryPop(out var v))
            {
                v = new RowView("");
                v.Pressed += page.OnRowPressed;
                v.Toggled += page.OnRowToggled;
                v.ContextRequested += (sender, e) => page.ShowMenu((RowView)sender, e);
            }
            if (args.Data is ProcessRow r) page.Bind(v, r);
            return v;
        }
        public void RecycleElement(ElementFactoryRecycleArgs args) { if (args.Element is RowView v) page.pool.Push(v); }
    }

    void OnRowPressed(RowView v)
    {
        selectedId = v.Id;
        selectedTargets = Selected?.Targets ?? Array.Empty<ProcessIdentity>();
        selectedNames.Clear();
        foreach (var p in AppState.Latest.Processes)
            if (selectedTargets.Contains(p.Identity)) selectedNames[p.Identity] = p.DisplayName.Length > 0 ? p.DisplayName : p.Name;
        for (int i = 0; i < flat.Count; i++) if (repeater.TryGetElement(i) is RowView rv) rv.SetSelected(rv.Id == selectedId);
        UpdateButtons();
    }

    void OnRowToggled(RowView v)
    {
        if (v.Id.StartsWith("section:")) { if (!collapsedSections.Remove(v.Id)) collapsedSections.Add(v.Id); }
        else if (!expanded.Remove(v.Id)) expanded.Add(v.Id);
        Refresh();
    }

    void UpdateButtons()
    {
        var r = Selected;
        bool canAct = r is { IsSection: false };
        endTask.IsEnabled = canAct;
        efficiency.IsEnabled = canAct && r!.Path.Length > 0;
        SetEfficiencyLabel(canAct && AppState.Efficient.Contains(r!.Path));
        restoreAll.Visibility = AppState.Efficient.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        restoreAll.Content = $"Restore all ({AppState.Efficient.Count})";
    }

    void SetEfficiencyLabel(bool on)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        p.Children.Add(Theme.Icon("", 14));
        p.Children.Add(new TextBlock { Text = on ? "Restore" : "Efficiency mode" });
        efficiency.Content = p;
        ToolTipService.SetToolTip(efficiency, on
            ? "Back to normal speed"
            : "Keeps the app running, but at the lowest priority, on power-saving cores, first in line to give back memory, and minimized. Bringing it to the front puts it back to normal.");
    }

    void EndSelected()
    {
        if (Selected is not { IsSection: false } || selectedTargets.Length == 0) return;
        var report = ProcessActions.End(selectedTargets);
        if (report.Message(selectedNames, "end") is { } message) _ = MainWindow.ShowMessage("Some weren’t ended", message);
    }

    void ToggleEfficiency()
    {
        if (Selected is not { IsSection: false } r || r.Path.Length == 0) return;
        bool on = !AppState.Efficient.Contains(r.Path);
        if (!Efficiency.Set(r.Path, on, AppState.Latest.Processes))
            _ = MainWindow.ShowMessage("Couldn’t change all of it", "Windows didn’t allow it for some of this app’s processes. Running Apex as administrator (⋯ › Restart as administrator) covers them too.");
        Refresh();
    }

    void RestoreAll()
    {
        Efficiency.RestoreAll(AppState.Latest.Processes);
        Refresh();
    }

    void ShowMenu(RowView v, ContextRequestedEventArgs e)
    {
        OnRowPressed(v);
        if (Selected is not { IsSection: false } r) return;
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action a, bool enabled = true)
        {
            var i = new MenuFlyoutItem { Text = text, Icon = Theme.Icon(glyph), IsEnabled = enabled };
            i.Click += (_, _) => a();
            menu.Items.Add(i);
        }
        Add("End task", "", EndSelected);
        Add(AppState.Efficient.Contains(r.Path) ? "Restore" : "Efficiency mode", "", ToggleEfficiency, r.Path.Length > 0);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Open file location", "", () => ProcessActions.ShowInExplorer(r.Path), r.Path.Length > 0);
        Add("Copy name", "", () =>
        {
            var d = new DataPackage(); d.SetText(r.Name); Clipboard.SetContent(d);
        });
        if (e.TryGetPosition(v, out var pt)) menu.ShowAt(v, pt); else menu.ShowAt(v);
        e.Handled = true;
    }
}

/// A column header: the total above the label, click to sort.
internal sealed class HeaderCell : Button
{
    readonly TextBlock total = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    readonly TextBlock label = new() { FontSize = 12 };
    readonly ProcessSortKey key;
    public event Action<ProcessSortKey>? Clicked;

    public HeaderCell(string text, ProcessSortKey key, bool left = false)
    {
        this.key = key;
        label.Text = text;
        var p = new StackPanel { HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right };
        if (!left) p.Children.Add(total); else total.Text = "";
        p.Children.Add(label);
        if (left) { p.VerticalAlignment = VerticalAlignment.Bottom; }
        Content = p;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        BorderThickness = new Thickness(0);
        Padding = new Thickness(6, 2, 6, 2);
        Click += (_, _) => Clicked?.Invoke(key);
    }

    public string Total { set => total.Text = value; }

    public void SetSort(ProcessSortKey current, bool asc)
    {
        label.Text = label.Text.TrimEnd(' ', '▲', '▼') + (current == key ? (asc ? " ▲" : " ▼") : "");
        label.Foreground = current == key ? Theme.Primary : Theme.Secondary;
    }
}

/// One row: section, program or process. Reused between refreshes.
internal sealed class RowView : Grid
{
    public string Id { get; set; }
    public event Action<RowView>? Pressed, Toggled;
    readonly TextBlock chevron = new() { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Width = 16, VerticalAlignment = VerticalAlignment.Center };
    readonly Image icon = new() { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly FontIcon leaf = new() { Glyph = "", FontSize = 12, Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed };
    readonly Border[] cells = new Border[4];
    readonly TextBlock[] values = new TextBlock[4];
    readonly Border selection = new() { CornerRadius = new CornerRadius(6) };
    string iconPath = "";
    int depth = -1;
    bool? section;
    readonly int[] heatNow = { -1, -1, -1, -1 };

    public static ColumnDefinition[] Columns() => new[]
    {
        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
        new ColumnDefinition { Width = new GridLength(90) }, new ColumnDefinition { Width = new GridLength(110) },
        new ColumnDefinition { Width = new GridLength(100) }, new ColumnDefinition { Width = new GridLength(80) },
    };

    public RowView(string id)
    {
        Id = id;
        Height = 30;
        Padding = new Thickness(8, 0, 8, 0);
        foreach (var c in Columns()) ColumnDefinitions.Add(c);
        SetColumnSpan(selection, 5); Children.Add(selection);
        var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
        namePanel.Children.Add(chevron); namePanel.Children.Add(icon); namePanel.Children.Add(name); namePanel.Children.Add(leaf);
        Children.Add(namePanel);
        for (int i = 0; i < 4; i++)
        {
            values[i] = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            cells[i] = new Border { Child = values[i], Padding = new Thickness(6, 0, 6, 0) };
            SetColumn(cells[i], i + 1); Children.Add(cells[i]);
        }
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Tapped += (_, _) => Pressed?.Invoke(this);
        DoubleTapped += (_, _) => Toggled?.Invoke(this);
        chevron.Tapped += (_, e) => { Toggled?.Invoke(this); e.Handled = true; };
    }

    public void Set(ProcessRow r, bool expandable, bool open, bool selected, bool efficient, double maxMem, double maxDisk, bool dark)
    {
        // Only what changed is touched: no new objects per update, no needless layout.
        var glyph = expandable ? (open ? "" : "") : "";
        if (chevron.Text != glyph) chevron.Text = glyph;
        if (depth != r.Depth) { depth = r.Depth; chevron.Margin = new Thickness(r.Depth * 18, 0, 6, 0); }
        if (name.Text != r.Name) name.Text = r.Name;
        if (section != r.IsSection)
        {
            section = r.IsSection;
            name.FontWeight = r.IsSection ? FontWeights.SemiBold : FontWeights.Normal;
            name.Foreground = r.IsSection ? Theme.Secondary : Theme.Primary;
            icon.Visibility = r.IsSection ? Visibility.Collapsed : Visibility.Visible;
            foreach (var v in values) v.FontWeight = r.IsSection ? FontWeights.SemiBold : FontWeights.Normal;
            leaf.Foreground = Theme.Solid(Theme.Green);
        }
        var leafVis = efficient ? Visibility.Visible : Visibility.Collapsed;
        if (leaf.Visibility != leafVis) leaf.Visibility = leafVis;
        if (!r.IsSection && r.Path != iconPath) { iconPath = r.Path; icon.Source = null; _ = Icons.Load(r.Path, icon); }
        string[] texts = { Fmt.Percent(r.Cpu), Fmt.Memory(r.Memory), DiskText(r.DiskPerSec), Fmt.Percent(r.Gpu) };
        int[] heat = { Heat.Level(r.Cpu, 100), Heat.Level(r.Memory, maxMem), Heat.Level(r.DiskPerSec, maxDisk), Heat.Level(r.Gpu, 100) };
        for (int i = 0; i < 4; i++)
        {
            if (values[i].Text != texts[i]) values[i].Text = texts[i];
            int h = r.IsSection ? 0 : heat[i];
            if (heatNow[i] != h) { heatNow[i] = h; cells[i].Background = Theme.HeatBrush(h, dark); }
        }
        SetSelected(selected);
    }

    static string DiskText(double bytesPerSec) =>
        bytesPerSec < 50_000 ? "0 MB/s" : (bytesPerSec / 1e6).ToString("0.0") + " MB/s";

    bool? selectedNow;
    static Brush? selectedBrush;
    public void SetSelected(bool on)
    {
        if (selectedNow == on) return;
        selectedNow = on;
        selection.Background = on ? (selectedBrush ??= Theme.Solid(((SolidColorBrush)Theme.Accent).Color, 0.22)) : null;
    }
}
