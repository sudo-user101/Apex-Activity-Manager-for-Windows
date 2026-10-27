using Apex.Core;
using Apex.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Apex.UI;

/// Performance › Memory › Memory compression: whether it's on, what it holds,
/// what it does, and a switch (Windows' own `Enable-MMAgent` /
/// `Disable-MMAgent -MemoryCompression`, as administrator; it applies after a
/// restart, so Apex remembers the change until then).
internal static class MemoryCompression
{
    /// The change waiting for a restart: "on" / "off", with the boot it was made in.
    static string? Pending()
    {
        var p = Settings.Get<string?>("compressionPending", null);
        if (p == null) return null;
        var parts = p.Split('@');
        // A restart since: the change has applied.
        if (parts.Length != 2 || !long.TryParse(parts[1], out var boot) || boot != BootTime()) { Settings.Set<string?>("compressionPending", null); return null; }
        return parts[0];
    }

    static long BootTime() => (DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64)).ToUnixTimeSeconds() / 60;

    public static async Task Show(FrameworkElement anchor, MemoryStats m)
    {
        var root = new StackPanel { Spacing = 12, Width = 520 };
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        state.Children.Add(Theme.Pill(m.CompressionOn ? "On" : "Off", m.CompressionOn ? Theme.Green : Color(150, 150, 160)));
        state.Children.Add(Theme.Text(MemoryText.Compression(m), 15));
        root.Children.Add(state);
        root.Children.Add(Theme.Caption(MemoryText.CompressionHelp(m)));

        var facts = new StackPanel { Spacing = 4 };
        facts.Children.Add(Theme.Text($"Page file in use: {MemoryText.PageFile(m)}", 13, Theme.Secondary));
        facts.Children.Add(Theme.Text($"Read back from disk: {MemoryText.ReadsFromDisk(m)}", 13, Theme.Secondary));
        facts.Children.Add(Theme.Text($"Memory pressure: {MemoryPressure.Title(m.Pressure)}", 13, Theme.Secondary));
        root.Children.Add(Theme.Card(facts, 12));

        var status = Theme.Text("", 13, Theme.Secondary);
        status.TextWrapping = TextWrapping.Wrap;
        var pending = Pending();
        if (pending != null) status.Text = $"Turned {pending} — restart Windows to finish.";
        bool want = !m.CompressionOn;
        var toggle = new Button { Content = want ? "Turn On…" : "Turn Off…" };
        toggle.IsEnabled = pending == null;
        ToolTipService.SetToolTip(toggle, "Windows asks for administrator permission. Takes effect after a restart.");
        toggle.Click += async (_, _) =>
        {
            toggle.IsEnabled = false;
            status.Text = "Waiting for permission…";
            bool ok = await Task.Run(() => Native.RunElevated("powershell.exe", "-NoProfile -NonInteractive -Command " + MemoryText.ToggleCommand(want)));
            if (ok)
            {
                Settings.Set<string?>("compressionPending", (want ? "on" : "off") + "@" + BootTime());
                status.Text = $"Turned {(want ? "on" : "off")} — restart Windows to finish.";
            }
            else
            {
                status.Text = "Not changed (permission was declined, or Windows refused).";
                toggle.IsEnabled = true;
            }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(toggle);
        row.Children.Add(status);
        root.Children.Add(row);

        var d = new ContentDialog { Title = "Memory Compression", Content = root, CloseButtonText = "Done", XamlRoot = anchor.XamlRoot };
        try { await d.ShowAsync(); } catch { }
    }

    static Windows.UI.Color Color(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);
}
