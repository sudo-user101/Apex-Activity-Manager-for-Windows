using Apex.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Apex.UI;

/// Performance: a sidebar of live mini graphs (CPU, Memory, each disk, each
/// network connection, GPU) and the chosen one in detail.
internal sealed class PerformancePage : IPage
{
    public UIElement View => root;
    readonly Grid root = new();
    readonly StackPanel sidebar = new() { Spacing = 4, Padding = new Thickness(10, 12, 10, 12) };
    readonly ContentControl detail = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly Dictionary<string, Tile> tiles = new();
    readonly Dictionary<string, Detail> details = new();
    string selected = "cpu";
    string layoutKey = "";

    public PerformancePage()
    {
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // The sidebar: translucent, like a macOS source list.
        var side = new Border
        {
            Child = new ScrollViewer { Content = sidebar }, Background = Theme.Res("LayerOnMicaBaseAltFillColorTransparentBrush"),
            BorderBrush = Theme.Res("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(0, 0, 1, 0),
        };
        root.Children.Add(side);
        var right = new ScrollViewer { Content = detail, Padding = new Thickness(28, 16, 28, 20) };
        Grid.SetColumn(right, 1); root.Children.Add(right);
    }

    public void Shown() => Update(AppState.Latest);

    public void Update(Snapshot s)
    {
        var resources = Resources(s);
        var key = string.Join("|", resources.Select(r => r.Key));
        if (key != layoutKey)
        {
            layoutKey = key;
            sidebar.Children.Clear();
            foreach (var r in resources)
            {
                if (!tiles.TryGetValue(r.Key, out var t))
                {
                    t = new Tile(r.Key, r.Color);
                    t.Click += (_, _) => Select(t.Key);
                    tiles[r.Key] = t;
                }
                sidebar.Children.Add(t);
            }
            if (!resources.Any(r => r.Key == selected)) selected = resources.FirstOrDefault().Key ?? "cpu";
            Select(selected);
        }
        foreach (var r in resources)
        {
            var t = tiles[r.Key];
            t.Set(r.Title, r.Value, r.Series, selected == r.Key);
        }
        if (details.TryGetValue(selected, out var d)) d.Update(s);
    }

    void Select(string key)
    {
        selected = key;
        foreach (var t in tiles.Values) t.SetSelected(t.Key == key);
        if (!details.TryGetValue(key, out var d)) details[key] = d = Detail.For(key);
        detail.Content = d.View;
        Motion.FadeIn(d.View);
        d.Update(AppState.Latest);
    }

    record struct Resource(string Key, string Title, string Value, TimeSeries Series, Color Color);

    static List<Resource> Resources(Snapshot s)
    {
        var list = new List<Resource>
        {
            new("cpu", "CPU", $"{Fmt.Percent(s.Cpu.Total)}  {s.Cpu.SpeedGHz:0.00} GHz", AppState.Cpu, Theme.Cpu),
            new("memory", "Memory", $"{Fmt.Memory(s.Memory.Used)}/{Fmt.Memory(s.Memory.Total)} ({Fmt.Percent(s.Memory.Percent)})", AppState.Memory, Theme.Memory),
        };
        foreach (var d in s.Disks)
            list.Add(new("disk:" + d.Name, d.Name, $"{(d.Model.Length > 0 ? d.Model + "  " : "")}{Fmt.Percent(d.ActivePercent)}", AppState.Get("disk:" + d.Name), Theme.Disk));
        foreach (var n in s.Networks)
            list.Add(new("net:" + n.Id, n.Kind, $"{n.Name}  S: {Fmt.Bits(n.OutPerSec)} R: {Fmt.Bits(n.InPerSec)}", AppState.Get("netin:" + n.Id), Theme.Network));
        foreach (var g in s.Gpus)
            list.Add(new("gpu", "GPU 0", $"{g.Name}  {Fmt.Percent(g.Utilization)}", AppState.Gpu, Theme.Gpu));
        return list;
    }

    /// A sidebar entry: mini graph, title, live value.
    sealed class Tile : Button
    {
        public string Key { get; }
        readonly Graph graph;
        readonly TextBlock title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
        readonly TextBlock value = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Border bar = new() { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 6, 8, 6) };

        public Tile(string key, Color color)
        {
            Key = key;
            graph = new Graph(color, 6) { Width = 74, Height = 46, ShowGrid = false };
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.Background = Theme.Solid(color);
            g.Children.Add(bar);
            Grid.SetColumn(graph, 1); g.Children.Add(graph);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            text.Children.Add(title); text.Children.Add(value);
            Grid.SetColumn(text, 2); g.Children.Add(text);
            Content = g;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Padding = new Thickness(6);
            CornerRadius = new CornerRadius(8);
            BorderThickness = new Thickness(0);
        }

        public void Set(string t, string v, TimeSeries s, bool sel)
        {
            title.Text = t; value.Text = v;
            graph.SetSeries((s, ((SolidColorBrush)bar.Background).Color, true));
        }

        public void SetSelected(bool on)
        {
            Background = on ? Theme.Subtle : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            bar.Opacity = on ? 1 : 0;
            value.Foreground = Theme.Secondary;
        }
    }
}

