using Apex.Core;
using Apex.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Apex.UI;

/// A page with a big title, a subtitle, optional search, and a list of rows in a card.
internal abstract class ListPage : IPage
{
    public UIElement View => root;
    readonly Grid root = new() { Padding = new Thickness(28, 16, 28, 16), RowSpacing = 14 };
    protected readonly TextBlock Subtitle = Theme.Text("", 14, Theme.Secondary);
    /// Rows are built only when they scroll into view (a virtualized list).
    protected readonly LazyRows Rows = new();
    protected readonly TextBox Search = new() { PlaceholderText = "Search", Width = 260, CornerRadius = new CornerRadius(8) };
    protected readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

    protected ListPage(string title, bool searchable)
    {
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = new StackPanel { Spacing = 2 };
        t.Children.Add(Theme.Title(title)); t.Children.Add(Subtitle);
        header.Children.Add(t);
        if (searchable) { Actions.Children.Add(Search); Search.TextChanged += (_, _) => Fill(); }
        Grid.SetColumn(Actions, 1); header.Children.Add(Actions);
        root.Children.Add(header);
        var card = Theme.Card(new ScrollViewer { Content = Rows.View }, 0);
        Grid.SetRow(card, 1); root.Children.Add(card);
    }

    public virtual void Update(Snapshot s) { }
    public void Shown() { Reload(); }
    protected abstract void Reload();
    protected abstract void Fill();

    /// A row: icon, two lines of text, then extra columns on the right.
    protected static Grid Row(UIElement? lead, string title, string detail, params UIElement[] trailing)
    {
        var g = new Grid { Padding = new Thickness(14, 8, 14, 8), ColumnSpacing = 12, MinHeight = 48 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (lead != null) g.Children.Add(lead);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (detail.Length > 0) text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = Theme.Secondary, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1); g.Children.Add(text);
        int col = 2;
        foreach (var t in trailing)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (t is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn((FrameworkElement)t, col++); g.Children.Add(t);
        }
        g.BorderBrush = Theme.Res("DividerStrokeColorDefaultBrush");
        g.BorderThickness = new Thickness(0, 0, 0, 1);
        return g;
    }

    protected static Image AppIcon(string path)
    {
        var img = new Image { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        _ = Icons.Load(path, img);
        return img;
    }
}

/// Startup apps: what Windows starts when you sign in, with an on / off switch each.
internal sealed class StartupPage : ListPage
{
    List<StartupEntry> items = new();
    public StartupPage() : base("Startup apps", true) { }

    protected override void Reload()
    {
        Task.Run(StartupItems.List).ContinueWith(t => MainWindow.Ui(() => { items = t.Result; Fill(); }));
    }

    protected override void Fill()
    {
        Subtitle.Text = $"{items.Count(i => i.Enabled)} of {items.Count} start when you sign in. Turning one off doesn’t uninstall it.";
        Rows.Clear();
        var q = Search.Text.Trim();
        foreach (var e in items.Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            var toggle = new ToggleSwitch { IsOn = e.Enabled, OnContent = "On", OffContent = "Off", MinWidth = 0 };
            var entry = e;
            toggle.Toggled += async (_, _) =>
            {
                bool want = toggle.IsOn;
                bool ok = await Task.Run(() => StartupItems.SetEnabled(entry, want));
                if (!ok) { toggle.IsOn = !want; await MainWindow.ShowMessage("Couldn’t change it", "This one is for all users, so Windows needs administrator permission."); }
                else items = items.Select(i => i == entry ? i with { Enabled = want } : i).ToList();
            };
            Rows.Add(() => Row(AppIcon(e.Path), e.Name, (e.Publisher.Length > 0 ? e.Publisher + " · " : "") + e.Command,
                Theme.Pill(e.Source, Theme.Cpu), toggle));
        }
    }
}

/// Windows services, with Start / Stop / Restart.
internal sealed class ServicesPage : ListPage
{
    List<ServicesList.Item> items = new();
    readonly ComboBox filter = new() { ItemsSource = new[] { "All", "Running", "Stopped" }, SelectedIndex = 0, CornerRadius = new CornerRadius(8) };

