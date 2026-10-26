using Gaska.SelfCheckout.Domain.Customers;
using Gaska.SelfCheckout.Domain.Sales;

namespace Gaska.SelfCheckout.Application.Abstractions;

public enum SalePaymentMethod
{
    Card
}

/// <param name="TransactionId">Unique kiosk transaction id stored in the ERP document (idempotency).</param>
/// <param name="Customer">Invoice customer; null for an anonymous receipt.</param>
public sealed record SalesDocumentRequest(
    Guid TransactionId,
    SaleDocumentKind Kind,
    Customer? Customer,
    CartSnapshot Cart,
    SalePaymentMethod PaymentMethod,
    decimal PaidAmount,
    string? PaymentReference);

/// <param name="Fiscal">The document was printed by the ERP on the fiscal printer (receipt, fiscal invoice).</param>
public sealed record SalesDocumentResult(bool Success, string? DocumentNumber, int? DocumentId, string? Error, int DocumentType = 0, bool Fiscal = false)
{
    public static SalesDocumentResult Failed(string error) => new(false, null, null, error);
}

/// <summary>
/// A document prepared in the ERP (header + lines, still open) – waiting for the payment result.
/// </summary>
public interface IPreparedDocument
{
    /// <summary>Adds the payment and confirms the document (receipts are printed fiscally by the ERP).</summary>
    Task<SalesDocumentResult> CommitAsync(decimal paidAmount, string? paymentReference);

    /// <summary>Deletes the prepared document (payment declined / cancelled).</summary>
    Task AbortAsync();
}

/// <summary>Sales documents in the ERP (receipt – fiscal printing done by the ERP; invoice).</summary>
public interface ISalesDocumentService
{
    Task<SalesDocumentResult> CreateAsync(SalesDocumentRequest request, CancellationToken ct = default);

    /// <summary>Prepares the document while the customer pays (shortens waiting after card authorization).</summary>
    Task<IPreparedDocument> PrepareAsync(SalesDocumentRequest request, CancellationToken ct = default);

    /// <summary>Looks for a document already issued for the kiosk transaction (e.g. after a failure).</summary>
    Task<SalesDocumentResult?> FindByTransactionIdAsync(Guid transactionId, CancellationToken ct = default);

    Task<ErpStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>SQL connection to the ERP database (catalog, prices, documents).</summary>
    Task<ErpStatus> GetDatabaseStatusAsync(CancellationToken ct = default) => Task.FromResult(new ErpStatus(true, ""));
}

public sealed record ErpStatus(bool Connected, string Message);
