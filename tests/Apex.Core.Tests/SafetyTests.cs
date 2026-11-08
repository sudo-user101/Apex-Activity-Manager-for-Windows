using Apex.Core;

namespace Apex.Core.Tests;

/// 2026-11-07, matching the Mac's crash and notice fixes.
public class SafeNumTests
{
    [Fact] public void OutOfRangeValuesDontThrow()
    {
        Assert.Equal(long.MaxValue, SafeNum.ToLong(ulong.MaxValue));
        Assert.Equal(int.MaxValue, SafeNum.ToInt(long.MaxValue));
        Assert.Equal(0, SafeNum.ToLong(double.NaN));
        Assert.Equal(long.MaxValue, SafeNum.ToLong(double.PositiveInfinity));
        Assert.Equal(0UL, SafeNum.ToULong(-5));
        Assert.Equal(0, SafeNum.ToLong("not a number"));
        Assert.Equal(0, SafeNum.ToLong(null));
        Assert.Equal(1024, SafeNum.ToLong(1024u));
        Assert.Equal(12, SafeNum.ToLong("12"));
        Assert.Equal(-3, SafeNum.ToInt((short)-3));
    }

    [Fact] public void FormattingShowsNumbersNeverNaN()
    {
        foreach (var v in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1e300, 1e300, -5 })
        {
            foreach (var s in new[] { Fmt.Bytes(v), Fmt.Memory(v), Fmt.Rate(v), Fmt.Bits(v), Fmt.Percent(v), Fmt.Duration(v) })
            {
                Assert.DoesNotContain("NaN", s);
                Assert.DoesNotContain("∞", s);
                Assert.DoesNotContain("-2147483648", s);
            }
        }
        Assert.Equal("0:00", Fmt.Clock(TimeSpan.FromSeconds(-10)));
        Assert.Equal("0 s", Fmt.Duration(double.NaN));
    }
}

public class NoticeTests
{
    [Fact] public void VersionsCompareByNumber()
    {
        Assert.True(AppUpdate.IsNewer("1.10.0", "1.9.0"));
        Assert.True(AppUpdate.IsNewer("1.7.0", "1.6.0"));
        Assert.False(AppUpdate.IsNewer("1.6.0", "1.6.0"));
        Assert.False(AppUpdate.IsNewer("1.6", "1.6.0"));
        Assert.False(AppUpdate.IsNewer("1.5.9", "1.6.0"));
        Assert.False(AppUpdate.IsNewer("nonsense", "1.0"));
    }

    [Fact] public void TheVersionInAReleaseAnswer()
    {
        Assert.Equal("1.7.0", AppUpdate.VersionFrom("{\"tag_name\":\"v1.7.0\"}"));
        Assert.Null(AppUpdate.VersionFrom("{\"tag_name\":\"latest-build\"}"));
        Assert.Null(AppUpdate.VersionFrom("{\"message\":\"rate limited\"}"));
        Assert.Null(AppUpdate.VersionFrom("not json"));
    }

    const string Log =
        "2026-11-01T10:00:00.0000000+02:00\nSystem.Exception: old\n\n" +
        "2026-11-07T09:30:00.0000000+02:00\nSystem.IO.FileNotFoundException: Could not find file 'C:\\Users\\Siyanda\\Secret Project\\notes.txt'.\n" +
        "   at Apex.Services.Sampler.Read() in D:\\a\\Apex\\src\\Apex.Windows\\Services\\Sampler.cs:line 135\n" +
        "   at Apex.MainWindow.Tick() in \\\\server\\share\\x.cs:line 9\n\n";

    [Fact] public void TheNewestNoteIsFound()
    {
        var last = CrashReport.Latest(Log);
        Assert.NotNull(last);
        Assert.StartsWith("System.IO.FileNotFoundException", last!.Value.Text);
        Assert.Equal(2026, last.Value.When.Year);
        Assert.Null(CrashReport.Latest(""));
    }

    [Fact] public void AReportHasNoPathsOrNames()
    {
        var cleaned = CrashReport.Clean(CrashReport.Latest(Log)!.Value.Text, "Siyanda");
        foreach (var secret in new[] { "Siyanda", "Secret Project", "C:\\", "D:\\", ":line", "\\\\server" })
            Assert.DoesNotContain(secret, cleaned);
        Assert.Contains("Apex.Services.Sampler.Read()", cleaned);
        var url = CrashReport.IssueUrl("1.7.0", "Windows 11 (26100)", cleaned);
        Assert.StartsWith(CrashReport.NewIssue + "?title=", url);
        Assert.DoesNotContain("Siyanda", Uri.UnescapeDataString(url));
    }
}
