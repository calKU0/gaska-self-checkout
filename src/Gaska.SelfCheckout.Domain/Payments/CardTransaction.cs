namespace Gaska.SelfCheckout.Domain.Payments;

public enum CardTransactionKind
{
    Sale,
    Refund,
    Reversal,
    Other
}

public enum CardTransactionOutcome
{
    Approved,
    Declined,
    /// <summary>No terminal response / communication failure – the result is unknown.</summary>
    Unknown,
    /// <summary>Transaction aborted (by the customer or the kiosk) before authorization.</summary>
    Cancelled
}

/// <summary>
/// Result of a card transaction with the data required on the payment confirmation
/// (independent of the terminal protocol and of the printer).
/// </summary>
public sealed record CardTransaction
{
    /// <summary>
    /// <see cref="ResultCode"/> when the terminal could not be reached before the transaction started – nothing was
    /// sent to the terminal, so no money was taken.
    /// </summary>
    public const string NoConnectionCode = "NO_CONNECTION";

    public required CardTransactionOutcome Outcome { get; init; }
    public CardTransactionKind Kind { get; init; } = CardTransactionKind.Sale;
    /// <summary>Terminal authorization result code (e.g. 0000, 1000, 44xx).</summary>
    public string ResultCode { get; init; } = "";
    public string? ResultDescription { get; init; }

    public decimal Amount { get; init; }
    public string Currency { get; init; } = "PLN";
    public DateTime? Timestamp { get; init; }

    public string? MerchantName { get; init; }
    public string? MerchantStreet { get; init; }
    public string? MerchantCity { get; init; }
    public string? MerchantId { get; init; }
    public string? TerminalId { get; init; }
    /// <summary>Acceptor transaction identifier (tag 1B01) – "RACHUNEK NR", used for reversals.</summary>
    public string? ReceiptNumber { get; init; }

    public string? CardName { get; init; }
    public string? MaskedPan { get; init; }
    public string? ExpiryDate { get; init; }
    /// <summary>Card read method (A, C, F, K, M, S).</summary>
    public string? ReadMethod { get; init; }
    public string? AuthorizationCode { get; init; }
    public string? AuthorizationCodeSource { get; init; }
    public string? Aid { get; init; }
    /// <summary>EMV application transaction counter (9F36) and application cryptogram (9F26) – printed for chip and contactless.</summary>
    public string? Atc { get; init; }
    public string? ApplicationCryptogram { get; init; }
    public string? ApplicationLabel { get; init; }
    /// <summary>Cardholder verification methods (tag 1B08) – 7 characters 0/1.</summary>
    public string? CvmList { get; init; }
    public string? CdcvmText { get; init; }
    public string? DeclineText { get; init; }
    public string? AuthorizationResponseCode { get; init; }
    /// <summary>BLIK transaction reference number (1B09).</summary>
    public string? MobileReference { get; init; }
    /// <summary>BLIK code as the terminal reports it (1B10) and how it was entered (1B11).</summary>
    public string? BlikCode { get; init; }
    public string? BlikEntryMode { get; init; }
    /// <summary>Reversal: the receipt number (1B01) of the reversed transaction – "DLA RACHUNKU" on the printout.</summary>
    public string? OriginalReceiptNumber { get; init; }
    /// <summary>Printout masking rules (tag 1B02).</summary>
    public string? MaskingRules { get; init; }
    public string? AdditionalText { get; init; }
    public string? DccOptOutText { get; init; }
    public string? InstallmentText { get; init; }
    public string? InstallmentIssuerText { get; init; }
    public string? EcrTransactionId { get; init; }

    public DccInfo? Dcc { get; init; }

    /// <summary>Raw TLV tags from the terminal response (logs / reprint).</summary>
    public IReadOnlyDictionary<string, string> RawTags { get; init; } = new Dictionary<string, string>();

    public bool IsApproved => Outcome == CardTransactionOutcome.Approved;
    public bool IsContactless => ReadMethod is "A" or "M";
    public bool IsBlik => string.Equals(CardName, "BLIK", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(BlikCode);
    public bool PinVerified => CvmFlag(0) || CvmFlag(1) || CvmFlag(2);
    public bool SignatureRequired => CvmFlag(3);
    public bool CdcvmVerified => CvmFlag(6);

    private bool CvmFlag(int index) => CvmList is { } c && c.Length > index && c[index] == '1';
}

public sealed record DccInfo(
    decimal? ExchangeRate,
    string? Currency,
    string? Amount,
    string? Markup,
    string? Disclaimer,
    string? ConversionFactor);