/// The right-hand side for one resource.
internal abstract class Detail
{
    public readonly StackPanel View = new() { Spacing = 14 };
    protected readonly TextBlock Heading = Theme.Title("");
    protected readonly TextBlock Device = Theme.Text("", 15, Theme.Secondary);
    readonly WrapPanelLike stats = new();
    readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top };

    protected Detail(string title)
    {
        Heading.Text = title;
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(Heading); titles.Children.Add(Device);
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(titles);
        Grid.SetColumn(actions, 1); head.Children.Add(actions);
        View.Children.Add(head);
    }

    /// An icon button in the page header (the page's own tools), like the Mac app's.
    protected void AddAction(string glyph, string tip, Action action)
    {
        var b = new Button { Content = Theme.Icon(glyph, 16), Padding = new Thickness(10, 6, 10, 6) };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => action();
        actions.Children.Add(b);
    }

    public static Detail For(string key) => key switch
    {
        "cpu" => new CpuDetail(),
        "memory" => new MemoryDetail(),
        "gpu" => new GpuDetail(),
        _ when key.StartsWith("disk:") => new DiskDetail(key[5..]),
        _ when key.StartsWith("net:") => new NetDetail(key[4..]),
        _ => new CpuDetail(),
    };

    public abstract void Update(Snapshot s);

    /// The big graph card: caption above, "60 seconds" under it, Task Manager style.
    protected Graph BigGraph(Color color, string topLeft, string topRight, double height = 300)
    {
        var g = new Graph(color) { Height = height };
        var card = new StackPanel { Spacing = 4 };
        var top = new Grid();
        top.Children.Add(Theme.Text(topLeft, 12, Theme.Secondary));
        var tr = Theme.Text(topRight, 12, Theme.Secondary); tr.HorizontalAlignment = HorizontalAlignment.Right; top.Children.Add(tr);
        card.Children.Add(top); card.Children.Add(g);
        var bottom = new Grid();
        bottom.Children.Add(Theme.Text("60 seconds", 12, Theme.Secondary));
        var zero = Theme.Text("0", 12, Theme.Secondary); zero.HorizontalAlignment = HorizontalAlignment.Right; bottom.Children.Add(zero);
        card.Children.Add(bottom);
        View.Children.Add(Theme.Card(card));
        return g;
    }

    protected TextBlock AddStat(string label)
    {
        stats.Panel.Children.Add(Theme.Stat(label, out var v));
        return v;
    }

    protected void AddStatsCard() => View.Children.Add(Theme.Card(stats.Panel));

    /// Simple wrapping row of stat blocks.
    sealed class WrapPanelLike
    {
        public readonly VariableSizedWrapGrid Panel = new() { Orientation = Orientation.Horizontal, ItemWidth = 190 };
    }
}

