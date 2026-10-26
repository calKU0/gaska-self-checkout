using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Gaska.SelfCheckout.Application.Abstractions;

namespace Gaska.SelfCheckout.Infrastructure.Imaging;

/// <summary>
/// Converts an image (PNG / JPG / BMP) into a 1-bit bitmap for thermal printer graphic slots: optional rotation of
/// landscape images (narrow paper), scaling to the printer width and conversion to black/white tuned for line drawings
/// (coloured strokes count as ink, thin lines survive the downscaling).
/// </summary>
[SupportedOSPlatform("windows")]
public static class MonochromeImageConverter
{
    /// <summary>Largest image height accepted by POSNET printers (imgslotset hx – max. 640 dots).</summary>
    public const int MaxHeight = 640;

    /// <param name="inkLevel">A pixel is ink when its darkest colour channel is below this value (coloured lines count as ink).</param>
    /// <param name="coverage">Part of a target pixel's source area that must be ink to print it black.</param>
    public static MonochromeImage Convert(Stream source, int maxWidth, bool rotateLandscape = true, int inkLevel = 170, double coverage = 0.2)
    {
        using var original = new Bitmap(source);
        if (rotateLandscape && original.Width > original.Height)
            original.RotateFlip(RotateFlipType.Rotate90FlipNone);

        var ink = InkMask(original, inkLevel);
        int sourceWidth = original.Width, sourceHeight = original.Height;

        var width = Math.Max(8, Math.Min(maxWidth, sourceWidth) & ~7); // whole bytes per row, never enlarged
        var scale = sourceWidth / (double)width;
        var height = Math.Max(1, (int)Math.Round(sourceHeight / scale));

        // area sampling: a target pixel is black when enough of its source area is ink
        var bytesPerRow = width / 8;
        var data = new byte[bytesPerRow * height];
        for (var y = 0; y < height; y++)
        {
            int y0 = (int)(y * scale), y1 = Math.Max(y0 + 1, Math.Min(sourceHeight, (int)((y + 1) * scale)));
            for (var x = 0; x < width; x++)
            {
                int x0 = (int)(x * scale), x1 = Math.Max(x0 + 1, Math.Min(sourceWidth, (int)((x + 1) * scale)));
                var count = 0;
                for (var sy = y0; sy < y1; sy++)
                for (var sx = x0; sx < x1; sx++)
                    if (ink[sy * sourceWidth + sx])
                        count++;
                if (count >= coverage * (y1 - y0) * (x1 - x0)) // black = 1, most significant bit on the left
                    data[y * bytesPerRow + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }

        return new MonochromeImage(width, height, data);
    }

    /// <summary>Splits a tall image into parts the printer accepts (one graphic slot each, printed one after another).</summary>
    public static IReadOnlyList<MonochromeImage> Split(MonochromeImage image, int maxHeight = MaxHeight)
    {
        var bytesPerRow = (image.Width + 7) / 8;
        var count = (image.Height + maxHeight - 1) / maxHeight;
        var partHeight = (image.Height + count - 1) / count; // equal parts
        var parts = new List<MonochromeImage>();
        for (var top = 0; top < image.Height; top += partHeight)
        {
            var h = Math.Min(partHeight, image.Height - top);
            parts.Add(new MonochromeImage(image.Width, h, image.Data.AsSpan(top * bytesPerRow, h * bytesPerRow).ToArray()));
        }

        return parts;
    }

    /// <summary>Renders a 1-bit image as PNG (preview of what the printer will print).</summary>
    public static void SavePreview(MonochromeImage image, string path)
    {
        using var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        var bytesPerRow = (image.Width + 7) / 8;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            bitmap.SetPixel(x, y, (image.Data[y * bytesPerRow + x / 8] & (0x80 >> (x % 8))) != 0 ? Color.Black : Color.White);
        bitmap.Save(path, ImageFormat.Png);
    }

    private static bool[] InkMask(Bitmap image, int inkLevel)
    {
        var mask = new bool[image.Width * image.Height];
        var bits = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bits.Stride];
            for (var y = 0; y < image.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, bits.Stride);
                for (var x = 0; x < image.Width; x++)
                {
                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2], a = row[x * 4 + 3];
                    // transparent = paper; the darkest channel makes yellow/orange/green strokes ink
                    mask[y * image.Width + x] = a > 128 && Math.Min(r, Math.Min(g, b)) < inkLevel;
                }
            }
        }
        finally
        {
            image.UnlockBits(bits);
        }

        return mask;
    }
}
