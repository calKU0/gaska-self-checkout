using System.Text;
using System.Text.Json;
using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Application.Checkout;
using Gaska.SelfCheckout.Application.Options;
using Gaska.SelfCheckout.Application.Printing;
using Gaska.SelfCheckout.Domain.Catalog;
using Gaska.SelfCheckout.Domain.Payments;
using Gaska.SelfCheckout.Domain.Sales;
using Gaska.SelfCheckout.Infrastructure.Erp;
using Gaska.SelfCheckout.Infrastructure.Erp.Catalog;
using Gaska.SelfCheckout.Infrastructure.Imaging;
using Gaska.SelfCheckout.Infrastructure.Settings;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Web.Services;

/// <param name="Blocking">A failure stops the sale on the kiosk (see <see cref="KioskHealthMonitor"/>).</param>
public sealed record DeviceStatus(string Name, bool Ok, string Message, bool Blocking = true);

/// <summary>Service panel operations – always with exclusive access to the devices.</summary>
public sealed class ServiceOperations(
    IPaymentTerminal terminal,
    IReceiptPrinter printer,
    ISalesDocumentService erp,
    IProductCatalog catalogService,
    ICustomerDirectory customers,
    ITransactionJournal journal,
    IWarehouseLocations warehouseLocations,
    CatalogCache catalog,
    ElasticProductIndex productIndex,
    IOptionsMonitor<ErpOptions> erpOptions,
    CardSlipFormatter formatter,
    PickupTicketBuilder pickupBuilder,
    HardwareLock hardwareLock,
    IOptionsMonitor<KioskOptions> kiosk,
    IOptionsMonitor<PickupOptions> pickup,
    IWebHostEnvironment environment,
    StationSettingsStore settingsStore,
    KioskHealthMonitor health,
    ILogger<ServiceOperations> logger)
{
    private string StationId => kiosk.CurrentValue.StationId;

    public async Task<IReadOnlyList<DeviceStatus>> GetStatusAsync() =>
        await CheckAsync(TimeSpan.FromSeconds(30))
        ?? [new("Urządzenia", false, "Urządzenia są zajęte (transakcja klienta lub inicjalizacja) – spróbuj za chwilę.", Blocking: false)];

    /// <summary>
    /// Checks all connections and reports the result to the health monitor; null when the devices are busy
    /// (a customer payment is never interrupted by a check).
    /// </summary>
    public async Task<IReadOnlyList<DeviceStatus>?> CheckAsync(TimeSpan lockTimeout, CancellationToken ct = default)
    {
        var list = new List<DeviceStatus>();
        using var lease = await hardwareLock.TryAcquireAsync(lockTimeout, ct);
        if (lease is null)
            return null;

        list.Add(await Safe("Terminal płatniczy", async () =>
        {
            // silent – runs every minute from the health monitor
            var info = await terminal.CheckAsync(ct);
            return (info.Connected, $"{info.Message}. TID {info.TerminalId}");
        }));
        list.Add(await Safe("Drukarka fiskalna", async () =>
        {
            // paper running low is only a warning – the sale stops when the printer cannot print
            var s = await printer.GetStatusAsync(ct);
            return (s.Online, s.Message);
        }));
        list.Add(await Safe("Comarch ERP XL", async () =>
        {
            var s = await erp.GetStatusAsync(ct);
            return (s.Connected, s.Message);
        }));
        list.Add(await Safe("Baza danych", async () =>
        {
            var s = await erp.GetDatabaseStatusAsync(ct);
            return (s.Connected, s.Message);
        }));
        var snap = catalog.Current;
        list.Add(new DeviceStatus("Katalog towarów", snap.IsLoaded,
            snap.IsLoaded ? $"{snap.Products.Count} towarów na stanie, {snap.Categories.Count} kategorii (aktualizacja {snap.LoadedAt:HH:mm:ss})" : "Wczytywanie…",
            Blocking: false));
        list.Add(await Safe("Wyszukiwarka (Elasticsearch)", async () =>
        {
            var count = await productIndex.CountAsync(ct);
            return (count > 0, $"{count} towarów w indeksie {erpOptions.CurrentValue.Elasticsearch.Index}" + (snap.IndexedProducts > 0
                ? $", kategorie: {snap.IndexedProducts} towarów"
                : ", kategorie z XL (tylko towary na stanie)"));
        }, blocking: false));
        health.Report(list);
        return list;
    }

    public Task<string> TestTerminalAsync() => Run(async () =>
    {
        var info = await terminal.TestConnectionAsync();
        return $"{info.Message}\nTID: {info.TerminalId}\nAkceptant: {info.MerchantName}\nStatus diagnostyczny: {info.DiagnosticStatus}";
    });

    public Task<string> UploadStatusAsync() => Run(async () => Format(await terminal.GetUploadStatusAsync()));

    public Task<string> CloseDayAsync(bool print) => Run(async () =>
    {
        var report = await terminal.CloseDayAsync();
        if (report.Success && print)
            await printer.PrintAsync(new PrintDocument(PrintDocumentKind.Report,
                [PrintLine.Center(report.Title.ToUpperInvariant(), bold: true), PrintLine.Blank, .. report.Lines.Select(l => new PrintLine(l))]));
        return Format(report);
    });

    public Task<string> LastTransactionAsync(bool reprint) => Run(async () =>
    {
        var tx = await terminal.GetLastTransactionAsync();
        if (tx is null)
            return "Brak ostatniej transakcji w terminalu (po zamknięciu dnia batch jest pusty).";
        var doc = formatter.Format(tx, SlipCopy.Cardholder, printer.LineWidth, StationId);
        if (reprint)
            await printer.PrintAsync(doc);
        return $"Wynik: {tx.ResultCode} – {tx.ResultDescription}\n\n" + doc.ToPlainText(printer.LineWidth);
    });

    /// <summary>Reprints the card confirmation of a journal transaction.</summary>
    public Task<string> ReprintAsync(CheckoutRecord record, bool merchantCopy) => Run(async () =>
    {
        if (record.CardJson is null)
            return "Brak danych transakcji kartowej.";
        var tx = JsonSerializer.Deserialize<CardTransaction>(record.CardJson)!;
        var doc = formatter.Format(tx, merchantCopy ? SlipCopy.Merchant : SlipCopy.Cardholder, printer.LineWidth, StationId);
        await printer.PrintAsync(doc);
        return "Wydrukowano.\n\n" + doc.ToPlainText(printer.LineWidth);
    });

    /// <summary>Reverses the payment of a journal transaction (e.g. when no document was created).</summary>
    public Task<string> ReverseAsync(CheckoutRecord record) => Run(async () =>
    {
        // the document may exist although the journal does not know it (e.g. a restart right after the receipt)
        if (await erp.FindByTransactionIdAsync(record.Id) is { Success: true } document)
        {
            record.DocumentNumber = document.DocumentNumber;
            record.DocumentId = document.DocumentId;
            record.State = CheckoutState.DocumentCreated;
            record.UpdatedAt = DateTime.Now;
            await journal.UpdateAsync(record);
            return $"Nie unieważniono płatności – w XL istnieje dokument {document.DocumentNumber} dla tej transakcji.";
        }

        var tx = await terminal.ReverseAsync(
            new CardReversalRequest(record.Amount, record.CardReceiptNumber, (await journal.NextEcrTransactionNumberAsync()).ToString()),
            SilentTerminalInteraction.Instance);
        if (tx.IsApproved)
        {
            record.State = CheckoutState.DocumentFailedReversed;
            record.Error = (record.Error is null ? "" : record.Error + " | ") + "Reversed from the service panel.";
            record.UpdatedAt = DateTime.Now;
            await journal.UpdateAsync(record);
            await printer.PrintAsync(formatter.Format(tx with { Kind = CardTransactionKind.Reversal }, SlipCopy.Cardholder, printer.LineWidth, StationId));
            logger.LogInformation("Payment of transaction {Id} reversed from the service panel", record.Id);
        }

        return $"{tx.ResultCode} – {tx.ResultDescription}";
    });

    /// <summary>Cart of a journal transaction (as paid).</summary>
    public static IReadOnlyList<CartSnapshotLine> CartOf(CheckoutRecord record)
    {
        try
        {
            return JsonSerializer.Deserialize<List<CartSnapshotLine>>(record.CartJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The payment was approved but no document exists – the document can be issued from the panel.</summary>
    public static bool CanIssueDocument(CheckoutRecord record) =>
        record.State is CheckoutState.PaymentApproved or CheckoutState.NeedsAttention && record.DocumentNumber is null &&
        record.CardResultCode is "0000" or "0002" or "0003" && record.CardJson is not null;

    /// <summary>
    /// Issues the receipt / invoice of a paid transaction that has no document (e.g. XL or the workstation failed after
    /// the payment): the products of the paid cart, the payment from the terminal; XL prints a receipt fiscally. The
    /// transaction id makes it safe – an existing document is never issued twice.
    /// </summary>
    public Task<string> IssueDocumentAsync(CheckoutRecord record) => Run(async () =>
    {
        if (!CanIssueDocument(record))
            return "Dla tej transakcji nie można wystawić dokumentu (brak zaakceptowanej płatności lub dokument już istnieje).";

        var existing = await erp.FindByTransactionIdAsync(record.Id);
        if (existing is { Success: true })
        {
            await MarkDocument(record, existing, "Document found in XL from the service panel.");
            return $"Dokument już istnieje w XL: {existing.DocumentNumber}. Nie wystawiono drugiego.";
        }

        if (existing is { DocumentId: { } buffered })
            return $"W buforze XL jest niezatwierdzony dokument tej transakcji (GID {buffered}) – zatwierdź go lub usuń w XL i spróbuj ponownie.";

        var card = JsonSerializer.Deserialize<CardTransaction>(record.CardJson!)!;
        var paid = CartOf(record);
        if (paid.Count == 0)
            return "Brak zapisanych pozycji koszyka – wystaw dokument ręcznie w XL.";
        var total = paid.Sum(l => l.Total);
        if (total != card.Amount)
            return $"Suma pozycji {total:N2} zł różni się od zapłaconej kwoty {card.Amount:N2} zł – wystaw dokument ręcznie w XL.";

        var kind = record.DocumentKind == nameof(SaleDocumentKind.Invoice) ? SaleDocumentKind.Invoice : SaleDocumentKind.Receipt;
        Domain.Customers.Customer? customer = null;
        if (record.CustomerId is { } customerId)
            customer = await customers.GetByIdAsync(customerId)
                       ?? throw new InvalidOperationException($"Nie znaleziono kontrahenta {record.CustomerAcronym} w XL.");
        else if (record.CustomerAcronym is not null || kind == SaleDocumentKind.Invoice)
            return $"Transakcja nie zawiera numeru kontrahenta {record.CustomerAcronym} – wystaw dokument ręcznie w XL.";

        // warehouses from the current stock (the goods are still there – nothing was issued)
        var products = await catalogService.GetProductsAsync(paid.Select(l => l.ProductId));
        var lines = new List<CartSnapshotLine>();
        foreach (var line in paid)
        {
            if (!products.TryGetValue(line.ProductId, out var product) || product.AvailableQuantity < line.Quantity)
                return $"Brak wystarczającego stanu towaru {line.Code} ({line.Quantity:0.##} {line.Unit}) – wystaw dokument ręcznie w XL.";
            lines.Add(line with { Allocations = product.Allocate(line.Quantity) });
        }

        var cart = new CartSnapshot(lines);
        var result = await erp.CreateAsync(new SalesDocumentRequest(record.Id, kind, customer, cart, SalePaymentMethod.Card,
            card.Amount, CheckoutService.PaymentReference(card)));
        if (!result.Success)
        {
            record.Error = (record.Error is null ? "" : record.Error + " | ") + "Service panel: " + result.Error;
            record.UpdatedAt = DateTime.Now;
            await journal.UpdateAsync(record);
            return "Nie udało się wystawić dokumentu: " + result.Error;
        }

        await MarkDocument(record, result, "Document issued from the service panel.");
        logger.LogInformation("Document {Document} of paid transaction {Id} issued from the service panel", result.DocumentNumber, record.Id);

        var message = $"Wystawiono {(kind == SaleDocumentKind.Invoice ? "fakturę" : "paragon")} {result.DocumentNumber}" +
                      (result.Fiscal ? " (wydrukowany na drukarce fiskalnej)." : ".");
        if (cart.RequiresPickup)
        {
            try
            {
                var locations = await warehouseLocations.GetLocationsAsync(lines.Select(l => l.ProductId).ToList());
                await printer.PrintAsync(pickupBuilder.Build(result.DocumentNumber ?? record.Id.ToString()[..8], cart, locations, pickup.CurrentValue, printer.LineWidth));
                message += " Wydrukowano bilet odbioru towaru.";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pick-up ticket of {Document} could not be printed", result.DocumentNumber);
                message += " Biletu odbioru nie udało się wydrukować: " + ex.Message;
            }
        }

        return message;
    });

    private async Task MarkDocument(CheckoutRecord record, SalesDocumentResult document, string note)
    {
        record.DocumentNumber = document.DocumentNumber;
        record.DocumentId = document.DocumentId;
        record.State = CheckoutState.Completed;
        record.Error = (record.Error is null ? "" : record.Error + " | ") + note;
        record.UpdatedAt = DateTime.Now;
        await journal.UpdateAsync(record);
    }

    /// <summary>Checks in the ERP whether a document exists for the transaction (after a failure).</summary>
    public async Task<string> CheckDocumentAsync(CheckoutRecord record)
    {
        var doc = await erp.FindByTransactionIdAsync(record.Id);
        if (doc is { Success: true })
        {
            record.DocumentNumber = doc.DocumentNumber;
            record.DocumentId = doc.DocumentId;
            if (record.State is CheckoutState.PaymentApproved or CheckoutState.NeedsAttention)
                record.State = CheckoutState.DocumentCreated;
            record.UpdatedAt = DateTime.Now;
            await journal.UpdateAsync(record);
            return $"Dokument istnieje: {doc.DocumentNumber}";
        }

        return doc is null ? "Brak dokumentu w XL dla tej transakcji." : $"Dokument w stanie: {doc.Error}";
    }

    public Task<string> PrintTestAsync() => Run(async () =>
    {
        await printer.PrintAsync(new PrintDocument(PrintDocumentKind.Other,
        [
            PrintLine.Center("WYDRUK TESTOWY", bold: true),
            PrintLine.Center($"Kasa {StationId}"),
            new PrintLine("Polskie znaki: ąćęłńóśźż ĄĆĘŁŃÓŚŹŻ"),
            new PrintLine(CardSlipFormatter.Cols("Lewa kolumna", "Prawa", printer.LineWidth)),
            PrintLine.Qr("TEST QR " + StationId),
            new PrintLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}", PrintAlign.Right)
        ]));
        return $"Wydrukowano (szerokość linii {printer.LineWidth} znaków).";
    });

    /// <summary>Uploads the pick-up map into the printer graphic slot (the bundled map or an uploaded file).</summary>
    public Task<string> UploadMapAsync(Stream? image = null) => Run(async () =>
    {
        const int slot = PickupTicketBuilder.MapImageSlot;
        await using var source = image is null
            ? File.OpenRead(Path.Combine(environment.ContentRootPath, "Assets", "pickup-map.png"))
            : await CopyAsync(image);
        var parts = await PrinterImageUploader.UploadAsync(printer, source, slot);
        settingsStore.Save($"{PickupOptions.Section}:MapImageParts", parts.Count);
        var size = $"{parts[0].Width}×{parts.Sum(p => p.Height)}";
        logger.LogInformation("Pick-up map uploaded to printer slots {Slot}-{Last} ({Size})", slot, slot + parts.Count - 1, size);
        return $"Mapka zapisana w pamięci drukarki (sloty {slot}–{slot + parts.Count - 1}, {size} px).";
    });

    /// <summary>Prints a sample pick-up ticket for products from the non-primary warehouse.</summary>
    public Task<string> PrintPickupTestAsync() => Run(async () =>
    {
        var productIds = catalog.Current.Products.Keys.Take(400).ToList();
        var locations = await warehouseLocations.GetLocationsAsync(productIds);
        var sample = locations.Take(3).ToList();
        if (sample.Count == 0)
            return "Brak towarów z lokalizacjami WMS w magazynie odbioru (dodaj magazyn odbioru w konfiguracji).";
        var erp = erpOptions.CurrentValue;
        var pickupWarehouse = erp.Warehouses.FirstOrDefault(w => !w.Primary)?.Code ?? "";
        var lines = sample.Select(l =>
        {
            var entry = catalog.Current.Products[l.Key];
            return new CartSnapshotLine(entry.Id, entry.Code, entry.Name, "szt.", 1, 0, 0, [new WarehouseAllocation(pickupWarehouse, 1, false)]);
        }).ToList();
        var ticket = pickupBuilder.Build($"PA-TEST/{DateTime.Now:yy}/{erp.XlApi.ReceiptSeries}", new CartSnapshot(lines), locations, pickup.CurrentValue, printer.LineWidth);
        await printer.PrintAsync(ticket);
        return ticket.ToPlainText(printer.LineWidth);
    });

    public Task<IReadOnlyList<CheckoutRecord>> RecentAsync(int count = 50) => journal.GetRecentAsync(count);

    private static async Task<Stream> CopyAsync(Stream source)
    {
        var memory = new MemoryStream();
        await source.CopyToAsync(memory);
        memory.Position = 0;
        return memory;
    }

    private async Task<string> Run(Func<Task<string>> action)
    {
        using var lease = await hardwareLock.TryAcquireAsync(TimeSpan.FromSeconds(3));
        if (lease is null)
            return "Urządzenia są zajęte (trwa transakcja klienta). Spróbuj ponownie za chwilę.";
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Service operation failed");
            return "Błąd: " + ex.Message;
        }
    }

    private static async Task<DeviceStatus> Safe(string name, Func<Task<(bool, string)>> action, bool blocking = true)
    {
        try
        {
            var (ok, message) = await action();
            return new DeviceStatus(name, ok, message, blocking);
        }
        catch (Exception ex)
        {
            return new DeviceStatus(name, false, ex.Message, blocking);
        }
    }

    private static string Format(TerminalReport report)
    {
        var sb = new StringBuilder(report.Title).AppendLine();
        if (!report.Success)
            sb.AppendLine("BŁĄD: " + report.Error);
        foreach (var l in report.Lines)
            sb.AppendLine(l);
        return sb.ToString();
    }
}
