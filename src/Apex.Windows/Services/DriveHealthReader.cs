using System.Management;
using Apex.Core;

namespace Apex.Services;

/// What Windows' storage service reports for each physical drive
/// (MSFT_PhysicalDisk), and its reliability counters (wear, temperature,
/// errors, power-on hours; Windows gives those to administrators only).
/// Read-only. Judged by `Apex.Core.DriveHealth`.
internal static class DriveHealthReader
{
    const string Scope = @"root\Microsoft\Windows\Storage";

    /// Every physical drive by its disk number ("Disk 0").
    public static Dictionary<int, DriveHealth.Drive> Read()
    {
        var counters = new Dictionary<int, ManagementBaseObject>();
        try
        {
            using var rs = new ManagementObjectSearcher(Scope, "SELECT * FROM MSFT_StorageReliabilityCounter");
            foreach (var o in rs.Get())
                if (int.TryParse(o["DeviceId"]?.ToString(), out var id)) counters[id] = o;
        }
        catch { /* not administrator, or not reported: the verdict says so */ }

        var drives = new Dictionary<int, DriveHealth.Drive>();
        try
        {
            using var s = new ManagementObjectSearcher(Scope, "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk");
            foreach (var o in s.Get())
            {
                if (!int.TryParse(o["DeviceId"]?.ToString(), out var id)) continue;
                counters.TryGetValue(id, out var c);
                drives[id] = new DriveHealth.Drive(
                    Name: (o["FriendlyName"] as string)?.Trim() ?? $"Disk {id}",
                    Media: DriveHealth.MediaName(ToInt(o["MediaType"])),
                    Bus: DriveHealth.BusName(ToInt(o["BusType"])),
                    Size: (long)ToULong(o["Size"]),
                    Health: DriveHealth.HealthName(ToInt(o["HealthStatus"])),
                    Operational: o["OperationalStatus"] is ushort[] st && st.Length > 0 ? DriveHealth.OperationalName(st[0]) : "",
                    WearPercent: c == null ? null : ToNullableInt(c["Wear"]),
                    TemperatureC: c == null ? null : ToNullableInt(c["Temperature"]),
                    ReadErrors: c == null ? null : ToNullableLong(c["ReadErrorsTotal"]),
                    WriteErrors: c == null ? null : ToNullableLong(c["WriteErrorsTotal"]),
                    PowerOnHours: c == null ? null : ToNullableLong(c["PowerOnHours"]));
            }
        }
        catch { }
        return drives;
    }

    // Out-of-range or odd values must not stop the app (SafeNum).
    static int ToInt(object? v) => v == null ? -1 : SafeNum.ToInt(v);
    static ulong ToULong(object? v) => v == null ? 0 : SafeNum.ToULong(v);
    static int? ToNullableInt(object? v) => v == null ? null : SafeNum.ToInt(v);
    static long? ToNullableLong(object? v) => v == null ? null : SafeNum.ToLong(v);
}
