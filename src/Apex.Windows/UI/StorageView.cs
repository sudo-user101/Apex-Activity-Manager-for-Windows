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
    public static async Task Show(FrameworkElement anchor, bool analyze = false)
    {
        var tabs = new Pivot();
        tabs.Items.Add(new PivotItem { Header = "Clean", Content = CleanPane() });
        tabs.Items.Add(new PivotItem { Header = "Analyze", Content = AnalyzePane() });
        if (analyze) tabs.SelectedIndex = 1;
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

    /// Walks kept for this session, by place: switching back is instant.
    static readonly Dictionary<string, (DiskTree Tree, DateTime At)> scans = new(StringComparer.OrdinalIgnoreCase);

    static List<(string Label, string Path)> Places()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<(string, string)> { ("Your files", home) };
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown) continue;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "USB drive" : "Local Disk") : d.VolumeLabel;
                list.Add(($"{d.Name.TrimEnd('\\')} {label} · {Fmt.Bytes(d.AvailableFreeSpace)} free of {Fmt.Bytes(d.TotalSize)}", d.RootDirectory.FullName));
            }
            catch { }
        }
        return list;
    }

    static UIElement AnalyzePane()
    {
        var root = new StackPanel { Spacing = 10, Width = 720 };
        var places = Places();
        var where = new ComboBox { MinWidth = 340 };
        foreach (var p in places) where.Items.Add(p.Label);
        var scanButton = new Button { Content = "Scan" };
        var progress = new ProgressRing { IsActive = false, Width = 18, Height = 18 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(where); top.Children.Add(scanButton); top.Children.Add(progress);
        root.Children.Add(top);

        var foldersView = new RadioButton { Content = "Folders", GroupName = "analyzeView", IsChecked = true };
        var largestView = new RadioButton { Content = "Largest files", GroupName = "analyzeView" };
        var views = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        views.Children.Add(foldersView); views.Children.Add(largestView);
        root.Children.Add(views);

        var crumbs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var list = new StackPanel { Spacing = 2 };
        var status = Theme.Text("", 13, Theme.Secondary);
        status.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(crumbs);
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360 });
        root.Children.Add(status);

        CancellationTokenSource? scan = null;
        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DiskTree? tree = null;
        string? showing = null;

        void Show(string folder)
        {
            if (tree == null) return;
            showing = folder;
            list.Children.Clear(); crumbs.Children.Clear();
            if (largestView.IsChecked == true)
            {
                var files = tree.Largest();
                long max = Math.Max(1, files.Select(f => f.Size).DefaultIfEmpty(1).Max());
                foreach (var f in files.Take(60)) list.Children.Add(Row(f.Path, f.Size, f.OnlineOnly ? f.Size : 0, false, max, Show, Removed, true));
                status.Text = Summary(tree) + " The biggest files anywhere in it.";
                return;
            }
            var node = tree.Find(folder) ?? tree.Top;
            var trail = new List<DiskTree.Folder>();
            for (var p = node.Path; ; )
            {
                if (tree.Find(p) is { } n) trail.Insert(0, n);
                if (string.Equals(p, tree.Root, StringComparison.OrdinalIgnoreCase)) break;
                var up = Path.GetDirectoryName(p.TrimEnd('\\'));
                if (up == null) break;
                p = up;
            }
            foreach (var n in trail)
            {
                var b = new HyperlinkButton { Content = n == tree.Top ? Path.GetFileName(tree.Root.TrimEnd('\\')) is { Length: > 0 } r ? r : tree.Root : n.Name };
                var target = n.Path;
                b.Click += (_, _) => Show(target);
                crumbs.Children.Add(b);
                crumbs.Children.Add(Theme.Text("›", 13, Theme.Secondary));
            }
            if (crumbs.Children.Count > 0) crumbs.Children.RemoveAt(crumbs.Children.Count - 1);
            var items = DiskTree.Contents(node).Take(80).ToList();
            long biggest = Math.Max(1, items.Select(i => i.Size).DefaultIfEmpty(1).Max());
            foreach (var i in items) list.Children.Add(Row(i.Path, i.Size, i.OnlineOnly, i.IsFolder, biggest, Show, Removed, false));
            // Folders still being measured (the drive's top level, while a scan runs).
            if (node == tree.Top) foreach (var d in pending.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) list.Children.Add(PendingRow(d));
            status.Text = (pending.Count > 0 ? $"Measuring {pending.Count} more folder{(pending.Count == 1 ? "" : "s")}… " : "")
                + $"{Fmt.Bytes(node.Size)} in {node.Name}" + (node.OnlineOnly > 0 ? $" ({Fmt.Bytes(node.OnlineOnly)} online-only, not on this PC)" : "")
                + ". Double-click a folder to look inside. " + Summary(tree);
        }

        void Removed(string path)
        {
            tree?.Remove(path);
            if (showing != null) Show(showing);
        }

        string Summary(DiskTree t)
        {
            var at = scans.TryGetValue(t.Root, out var e) ? e.At : DateTime.Now;
            var mins = (int)(DateTime.Now - at).TotalMinutes;
            return $"{t.FileCount:N0} files, measured {(mins < 1 ? "just now" : $"{mins} min ago")}"
                + (t.Unreadable > 0 ? $"; {t.Unreadable:N0} folders Windows didn't let Apex read (other users' or the system's) aren't counted" : "")
                + (t.Stopped ? "; stopped before the end, so totals are partial" : "") + "." + HardLinkNote(t);
        }

        // Windows keeps many of its own files in several folders at once (hard links,
        // mostly in WinSxS); a walk counts each place, as File Explorer does.
        static string HardLinkNote(DiskTree t)
        {
            try
            {
                if (!string.Equals(Path.GetPathRoot(t.Root), t.Root, StringComparison.OrdinalIgnoreCase)) return "";
                var d = new DriveInfo(t.Root);
                long used = d.TotalSize - d.TotalFreeSpace;
                return t.Top.Size - t.Top.OnlineOnly > used * 1.02
                    ? $" The drive has {Fmt.Bytes(used)} in use: Windows keeps some of its files in several folders at once (hard links), and each place is counted, so the total here is higher."
                    : "";
            }
            catch { return ""; }
        }

        // Shown as it's measured, like the Mac: the top level at once, each of its
        // folders (three at a time) filled in as soon as it's done, biggest first.
        void Start(bool fresh)
        {
            if (where.SelectedIndex < 0) return;
            var path = places[where.SelectedIndex].Path;
            scan?.Cancel();
            if (!fresh && scans.TryGetValue(path, out var cached)) { tree = cached.Tree; pending.Clear(); Show(path); scanButton.Content = "Scan Again"; return; }
            var cts = scan = new CancellationTokenSource();
            var t = tree = new DiskTree(path);
            pending.Clear();
            progress.IsActive = true;
            scanButton.Content = "Stop";
            Show(path);
            status.Text = "Measuring… nothing is downloaded or changed.";
            DateTime lastShown = DateTime.MinValue;
            void ShowSoon() { if (DateTime.UtcNow - lastShown > TimeSpan.FromMilliseconds(250) && showing != null) { lastShown = DateTime.UtcNow; Show(showing); } }
            _ = Task.Run(async () =>
            {
                var dirs = new List<string>();
                var files = new List<DiskTree.FileEntry>();
                try
                {
                    foreach (var fi in new DirectoryInfo(path).EnumerateFileSystemInfos())
                    {
                        if (fi is DirectoryInfo di) { if (di.LinkTarget == null) dirs.Add(di.FullName); }
                        else if (fi is FileInfo f) files.Add(new DiskTree.FileEntry(f.FullName, f.Length, DiskTree.IsOnlineOnly((int)f.Attributes)));
                    }
                }
                catch { }
                MainWindow.Ui(() =>
                {
                    if (scan != cts) return;
                    foreach (var f in files) t.AddRootFile(f);
                    foreach (var d in dirs) pending.Add(d);
                    Show(path);
                });
                using var gate = new SemaphoreSlim(3);
                await Task.WhenAll(dirs.Select(async d =>
                {
                    try { await gate.WaitAsync(cts.Token); } catch (OperationCanceledException) { return; }
                    try
                    {
                        var sub = DiskTree.Scan(d, cts.Token);
                        MainWindow.Ui(() => { if (scan != cts) return; t.Graft(sub); pending.Remove(d); ShowSoon(); });
                    }
                    finally { gate.Release(); }
                }));
                MainWindow.Ui(() =>
                {
                    if (scan != cts) return;      // another place was chosen meanwhile: this walk is dropped
                    pending.Clear();
                    if (!cts.IsCancellationRequested) scans[path] = (t, DateTime.Now);     // a stopped scan isn't kept as if complete
                    progress.IsActive = false;
                    scanButton.Content = "Scan Again";
                    scan = null;
                    if (showing != null) Show(showing);
                });
            });
        }

        scanButton.Click += (_, _) =>
        {
            if (scan != null) { scan.Cancel(); return; }      // Stop: keeps what was measured
            Start(true);
        };
        where.SelectionChanged += (_, _) => Start(false);
        foldersView.Checked += (_, _) => { if (tree != null) Show(showing ?? tree.Root); };
        largestView.Checked += (_, _) => { if (tree != null) Show(showing ?? tree.Root); };
        where.SelectedIndex = 0;
        return root;
    }

    /// A folder at the top level that's still being measured.
    static UIElement PendingRow(string path)
    {
        var g = new Grid { ColumnSpacing = 10, Padding = new Thickness(4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(Theme.Icon("\uE8B7", 14));
        name.Children.Add(Theme.Text(Path.GetFileName(path.TrimEnd('\\')), 14, Theme.Secondary));
        g.Children.Add(name);
        var m = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        m.Children.Add(new ProgressRing { IsActive = true, Width = 12, Height = 12 });
        m.Children.Add(Theme.Text("Measuring…", 12, Theme.Secondary));
        Grid.SetColumn(m, 1); g.Children.Add(m);
        return g;
    }

    static UIElement Row(string path, long size, long onlineOnly, bool folder, long max, Action<string> open, Action<string> removed, bool showFolder)
    {
        var g = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 4, 4, 4), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(Theme.Icon(folder ? "" : "", 14));
        var title = new StackPanel();
        title.Children.Add(Theme.Text(Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path, 14));
        if (showFolder) title.Children.Add(Theme.Text(Path.GetDirectoryName(path) ?? "", 11, Theme.Tertiary));
        name.Children.Add(title);
        if (onlineOnly > 0)
        {
            var cloud = Theme.Icon("", 12);
            ToolTipService.SetToolTip(cloud, onlineOnly >= size ? "Online-only: in the cloud, not stored on this PC" : $"{Fmt.Bytes(onlineOnly)} of it is online-only (not stored on this PC)");
            name.Children.Add(cloud);
        }
        g.Children.Add(name);
        var bar = new ProgressBar { Maximum = max, Value = size, VerticalAlignment = VerticalAlignment.Center };
        var sizeText = Theme.Text(Fmt.Bytes(size) + (onlineOnly > 0 && onlineOnly < size ? $" · {Fmt.Bytes(size - onlineOnly)} on this PC" : ""), 12, Theme.Secondary);
        var mid = new StackPanel(); mid.Children.Add(sizeText); mid.Children.Add(bar);
        Grid.SetColumn(mid, 1); g.Children.Add(mid);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (folder)
        {
            var compress = new Button { Content = Theme.Icon("", 14) };
            ToolTipService.SetToolTip(compress, "Compress in place: screenshots and scans in this folder made smaller, keeping their names");
            compress.Click += (_, _) => _ = CompressInPlace(g, path);
            actions.Children.Add(compress);
        }
        var show = new Button { Content = Theme.Icon("", 14) };
        ToolTipService.SetToolTip(show, "Show in File Explorer");
        show.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{path}\"");
        var bin = new Button { Content = Theme.Icon("", 14) };
        ToolTipService.SetToolTip(bin, "Move to the Recycle Bin (you can restore it from there)");
        bin.Click += (_, _) => { bin.IsEnabled = false; if (StorageClean.ToRecycleBin(path)) removed(path); else bin.IsEnabled = true; };
        actions.Children.Add(show); actions.Children.Add(bin);
        Grid.SetColumn(actions, 2); g.Children.Add(actions);
        if (folder) g.DoubleTapped += (_, _) => open(path);
        return g;
    }

    // ---- Compress in place ----------------------------------------------------------

    /// Like the Mac: count → smaller copies next to each photo → Done swaps
    /// them in (originals to the Recycle Bin), Cancel throws the copies away.
    public static async Task CompressInPlace(FrameworkElement anchor, string folder)
    {
        var body = new StackPanel { Spacing = 10, Width = 520 };
        var text = Theme.Text("Counting photos…", 14);
        text.TextWrapping = TextWrapping.Wrap;
        var bar = new ProgressBar { Visibility = Visibility.Collapsed };
        var note = Theme.Caption("Screenshots and scans (PNG, BMP, TIFF) become JPEGs at high quality, keeping their names. "
            + "JPEG photos are left as they are: re-saving one loses quality and its details (date taken, place). "
            + "Online-only files are left too (they'd have to download first).");
        body.Children.Add(text); body.Children.Add(bar); body.Children.Add(note);
        var d = new ContentDialog { Title = "Compress in Place", Content = body, PrimaryButtonText = "Make Smaller Copies", CloseButtonText = "Cancel",
                                    IsPrimaryButtonEnabled = false, XamlRoot = anchor.XamlRoot };
        var cts = new CancellationTokenSource();
        List<InPlaceCompress.Photo> photos = new();
        List<InPlaceCompress.Copy> copies = new();
        bool made = false, done = false;

        var (found, online) = await Task.Run(() => InPlaceCompress.Plan(folder));
        int jpegs = await Task.Run(() => { try { return Directory.EnumerateFiles(folder).Count(InPlaceCompress.IsJpeg); } catch { return 0; } });
        photos = found;
        text.Text = photos.Count == 0
            ? "No screenshots or scans to compress here." + (jpegs > 0 ? $" ({jpegs} JPEG photos are already compressed.)" : "")
            : $"{photos.Count} to compress ({Fmt.Bytes(photos.Sum(p => p.Size))})." + (online > 0 ? $" {online} online-only left out." : "")
              + (jpegs > 0 ? $" {jpegs} JPEGs left as they are." : "");
        d.IsPrimaryButtonEnabled = photos.Count > 0;

        d.PrimaryButtonClick += async (_, args) =>
        {
            if (done) return;
            args.Cancel = true;      // stay open: copies first, then Done
            var deferral = args.GetDeferral();
            if (!made)
            {
                d.IsPrimaryButtonEnabled = false;
                bar.Visibility = Visibility.Visible;
                var r = await PhotoCompress.MakeCopies(photos, (i, n) => MainWindow.Ui(() => { bar.Maximum = n; bar.Value = i; text.Text = $"Making smaller copies… {i} of {n}"; }), cts.Token);
                copies = r.Copies;
                made = true;
                bar.Visibility = Visibility.Collapsed;
                long before = copies.Sum(c => c.Before), after = copies.Sum(c => c.After);
                text.Text = copies.Count == 0
                    ? "None of them could be made smaller." + (r.Skipped.Count > 0 ? $" ({r.Skipped[0].Why})" : "")
                    : $"Saves {Fmt.Bytes(before - after)}: {copies.Count} photos go from {Fmt.Bytes(before)} to {Fmt.Bytes(after)}."
                      + (r.Skipped.Count > 0 ? $" {r.Skipped.Count} left as they are (for example “{r.Skipped[0].Name}”: {r.Skipped[0].Why})." : "")
                      + " Done puts them in place and moves the originals to the Recycle Bin.";
                d.PrimaryButtonText = "Done";
                d.IsPrimaryButtonEnabled = copies.Count > 0;
            }
            else
            {
                d.IsPrimaryButtonEnabled = false;
                var failed = await Task.Run(() => InPlaceCompress.SwapIn(copies, StorageClean.ToRecycleBin));
                InPlaceCompress.Discard(copies.Where(c => failed.Contains(Path.GetFileName(c.Original))));
                done = true;
                text.Text = failed.Count == 0 ? $"Done: {copies.Count} photos replaced; the originals are in the Recycle Bin."
                                              : $"{copies.Count - failed.Count} replaced; {failed.Count} left as they were (for example “{failed[0]}”).";
                d.PrimaryButtonText = ""; d.CloseButtonText = "Close";
            }
            deferral.Complete();
        };
        try { await d.ShowAsync(); } catch { }
        cts.Cancel();
        if (!done) InPlaceCompress.Discard(copies);
    }
}
