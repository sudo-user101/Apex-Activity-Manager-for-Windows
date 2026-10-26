namespace Apex.Core;

/// How strongly a cell is shaded in the Processes table (the Windows-style
/// "heat" columns): 0 none … 5 strongest.
public static class Heat
{
    public static int Level(double value, double max)
    {
        if (max <= 0 || value <= 0) return 0;
        double f = value / max;
        return f switch
        {
            < 0.02 => 0,
            < 0.10 => 1,
            < 0.25 => 2,
            < 0.50 => 3,
            < 0.80 => 4,
            _ => 5,
        };
    }
}
