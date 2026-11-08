using Apex.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Apex.UI;

/// The strip above each page: a crash last time, or a newer version.
internal sealed class Notices : StackPanel
{
    readonly InfoBar crash = new()
    {
        Title = "Apex quit unexpectedly last time",
        Message = "Reporting it helps fix it. You’ll see everything on GitHub before it’s sent.",
        Severity = InfoBarSeverity.Warning, IsClosable = false,
    };
    readonly InfoBar update = new() { Severity = InfoBarSeverity.Informational, IsClosable = false };
    string? report;

    public Notices()
    {
        Spacing = 0;
        var send = new Button { Content = "Report on GitHub…" };
        send.Click += (_, _) => { if (report != null) CrashCheck.Report(report); Hide(crash); };
        var later = new Button { Content = "Not Now" };
        later.Click += (_, _) => { CrashCheck.MarkSeen(); Hide(crash); };
        crash.Content = Pair(send, later);

        var get = new Button { Content = "Download" };
        get.Click += (_, _) => UpdateCheck.OpenDownload();
        var skip = new Button { Content = "Skip This Version" };
        skip.Click += (_, _) => UpdateCheck.Skip();
        update.Content = Pair(get, skip);

        Children.Add(crash);
        Children.Add(update);
        crash.IsOpen = false; update.IsOpen = false;
        UpdateCheck.Changed += v => MainWindow.Ui(() => ShowUpdate(v));
    }

    static StackPanel Pair(Button a, Button b) =>
        new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 12), Children = { a, b } };

    public void ShowCrash(string? cleaned)
    {
        report = cleaned;
        crash.IsOpen = cleaned != null;
    }

    void ShowUpdate(string? v)
    {
        update.Title = v == null ? "" : $"Apex {v} is available";
        update.IsOpen = v != null;
    }

    static void Hide(InfoBar b) => b.IsOpen = false;
}
