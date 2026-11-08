using System.Diagnostics;
using Apex.Core;
using Apex.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Apex.UI;

/// Process details › Check on VirusTotal, as on the Mac: the answer shows here,
/// with the person's own free key. Everything stays inside this card (Windows
/// shows one dialog at a time).
internal sealed class VirusTotalCard
{
    readonly string path;
    readonly StackPanel body = new() { Spacing = 8 };
    string? sha;
    CancellationTokenSource? scanning;
    public UIElement View { get; }

    public VirusTotalCard(string path)
    {
        this.path = path;
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(Theme.Text("VirusTotal", 14, bold: true));
        p.Children.Add(body);
        View = Theme.Card(p, 12);
        Ready();
    }

    void Set(params UIElement[] items) { body.Children.Clear(); foreach (var i in items) body.Children.Add(i); }

    static TextBlock Words(string t) { var x = Theme.Text(t, 13, Theme.Secondary); x.TextWrapping = TextWrapping.Wrap; return x; }
    static Button Btn(string t, Action a) { var b = new Button { Content = t }; b.Click += (_, _) => a(); return b; }
    static HyperlinkButton Link(string t, string url) => new() { Content = t, NavigateUri = new Uri(url), Padding = new Thickness(0) };
    static StackPanel Row(params UIElement[] e) { var r = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 }; foreach (var x in e) r.Children.Add(x); return r; }

    void Ready() => Set(Words("Checks this program file, not the whole app. Only its fingerprint (SHA-256) is sent."), Btn("Check on VirusTotal", () => _ = Check()));

    void Busy(string t) => Set(Row(new ProgressRing { IsActive = true, Width = 18, Height = 18 }, Words(t)));

    async Task Check()
    {
        Busy("Checking…");
        sha ??= await VirusTotalClient.Sha256(path);
        if (sha == null) { Set(Words("Apex can’t read this file, so it can’t be checked."), Btn("Try Again", () => _ = Check())); return; }
        if (VirusTotalClient.Key is not { } key) { NeedsKey(null); return; }
        var (outcome, report, problem) = await VirusTotalClient.Lookup(sha, key);
        Show(outcome, report, problem);
    }

    void Show(VirusTotalClient.Outcome outcome, VirusTotal.Report? r, string? problem)
    {
        switch (outcome)
        {
            case VirusTotalClient.Outcome.Found:
                var color = r!.Malicious > 0 ? Theme.Red : r.Flagged > 0 ? Theme.Yellow : Theme.Green;
                Set(Row(Theme.Pill(r.Flagged == 0 ? "Not flagged" : $"{r.Flagged} flagged", color), Words(VirusTotal.Words(r))),
                    Words(r.Scanned is { } d ? "Last scanned " + d.LocalDateTime.ToString("d") : ""),
                    Row(Link("Open Full Report", VirusTotal.ReportPage(sha!)), Btn("Check Again", () => _ = Check())));
                break;
            case VirusTotalClient.Outcome.Unknown:
                Set(Words("VirusTotal has never seen this file, so there’s no result yet. That doesn’t mean it’s safe or harmful. Upload it to have it scanned."),
                    Row(Btn("Upload to VirusTotal…", ConfirmUpload), Btn("Check Again", () => _ = Check())));
                break;
            case VirusTotalClient.Outcome.StillScanning:
                Set(Words("VirusTotal is still scanning this file. Check again in a few minutes."), Btn("Check Again", () => _ = Check()));
                break;
            default:
                if (problem == VirusTotal.Problem(401)) { NeedsKey(problem); return; }
                Set(Words(problem ?? ""), Btn("Try Again", () => _ = Check()));
                break;
        }
    }

    void ConfirmUpload() => Set(
        Words("The whole file is sent, not just its fingerprint. VirusTotal shares it with security companies and paying customers, and its report is public. This can’t be undone, so don’t upload anything with private information in it."),
        Row(Btn("Upload", () => _ = Upload()), Btn("Cancel", () => Show(VirusTotalClient.Outcome.Unknown, null, null))));

    async Task Upload()
    {
        if (VirusTotalClient.Key is not { } key) { NeedsKey(null); return; }
        Busy("Uploading and scanning. This can take a few minutes.");
        scanning = new CancellationTokenSource();
        var (outcome, report, problem) = await VirusTotalClient.Scan(path, key, scanning.Token);
        Show(outcome, report, problem);
    }

    void NeedsKey(string? problem)
    {
        var box = new PasswordBox { PlaceholderText = "API key", Width = 300 };
        var save = Btn("Save", () => { if (VirusTotalClient.SaveKey(box.Password)) _ = Check(); });
        Set(Words(problem ?? "To show the answer here, Apex needs your free VirusTotal key. Only the file’s fingerprint is sent."),
            Row(box, save),
            Row(Link("Get a Free Key", VirusTotal.KeyPage), sha != null ? Link("Open in Browser", VirusTotal.ReportPage(sha)) : new TextBlock()),
            Words("In the browser, “not found” means VirusTotal has never seen this file."));
    }
}
