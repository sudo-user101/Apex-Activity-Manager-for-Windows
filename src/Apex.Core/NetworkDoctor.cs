namespace Apex.Core;

public enum Severity { Ok, Info, Notice, Warning, Critical }

/// One thing a Doctor found: what, why it matters, the evidence, likely
/// causes and steps, as in the Mac app's finding cards.
public sealed record Finding(string Id, string Title, string Why, Severity Severity,
    IReadOnlyList<(string Label, string Value)> Evidence, IReadOnlyList<string> Causes, IReadOnlyList<string> Steps, string Action = "");

/// Network Doctor, ported from the Mac: the checks go from the PC outwards
/// (link → address → router → internet → names → sign-in page), so the first
/// thing that fails is the place to fix. Pure: tested.
public static class NetworkDoctor
{
    public sealed record Ping(int Sent, int Received, double? AvgMs)
    {
        public double Loss => Sent > 0 ? 1 - (double)Received / Sent : 1;
        public bool Reachable => Received > 0;
    }

    public sealed class Input
    {
        public string Name { get; init; } = "Wi-Fi";
        public string Kind { get; init; } = "Wi-Fi";
        public bool IsUp { get; init; } = true;
        public string Address { get; init; } = "";
        public string Gateway { get; init; } = "";
        /// Wi-Fi signal quality as Windows reports it (0–100 %).
        public int? SignalPercent { get; init; }
        public Ping? Router { get; init; }
        public Ping? Internet { get; init; }
        public bool? HttpsOk { get; init; }
        public bool? DnsOk { get; init; }
        public double? DnsMs { get; init; }
        public bool? SignInPage { get; init; }
        public double? DownloadBitsPerSec { get; init; }
    }

