using System.Runtime.Versioning;
using Gaska.SelfCheckout.Application.Abstractions;

namespace Gaska.SelfCheckout.Infrastructure.Imaging;

/// <summary>
/// Stores a picture in the printer graphic memory at the full paper width. Pictures taller than one slot allows are
/// split into consecutive slots (first slot, first + 1, ...), printed one after another.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrinterImageUploader
{
    /// <param name="rotateLandscape">Rotate a landscape picture along the paper (bigger but much longer printout).</param>
    public static async Task<IReadOnlyList<MonochromeImage>> UploadAsync(
        IReceiptPrinter printer, Stream source, int firstSlot, bool rotateLandscape = false, CancellationToken ct = default)
    {
        await printer.GetStatusAsync(ct); // detects the paper width
        var parts = MonochromeImageConverter.Split(MonochromeImageConverter.Convert(source, printer.MaxImageWidth, rotateLandscape));
        for (var i = 0; i < parts.Count; i++)
            await printer.StoreImageAsync(firstSlot + i, parts[i], ct);
        return parts;
    }
}