    public ServicesPage() : base("Services", true)
    {
        Actions.Children.Insert(0, filter);
        filter.SelectionChanged += (_, _) => Fill();
        var refresh = new Button { Content = Theme.Icon("", 14) };
        ToolTipService.SetToolTip(refresh, "Refresh");
        refresh.Click += (_, _) => Reload();
        Actions.Children.Add(refresh);
    }

    protected override void Reload() =>
        Task.Run(ServicesList.List).ContinueWith(t => MainWindow.Ui(() => { items = t.Result; Fill(); }));

    protected override void Fill()
    {
        Subtitle.Text = $"{items.Count(i => i.Status == "Running")} running · {items.Count} in all";
        Rows.Clear();
        var q = Search.Text.Trim();
        var f = filter.SelectedItem as string ?? "All";
        foreach (var s in items.Where(i => (f == "All" || i.Status == f)
                     && (q.Length == 0 || i.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase))))
        {
            var color = s.Status == "Running" ? Theme.Green : s.Status == "Stopped" ? Microsoft.UI.Colors.Gray : Theme.Yellow;
            var more = new Button { Content = Theme.Icon("", 14), Padding = new Thickness(8, 4, 8, 4) };
            var menu = new MenuFlyout();
            foreach (var (label, action) in new[] { ("Start", "start"), ("Stop", "stop"), ("Restart", "restart") })
            {
                var item = new MenuFlyoutItem { Text = label, IsEnabled = action == "start" ? s.Status != "Running" : s.Status == "Running" };
                var name = s.Name;
                item.Click += async (_, _) =>
                {
                    bool ok = await Task.Run(() => ServicesList.Control(name, action));
                    if (!ok) await MainWindow.ShowMessage("Couldn’t " + label.ToLowerInvariant() + " it", "Windows didn’t allow it, or the service didn’t respond.");
                    Reload();
                };
                menu.Items.Add(item);
            }
            more.Flyout = menu;
            var start = Theme.Text(s.StartType, 12, Theme.Secondary); start.Width = 80;
            Rows.Add(() => Row(Theme.Icon("", 18), s.DisplayName, s.Name, start, Theme.Pill(s.Status, color), more));
        }
    }
}

/// Who's signed in, and what their apps use.
internal sealed class UsersPage : ListPage
{
    List<Sessions.Session> sessions = new();
    public UsersPage() : base("Users", false) { }

    protected override void Reload() =>
        Task.Run(Sessions.List).ContinueWith(t => MainWindow.Ui(() => { sessions = t.Result; Fill(); }));

    public override void Update(Snapshot s) => Fill();

    protected override void Fill()
    {
        Subtitle.Text = $"{sessions.Count} signed in";
        Rows.Clear();
        var procs = AppState.Latest.Processes;
        foreach (var u in sessions)
        {
            var mine = procs.Where(p => p.User == u.Id.ToString()).ToList();
            var cpu = Theme.Text(Fmt.Percent(mine.Sum(p => p.Cpu)) + " CPU", 13, Theme.Secondary); cpu.Width = 90;
            var mem = Theme.Text(Fmt.Memory(mine.Sum(p => p.Memory)), 13, Theme.Secondary); mem.Width = 90;
            Rows.Add(() => Row(Theme.Icon("", 22), u.User, $"Session {u.Id} · {mine.Count} processes",
                cpu, mem, Theme.Pill(u.State, u.State == "Active" ? Theme.Green : Theme.Yellow)));
        }
    }
}

/// A list whose rows are made only when they scroll into view, then kept:
/// a long list (services, startup apps) costs only what's on screen.
internal sealed class LazyRows
{
    public readonly ItemsRepeater View = new() { Layout = new StackLayout() };
    readonly System.Collections.ObjectModel.ObservableCollection<Lazy<UIElement>> items = new();

    public LazyRows() { View.ItemsSource = items; View.ItemTemplate = new Factory(); }
    public void Clear() => items.Clear();
    public void Add(Func<UIElement> make) => items.Add(new Lazy<UIElement>(make));
    public int Count => items.Count;

    sealed class Factory : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args) => ((Lazy<UIElement>)args.Data).Value;
        public void RecycleElement(ElementFactoryRecycleArgs args) { }      // kept with its item
    }
}
