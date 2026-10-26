using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Apex.UI;

/// App icons from their executables, loaded once and shared.
internal static class Icons
{
    static readonly Dictionary<string, BitmapImage?> cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task Load(string path, Image target)
    {
        if (path.Length == 0) { target.Source = null; return; }
        if (cache.TryGetValue(path, out var known)) { target.Source = known; return; }
        cache[path] = null;
        try
        {
            var bytes = await Task.Run(() =>
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            });
            if (bytes == null) return;
            var img = new BitmapImage();
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            await img.SetSourceAsync(stream);
            cache[path] = img;
            target.Source = img;
        }
        catch { }
    }
}
