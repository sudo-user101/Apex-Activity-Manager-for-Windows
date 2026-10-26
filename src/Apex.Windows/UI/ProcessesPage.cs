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
    readonly StackPanel rowsPanel = new();
    readonly Dictionary<string, RowView> views = new();
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

        var list = Theme.Card(new ScrollViewer { Content = rowsPanel, Padding = new Thickness(0, 4, 0, 4) }, 0);
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
        flat = ProcessTree.Flatten(tree, expandedNow, collapsedSections);
        double maxMem = Math.Max(1, s.Memory.Total * 0.25), maxDisk = 10_000_000;
        bool dark = Theme.IsDark(root);

        // Reuse row views (no flicker, cheap); re-add only when the order changed.
        var order = flat.Select(r => r.Id).ToList();
        bool sameOrder = rowsPanel.Children.Count == order.Count
                         && rowsPanel.Children.Cast<RowView>().Select(v => v.Id).SequenceEqual(order);
        var rows = new List<UIElement>(flat.Count);
        foreach (var r in flat)
        {
            if (!views.TryGetValue(r.Id, out var v))
            {
                v = new RowView(r.Id);
                v.Pressed += OnRowPressed;
                v.Toggled += OnRowToggled;
                v.ContextRequested += (sender, e) => ShowMenu((RowView)sender, e);
                views[r.Id] = v;
            }
            bool efficient = r.Path.Length > 0 && AppState.Efficient.Contains(r.Path);
            bool expandable = r.IsSection || r.Children.Count > 0;
            bool isOpen = r.IsSection ? !collapsedSections.Contains(r.Id) : expandedNow.Contains(r.Id);
            v.Set(r, expandable, isOpen, r.Id == selectedId, efficient, maxMem, maxDisk, dark);
            rows.Add(v);
        }
        if (!sameOrder)
        {
            rowsPanel.Children.Clear();
            foreach (var v in rows) rowsPanel.Children.Add(v);
        }
        foreach (var gone in views.Keys.Except(order).ToList()) views.Remove(gone);
        if (selectedId != null && !order.Contains(selectedId)) selectedId = null;
        UpdateButtons();
    }

    ProcessRow? Selected => flat.FirstOrDefault(r => r.Id == selectedId);

    void OnRowPressed(RowView v)
    {
        selectedId = v.Id;
        selectedTargets = Selected?.Targets ?? Array.Empty<ProcessIdentity>();
        selectedNames.Clear();
        foreach (var p in AppState.Latest.Processes)
            if (selectedTargets.Contains(p.Identity)) selectedNames[p.Identity] = p.DisplayName.Length > 0 ? p.DisplayName : p.Name;
        foreach (var (_, rv) in views) rv.SetSelected(rv.Id == selectedId);
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
    public string Id { get; }
    public event Action<RowView>? Pressed, Toggled;
    readonly TextBlock chevron = new() { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Width = 16, VerticalAlignment = VerticalAlignment.Center };
    readonly Image icon = new() { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
    readonly TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly FontIcon leaf = new() { Glyph = "", FontSize = 12, Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed };
    readonly Border[] cells = new Border[4];
    readonly TextBlock[] values = new TextBlock[4];
    readonly Border selection = new() { CornerRadius = new CornerRadius(6) };
    string iconPath = "";

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
        chevron.Text = expandable ? (open ? "" : "") : "";
        chevron.Margin = new Thickness(r.Depth * 18, 0, 6, 0);
        name.Text = r.Name;
        name.FontWeight = r.IsSection ? FontWeights.SemiBold : FontWeights.Normal;
        name.Foreground = r.IsSection ? Theme.Secondary : Theme.Primary;
        leaf.Visibility = efficient ? Visibility.Visible : Visibility.Collapsed;
        leaf.Foreground = Theme.Solid(Theme.Green);
        icon.Visibility = r.IsSection ? Visibility.Collapsed : Visibility.Visible;
        if (!r.IsSection && r.Path != iconPath) { iconPath = r.Path; _ = Icons.Load(r.Path, icon); }
        string[] texts = { Fmt.Percent(r.Cpu), Fmt.Memory(r.Memory), DiskText(r.DiskPerSec), Fmt.Percent(r.Gpu) };
        int[] heat = { Heat.Level(r.Cpu, 100), Heat.Level(r.Memory, maxMem), Heat.Level(r.DiskPerSec, maxDisk), Heat.Level(r.Gpu, 100) };
        for (int i = 0; i < 4; i++)
        {
            values[i].Text = texts[i];
            values[i].FontWeight = r.IsSection ? FontWeights.SemiBold : FontWeights.Normal;
            cells[i].Background = r.IsSection ? null : Theme.HeatBrush(heat[i], dark);
        }
        SetSelected(selected);
    }

    static string DiskText(double bytesPerSec) =>
        bytesPerSec < 50_000 ? "0 MB/s" : (bytesPerSec / 1e6).ToString("0.0") + " MB/s";

    public void SetSelected(bool on) =>
        selection.Background = on ? Theme.Solid(((SolidColorBrush)Theme.Accent).Color, 0.22) : null;
}
