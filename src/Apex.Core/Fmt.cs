using System.Globalization;

namespace Apex.Core;

/// Numbers in plain words, the same way everywhere in the app.
public static class Fmt
{
    static readonly CultureInfo C = CultureInfo.CurrentCulture;

    /// 1 536 000 → "1.5 MB" (decimal units, like the rest of Windows' storage UI).
    public static string Bytes(double bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = { "bytes", "KB", "MB", "GB", "TB", "PB" };
        int i = 0;
        while (bytes >= 1000 && i < units.Length - 1) { bytes /= 1000; i++; }
        if (i == 0) return $"{bytes:0} bytes";
        return bytes < 10 ? bytes.ToString("0.0", C) + " " + units[i] : bytes.ToString("0", C) + " " + units[i];
    }

    /// Memory in binary units (what Windows shows for RAM): 8589934592 → "8.0 GB".
    public static string Memory(double bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        if (i == 0) return $"{bytes:0} bytes";
        return (bytes < 100 ? bytes.ToString("0.0", C) : bytes.ToString("0", C)) + " " + units[i];
    }

    /// Bytes per second → "3.2 MB/s"; under 1 KB/s → "0 KB/s".
    public static string Rate(double bytesPerSec)
    {
        if (bytesPerSec < 1000) return "0 KB/s";
        return Bytes(bytesPerSec) + "/s";
    }

    /// Network speeds in bits, like Windows' Ethernet / Wi-Fi pages: "12.4 Mbps".
    public static string Bits(double bytesPerSec)
    {
        double bits = bytesPerSec * 8;
        if (bits < 1000) return $"{bits:0} bps";
        if (bits < 1e6) return (bits / 1e3).ToString("0.0", C) + " Kbps";
        if (bits < 1e9) return (bits / 1e6).ToString("0.0", C) + " Mbps";
        return (bits / 1e9).ToString("0.0", C) + " Gbps";
    }

    public static string Percent(double v) => v.ToString("0", C) + "%";

    /// 3725 s → "1:02:05"; 75 s → "1:15".
    public static string Clock(TimeSpan t)
    {
        var s = (int)Math.Max(0, t.TotalSeconds);
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    /// Up time like Windows: "3:04:12:09" (days:hours:minutes:seconds).
    public static string UpTime(TimeSpan t) =>
        $"{(int)t.TotalDays}:{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";

    /// 45 → "45 s", 200 → "3 min 20 s", 3900 → "1 h 5 min".
    public static string Duration(double seconds)
    {
        var s = (int)Math.Round(Math.Max(0, seconds));
        if (s < 60) return $"{s} s";
        if (s < 3600) return s % 60 == 0 ? $"{s / 60} min" : $"{s / 60} min {s % 60} s";
        return $"{s / 3600} h {s % 3600 / 60} min";
    }
}
