using Apex.Core;

namespace Apex.Core.Tests;

public class DriveDoctorTests
{
    static DriveHealth.Drive D(string health = "Healthy", string op = "OK", int? wear = 10, int? temp = 40, long? rerr = 0, long? werr = 0) =>
        new("Samsung SSD", "SSD", "NVMe", 512_000_000_000, health, op, wear, temp, rerr, werr, 1000);

    static List<Finding> Run(DriveHealth.Drive? d, long free = 200_000_000_000, long cap = 512_000_000_000, int? shut = 0, bool admin = true) =>
        DriveDoctor.Diagnose(new() { Health = d, Free = free, Capacity = cap, IsSystem = true, Letter = "C:", UnexpectedShutdowns = shut, IsAdmin = admin });

    [Fact] public void AHealthyDriveSaysSo()
    {
        var f = Run(D());
        Assert.Single(f);
        Assert.Equal("healthy", f[0].Id);
    }

    [Fact] public void AFailingDriveComesFirstAndSaysBackUp()
    {
        var f = Run(D(health: "Unhealthy", op: "Predictive failure", rerr: 3), free: 10_000_000_000);
        Assert.Equal("health.critical", f[0].Id);
        Assert.Equal(Severity.Critical, f[0].Severity);
        Assert.Contains(f[0].Steps, s => s.StartsWith("Back up"));
        Assert.Contains(f, x => x.Id == "errors" && x.Action == DriveDoctor.CheckDisk);
        Assert.Contains(f, x => x.Id == "space.low" && x.Action == DriveDoctor.Clean);
        Assert.DoesNotContain(f, x => x.Id == "healthy");
    }

    [Fact] public void WearHeatSpaceAndShutdownThresholds()
    {
        Assert.Contains(Run(D(wear: 85)), x => x.Id == "wear" && x.Severity == Severity.Notice);
        Assert.Contains(Run(D(wear: 95)), x => x.Id == "wear" && x.Severity == Severity.Warning);
        Assert.DoesNotContain(Run(D(wear: 70)), x => x.Id == "wear");
        Assert.Contains(Run(D(temp: 72)), x => x.Id == "heat");
        Assert.Contains(Run(D(), free: 20_000_000_000), x => x.Id == "space.low" && x.Severity == Severity.Warning);
        Assert.DoesNotContain(Run(D(), free: 100_000_000_000), x => x.Id == "space.low");
        Assert.Contains(Run(D(), shut: 4), x => x.Id == "shutdowns");
        Assert.DoesNotContain(Run(D(), shut: 2), x => x.Id == "shutdowns");
    }

    [Fact] public void MissingCountsAskForAdministratorOnlyWhenNotAdmin()
    {
        var d = D(wear: null, rerr: null, werr: null);
        Assert.Contains(Run(d, admin: false), x => x.Id == "admin" && x.Action == DriveDoctor.Permissions);
        Assert.DoesNotContain(Run(d, admin: true), x => x.Id == "admin");
        Assert.Equal("healthy", Run(d, admin: false)[0].Id);
    }

    [Fact] public void NoHealthDataStillGivesAnAnswer()
    {
        Assert.Equal("healthy", Run(null)[0].Id);
        Assert.Equal("space.low", Run(null, free: 1_000_000_000)[0].Id);
    }

    [Fact] public void Letters()
    {
        Assert.Equal("C:", DriveDoctor.FirstLetter("Disk 0 (C:)"));
        Assert.Equal("D:", DriveDoctor.FirstLetter("Disk 1 (d: E:)"));
        Assert.Equal("", DriveDoctor.FirstLetter("Disk 2"));
        Assert.Equal(2, DriveDoctor.CountEvents("<Events><Event xmlns='x'/><Event xmlns='x'/></Events>"));
        Assert.Equal(0, DriveDoctor.CountEvents(""));
    }
}
