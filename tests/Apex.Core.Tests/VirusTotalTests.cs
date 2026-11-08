using Apex.Core;

namespace Apex.Core.Tests;

/// Replies shaped as in VirusTotal's API documentation.
public class VirusTotalTests
{
    [Fact] public void AKnownFileGivesItsCounts()
    {
        var r = VirusTotal.FromLookup("{\"data\":{\"attributes\":{\"last_analysis_date\":1762214400,\"last_analysis_stats\":{\"malicious\":2,\"suspicious\":1,\"harmless\":3,\"undetected\":60,\"timeout\":4,\"type-unsupported\":9}}}}");
        Assert.NotNull(r);
        Assert.Equal(3, r!.Flagged);
        Assert.Equal(66, r.Scanners);
        Assert.Equal(1762214400, r.Scanned!.Value.ToUnixTimeSeconds());
        Assert.Contains("3 of 66", VirusTotal.Words(r));
    }

    [Fact] public void ScansReportOnlyWhenFinished()
    {
        Assert.Null(VirusTotal.FromAnalysis("{\"data\":{\"attributes\":{\"status\":\"queued\",\"stats\":{}}}}"));
        var done = VirusTotal.FromAnalysis("{\"data\":{\"attributes\":{\"status\":\"completed\",\"date\":1762214400,\"stats\":{\"undetected\":70}}}}");
        Assert.Equal(70, done!.Scanners);
        Assert.Contains("None of 70", VirusTotal.Words(done));
        Assert.Equal("abc", VirusTotal.IdFrom("{\"data\":{\"type\":\"analysis\",\"id\":\"abc\"}}"));
        Assert.Null(VirusTotal.FromLookup("not json"));
    }

    [Fact] public void TheKeyNeverGoesAnywhereElse()
    {
        Assert.True(VirusTotal.IsSafeUploadAddress("https://www.virustotal.com/_ah/upload/xyz"));
        foreach (var bad in new[] { "https://evil.example/up", "http://www.virustotal.com/up", "https://notvirustotal.com/up", "https://virustotal.com.evil.example/x", null, "" })
            Assert.False(VirusTotal.IsSafeUploadAddress(bad), bad);
    }

    [Fact] public void ProblemsInPlainWords()
    {
        Assert.Contains("didn’t accept", VirusTotal.Problem(401));
        Assert.Contains("4 checks a minute", VirusTotal.Problem(429));
        Assert.Contains("503", VirusTotal.Problem(503));
    }
}