internal sealed class CpuDetail : Detail
{
    readonly Graph graph;
    readonly TextBlock util, speed, procs, threads, handles, up, baseSpeed, cores, logical;
    public CpuDetail() : base("CPU")
    {
        graph = BigGraph(Theme.Cpu, "% Utilization", "100%");
        graph.SetSeries((AppState.Cpu, Theme.Cpu, true));
        util = AddStat("Utilization"); speed = AddStat("Speed"); procs = AddStat("Processes"); threads = AddStat("Threads");
        handles = AddStat("Handles"); up = AddStat("Up time"); baseSpeed = AddStat("Base speed"); cores = AddStat("Cores");
        logical = AddStat("Logical processors");
        AddStatsCard();
    }
    public override void Update(Snapshot s)
    {
        Device.Text = s.Cpu.Name;
        graph.Redraw();
        util.Text = Fmt.Percent(s.Cpu.Total); speed.Text = $"{s.Cpu.SpeedGHz:0.00} GHz";
        procs.Text = s.Cpu.Processes.ToString("N0"); threads.Text = s.Cpu.Threads.ToString("N0"); handles.Text = s.Cpu.Handles.ToString("N0");
        up.Text = Fmt.UpTime(s.Cpu.UpTime); baseSpeed.Text = $"{s.Cpu.BaseSpeedGHz:0.00} GHz";
        cores.Text = s.Cpu.Cores.ToString(); logical.Text = s.Cpu.LogicalProcessors.ToString();
    }
}

internal sealed class MemoryDetail : Detail
{
    readonly Graph graph;
    readonly StackedBar bar = new();
    readonly ContentControl pressure = new();
    readonly TextBlock inUse, available, committed, cached, paged, nonpaged;
    public MemoryDetail() : base("Memory")
    {
        AddAction("\uEC4A", "Free Up Memory", () => _ = FreeUpMemory.Show(View));
        graph = BigGraph(Theme.Memory, "Memory usage", "", 240);
        graph.SetSeries((AppState.Memory, Theme.Memory, true));
        var comp = new StackPanel { Spacing = 6 };
        var row = new Grid();
        row.Children.Add(Theme.Text("Memory composition", 12, Theme.Secondary));
        pressure.HorizontalAlignment = HorizontalAlignment.Right; row.Children.Add(pressure);
        comp.Children.Add(row); comp.Children.Add(bar);
        View.Children.Add(Theme.Card(comp));
        inUse = AddStat("In use (compressed)"); available = AddStat("Available"); committed = AddStat("Committed");
        cached = AddStat("Cached"); paged = AddStat("Paged pool"); nonpaged = AddStat("Non-paged pool");
        AddStatsCard();
    }
    public override void Update(Snapshot s)
    {
        var m = s.Memory;
        Device.Text = $"{Fmt.Memory(m.Total)} installed";
        graph.Redraw();
        long cachedPart = Math.Min(m.Cached, m.Available);
        bar.Set(m.Total, (m.Used, Theme.Memory, "In use"), (cachedPart, Color.FromArgb(255, 150, 150, 160), "Cached"));
        var level = m.Pressure;
        pressure.Content = Theme.Pill("Pressure: " + MemoryPressure.Title(level), Theme.PressureColor(level));
        inUse.Text = $"{Fmt.Memory(m.Used)} ({Fmt.Memory(m.Compressed)})"; available.Text = Fmt.Memory(m.Available);
        committed.Text = $"{Fmt.Memory(m.Committed)}/{Fmt.Memory(m.CommitLimit)}"; cached.Text = Fmt.Memory(m.Cached);
        paged.Text = Fmt.Memory(m.PagedPool); nonpaged.Text = Fmt.Memory(m.NonPagedPool);
    }
}

internal sealed class DiskDetail : Detail
{
    readonly string name;
    readonly Graph active, transfer;
    readonly TextBlock activeT, resp, read, write, capacity, free, system;
    public DiskDetail(string name) : base(name)
    {
        this.name = name;
        active = BigGraph(Theme.Disk, "Active time", "100%", 200);
        active.SetSeries((AppState.Get("disk:" + name), Theme.Disk, true));
        transfer = BigGraph(Theme.Disk, "Disk transfer rate (read, write)", "", 140);
        transfer.Max = null;
        transfer.SetSeries((AppState.Get("diskr:" + name), Theme.Disk, true), (AppState.Get("diskw:" + name), Color.FromArgb(255, 30, 120, 60), false));
        activeT = AddStat("Active time"); resp = AddStat("Average response time"); read = AddStat("Read speed"); write = AddStat("Write speed");
        capacity = AddStat("Capacity"); free = AddStat("Free"); system = AddStat("System disk");
        AddStatsCard();
    }
    public override void Update(Snapshot s)
    {
        var d = s.Disks.FirstOrDefault(x => x.Name == name);
        if (d == null) return;
        Device.Text = d.Model;
        active.Redraw(); transfer.Redraw();
        activeT.Text = Fmt.Percent(d.ActivePercent); resp.Text = $"{d.ResponseMs:0.0} ms";
        read.Text = Fmt.Rate(d.ReadPerSec); write.Text = Fmt.Rate(d.WritePerSec);
        capacity.Text = d.Capacity > 0 ? Fmt.Bytes(d.Capacity) : "—"; free.Text = d.Capacity > 0 ? Fmt.Bytes(d.Free) : "—";
        system.Text = d.IsSystem ? "Yes" : "No";
    }
}

