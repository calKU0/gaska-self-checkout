using System.Globalization;
using cdn_api;
using Dapper;
using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Domain.Sales;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Gaska.SelfCheckout.Infrastructure.Erp.XlApi;

/// <summary>
/// Creates receipts (PA) and invoices (FS) in Comarch ERP XL through XL API. Confirming a receipt with Fiskalny=1
/// makes XL print the fiscal receipt itself on the configured fiscal printer.
/// </summary>
public sealed class XlSalesDocumentService(XlSession xl, ILogger<XlSalesDocumentService> logger) : ISalesDocumentService
{
    public const int ReceiptType = 2034;  // PA
    public const int InvoiceType = 2033;  // FS
    private const int ProductType = 16;   // product card GIDTyp
    private const int CustomerType = 32;  // customer card GIDTyp

    /// <summary>Document type, series and fiscal printing chosen for a request.</summary>
    private sealed record DocumentProfile(bool Invoice, string Series, bool Fiscal);

    private XlSession Session => xl;
    private ILogger Logger => logger;

    public Task<SalesDocumentResult> CreateAsync(SalesDocumentRequest request, CancellationToken ct = default) =>
        xl.RunAsync(_ =>
        {
            var prepared = PrepareWithRelogin(request, out var failure);
            return prepared is null ? failure! : Commit(prepared.Value, request.TransactionId, Profile(request), request.PaidAmount, request.PaymentReference);
        });

    public async Task<IPreparedDocument> PrepareAsync(SalesDocumentRequest request, CancellationToken ct = default)
    {
        var (documentId, failure) = await xl.RunAsync(_ =>
        {
            var id = PrepareWithRelogin(request, out var error);
            return (id, error);
        });
        if (documentId is null)
            throw new InvalidOperationException(failure?.Error ?? "The document could not be prepared.");
        return new PreparedDocument(this, documentId.Value, request.TransactionId, Profile(request));
    }

