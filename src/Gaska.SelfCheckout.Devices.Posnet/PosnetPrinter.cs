using System.IO.Ports;
using Gaska.SelfCheckout.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Devices.Posnet;

/// <summary>
/// POSNET fiscal printer (Thermal XL2 Online) – non-fiscal printouts in the "super form" (form 200).
/// The serial port is opened only for the duration of a job because Comarch ERP XL uses the same port for receipts.
/// </summary>
public sealed class PosnetPrinter(IOptionsMonitor<PosnetOptions> options, ILogger<PosnetPrinter> logger) : IReceiptPrinter
{
    /// <summary>Maximum number of graphic bytes per imgslotappend command (1 KB of HEX text).</summary>
    private const int ImageChunkBytes = 500;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private PaperWidth? _paper;
    private PosnetCodePage? _codePage;

    private PosnetOptions Settings => options.CurrentValue;

    public int LineWidth => Settings.LineWidth > 0 ? Settings.LineWidth : _paper?.Characters ?? 40;

    public int MaxImageWidth => _paper?.Dots ?? 432;

    /// <summary>Sends a single protocol command (service / diagnostics) and returns the response fields.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ExecuteAsync(string command, (string Name, string Value)[] parameters, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var session = await OpenAsync(ct);
            return session.Execute(command, parameters).Values;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ContinueAfterNextFiscalDocumentAsync(bool enabled, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var session = await OpenAsync(ct);
            // ftrcfg fe: 3 – the next receipt/invoice ends without feed and cut, the following non-fiscal printout
            // has no header; 0 – feed and cut (default)
            session.Execute("ftrcfg", ("fe", enabled ? "3" : "0"));
            logger.LogDebug("Next fiscal document ending: {Mode}", enabled ? "continue without header" : "cut");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PrintAsync(PrintDocument document, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var session = await OpenAsync(ct);
            _paper ??= DetectPaper(session);

            var settings = Settings;
            var form = settings.FormNumber.ToString();
            var header = document.Kind switch
            {
                PrintDocumentKind.CardPayment => settings.HeaderCardPayment,
                PrintDocumentKind.CardDeclined => settings.HeaderCardDeclined,
                PrintDocumentKind.CardReversal => settings.HeaderCardReversal,
                PrintDocumentKind.Report => settings.HeaderReport,
                PrintDocumentKind.PickupTicket => settings.HeaderPickupTicket,
                _ => settings.HeaderOther
            };

            session.Execute("formstart", ("fn", form), ("fh", header.ToString()));
            try
            {
                foreach (var line in document.Lines)
                    PrintLine(session, form, line, settings);
            }
            finally
            {
                session.Execute("formend", ("fn", form));
            }

            logger.LogInformation("Non-fiscal document {Kind} printed ({Lines} lines)", document.Kind, document.Lines.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PrintLine(PosnetSession session, string form, PrintLine line, PosnetOptions settings)
    {
        switch (line.Kind)
        {
            case PrintLineKind.Separator:
                session.Execute("formcmd", ("fn", form), ("cm", "1"));
                return;

            case PrintLineKind.QrCode:
                // the code is prepared first, then printed inside the form
                session.Execute("qrcode", ("px", settings.QrModuleSize.ToString()), ("el", "1"), ("tx", PosnetProtocol.Sanitize(line.Text).Replace("&&", "&")));
                session.Execute("formbarcode2d", ("fn", form));
                return;

            case PrintLineKind.Image:
                try
                {
                    session.Execute("formimage", ("fn", form), ("no", line.ImageSlot.ToString()));
                }
                catch (PosnetException ex)
                {
                    // a missing graphic must not stop the rest of the document
                    logger.LogWarning(ex, "Graphic slot {Slot} could not be printed (is the image uploaded?)", line.ImageSlot);
                }

                return;
        }

        foreach (var text in line.Text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                session.Execute("formcmd", ("fn", form), ("cm", "0"));
                continue;
            }

            var width = line.Wide ? LineWidth / 2 : LineWidth;
            foreach (var chunk in Chunk(text, width))
                session.Execute("formformattedline", ("s1", Format(chunk, line, width)), ("fn", form));
        }
    }

    public async Task<PrinterStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _codePage = null; // a status check re-reads the printer configuration
            using var session = await OpenAsync(ct);
            _paper ??= DetectPaper(session);
            var device = session.Execute("sdev");
            var mechanism = session.Execute("sprn").Get("pr") ?? "0";
            var paperLow = device.Get("pe") is "T" or "1";
            var message = mechanism switch
            {
                "0" => paperLow ? "Kończy się papier" : "Drukarka gotowa",
                "1" or "3" => "Otwarta pokrywa drukarki",
                "5" => "Brak papieru",
                "8" => "Błąd obcinacza",
                _ => $"Błąd mechanizmu ({mechanism})"
            };
            return new PrinterStatus(mechanism == "0", mechanism != "5" && !paperLow, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PrinterStatus(false, false, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StoreImageAsync(int slot, MonochromeImage image, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var session = await OpenAsync(ct);
            session.Execute("imgslotset", ("no", slot.ToString()), ("wx", image.Width.ToString()), ("hx", image.Height.ToString()));
            for (var offset = 0; offset < image.Data.Length; offset += ImageChunkBytes)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(ImageChunkBytes, image.Data.Length - offset);
                session.Execute("imgslotappend", ("no", slot.ToString()), ("tx", Convert.ToHexString(image.Data, offset, count)));
            }

            for (var i = 0; i < 50 && session.Execute("imgslotisbusy").Get("ib") is "1" or "T"; i++)
                await Task.Delay(200, ct);

            logger.LogInformation("Graphic {Width}x{Height} stored in printer slot {Slot}", image.Width, image.Height, slot);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record PaperWidth(int Characters, int Dots);

    private PaperWidth DetectPaper(PosnetSession session)
    {
        try
        {
            // 0 – 80 mm / 56 characters, 1 – 57 mm / 40 characters, 2 – 80 mm / 40 characters
            return session.Execute("prnwidthget").Get("wd") switch
            {
                "0" => new PaperWidth(56, 576),
                "2" => new PaperWidth(40, 576),
                _ => new PaperWidth(40, 432)
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Paper width could not be read – assuming 57 mm / 40 characters");
            return new PaperWidth(40, 432);
        }
    }

    /// <summary>
    /// Reads the code page of the printer PC interface the kiosk is connected through: a virtual COM port over USB
    /// uses the printer USB interface, a physical port the COM interface (each PC interface has its own code page).
    /// </summary>
    private PosnetCodePage DetectCodePage(PosnetSession session)
    {
        try
        {
            var connection = ConnectionType(Settings.PortName);
            (string Pi, IReadOnlyDictionary<string, string> Values)? fallback = null;
            foreach (var pi in new[] { "1", "2" })
            {
                var values = session.Execute("pccfgget", ("pi", pi)).Values;
                var id = values.GetValueOrDefault("id");
                if (id == connection)
                    return Report(pi, values);
                if (id is not null and not "0")
                    fallback ??= (pi, values);
            }

            if (fallback is { } f)
                return Report(f.Pi, f.Values);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Printer code page could not be read – assuming WINDOWS-1250");
        }

        return PosnetCodePage.Windows1250;

        PosnetCodePage Report(string pi, IReadOnlyDictionary<string, string> values)
        {
            var codePage = values.GetValueOrDefault("cp") switch { "1" => PosnetCodePage.Latin2, "2" => PosnetCodePage.Mazovia, _ => PosnetCodePage.Windows1250 };
            logger.LogInformation("Printer code page: {CodePage} (interface PC{Interface}, {Type}, protocol {Protocol})", codePage, pi,
                values.GetValueOrDefault("id") switch { "1" => "COM", "2" => "USB", "4" => "TCP/IP", "7" => "Bluetooth", var x => x },
                values.GetValueOrDefault("pr") == "1" ? "Thermal" : "POSNET");
            return codePage;
        }
    }

    /// <summary>Printer interface type (pccfgget id) of a Windows serial port: 2 – USB, 7 – Bluetooth, 1 – COM.</summary>
    private static string ConnectionType(string portName)
    {
        if (!OperatingSystem.IsWindows())
            return "1";
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        var device = key?.GetValueNames().FirstOrDefault(n => string.Equals(key.GetValue(n) as string, portName, StringComparison.OrdinalIgnoreCase)) ?? "";
        return device.Contains("USB", StringComparison.OrdinalIgnoreCase) || device.Contains("VCP", StringComparison.OrdinalIgnoreCase) ? "2"
            : device.Contains("Bth", StringComparison.OrdinalIgnoreCase) ? "7"
            : "1";
    }

    /// <summary>POSNET formatting: &amp;c – centered, &amp;b – double width, &amp;h – double height, &amp;s – bold.</summary>
    private static string Format(string text, PrintLine line, int width)
    {
        var t = PosnetProtocol.Sanitize(text);
        if (line.Bold) t = $"&s{t}&s";
        if (line.Wide) t = $"&b{t}&b";
        if (line.High) t = $"&h{t}&h";
        return line.Align switch
        {
            PrintAlign.Center => "&c" + t,
            PrintAlign.Right => new string(' ', Math.Max(0, width - text.Length)) + t,
            _ => t
        };
    }

    private static IEnumerable<string> Chunk(string text, int width)
    {
        width = Math.Max(10, width);
        for (var i = 0; i < text.Length; i += width)
            yield return text.Substring(i, Math.Min(width, text.Length - i));
    }

    private async Task<PosnetSession> OpenAsync(CancellationToken ct)
    {
        var settings = Settings;
        for (var attempt = 0; ; attempt++)
        {
            var port = new SerialPort(settings.PortName, settings.BaudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = settings.ResponseTimeoutMs,
                WriteTimeout = settings.ResponseTimeoutMs,
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true
            };
            try
            {
                port.Open();
                port.DiscardInBuffer();
                var session = new PosnetSession(port, logger);
                _codePage ??= settings.CodePage == PosnetCodePage.Auto ? DetectCodePage(session) : settings.CodePage;
                session.Encoding = PosnetProtocol.GetEncoding(_codePage.Value);
                return session;
            }
            catch (UnauthorizedAccessException) when (attempt < settings.PortBusyRetries)
            {
                // port in use (e.g. XL is printing the receipt) – wait
                port.Dispose();
                logger.LogDebug("Port {Port} busy – retrying", settings.PortName);
                await Task.Delay(settings.PortBusyDelayMs, ct);
            }
            catch
            {
                port.Dispose();
                throw;
            }
        }
    }
}

internal sealed class PosnetSession(SerialPort port, ILogger logger) : IDisposable
{
    /// <summary>Text encoding matching the printer code page.</summary>
    public System.Text.Encoding Encoding { get; set; } = PosnetProtocol.Encoding;

    public PosnetResponse Execute(string command, params (string Name, string Value)[] parameters)
    {
        var frame = PosnetProtocol.BuildFrame(command, parameters, Encoding);
        logger.LogDebug("POSNET -> {Frame}", Encoding.GetString(frame, 1, Math.Min(frame.Length - 2, 300)));
        port.Write(frame, 0, frame.Length);

        var buffer = new List<byte>(64);
        var started = false;
        while (true)
        {
            int b;
            try
            {
                b = port.ReadByte();
            }
            catch (TimeoutException)
            {
                throw new PosnetException(-2, $"The printer does not respond (command {command}).");
            }

            if (b == PosnetProtocol.Stx) { started = true; buffer.Clear(); continue; }
            if (!started) continue;
            if (b == PosnetProtocol.Etx) break;
            buffer.Add((byte)b);
        }

        var response = PosnetProtocol.ParseResponse(buffer.ToArray(), Encoding);
        logger.LogDebug("POSNET <- {Response}", Encoding.GetString(buffer.ToArray()));
        if (response.IsFrameError || response.Error is not null)
            throw new PosnetException(response.Error ?? -1, $"The printer returned error {response.Error} for command {command}.");
        return response;
    }

    public void Dispose()
    {
        try { port.Close(); } catch { /* ignore */ }
        port.Dispose();
    }
}
