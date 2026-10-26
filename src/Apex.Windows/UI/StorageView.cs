using System.Diagnostics;
using Apex.Core;
using Apex.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Apex.UI;

/// Free Up Space (from the Disk page), as on the Mac: **Clean** removes the
/// leftovers Windows and apps make, each explained, with sizes; **Analyze**
/// shows what takes the space, biggest first, to drill into and move to the
/// Recycle Bin. Nothing of yours is touched without you choosing it.
internal static class StorageView
{
    public static async Task Show(FrameworkElement anchor)
    {
        var tabs = new Pivot();
        tabs.Items.Add(new PivotItem { Header = "Clean", Content = CleanPane() });
        tabs.Items.Add(new PivotItem { Header = "Analyze", Content = AnalyzePane() });
        var d = new ContentDialog { Title = "Free Up Space", Content = tabs, CloseButtonText = "Done", XamlRoot = anchor.XamlRoot };
        d.Resources["ContentDialogMaxWidth"] = 820.0;
        try { await d.ShowAsync(); } catch { }
    }

    // ---- Clean ----------------------------------------------------------------

    static UIElement CleanPane()
    {
        var root = new StackPanel { Spacing = 10, Width = 720 };
        var list = new StackPanel { Spacing = 4 };
        var status = Theme.Text("Looking for things to clean…", 13, Theme.Secondary); status.TextWrapping = TextWrapping.Wrap;
        var clean = new Button { Content = "Clean", IsEnabled = false, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var rows = new List<(CheckBox Box, CleanCategory Cat, long Size, int Count)>();

        root.Children.Add(Theme.Text("Leftovers Windows and apps make. Files in use are skipped; your documents, photos and downloads are never included.", 13, Theme.Secondary));
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360 });
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        bottom.Children.Add(clean);
        var (rbSize, rbItems) = StorageClean.RecycleBin();
        var empty = new Button { Content = rbItems > 0 ? $"Empty Recycle Bin · {Fmt.Bytes(rbSize)}" : "Recycle Bin is empty", IsEnabled = rbItems > 0 };
        bottom.Children.Add(empty);
        root.Children.Add(bottom);
        root.Children.Add(status);

        void UpdateButton()
        {
            var chosen = rows.Where(r => r.Box.IsChecked == true).ToList();
            long total = chosen.Sum(r => r.Size);
            clean.Content = chosen.Count == 0 ? "Clean" : $"Clean {chosen.Sum(r => r.Count):N0} files · {Fmt.Bytes(total)}";
            clean.IsEnabled = chosen.Count > 0 && total > 0;
        }

        // Scan off the UI thread, one category at a time.
        _ = Task.Run(() =>
        {
            foreach (var c in StorageClean.Categories())
            {
                bool blocked = StorageClean.Blocked(c);
                var items = Cleaner.Scan(c, DateTime.Now);
                long size = items.Sum(i => i.Size);
                MainWindow.Ui(() =>
                {
                    var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(4, 6, 4, 6) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var box = new CheckBox { IsChecked = !blocked && size > 0 && !c.NeedsAdmin, IsEnabled = !blocked && size > 0 };
                    var text = new StackPanel();
                    text.Children.Add(Theme.Text(c.Title + (c.NeedsAdmin ? "  ·  needs administrator" : ""), 14, null, bold: true));
                    var detail = Theme.Text(blocked ? "Skipped: close the browser first." : c.Detail, 12, Theme.Secondary); detail.TextWrapping = TextWrapping.Wrap;
                    text.Children.Add(detail);
                    box.Content = text;
                    box.Click += (_, _) => UpdateButton();
                    row.Children.Add(box);
                    var sz = Theme.Text(size > 0 ? $"{Fmt.Bytes(size)} · {items.Count:N0}" : "nothing", 13, Theme.Secondary);
                    Grid.SetColumn(sz, 1); row.Children.Add(sz);
                    list.Children.Add(row);
                    rows.Add((box, c, size, items.Count));
                    UpdateButton();
                });
            }
            MainWindow.Ui(() => status.Text = "Ready. Tick what to remove.");
        });

        clean.Click += async (_, _) =>
        {
            var chosen = rows.Where(r => r.Box.IsChecked == true).Select(r => r.Cat).ToList();
            clean.IsEnabled = false;
            status.Text = "Cleaning…";
            var mine = await Task.Run(() => StorageClean.Clean(chosen.Where(c => !c.NeedsAdmin)));
            var admin = chosen.Where(c => c.NeedsAdmin).ToList();
            StorageClean.Outcome? elevated = admin.Count > 0 ? await StorageClean.CleanAsAdmin(admin) : new StorageClean.Outcome(0, 0, 0);
            long freed = mine.Freed + (elevated?.Freed ?? 0);
            int skipped = mine.Skipped + (elevated?.Skipped ?? 0);
            status.Text = $"Freed {Fmt.Bytes(freed)}." + (skipped > 0 ? $" {skipped:N0} file{(skipped == 1 ? " was" : "s were")} in use and left alone." : "")
                          + (admin.Count > 0 && elevated == null ? " The administrator items weren’t cleaned (Windows didn’t get permission)." : "");
            foreach (var r in rows.Where(r => r.Box.IsChecked == true)) { r.Box.IsChecked = false; r.Box.IsEnabled = false; }
            UpdateButton();
        };

