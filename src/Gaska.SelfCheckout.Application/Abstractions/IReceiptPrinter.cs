namespace Gaska.SelfCheckout.Application.Abstractions;

public enum PrintAlign
{
    Left,
    Center,
    Right
}

public enum PrintDocumentKind
{
    CardPayment,
    CardDeclined,
    CardReversal,
    Report,
    PickupTicket,
    Other
}

public enum PrintLineKind
{
    Text,
    /// <summary>2D QR code with <see cref="PrintLine.Text"/> as content.</summary>
    QrCode,
    /// <summary>Graphic stored in the printer memory slot <see cref="PrintLine.ImageSlot"/>.</summary>
    Image,
    Separator
}

public sealed record PrintLine(string Text, PrintAlign Align = PrintAlign.Left, bool Bold = false, bool Wide = false, bool High = false)
{
    public PrintLineKind Kind { get; init; } = PrintLineKind.Text;
    public int ImageSlot { get; init; }

    public static PrintLine Blank { get; } = new("");
    public static PrintLine Separator { get; } = new("") { Kind = PrintLineKind.Separator };
    public static PrintLine Center(string text, bool bold = false, bool wide = false) => new(text, PrintAlign.Center, bold, wide);
    public static PrintLine Qr(string content) => new(content) { Kind = PrintLineKind.QrCode };
    public static PrintLine Image(int slot) => new("") { Kind = PrintLineKind.Image, ImageSlot = slot };
}

/// <summary>Non-fiscal document; the printer chooses the header according to the document kind.</summary>
public sealed record PrintDocument(PrintDocumentKind Kind, IReadOnlyList<PrintLine> Lines)
{
    public string ToPlainText(int width) => string.Join(Environment.NewLine, Lines.Select(l => l.Kind switch
    {
        PrintLineKind.QrCode => $"[QR: {l.Text}]",
        PrintLineKind.Image => $"[IMAGE #{l.ImageSlot}]",
        PrintLineKind.Separator => new string('-', width),
        _ => l.Align switch
        {
            PrintAlign.Center => l.Text.PadLeft((width + l.Text.Length) / 2),
            PrintAlign.Right => l.Text.PadLeft(width),
            _ => l.Text
        }
    }));
}

public sealed record PrinterStatus(bool Online, bool PaperOk, string Message);

/// <summary>1-bit monochrome bitmap (black = 1, most significant bit on the left) for printer graphic slots.</summary>
public sealed record MonochromeImage(int Width, int Height, byte[] Data);

/// <summary>Non-fiscal printouts (card payment confirmations, terminal reports, pick-up tickets).</summary>
public interface IReceiptPrinter
{
    /// <summary>Line width in characters (normal font).</summary>
    int LineWidth { get; }

    /// <summary>Maximum graphic width in dots.</summary>
    int MaxImageWidth { get; }

    Task PrintAsync(PrintDocument document, CancellationToken ct = default);

    Task<PrinterStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Stores a graphic in the printer memory slot (e.g. the pick-up map).</summary>
    Task StoreImageAsync(int slot, MonochromeImage image, CancellationToken ct = default);

    /// <summary>
    /// Makes the next fiscal document (printed by the ERP) end without cutting, so that the following non-fiscal
    /// printout continues on the same strip without the printer header; false restores the normal ending.
    /// </summary>
    Task ContinueAfterNextFiscalDocumentAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
}