internal sealed class NetDetail : Detail
{
    readonly string id;
    readonly Graph graph;
    readonly TextBlock send, receive, adapter, kind, v4, v6, speed;
    public NetDetail(string id) : base("Network")
    {
        this.id = id;
        graph = BigGraph(Theme.Network, "Throughput (receive, send)", "", 260);
        graph.Max = null;
        graph.SetSeries((AppState.Get("netin:" + id), Theme.Network, true), (AppState.Get("netout:" + id), Color.FromArgb(255, 200, 100, 0), false));
        send = AddStat("Send"); receive = AddStat("Receive"); adapter = AddStat("Adapter name"); kind = AddStat("Connection type");
        v4 = AddStat("IPv4 address"); v6 = AddStat("IPv6 address"); speed = AddStat("Link speed");
        AddStatsCard();
    }
    public override void Update(Snapshot s)
    {
        var n = s.Networks.FirstOrDefault(x => x.Id == id);
        if (n == null) return;
        Heading.Text = n.Kind; Device.Text = n.Description;
        graph.Redraw();
        send.Text = Fmt.Bits(n.OutPerSec); receive.Text = Fmt.Bits(n.InPerSec); adapter.Text = n.Name; kind.Text = n.Kind;
        v4.Text = n.IPv4.Length > 0 ? n.IPv4 : "—"; v6.Text = n.IPv6.Length > 0 ? n.IPv6 : "—";
        speed.Text = n.SpeedBitsPerSec > 0 ? Fmt.Bits(n.SpeedBitsPerSec / 8.0) : "—";
    }
}

internal sealed class GpuDetail : Detail
{
    readonly Graph[] engines = new Graph[4];
    readonly Graph memory;
    static readonly string[] Names = { "3D", "Copy", "VideoDecode", "VideoEncode" };
    readonly TextBlock util, dedicated, shared;
    public GpuDetail() : base("GPU")
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < 4; i++)
        {
            var p = new StackPanel { Spacing = 4 };
            p.Children.Add(Theme.Text(Names[i] switch { "VideoDecode" => "Video Decode", "VideoEncode" => "Video Encode", var n => n }, 12, Theme.Secondary));
            engines[i] = new Graph(Theme.Gpu) { Height = 130 };
            engines[i].SetSeries((AppState.Get("gpu:" + Names[i]), Theme.Gpu, true));
            p.Children.Add(engines[i]);
            Grid.SetColumn(p, i % 2); Grid.SetRow(p, i / 2); grid.Children.Add(p);
        }
        View.Children.Add(Theme.Card(grid));
        memory = BigGraph(Theme.Gpu, "Dedicated GPU memory usage", "", 90);
        memory.Max = null;
        memory.SetSeries((AppState.Get("gpumem"), Theme.Gpu, true));
        util = AddStat("Utilization"); dedicated = AddStat("Dedicated GPU memory"); shared = AddStat("Shared GPU memory");
        AddStatsCard();
    }
    public override void Update(Snapshot s)
    {
        var g = s.Gpus.FirstOrDefault();
        if (g == null) return;
        Device.Text = g.Name;
        foreach (var e in engines) e.Redraw();
        memory.Redraw();
        util.Text = Fmt.Percent(g.Utilization);
        dedicated.Text = g.DedicatedTotal > 0 ? $"{Fmt.Memory(g.DedicatedUsed)}/{Fmt.Memory(g.DedicatedTotal)}" : Fmt.Memory(g.DedicatedUsed);
        shared.Text = Fmt.Memory(g.SharedUsed);
    }
}

/// Gentle fades between pages and views (a macOS touch).
internal static class Motion
{
    public static void FadeIn(UIElement e, int ms = 180)
    {
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var a = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(a, e);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(a, "Opacity");
        sb.Children.Add(a);
        sb.Begin();
    }
}
