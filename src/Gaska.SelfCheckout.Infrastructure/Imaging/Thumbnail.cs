using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Gaska.SelfCheckout.Infrastructure.Imaging;

/// <summary>Scales a photo down to a JPEG thumbnail (product photos are often several hundred KB).</summary>
public static class Thumbnail
{
    public static byte[] ToJpeg(byte[] image, int maxSize, long quality = 82)
    {
        using var source = new Bitmap(new MemoryStream(image));
        var scale = Math.Min(1.0, maxSize / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(target))
        {
            g.Clear(Color.White); // transparent PNGs on white
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, width, height);
        }

        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        using var output = new MemoryStream();
        target.Save(output, encoder, parameters);
        return output.ToArray();
    }
}
