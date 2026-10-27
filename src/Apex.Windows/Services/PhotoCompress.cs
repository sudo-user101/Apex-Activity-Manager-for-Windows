using System.Runtime.InteropServices.WindowsRuntime;
using Apex.Core;
using Windows.Graphics.Imaging;

namespace Apex.Services;

/// Compress in place, the Windows part: each photo re-saved as a JPEG with
/// Windows' own encoder (WIC), next to the original under a hidden name.
/// Photos with see-through parts are left as they are (JPEG has no
/// transparency), and so is any copy that isn't smaller.
internal static class PhotoCompress
{
    public const double Quality = 0.82;

    public sealed record Result(List<InPlaceCompress.Copy> Copies, List<(string Name, string Why)> Skipped);

    public static async Task<Result> MakeCopies(IReadOnlyList<InPlaceCompress.Photo> photos, Action<int, int> progress, CancellationToken token)
    {
        var copies = new List<InPlaceCompress.Copy>();
        var skipped = new List<(string, string)>();
        for (int i = 0; i < photos.Count && !token.IsCancellationRequested; i++)
        {
            progress(i, photos.Count);
            var p = photos[i];
            var tmp = InPlaceCompress.TempPath(p.Path);
            try
            {
                var why = await Encode(p.Path, tmp);
                long after = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
                if (why != null || after <= 0 || after >= p.Size)
                {
                    try { File.Delete(tmp); } catch { }
                    skipped.Add((Path.GetFileName(p.Path), why ?? "already as small as it gets"));
                    continue;
                }
                copies.Add(new InPlaceCompress.Copy(p.Path, tmp, InPlaceCompress.FinalPath(p.Path), p.Size, after));
            }
            catch (Exception e)
            {
                try { File.Delete(tmp); } catch { }
                skipped.Add((Path.GetFileName(p.Path), e.Message));
            }
        }
        progress(photos.Count, photos.Count);
        return new Result(copies, skipped);
    }

    /// Writes `dst` as a JPEG of `src`; returns why not, or null when written.
    public static async Task<string?> Encode(string src, string dst)
    {
        using var input = File.OpenRead(src).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        if (HasTransparency(bitmap)) return "has see-through parts (a JPEG can't keep them)";
        using var opaque = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        using (var output = File.Create(dst).AsRandomAccessStream())
        {
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(Quality, Windows.Foundation.PropertyType.Single) };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
            encoder.SetSoftwareBitmap(opaque);
            await encoder.FlushAsync();
        }
        return null;
    }

    static bool HasTransparency(SoftwareBitmap b)
    {
        var buffer = new Windows.Storage.Streams.Buffer((uint)(b.PixelWidth * b.PixelHeight * 4));
        b.CopyToBuffer(buffer);
        var bytes = buffer.ToArray();
        for (int i = 3; i < bytes.Length; i += 4) if (bytes[i] != 255) return true;
        return false;
    }

    /// For the self-test: a PNG of `w`×`h` like a screenshot (flat colours, some text-like detail), optionally see-through.
    public static async Task WriteTestPng(string path, int w, int h, bool seeThrough)
    {
        var px = new byte[w * h * 4];
        uint seed = 12345;
        // Light noise, like the anti-aliasing and photos in real screenshots (a flat test image would stay smaller as a PNG).
        byte Noisy(int v) { seed = seed * 1664525 + 1013904223; return (byte)Math.Clamp(v + (int)(seed >> 28) - 8, 0, 255); }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool line = (y % 24 < 2) || ((x / 7 + y / 13) % 11 == 0 && y % 24 > 6 && y % 24 < 18);
                px[i] = Noisy(line ? 30 : 240 - x * 40 / w); px[i + 1] = Noisy(line ? 30 : 236 - y * 30 / h); px[i + 2] = Noisy(line ? 30 : 230);
                px[i + 3] = (byte)(seeThrough && x < w / 4 ? 0 : 255);
            }
        using var output = File.Create(path).AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)w, (uint)h, 96, 96, px);
        await encoder.FlushAsync();
    }
}
