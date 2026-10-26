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

    public void SetSeries(params (TimeSeries Series, Color Color, bool Fill)[] list)
    {
        series.Clear(); series.AddRange(list); Redraw();
    }

    public void Redraw()
    {
        double w = canvas.ActualWidth > 0 ? canvas.ActualWidth : ActualWidth - 2;
        double h = canvas.ActualHeight > 0 ? canvas.ActualHeight : ActualHeight - 2;
        canvas.Children.Clear();
        if (w <= 2 || h <= 2) return;
        var tint = (BorderBrush as SolidColorBrush)?.Color ?? Theme.Cpu;
        if (ShowGrid)
        {
            var gridBrush = Theme.Solid(tint, 0.14);
            for (int i = 1; i < 10; i++) canvas.Children.Add(new Line { X1 = w * i / 10, X2 = w * i / 10, Y1 = 0, Y2 = h, Stroke = gridBrush, StrokeThickness = 0.5 });
            for (int i = 1; i < 4; i++) canvas.Children.Add(new Line { X1 = 0, X2 = w, Y1 = h * i / 4, Y2 = h * i / 4, Stroke = gridBrush, StrokeThickness = 0.5 });
        }
        var now = DateTime.Now;
        double top = Max ?? Math.Max(1, series.Select(s => s.Series.Max).DefaultIfEmpty(1).Max() * 1.15);
        foreach (var (s, color, fill) in series)
        {
            var pts = s.Normalised(now).Select(p => new Point(p.X * w, h - Math.Clamp(p.Value / top, 0, 1) * h)).ToList();
            if (pts.Count < 2) continue;
            if (fill)
            {
                var poly = new Polygon
                {
                    Fill = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                        GradientStops = { new GradientStop { Color = Color.FromArgb(90, color.R, color.G, color.B), Offset = 0 },
                                          new GradientStop { Color = Color.FromArgb(18, color.R, color.G, color.B), Offset = 1 } },
                    },
                };
                foreach (var p in pts) poly.Points.Add(p);
                poly.Points.Add(new Point(pts[^1].X, h)); poly.Points.Add(new Point(pts[0].X, h));
                canvas.Children.Add(poly);
            }
            var line = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
            foreach (var p in pts) line.Points.Add(p);
            canvas.Children.Add(line);
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
