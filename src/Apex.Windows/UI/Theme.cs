using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Apex.UI;

/// Apex's look on Windows: Windows underneath (Mica, Segoe, Fluent icons,
/// Task Manager-style graphs), with macOS touches (large bold titles, rounded
/// cards, pill tabs, soft fills, gentle fades).
internal static class Theme
{
    public const double Radius = 10;

    // Graph colours: the resource colours people know from Task Manager, in macOS's softer tones.
    public static readonly Color Cpu = Color.FromArgb(255, 10, 132, 255);
    public static readonly Color Memory = Color.FromArgb(255, 175, 82, 222);
    public static readonly Color Disk = Color.FromArgb(255, 52, 199, 89);
    public static readonly Color Network = Color.FromArgb(255, 255, 149, 0);
    public static readonly Color Gpu = Color.FromArgb(255, 90, 200, 250);
    public static readonly Color Green = Color.FromArgb(255, 52, 199, 89);
    public static readonly Color Yellow = Color.FromArgb(255, 255, 204, 0);
    public static readonly Color Red = Color.FromArgb(255, 255, 69, 58);

    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    public static Brush Primary => Res("TextFillColorPrimaryBrush");
    public static Brush Secondary => Res("TextFillColorSecondaryBrush");
    public static Brush Tertiary => Res("TextFillColorTertiaryBrush");
    public static Brush CardFill => Res("CardBackgroundFillColorDefaultBrush");
    public static Brush CardStroke => Res("CardStrokeColorDefaultBrush");
    public static Brush Subtle => Res("SubtleFillColorSecondaryBrush");
    public static Brush Accent => Res("AccentFillColorDefaultBrush");

    public static SolidColorBrush Solid(Color c, double opacity = 1) => new(c) { Opacity = opacity };

    /// The big bold page title, like macOS.
    public static TextBlock Title(string text) => new()
    {
        Text = text, FontSize = 28, FontWeight = FontWeights.Bold, Foreground = Primary,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Text(string text, double size = 14, Brush? color = null, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = color ?? Primary, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Caption(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Secondary,
        CharacterSpacing = 40,
    };

    /// A rounded card (macOS) on Windows' card colours.
    public static Border Card(UIElement child, double padding = 16) => new()
    {
        Child = child, Background = CardFill, BorderBrush = CardStroke, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Radius), Padding = new Thickness(padding),
    };

    /// A small rounded label: "Running", "Normal", "On".
    public static Border Pill(string text, Color color)
    {
        return new Border
        {
            Background = Solid(color, 0.18), CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 3),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Solid(color) },
        };
    }

    public static FontIcon Icon(string glyph, double size = 16) => new() { Glyph = glyph, FontSize = size };

    /// A label above a value, as on Task Manager's Performance pages.
    public static StackPanel Stat(string label, out TextBlock value, double valueSize = 20)
    {
        value = new TextBlock { FontSize = valueSize, FontWeight = FontWeights.SemiBold, Foreground = Primary, Text = "—" };
        var p = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 28, 14) };
        p.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Secondary });
        p.Children.Add(value);
        return p;
    }

    public static Color PressureColor(Core.PressureLevel l) => l switch
    {
        Core.PressureLevel.Critical => Red,
        Core.PressureLevel.Elevated => Yellow,
        _ => Green,
    };

    /// Heat shading for the Processes table's numbers (Windows-style), 0…5.
    public static Brush? HeatBrush(int level, bool dark)
    {
        if (level <= 0) return null;
        double[] alpha = { 0, 0.10, 0.18, 0.28, 0.40, 0.55 };
        var c = dark ? Color.FromArgb(255, 255, 179, 64) : Color.FromArgb(255, 255, 196, 0);
        return Solid(c, alpha[Math.Clamp(level, 0, 5)]);
    }

    public static bool IsDark(FrameworkElement e) => e.ActualTheme == ElementTheme.Dark;
}
