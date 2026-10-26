using Gaska.SelfCheckout.Domain.Payments;

namespace Gaska.SelfCheckout.Application.Abstractions;

public sealed record CardSaleRequest(decimal Amount, string EcrTransactionId);

/// <param name="OriginalReceiptNumber">Acceptor transaction id (1B01); when empty the terminal reverses the last approved transaction.</param>
public sealed record CardReversalRequest(decimal Amount, string? OriginalReceiptNumber, string EcrTransactionId);

/// <summary>Refund ("uznanie") – money returned to the card; unlike a reversal it works after the end of day too.</summary>
/// <param name="AuthorizationCode">Optional authorization code of the original sale.</param>
public sealed record CardRefundRequest(decimal Amount, string EcrTransactionId, string? AuthorizationCode = null);

public sealed record TerminalInfo(bool Connected, string? TerminalId, string? MerchantName, int DiagnosticStatus, string Message);

public sealed record TerminalReport(bool Success, string Title, IReadOnlyList<string> Lines, string? Error);

/// <summary>Payment terminal (PIN pad).</summary>
public interface IPaymentTerminal
{
    /// <summary>
    /// Card / BLIK sale. Cancelling the token aborts the transaction on the terminal (until authorization is sent).
    /// When the response is lost the implementation checks the terminal's last transaction itself.
    /// </summary>
    Task<CardTransaction> SaleAsync(CardSaleRequest request, ITerminalInteraction interaction, CancellationToken ct = default);

    Task<CardTransaction> ReverseAsync(CardReversalRequest request, ITerminalInteraction interaction, CancellationToken ct = default);

    /// <summary>Refund to the card (transaction type 20 – "uznanie").</summary>
    Task<CardTransaction> RefundAsync(CardRefundRequest request, ITerminalInteraction interaction, CancellationToken ct = default);

    Task<CardTransaction?> GetLastTransactionAsync(CancellationToken ct = default);

    /// <summary>Connection test requested by staff – the terminal shows "Test zakończony pomyślnie".</summary>
    Task<TerminalInfo> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>Silent availability check (session hello, nothing shown on the terminal) – periodic health checks.</summary>
    Task<TerminalInfo> CheckAsync(CancellationToken ct = default) => TestConnectionAsync(ct);

    /// <summary>End of day (transaction upload to the acquirer) with a report.</summary>
    Task<TerminalReport> CloseDayAsync(CancellationToken ct = default);

    /// <summary>Last transaction upload status (ecr_prompt / pinpad_prompt type 04).</summary>
    Task<TerminalReport> GetUploadStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Informational report of the transactions in the terminal – nothing is uploaded for settlement
    /// (ecr_report_start type 2; <paramref name="resetCounters"/> – action 3, otherwise 2).
    /// </summary>
    Task<TerminalReport> TransactionReportAsync(bool resetCounters, CancellationToken ct = default);
}

public enum TerminalPromptKind
{
    YesNo,
    /// <summary>Choose one of the options (e.g. BLIK / DCC currency).</summary>
    Choice,
    /// <summary>Numeric input (amount, code).</summary>
    Numeric,
    /// <summary>Confirmation with a button.</summary>
    Acknowledge
}

public sealed record TerminalPromptOption(string Value, string Label);

public sealed record TerminalPrompt(
    TerminalPromptKind Kind,
    string Text,
    IReadOnlyList<TerminalPromptOption> Options,
    TimeSpan? Timeout = null);

/// <param name="Cancelled">The customer gave up.</param>
/// <param name="Value">Option number, "0" = YES / "1" = NO, or the entered number.</param>
public sealed record TerminalReply(bool Cancelled, string? Value)
{
    public static TerminalReply Cancel { get; } = new(true, null);
    public static TerminalReply Of(string value) => new(false, value);
}

/// <summary>Customer interaction during a transaction – terminal messages and questions that need an answer.</summary>
public interface ITerminalInteraction
{
    void ShowMessage(string text);

    /// <summary>Asks the customer; the token is cancelled when the terminal withdraws the question (pinpad_stop_display).</summary>
    Task<TerminalReply> AskAsync(TerminalPrompt prompt, CancellationToken ct);

    void ClearMessage();
}

/// <summary>Interaction without UI (service operations) – messages are collected, questions are cancelled.</summary>
public sealed class SilentTerminalInteraction : ITerminalInteraction
{
    public static SilentTerminalInteraction Instance { get; } = new();
    public List<string> Messages { get; } = [];
    public void ShowMessage(string text) => Messages.Add(text);
    public Task<TerminalReply> AskAsync(TerminalPrompt prompt, CancellationToken ct) => Task.FromResult(TerminalReply.Cancel);
    public void ClearMessage() { }
}
