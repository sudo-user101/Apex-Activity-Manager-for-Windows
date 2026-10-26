using Apex.Core;
using Apex.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Apex.UI;

/// Free Up Memory, as on the Mac: your apps by memory; tick some, then put
/// them in Efficiency mode, trim their memory now, or close them and open
/// them again later. Restore All is always there.
internal static class FreeUpMemory
{
    sealed record AppItem(string Name, string Path, long Memory, ProcessIdentity[] Targets);

    /// Programs with a window, helpers included, biggest first.
    static List<AppItem> Apps(Snapshot s) => s.Processes
        .Where(p => p.Path.Length > 0 && p.Pid != Environment.ProcessId)
        .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Any(p => p.HasWindow))
        .Select(g =>
        {
            var main = g.FirstOrDefault(p => p.HasWindow) ?? g.First();
            return new AppItem(main.DisplayName.Length > 0 ? main.DisplayName : main.Name, g.Key, g.Sum(p => p.Memory), g.Select(p => p.Identity).ToArray());
        })
        .OrderByDescending(a => a.Memory).ToList();

    public static async Task Show(FrameworkElement anchor)
    {
        var apps = Apps(AppState.Latest);
        var boxes = new List<(CheckBox Box, AppItem App)>();
        var list = new StackPanel { Spacing = 2 };
        foreach (var a in apps)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new CheckBox { Content = a.Name + (Efficiency.Programs.Contains(a.Path) ? "  🍃" : "") };
            row.Children.Add(box);
            var size = Theme.Text(Fmt.Memory(a.Memory), 13, Theme.Secondary); size.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(size, 1); row.Children.Add(size);
            list.Children.Add(row);
            boxes.Add((box, a));
        }
        if (apps.Count == 0) list.Children.Add(Theme.Text("No apps with windows are open.", 13, Theme.Secondary));

        var status = Theme.Text("", 13, Theme.Secondary);
        status.TextWrapping = TextWrapping.Wrap;
        List<AppItem> Chosen() => boxes.Where(b => b.Box.IsChecked == true).Select(b => b.App).ToList();

        Button Act(string text, string tip, Action run)
        {
            var b = new Button { Content = text };
            ToolTipService.SetToolTip(b, tip);
            b.Click += (_, _) => run();
            return b;
        }
        var buttons = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 168, ItemHeight = 40 };
        buttons.Children.Add(Act("Efficiency mode", "Keep them running at the lowest cost: power-saving cores, lowest priority, first to give back memory, minimized", () =>
        {
            var c = Chosen();
            foreach (var a in c) Efficiency.Set(a.Path, true, AppState.Latest.Processes);
            status.Text = c.Count == 0 ? "Tick the apps first." : $"{c.Count} app{(c.Count == 1 ? "" : "s")} in Efficiency mode. Bringing one to the front puts it back to normal.";
        }));
        buttons.Children.Add(Act("Trim memory", "Moves their memory out of use now; it comes back as they need it", () =>
        {
            var c = Chosen();
            var targets = new HashSet<ProcessIdentity>(c.SelectMany(a => a.Targets));
            int n = Efficiency.Trim(AppState.Latest.Processes.Where(p => targets.Contains(p.Identity)));
            status.Text = c.Count == 0 ? "Tick the apps first." : $"Trimmed {n} process{(n == 1 ? "" : "es")}. Windows gives the memory back to them as they use it, so this helps most for apps you aren't using.";
        }));
        buttons.Children.Add(Act("Close and remember", "Closes them like clicking ×, so they can ask to save; Reopen brings them back", () =>
        {
            var c = Chosen();
            int n = AppSessions.Close(c.Select(a => (a.Name, a.Path, a.Targets)));
            status.Text = c.Count == 0 ? "Tick the apps first." : $"Asked {n} app{(n == 1 ? "" : "s")} to close. Use Reopen to open them again.";
        }));
        buttons.Children.Add(Act("Reopen", "Opens the apps you closed here again", () =>
        {
            int n = AppSessions.Reopen();
            status.Text = n == 0 ? "Nothing to reopen." : $"Opening {n} app{(n == 1 ? "" : "s")}.";
        }));
        buttons.Children.Add(Act($"Restore all ({Efficiency.Programs.Count})", "Every app in Efficiency mode back to normal, windows included", () =>
        {
            Efficiency.RestoreAll(AppState.Latest.Processes);
            status.Text = "Everything is back to normal.";
        }));

        // With no apps open, only Reopen and Restore all can do anything.
        if (apps.Count == 0) foreach (var b in buttons.Children.OfType<Button>().Take(3)) b.IsEnabled = false;
        var content = new StackPanel { Spacing = 12, Width = 520 };
        content.Children.Add(Theme.Text("Your apps, biggest first. Tick the ones you aren’t using right now.", 14, Theme.Secondary));
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320 });
        content.Children.Add(buttons);
        content.Children.Add(status);
        var dialog = new ContentDialog { Title = "Free Up Memory", Content = content, CloseButtonText = "Done", XamlRoot = anchor.XamlRoot };
        try { await dialog.ShowAsync(); } catch { }
    }
}
