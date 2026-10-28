using Apex.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Apex.UI;

/// A line graph of the last minute: Task Manager's grid and tinted border,
/// with rounded corners and a soft fill.
internal sealed class Graph : Grid
{
    readonly Canvas canvas = new();
    readonly List<(TimeSeries Series, Color Color, bool Fill)> series = new();
    public double? Max { get; set; } = 100;
    public bool ShowGrid { get; set; } = true;

    public Graph(Color tint, double corner = 8)
    {
        Children.Add(canvas);
        CornerRadius = new CornerRadius(corner);
        BorderThickness = new Thickness(1);
        BorderBrush = Theme.Solid(tint, 0.55);
        Background = Theme.Solid(tint, 0.04);
        SizeChanged += (_, _) => Redraw();
    }

    // Made once and reused: grid lines (remade only when the size changes), and per
    // series one fill and one line whose points are swapped in each update.
    readonly List<(Polygon? Fill, Polyline Line)> shapes = new();
    Size gridFor;
    readonly List<Line> gridLines = new();

    public void SetSeries(params (TimeSeries Series, Color Color, bool Fill)[] list)
    {
        series.Clear(); series.AddRange(list);
        foreach (var (f, l) in shapes) { if (f != null) canvas.Children.Remove(f); canvas.Children.Remove(l); }
        shapes.Clear();
        foreach (var (_, color, fill) in series)
        {
            Polygon? poly = null;
            if (fill)
            {
                poly = new Polygon
                {
                    Fill = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                        GradientStops = { new GradientStop { Color = Color.FromArgb(90, color.R, color.G, color.B), Offset = 0 },
                                          new GradientStop { Color = Color.FromArgb(18, color.R, color.G, color.B), Offset = 1 } },
                    },
                };
                canvas.Children.Add(poly);
            }
            var line = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
            canvas.Children.Add(line);
            shapes.Add((poly, line));
        }
        Redraw();
    }

    public void Redraw()
    {
        double w = canvas.ActualWidth > 0 ? canvas.ActualWidth : ActualWidth - 2;
        double h = canvas.ActualHeight > 0 ? canvas.ActualHeight : ActualHeight - 2;
        if (w <= 2 || h <= 2) return;
        if (ShowGrid && gridFor != new Size(w, h))
        {
            gridFor = new Size(w, h);
            foreach (var l in gridLines) canvas.Children.Remove(l);
            gridLines.Clear();
            var tint = (BorderBrush as SolidColorBrush)?.Color ?? Theme.Cpu;
            var gridBrush = Theme.Solid(tint, 0.14);
            for (int i = 1; i < 10; i++) gridLines.Add(new Line { X1 = w * i / 10, X2 = w * i / 10, Y1 = 0, Y2 = h, Stroke = gridBrush, StrokeThickness = 0.5 });
            for (int i = 1; i < 4; i++) gridLines.Add(new Line { X1 = 0, X2 = w, Y1 = h * i / 4, Y2 = h * i / 4, Stroke = gridBrush, StrokeThickness = 0.5 });
            for (int i = 0; i < gridLines.Count; i++) canvas.Children.Insert(i, gridLines[i]);      // behind the lines
        }
        var now = DateTime.Now;
        double top = Max ?? Math.Max(1, series.Select(s => s.Series.Max).DefaultIfEmpty(1).Max() * 1.15);
        for (int k = 0; k < series.Count && k < shapes.Count; k++)
        {
            var (fill, line) = shapes[k];
            var linePts = new PointCollection();
            foreach (var p in series[k].Series.Normalised(now)) linePts.Add(new Point(p.X * w, h - Math.Clamp(p.Value / top, 0, 1) * h));
            if (linePts.Count < 2) { line.Points = new PointCollection(); if (fill != null) fill.Points = new PointCollection(); continue; }
            line.Points = linePts;                                    // one change, not one per point
            if (fill != null)
            {
                var area = new PointCollection();
                foreach (var p in linePts) area.Add(p);
                area.Add(new Point(linePts[^1].X, h)); area.Add(new Point(linePts[0].X, h));
                fill.Points = area;
            }
        }
    }
}

/// One horizontal bar split into parts (memory composition).
internal sealed class StackedBar : Grid
{
    public StackedBar() { Height = 34; CornerRadius = new CornerRadius(6); }

    public void Set(double total, params (double Value, Color Color, string Label)[] parts)
    {
        Children.Clear(); ColumnDefinitions.Clear();
        double used = 0;
        int col = 0;
        foreach (var (v, c, label) in parts)
        {
            if (v <= 0 || total <= 0) continue;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(v / total, GridUnitType.Star) });
            var b = new Border { Background = Theme.Solid(c, 0.75), BorderBrush = Theme.Solid(c), BorderThickness = new Thickness(0, 0, 1, 0) };
            ToolTipService.SetToolTip(b, label);
            SetColumn(b, col++); Children.Add(b);
            used += v;
        }
        if (total > used)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength((total - used) / total, GridUnitType.Star) });
            var rest = new Border { Background = Theme.Subtle };
            SetColumn(rest, col); Children.Add(rest);
        }
    }
}