        // Two steps, in place (a dialog can't open over this one): items there can't be restored afterwards.
        bool armed = false;
        empty.Click += (_, _) =>
        {
            if (!armed) { armed = true; empty.Content = $"Empty {rbItems:N0} items for good?"; return; }
            bool ok = StorageClean.EmptyRecycleBin();
            empty.Content = ok ? "Recycle Bin emptied" : "Windows didn’t empty it";
            empty.IsEnabled = false;
        };
        return root;
    }

    // ---- Analyze ------------------------------------------------------------------

    static UIElement AnalyzePane()
    {
        var root = new StackPanel { Spacing = 10, Width = 720 };
        var crumbs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var list = new StackPanel { Spacing = 2 };
        var status = Theme.Text("", 13, Theme.Secondary);
        root.Children.Add(crumbs);
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380 });
        root.Children.Add(status);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stack = new Stack<string>();
        CancellationTokenSource? scan = null;

        void Open(string folder)
        {
            scan?.Cancel();
            scan = new CancellationTokenSource();
            var token = scan.Token;
            crumbs.Children.Clear();
            var parts = new List<string>();
            for (var d = new DirectoryInfo(folder); d != null; d = d.Parent) { parts.Insert(0, d.FullName); if (d.FullName.Equals(home, StringComparison.OrdinalIgnoreCase)) break; }
            foreach (var p in parts)
            {
                var b = new HyperlinkButton { Content = p.Equals(home, StringComparison.OrdinalIgnoreCase) ? "Your files" : new DirectoryInfo(p).Name };
                b.Click += (_, _) => Open(p);
                crumbs.Children.Add(b);
                crumbs.Children.Add(Theme.Text("›", 13, Theme.Secondary));
            }
            if (crumbs.Children.Count > 0) crumbs.Children.RemoveAt(crumbs.Children.Count - 1);
            list.Children.Clear();
            status.Text = "Measuring…";
            _ = Task.Run(() =>
            {
                var entries = new List<(string Path, long Size, bool Folder)>();
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(folder))
                    {
                        if (token.IsCancellationRequested) return;
                        if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        entries.Add((d, FolderSize(d, token), true));
                    }
                    foreach (var f in Directory.EnumerateFiles(folder))
                        try { entries.Add((f, new FileInfo(f).Length, false)); } catch { }
                }
                catch { }
                if (token.IsCancellationRequested) return;
                var top = entries.OrderByDescending(e => e.Size).Take(40).ToList();
                long max = Math.Max(1, top.Select(e => e.Size).DefaultIfEmpty(1).Max());
                MainWindow.Ui(() =>
                {
                    if (token.IsCancellationRequested) return;
                    foreach (var e in top) list.Children.Add(Row(e.Path, e.Size, e.Folder, max, Open, () => Open(folder)));
                    status.Text = $"{Fmt.Bytes(entries.Sum(e => e.Size))} in {new DirectoryInfo(folder).Name}. Double-click a folder to look inside.";
                });
            });
        }
        Open(home);
        return root;
    }

    static UIElement Row(string path, long size, bool folder, long max, Action<string> open, Action refresh)
    {
        var g = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 4, 4, 4), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(Theme.Icon(folder ? "" : "", 14));
        name.Children.Add(Theme.Text(Path.GetFileName(path), 14));
        g.Children.Add(name);
        var bar = new ProgressBar { Maximum = max, Value = size, VerticalAlignment = VerticalAlignment.Center };
        var sizeText = Theme.Text(Fmt.Bytes(size), 12, Theme.Secondary);
        var mid = new StackPanel(); mid.Children.Add(sizeText); mid.Children.Add(bar);
        Grid.SetColumn(mid, 1); g.Children.Add(mid);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var show = new Button { Content = Theme.Icon("", 14) };
        ToolTipService.SetToolTip(show, "Show in File Explorer");
        show.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{path}\"");
        var bin = new Button { Content = Theme.Icon("", 14) };
        ToolTipService.SetToolTip(bin, "Move to the Recycle Bin (you can restore it from there)");
        bin.Click += (_, _) => { bin.IsEnabled = false; if (StorageClean.ToRecycleBin(path)) refresh(); else bin.IsEnabled = true; };
        actions.Children.Add(show); actions.Children.Add(bin);
        Grid.SetColumn(actions, 2); g.Children.Add(actions);
        if (folder) g.DoubleTapped += (_, _) => open(path);
        return g;
    }

    static long FolderSize(string dir, CancellationToken token)
    {
        long total = 0;
        var stack = new Stack<string>(); stack.Push(dir);
        while (stack.Count > 0 && !token.IsCancellationRequested)
        {
            var d = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(d)) try { total += new FileInfo(f).Length; } catch { }
                foreach (var s in Directory.EnumerateDirectories(d))
                    if (!new DirectoryInfo(s).Attributes.HasFlag(FileAttributes.ReparsePoint)) stack.Push(s);
            }
            catch { }
        }
        return total;
    }
}
