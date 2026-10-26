namespace Gaska.SelfCheckout.Application.Abstractions;

public enum CheckoutState
{
    Started = 0,
    PaymentDeclined = 10,
    PaymentCancelled = 11,
    PaymentApproved = 20,
    DocumentCreated = 30,
    Completed = 40,
    /// <summary>The ERP document was not created, the payment has been reversed on the terminal.</summary>
    DocumentFailedReversed = 50,
    /// <summary>Needs staff attention (e.g. payment taken, no document and the reversal failed).</summary>
    NeedsAttention = 90
}

/// <summary>Kiosk transaction journal entry (local, durable history of every purchase attempt).</summary>
public sealed class CheckoutRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public CheckoutState State { get; set; }
    /// <summary>"Receipt" or "Invoice".</summary>
    public string DocumentKind { get; set; } = "Receipt";
    public string? CustomerAcronym { get; set; }
    /// <summary>XL customer card (invoice / receipt with "Mam konto") – needed to issue the document later.</summary>
    public int? CustomerId { get; set; }
    public decimal Amount { get; set; }
    public string CartJson { get; set; } = "[]";
    public string? EcrTransactionId { get; set; }
    public string? CardReceiptNumber { get; set; }
    public string? CardResultCode { get; set; }
    public string? CardJson { get; set; }
    public string? DocumentNumber { get; set; }
    public int? DocumentId { get; set; }
    public bool SlipPrinted { get; set; }
    /// <summary>Merchant copy of the payment confirmation (kept electronically).</summary>
    public string? MerchantSlip { get; set; }
    public string? Error { get; set; }
}

public interface ITransactionJournal
{
    Task AddAsync(CheckoutRecord record, CancellationToken ct = default);
    Task UpdateAsync(CheckoutRecord record, CancellationToken ct = default);
    Task<CheckoutRecord?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<CheckoutRecord>> GetRecentAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<CheckoutRecord>> GetRequiringAttentionAsync(CancellationToken ct = default);

    /// <summary>Next ECR transaction number (ECR_TRANSACTION_ID field, 1-999999).</summary>
    Task<int> NextEcrTransactionNumberAsync(CancellationToken ct = default);
}
