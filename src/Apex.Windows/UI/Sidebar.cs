using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Apex.UI;

/// Task Manager's left navigation, styled like a macOS sidebar: translucent,
/// rounded selection pills, folds down to icons with ☰.
internal sealed class Sidebar : Grid
{
    readonly StackPanel top = new() { Spacing = 2 };
    readonly StackPanel bottom = new() { Spacing = 2 };
    readonly List<(Button Button, TextBlock Label, int Index)> items = new();
    public int Selected { get; private set; } = -1;
    public bool Compact { get; private set; }
    public event Action<int>? SelectionChanged;
    public event Action<FrameworkElement>? SettingsClicked;
    public const double Wide = 220, Narrow = 60;

    public Sidebar(IEnumerable<(string Label, string Glyph)> pages)
    {
        Width = Wide;
        Padding = new Thickness(8, 4, 8, 10);
        Background = Theme.Res("LayerOnMicaBaseAltFillColorTransparentBrush");
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var menu = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 15 }, Width = 40, Height = 36, Margin = new Thickness(2, 0, 0, 8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
        };
        ToolTipService.SetToolTip(menu, "Show or hide the names");
        menu.Click += (_, _) => SetCompact(!Compact);
        grid.Children.Add(menu);

        int i = 0;
        foreach (var (label, glyph) in pages) top.Children.Add(Item(label, glyph, i++));
        Grid.SetRow(top, 1); grid.Children.Add(top);

        var settings = Item("Settings", "", -1);
        bottom.Children.Add(settings);
        Grid.SetRow(bottom, 2); grid.Children.Add(bottom);
        Children.Add(grid);
    }

    Button Item(string label, string glyph, int index)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
        row.Children.Add(text);
        var b = new Button
        {
            Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        ToolTipService.SetToolTip(b, label);
        if (index >= 0) { b.Click += (_, _) => Select(index); items.Add((b, text, index)); }
        else b.Click += (_, _) => SettingsClicked?.Invoke(b);
        return b;
    }

    public void Select(int index, bool notify = true)
    {
        Selected = index;
        foreach (var (b, label, i) in items)
        {
            bool on = i == index;
            // A soft accent pill, like a macOS sidebar selection.
            b.Background = on ? Theme.Solid(((SolidColorBrush)Theme.Accent).Color, 0.16) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            b.Foreground = on ? Theme.Accent : Theme.Primary;
            label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (notify) SelectionChanged?.Invoke(index);
    }

    public void SetCompact(bool compact)
    {
        Compact = compact;
        Width = compact ? Narrow : Wide;
        foreach (var (_, label, _) in items) label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var b in bottom.Children.OfType<Button>())
            if (b.Content is StackPanel p && p.Children.Count > 1) p.Children[1].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
}