    public static List<Finding> Diagnose(Input i)
    {
        var f = new List<Finding>();
        bool wifi = i.Kind == "Wi-Fi";
        var wifiEvidence = wifi && i.SignalPercent is { } sp ? new List<(string, string)> { ("Signal", $"{sp}% ({SignalWord(sp)})") } : new();
        static List<(string, string)> E(params (string, string)[] e) => e.ToList();

        if (!i.IsUp)
        {
            f.Add(new("net.down", wifi ? "Wi-Fi isn’t connected to a network" : $"{i.Name} has no connection", "Nothing can get online until this connection is up.",
                Severity.Critical, E(("Status", "Not connected")),
                wifi ? new[] { "Wi-Fi is turned off", "Not joined to a network, or the password changed", "Out of range of the router" }
                     : new[] { "The cable is unplugged or damaged", "The router or switch port is off" },
                wifi ? new[] { "Turn Wi-Fi on (Win+A) and choose your network" } : new[] { "Check both ends of the cable, or try another port on the router" },
                wifi ? "wifi-settings" : "network-settings"));
            return f;
        }
        if (i.Address.Length == 0 || i.Address.StartsWith("169.254."))
            f.Add(new("net.dhcp", "The router didn’t give this PC an address", "Without an address from the router, the PC is connected but can’t talk to anything.",
                Severity.Critical, E(("Address", i.Address.Length == 0 ? "none" : i.Address + " (made up by Windows)")),
                new[] { "The router is busy or needs a restart", "The router has run out of addresses for new devices" },
                new[] { "Turn Wi-Fi off and on again (or unplug and replug the cable)", "Restart the router (unplug it for 10 seconds)" }, "network-settings"));

        if (i.Router is { } r && i.Gateway.Length > 0)
        {
            bool weak = wifi && (i.SignalPercent ?? 100) < 40;
            if (!r.Reachable)
                f.Add(new("net.router", "The router doesn’t answer", "Everything goes through the router; if it doesn’t answer, nothing gets online.",
                    Severity.Critical, E(("Router", i.Gateway), ("Answers", $"0 of {r.Sent}")).Concat(wifiEvidence).ToList(),
                    (weak ? new[] { "The Wi-Fi signal is too weak here" } : Array.Empty<string>()).Concat(new[] { "The router has frozen", "Some routers ignore these checks (then the next results tell more)" }).ToList(),
                    new[] { "Move closer to the router, or restart it (unplug for 10 seconds)" }));
            else if (r.Loss >= 0.2)
                f.Add(new("net.router.loss", "The connection to your router keeps dropping", $"{Math.Round(r.Loss * 100)}% of messages to the router were lost. Pages stall, calls break up.",
                    Severity.Warning, E(("Lost", $"{r.Sent - r.Received} of {r.Sent}")).Concat(wifiEvidence).ToList(),
                    wifi ? new[] { "Weak signal or walls in the way", "Interference from other networks or devices" } : new[] { "A damaged cable or loose plug" },
                    wifi ? new[] { "Move closer to the router", "Use the 5 GHz network if your router has one" } : new[] { "Try another cable" }));
            else if (wifi && r.AvgMs is > 30)
                f.Add(new("net.router.slow", "Your Wi-Fi is congested", $"The router takes {r.AvgMs:0} ms to answer; on a healthy Wi-Fi it's under 10 ms.",
                    Severity.Notice, E(("Router answers in", $"{r.AvgMs:0} ms")).Concat(wifiEvidence).ToList(),
                    new[] { "Many devices busy on the same Wi-Fi", "Neighbouring networks on the same channel", "A weak signal" },
                    new[] { "Pause big downloads or streams on other devices", "Restart the router so it picks a quieter channel" }));
        }

        bool routerOk = i.Router?.Reachable ?? true;
        bool internetOk = (i.Internet?.Reachable ?? false) || (i.HttpsOk ?? false);
        if (routerOk && (i.Internet != null || i.HttpsOk != null) && !internetOk && i.SignInPage != true)
            f.Add(new("net.internet", "Your router is fine, but the internet behind it isn’t", "The PC reaches the router, but nothing past it. The problem is the internet connection itself, not this PC.",
                Severity.Critical, E(("Router", "answers"), ("Internet", "no answer")),
                new[] { "Your internet provider has an outage", "The modem or router lost its internet connection", "An unpaid or expired plan" },
                new[] { "Restart the modem and router (unplug both for 30 seconds, then the modem first)", "If other devices can't get online either, contact your internet provider" }));
        else if (i.Internet is { Reachable: true, AvgMs: > 150 } p)
            f.Add(new("net.latency", "The internet is slow to respond", $"Messages take {p.AvgMs:0} ms to get a reply; video calls and games feel laggy above about 100 ms.",
                Severity.Notice, E(("Reply time", $"{p.AvgMs:0} ms")), new[] { "Busy network at home", "A VPN routing traffic far away", "Congestion at your internet provider" }, Array.Empty<string>()));

        if (internetOk && i.DnsOk == false)
            f.Add(new("net.dns", "Website names don’t work (DNS)", "The internet is reachable, but turning names like microsoft.com into addresses fails, so websites won't open.",
                Severity.Warning, E(("Looking up microsoft.com", "failed")), new[] { "The router's name service has stopped", "Your provider's name service is down" },
                new[] { "Restart the router", "Or set DNS servers to 1.1.1.1 and 8.8.8.8 (Settings › Network & internet › your connection › DNS server assignment)" }, "network-settings"));
        else if (i.DnsMs is > 300)
            f.Add(new("net.dns.slow", "Website names are slow to look up", $"Each new website waits {i.DnsMs:0} ms before it starts loading.",
                Severity.Notice, E(("Looking up microsoft.com", $"{i.DnsMs:0} ms")), Array.Empty<string>(),
                new[] { "Set DNS servers to 1.1.1.1 and 8.8.8.8 (Settings › Network & internet › your connection › DNS server assignment)" }, "network-settings"));

        if (i.SignInPage == true)
            f.Add(new("net.portal", "This network wants you to sign in", "Until you accept its terms or sign in, the network blocks the internet.",
                Severity.Warning, E(("Test page", "replaced by the network's own page")), Array.Empty<string>(),
                new[] { "Open the sign-in page and accept or log in" }, "sign-in"));

        if (wifi && i.SignalPercent is { } s && !f.Any(x => x.Id.StartsWith("net.router")))
        {
            if (s < 40)
                f.Add(new("net.signal", "The Wi-Fi signal is weak here", $"At {s}% the connection slows down and drops now and then.", Severity.Warning, wifiEvidence,
                    Array.Empty<string>(), new[] { "Move closer to the router, or remove walls and metal in the way", "A mesh system or extender helps in far rooms" }));
            else if (s < 60)
                f.Add(new("net.signal", "The Wi-Fi signal is only fair", "Fine for browsing; video calls, games and big downloads may stutter.", Severity.Info, wifiEvidence,
                    Array.Empty<string>(), Array.Empty<string>()));
        }

        if (i.DownloadBitsPerSec is { } d && d < 5e6)
            f.Add(new("net.speed", "The internet speed is low", $"{d / 1e6:0.0} Mb/s down: enough for email and browsing, slow for video.", Severity.Notice,
                E(("Download", $"{d / 1e6:0.0} Mb/s")), new[] { "Your plan's speed", "Others using the internet at the same time", "Wi-Fi limits (check the signal above)" }, Array.Empty<string>()));

        if (f.Count == 0)
        {
            var ev = E(("Address", i.Address));
            if (i.Router is { } rr) ev.Add(("Router", rr.AvgMs is { } a ? $"answers in {a:0} ms" : "answers"));
            if (i.Internet is { Reachable: true } ip) ev.Add(("Internet", ip.AvgMs is { } b ? $"answers in {b:0} ms" : "answers"));
            if (i.DnsMs is { } dm) ev.Add(("Names (DNS)", $"{dm:0} ms"));
            f.Add(new("net.ok", "Everything checks out", "This PC, the router, the internet and website names all work. If something still feels slow, it's likely the website or app itself.",
                Severity.Ok, ev.Concat(wifiEvidence).ToList(), Array.Empty<string>(), Array.Empty<string>()));
        }
        return f.OrderByDescending(x => x.Severity).ToList();
    }

    public static string SignalWord(int percent) => percent >= 80 ? "excellent" : percent >= 60 ? "good" : percent >= 40 ? "fair" : "weak";
}
