using Apex.Core;
using Apex.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI;

namespace Apex.UI;

/// The panel that opens from the tray icon: the Windows counterpart of the
/// Mac app's menu bar panel. Tabs: Monitor, Energy, Clipboard. It hides when
/// you click anywhere else, like Windows' own flyouts.
internal sealed class TrayPanel : Window
{
    public static TrayPanel? Current { get; private set; }
    readonly Grid root = new() { Padding = new Thickness(14), RowSpacing = 10 };
    readonly ContentControl body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly ToggleButton[] tabButtons;
    readonly string[] tabNames = { "Monitor", "Energy", "Clipboard" };
    readonly MonitorTab monitor = new();
    readonly EnergyTab energy = new();
    readonly ClipboardTab clipboard = new();
    int tab = Settings.Get("trayTab", 0);
    public event Action? OpenApp;

    public TrayPanel()
    {
        Current = this;
        Title = "Apex";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(true, false);
            p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        string[] glyphs = { "", "", "" };
        tabButtons = new ToggleButton[tabNames.Length];
        for (int i = 0; i < tabNames.Length; i++)
        {
            int index = i;
            var b = new ToggleButton { Content = Theme.Icon(glyphs[i], 16), Padding = new Thickness(18, 6, 18, 6) };
            ToolTipService.SetToolTip(b, tabNames[i]);
            b.Click += (_, _) => Select(index);
            tabButtons[i] = b; tabs.Children.Add(b);
        }
        root.Children.Add(tabs);
        Grid.SetRow(body, 1); root.Children.Add(body);

        var footer = new Grid();
        var open = new HyperlinkButton { Content = "Open Apex" };
        open.Click += (_, _) => { Hide(); OpenApp?.Invoke(); };
        footer.Children.Add(open);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root;

        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) Hide(); };
        Select(Math.Clamp(tab, 0, tabNames.Length - 1));
    }

    void Select(int index)
    {
        tab = index;
        Settings.Set("trayTab", index);
        for (int i = 0; i < tabButtons.Length; i++) tabButtons[i].IsChecked = i == index;
        body.Content = index switch { 1 => energy.View, 2 => clipboard.View, _ => monitor.View };
        Update(AppState.Latest);
    }

    public void Update(Snapshot s)
    {
        if (!AppWindow.IsVisible) return;
        if (tab == 0) monitor.Update(s); else if (tab == 1) energy.Update(s); else clipboard.Update();
    }

    /// Shows it just above the notification area (or beside it, wherever the taskbar is).
    public void Toggle()
    {
        if (AppWindow.IsVisible) { Hide(); return; }
        var cursor = Tray.Cursor();
        var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary);
        double scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;
        if (scale <= 0) scale = 1;
        int w = (int)(380 * scale), h = (int)(560 * scale), margin = (int)(12 * scale);
        var work = area.WorkArea; var outer = area.OuterBounds;
        int x = Math.Clamp(cursor.X - w / 2, work.X + margin, work.X + work.Width - w - margin);
        int y = work.Y + work.Height - h - margin;                                   // taskbar at the bottom (usual)
        if (work.Y > outer.Y) y = work.Y + margin;                                    // taskbar at the top
        if (work.X > outer.X) x = work.X + margin;                                    // on the left
        else if (work.Width < outer.Width && work.X == outer.X && cursor.X > work.X + work.Width) x = work.X + work.Width - w - margin;   // on the right
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        AppWindow.Show();
        Activate();
        Update(AppState.Latest);
    }

    void Hide() => AppWindow.Hide();

    // ---- Tabs -------------------------------------------------------------

    /// CPU, Memory, GPU, Disk, Network: a value and a mini graph each.
    sealed class MonitorTab
    {
        public readonly StackPanel View = new() { Spacing = 8 };
        readonly List<(TextBlock Value, Graph Graph, Func<Snapshot, string> Text)> rows = new();

        public MonitorTab()
        {
            Add("CPU", Theme.Cpu, AppState.Cpu, s => Fmt.Percent(s.Cpu.Total));
            Add("Memory", Theme.Memory, AppState.Memory, s => $"{Fmt.Percent(s.Memory.Percent)} · {MemoryPressure.Title(s.Memory.Pressure)}");
            Add("GPU", Theme.Gpu, AppState.Gpu, s => Fmt.Percent(s.Gpus.Select(g => g.Utilization).DefaultIfEmpty(0).Max()));
        }

        void Add(string name, Color color, TimeSeries series, Func<Snapshot, string> text)
        {
            var g = new Graph(color, 6) { Height = 34, Width = 130, ShowGrid = false };
            g.SetSeries((series, color, true));
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(Theme.Text(name, 13, Theme.Secondary));
            var value = Theme.Text("", 16, null, bold: true);
            labels.Children.Add(value);
            row.Children.Add(labels);
            Grid.SetColumn(g, 1); row.Children.Add(g);
            View.Children.Add(Theme.Card(row, 10));
            rows.Add((value, g, text));
        }

        public void Update(Snapshot s)
        {
            // Disk and network rows follow the system disk and the connection in use.
            if (rows.Count == 3)
            {
                var disk = s.Disks.FirstOrDefault(d => d.IsSystem) ?? s.Disks.FirstOrDefault();
                if (disk != null) Add("Disk", Theme.Disk, AppState.Get("disk:" + disk.Name), x =>
                {
                    var d = x.Disks.FirstOrDefault(v => v.Name == disk.Name);
                    return d == null ? "–" : $"{Fmt.Percent(d.ActivePercent)} · ↓{Fmt.Rate(d.ReadPerSec)} ↑{Fmt.Rate(d.WritePerSec)}";
                });
                var net = s.Networks.Where(n => n.Up).OrderByDescending(n => n.InPerSec + n.OutPerSec).FirstOrDefault();
                if (net != null) Add("Network · " + net.Name, Theme.Network, AppState.Get("netin:" + net.Id), x =>
                {
                    var n = x.Networks.FirstOrDefault(v => v.Id == net.Id);
                    return n == null ? "–" : $"↓{Fmt.Rate(n.InPerSec)} ↑{Fmt.Rate(n.OutPerSec)}";
                });
            }
            foreach (var (value, graph, text) in rows) { value.Text = text(s); graph.Redraw(); }
        }
    }

    /// Battery, power source, and Stay Awake.
    sealed class EnergyTab
    {
        public readonly StackPanel View = new() { Spacing = 10 };
        readonly TextBlock battery = Theme.Text("", 28, null, bold: true), source = Theme.Text("", 13, Theme.Secondary);
        readonly ToggleSwitch awake = new() { Header = "Keep this PC awake" };
        readonly ComboBox duration = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        readonly CheckBox display = new() { Content = "Keep the screen on too" };
        readonly TextBlock awakeNote = Theme.Text("", 12, Theme.Secondary);
        bool updating;

        public EnergyTab()
        {
            var b = new StackPanel { Spacing = 2 };
            b.Children.Add(Theme.Text("Battery", 13, Theme.Secondary)); b.Children.Add(battery); b.Children.Add(source);
            View.Children.Add(Theme.Card(b, 12));

            foreach (var d in new[] { "15 minutes", "1 hour", "2 hours", "5 hours", "Until I turn it off" }) duration.Items.Add(d);
            duration.SelectedIndex = 1;
            var a = new StackPanel { Spacing = 8 };
            a.Children.Add(awake); a.Children.Add(duration); a.Children.Add(display); a.Children.Add(awakeNote);
            View.Children.Add(Theme.Card(a, 12));
            awake.Toggled += (_, _) => { if (!updating) Apply(); };
            duration.SelectionChanged += (_, _) => { if (!updating && awake.IsOn) Apply(); };
            display.Click += (_, _) => { if (awake.IsOn) Apply(); };
            StayAwake.Changed += () => MainWindow.Ui(() => Update(AppState.Latest));
        }

        void Apply()
        {
            if (!awake.IsOn) { StayAwake.Stop(); return; }
            TimeSpan? d = duration.SelectedIndex switch { 0 => TimeSpan.FromMinutes(15), 1 => TimeSpan.FromHours(1), 2 => TimeSpan.FromHours(2), 3 => TimeSpan.FromHours(5), _ => null };
            StayAwake.Start(d, display.IsChecked == true);
        }

        public void Update(Snapshot s)
        {
            var bt = s.Battery;
            battery.Text = bt.HasBattery ? $"{bt.Percent}%" : "No battery";
            source.Text = !bt.HasBattery ? "Plugged in" : bt.Charging ? "Charging" : bt.OnPower ? "Plugged in, not charging"
                : bt.Remaining is { } r ? $"On battery · about {Fmt.Clock(r)} left" : "On battery";
            updating = true;
            awake.IsOn = StayAwake.IsOn;
            updating = false;
            awakeNote.Text = !StayAwake.IsOn ? "Windows sleeps as usual."
                : StayAwake.Until is { } u ? $"Awake until {u:t}." : "Awake until you turn it off.";
        }
    }

    /// The last 50 copies: click one to copy it again.
    sealed class ClipboardTab
    {
        public readonly Grid View = new() { RowSpacing = 8 };
        readonly TextBox search = new() { PlaceholderText = "Search copied items" };
        readonly StackPanel list = new() { Spacing = 4 };
        readonly ToggleSwitch on = new() { OnContent = "Keeping history", OffContent = "History off" };

        public ClipboardTab()
        {
            View.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            View.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            View.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            View.Children.Add(search);
            var scroll = new ScrollViewer { Content = list };
            Grid.SetRow(scroll, 1); View.Children.Add(scroll);
            var footer = new Grid();
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            on.IsOn = ClipboardHistory.Shared.Enabled;
            on.Toggled += (_, _) => ClipboardHistory.Shared.Enabled = on.IsOn;
            footer.Children.Add(on);
            var winv = new HyperlinkButton { Content = "Win+V" };
            ToolTipService.SetToolTip(winv, "Windows’ own clipboard history (Settings › System › Clipboard)");
            winv.Click += (_, _) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:clipboard"));
            Grid.SetColumn(winv, 1); footer.Children.Add(winv);
            var clear = new Button { Content = "Clear" };
            clear.Click += (_, _) => ClipboardHistory.Shared.Clear();
            Grid.SetColumn(clear, 2); footer.Children.Add(clear);
            Grid.SetRow(footer, 2); View.Children.Add(footer);
            search.TextChanged += (_, _) => Update();
            ClipboardHistory.Shared.Changed += () => MainWindow.Ui(Update);
        }

        public void Update()
        {
            list.Children.Clear();
            var items = ClipboardHistory.Shared.Items.Where(i => i.Matches(search.Text.Trim())).ToList();
            if (items.Count == 0)
            {
                var empty = Theme.Text(ClipboardHistory.Shared.Enabled ? "Copy something and it appears here. Password managers’ copies are skipped." : "Clipboard history is off.", 13, Theme.Secondary);
                empty.TextWrapping = TextWrapping.Wrap;
                list.Children.Add(empty);
            }
            foreach (var item in items)
            {
                var row = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6, 10, 6) };
                var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                p.Children.Add(Theme.Icon(item.Kind switch { ClipKind.Files => "", ClipKind.Picture => "", _ => "" }, 14));
                var t = Theme.Text(item.Title, 13); t.TextTrimming = TextTrimming.CharacterEllipsis; t.MaxWidth = 280; t.MaxLines = 2; t.TextWrapping = TextWrapping.Wrap;
                p.Children.Add(t);
                row.Content = p;
                ToolTipService.SetToolTip(row, $"Copied {item.Copied:t} · click to copy again");
                row.Click += (_, _) => _ = CopyAgain(item);
                list.Children.Add(row);
            }
        }

        static async Task CopyAgain(ClipItem item)
        {
            var d = new DataPackage();
            switch (item.Kind)
            {
                case ClipKind.Text: d.SetText(item.Text); break;
                case ClipKind.Files:
                    var files = new List<Windows.Storage.IStorageItem>();
                    foreach (var f in item.Files)
                    {
                        try { files.Add(Directory.Exists(f) ? await Windows.Storage.StorageFolder.GetFolderFromPathAsync(f) : await Windows.Storage.StorageFile.GetFileFromPathAsync(f)); } catch { }
                    }
                    if (files.Count == 0) return;
                    d.SetStorageItems(files);
                    break;
                case ClipKind.Picture when item.Dib != null:
                    d.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(await DibToBmp(item.Dib)));
                    break;
            }
            ClipboardHistory.Shared.WillCopy();
            Clipboard.SetContent(d);
        }

        /// A DIB plus a BMP file header is a .bmp file.
        static async Task<Windows.Storage.Streams.IRandomAccessStream> DibToBmp(byte[] dib)
        {
            int headerSize = BitConverter.ToInt32(dib, 0), bits = BitConverter.ToInt16(dib, 14), colors = BitConverter.ToInt32(dib, 32);
            int palette = (bits <= 8 ? (colors == 0 ? 1 << bits : colors) : 0) * 4;
            if (BitConverter.ToInt32(dib, 16) == 3 && headerSize == 40) palette += 12;      // BI_BITFIELDS masks
            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(14 + headerSize + palette).CopyTo(bmp, 10);
            dib.CopyTo(bmp, 14);
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(bmp.AsBuffer());
            stream.Seek(0);
            return stream;
        }
    }
}
