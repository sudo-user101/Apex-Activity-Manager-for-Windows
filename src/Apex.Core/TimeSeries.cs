namespace Apex.Core;

/// A rolling series of readings with their times (the last few minutes),
/// for the graphs. Old points drop off as new ones come in.
public sealed class TimeSeries
{
    readonly List<(DateTime Time, double Value)> points = new();
    public TimeSpan Window { get; }

    public TimeSeries(TimeSpan window) { Window = window; }

    public void Add(DateTime t, double v)
    {
        points.Add((t, v));
        var cutoff = t - Window;
        int drop = 0;
        while (drop < points.Count && points[drop].Time < cutoff) drop++;
        if (drop > 0) points.RemoveRange(0, drop);
    }

    public void Clear() => points.Clear();
    public int Count => points.Count;
    public double Last => points.Count > 0 ? points[^1].Value : 0;
    public double Max => points.Count > 0 ? points.Max(p => p.Value) : 0;
    public double Average => points.Count > 0 ? points.Average(p => p.Value) : 0;
    public IReadOnlyList<(DateTime Time, double Value)> Points => points;

    /// Positions 0…1 across the window (0 = oldest edge, 1 = now) for drawing.
    public IEnumerable<(double X, double Value)> Normalised(DateTime now)
    {
        var start = now - Window;
        foreach (var p in points)
            yield return (Math.Clamp((p.Time - start).TotalSeconds / Window.TotalSeconds, 0, 1), p.Value);
    }
}