    public async Task<ErpStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var session = await xl.RunAsync(id => id);
            return new ErpStatus(true, $"Zalogowano do XL (sesja {session}, operator {xl.Settings.Operator}, baza {xl.Settings.Database})");
        }
        catch (Exception ex)
        {
            return new ErpStatus(false, ex.Message);
        }
    }

    public async Task<ErpStatus> GetDatabaseStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(xl.Erp.ConnectionString) { ConnectTimeout = 5 };
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync(ct);
            await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", commandTimeout: 5, cancellationToken: ct));
            return new ErpStatus(true, $"Połączono z bazą {conn.Database} ({conn.DataSource})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ErpStatus(false, ex.Message);
        }
    }

    /// <summary>Document of a transaction – a confirmed one first (state 1-2 = buffer, confirmed ≥ 3).</summary>
    private const string FindByGuidSql =
        "SELECT TOP 1 TrN_GIDTyp, TrN_GIDNumer, TrN_Stan FROM cdn.TraNag WHERE TrN_GUID = @guid " +
        "ORDER BY CASE WHEN TrN_Stan >= 3 THEN 0 ELSE 1 END, TrN_GIDNumer DESC";

    public async Task<SalesDocumentResult?> FindByTransactionIdAsync(Guid transactionId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(xl.Erp.ConnectionString);
        var row = await conn.QueryFirstOrDefaultAsync<(int Type, int Number, int State)>(new CommandDefinition(
            FindByGuidSql, new { guid = transactionId.ToString() }, cancellationToken: ct));
        if (row.Number == 0)
            return null;

        // state 1-2 = buffer; confirmed ≥ 3
        if (row.State < 3)
            return new SalesDocumentResult(false, null, row.Number, "Document still in the buffer", row.Type);

        return new SalesDocumentResult(true, await DocumentNumberSqlAsync(conn, row.Type, row.Number, ct), row.Number, null, row.Type);
    }

    internal static async Task<string?> DocumentNumberSqlAsync(SqlConnection conn, int type, int number, CancellationToken ct)
    {
        try
        {
            return await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT cdn.NazwaObiektu(@type, @number, 0, 2)", new { type, number }, cancellationToken: ct));
        }
        catch (SqlException)
        {
            return await conn.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT CONCAT(CASE TrN_GIDTyp WHEN 2033 THEN 'FS-' ELSE 'PA-' END, TrN_TrNNumer, '/', RIGHT(TrN_TrNRok, 2), '/', TrN_TrNSeria) FROM cdn.TraNag WHERE TrN_GIDTyp=@type AND TrN_GIDNumer=@number",
                new { type, number }, cancellationToken: ct));
        }
    }

    /// <summary>
    /// Receipt: receipt series, fiscal according to the settings. Invoice (businesses with a tax id only): the invoice
    /// series (not fiscal – sent to KSeF).
    /// </summary>
    private DocumentProfile Profile(SalesDocumentRequest request)
    {
        var s = xl.Settings;
        if (request.Kind != SaleDocumentKind.Invoice)
            return new(false, s.ReceiptSeries, s.PrintFiscal);
        return new(true, s.InvoiceSeries, Fiscal: false);
    }

    private sealed class PreparedDocument(XlSalesDocumentService owner, int documentId, Guid transactionId, DocumentProfile profile) : IPreparedDocument
    {
        private int _finished;

        public Task<SalesDocumentResult> CommitAsync(decimal paidAmount, string? paymentReference) =>
            Interlocked.Exchange(ref _finished, 1) == 1
                ? Task.FromResult(SalesDocumentResult.Failed("The document has already been closed."))
                : owner.Session.RunAsync(_ => owner.Commit(documentId, transactionId, profile, paidAmount, paymentReference));

        public Task AbortAsync() =>
            Interlocked.Exchange(ref _finished, 1) == 1
                ? Task.CompletedTask
                : owner.Session.RunAsync(_ =>
                {
                    owner.Delete(documentId);
                    owner.Logger.LogInformation("Prepared document deleted (payment not completed)");
                    return true;
                });
    }

    private int? PrepareWithRelogin(SalesDocumentRequest request, out SalesDocumentResult? failure)
    {
        var id = Prepare(request, out failure, out var code);
        if (id is null && XlSession.IsSessionError(code))
        {
            logger.LogWarning("XL session expired – logging in again");
            xl.Relogin();
            id = Prepare(request, out failure, out _);
        }

        return id;
    }

    /// <summary>Step 1: document header + lines (document open, stock taken into the document).</summary>
    private int? Prepare(SalesDocumentRequest request, out SalesDocumentResult? failure, out int code)
    {
        failure = null;
        code = 0;
        var s = xl.Settings;
        var profile = Profile(request);
        var invoice = profile.Invoice;
        var documentId = 0;

        if (invoice && request.Customer is not { CanReceiveInvoice: true })
        {
            failure = SalesDocumentResult.Failed($"Invoice not allowed for customer {request.Customer?.Acronym ?? "-"} (business with a tax id required)");
            logger.LogError("Document not created: {Error}", failure.Error);
            return null;
        }

        // every line must be fully covered by warehouse allocations – otherwise the document would not match the payment
        if (request.Cart.Lines.FirstOrDefault(l => l.Allocations.Sum(a => a.Quantity) != l.Quantity) is { } uncovered)
        {
            failure = SalesDocumentResult.Failed($"Insufficient stock of {uncovered.Code}: {uncovered.Allocations.Sum(a => a.Quantity):0.##} of {uncovered.Quantity:0.##} allocated");
            logger.LogError("Document not created: {Error}", failure.Error);
            return null;
        }

        var header = new XLDokumentNagInfo_20251
        {
            Wersja = s.ApiVersion,
            Typ = invoice ? InvoiceType : ReceiptType,
            Tryb = 2, // batch mode
            Seria = profile.Series,
            MagazynZ = xl.Erp.PrimaryWarehouse.Code,
            Forma = s.CardPaymentForm,
            GUID = request.TransactionId.ToString(),
            Opis = Truncate(s.DocumentDescription, 2000)
        };
        if (request.Customer is { } customer)
        {
            // invoice customer, or a receipt customer identified by their card (sales history on the customer card)
            header.KntTyp = CustomerType;
            header.KntNumer = customer.Id;
            if (invoice)
            {
                header.NB = "B"; // VAT from gross – the invoice total always equals the amount paid by card
                // fiscal invoice: the flag has to be set on the header (TrN_Fiskalny=2), the close prints it fiscally
                header.Fiskalny = profile.Fiscal ? 1 : 0;
            }

            logger.LogInformation("{Kind} for {Customer}: series {Series}, fiscal {Fiscal}",
                invoice ? "Invoice" : "Receipt", customer.Acronym, profile.Series, profile.Fiscal);
        }
        else
        {
            header.Akronim = s.CustomerAcronym;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rc = cdn_api.cdn_api.XLNowyDokument(xl.SessionId, ref documentId, header);
        logger.LogDebug("XLNowyDokument: {Ms} ms", sw.ElapsedMilliseconds);
        if (rc != 0)
        {
            code = rc;
            failure = Fail(XlSession.FnNewDocument, rc, "Document could not be created");
            return null;
        }

        try
        {
            foreach (var line in request.Cart.Lines)
            foreach (var allocation in line.Allocations)
            {
                var element = new XLDokumentElemInfo_20251
                {
                    Wersja = s.ApiVersion,
                    TwrTyp = ProductType,
                    TwrNumer = line.ProductId,
                    TwrReq = 1,
                    Ilosc = allocation.Quantity.ToString("0.####", CultureInfo.InvariantCulture),
                    // the gross price shown to and paid by the customer + price type stored on the line
                    Cena = line.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture),
                    CenaSpr = xl.Erp.PriceNumber,
                    Magazyn = allocation.WarehouseCode,
                    MagReq = 1
                };
                rc = cdn_api.cdn_api.XLDodajPozycje(documentId, element);
                if (rc != 0)
                {
                    code = rc;
                    failure = Fail(XlSession.FnAddLine, rc, $"Line {line.Code} ({allocation.WarehouseCode}) could not be added");
                    Delete(documentId);
                    return null;
                }
            }

            logger.LogDebug("XLDodajPozycje ({Count}): {Ms} ms", request.Cart.Lines.Count, sw.ElapsedMilliseconds);
            return documentId;
        }
        catch
        {
            Delete(documentId);
            throw;
        }
    }

    /// <summary>Step 2: card payment + confirmation (XL prints the fiscal receipt).</summary>
    private SalesDocumentResult Commit(int documentId, Guid transactionId, DocumentProfile profile, decimal paidAmount, string? paymentReference)
    {
        var s = xl.Settings;
        var invoice = profile.Invoice;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var payment = new XLPlatnoscInfo_20251
            {
                Wersja = s.ApiVersion,
                Forma = s.CardPaymentForm,
                Kwota = paidAmount.ToString("0.00", CultureInfo.InvariantCulture),
                Notatki = Truncate(paymentReference ?? "", 1000)
            };
            var rc = cdn_api.cdn_api.XLDodajPlatnosc(documentId, payment);
            logger.LogDebug("XLDodajPlatnosc: {Ms} ms", sw.ElapsedMilliseconds);
            if (rc != 0)
            {
                var failure = Fail(XlSession.FnAddPayment, rc, "Payment could not be added");
                Delete(documentId);
                return failure;
            }

            var close = new XLZamkniecieDokumentuInfo_20251
            {
                Wersja = s.ApiVersion,
                Tryb = s.CloseMode,
                Fiskalny = profile.Fiscal ? 1 : 0,
                KSeF = invoice ? s.InvoiceKsefMode : 0
            };
            rc = cdn_api.cdn_api.XLZamknijDokument(documentId, close);
            logger.LogDebug("XLZamknijDokument: {Ms} ms", sw.ElapsedMilliseconds);
            if (rc != 0)
            {
                var failure = Fail(XlSession.FnCloseDocument, rc, "Document could not be confirmed");
                // XL may report an error although the document was confirmed (and the fiscal receipt printed) –
                // deleting it and creating it again would print a second receipt for one payment
                if (FindConfirmed(transactionId) is { } confirmed)
                {
                    logger.LogWarning("XLZamknijDokument returned {Rc}, but document {Number} is confirmed – kept", rc, confirmed.DocumentNumber);
                    return confirmed with { Fiscal = profile.Fiscal };
                }

                Delete(documentId);
                return failure;
            }

            var type = close.GidTyp == 0 ? (invoice ? InvoiceType : ReceiptType) : close.GidTyp;
            var number = DocumentNumber(type, close.GidNumer);
            logger.LogInformation("Document {Number} created (GID {Type}:{Gid})", number, type, close.GidNumer);
            return new SalesDocumentResult(true, number, close.GidNumer, null, type, profile.Fiscal);
        }
        catch
        {
            Delete(documentId);
            throw;
        }
    }

    /// <summary>Confirmed document of the transaction (synchronous – called on the XL thread).</summary>
    private SalesDocumentResult? FindConfirmed(Guid transactionId)
    {
        try
        {
            using var conn = new SqlConnection(xl.Erp.ConnectionString);
            var row = conn.QueryFirstOrDefault<(int Type, int Number, int State)>(FindByGuidSql, new { guid = transactionId.ToString() });
            return row.Number != 0 && row.State >= 3
                ? new SalesDocumentResult(true, DocumentNumber(row.Type, row.Number), row.Number, null, row.Type)
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check whether the document was confirmed");
            return null;
        }
    }

    private string? DocumentNumber(int type, int number)
    {
        var info = new XLNumerDokumentuInfo_20251 { Wersja = xl.ApiVersion, GIDTyp = type, GIDNumer = number };
        return cdn_api.cdn_api.XLPobierzNumerDokumentu(info) == 0 ? info.NumerDokumentu : null;
    }

    private void Delete(int documentId)
    {
        try
        {
            var rc = cdn_api.cdn_api.XLZamknijDokument(documentId, new XLZamkniecieDokumentuInfo_20251 { Wersja = xl.ApiVersion, Tryb = -1 });
            if (rc != 0)
                logger.LogWarning("Unconfirmed document could not be deleted (code {Rc})", rc);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Deleting the unconfirmed document failed");
        }
    }

    private SalesDocumentResult Fail(int function, int code, string context)
    {
        var description = xl.Describe(function, code);
        logger.LogError("{Context}: code {Code} – {Description}", context, code, description);
        return SalesDocumentResult.Failed($"{context} (XL {code}: {description})");
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
