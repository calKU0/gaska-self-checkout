using System.Text;
using System.Text.Json;
using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Application.Checkout;
using Gaska.SelfCheckout.Application.Options;
using Gaska.SelfCheckout.Application.Printing;
using Gaska.SelfCheckout.Domain.Payments;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace Gaska.SelfCheckout.Web.Services;

public enum TerminalTestOperation
{
    Hello,
    Sale,
    Refund,
    ReverseLast,
    LastTransaction,
    UploadStatus,
    Report,
    ReportWithReset,
    CloseDay
}

/// <param name="Prompt">A terminal question waiting for the operator (BLIK, DCC, "continue with another code or card?").</param>
/// <param name="LogFile">The ECR↔POS log of the test (file name in logs/polcard/cert).</param>
public sealed record TerminalTestStatus(
    bool IsRunning,
    string? TestNumber,
    TerminalTestOperation? Operation,
    string? TerminalMessage,
    TerminalPrompt? Prompt,
    string? Result,
    string? LogFile)
{
    public static TerminalTestStatus Idle { get; } = new(false, null, null, null, null, null, null);
}

/// <summary>
/// One run of a certification test – what goes into the sheet: the log file (column J) and the receipt numbers of the
/// merchant and cardholder copies (columns L and M; "N/A" when that copy was not printed).
/// </summary>
public sealed record TerminalTestResult(
    string Test,
    TerminalTestOperation Operation,
    DateTime At,
    string Outcome,
    string LogFile,
    string MerchantReceipt,
    string CardholderReceipt);

/// <summary>
/// Fiserv certification tests run from the service panel: terminal operations for any amount, without an ERP document,
/// with the same terminal dialogue (every question goes to the operator – nothing is answered automatically) and the
/// same printouts as the kiosk. The ECR↔POS messages of each test go to a log file of their own
/// (<see cref="CertificationLogSink"/>).
/// </summary>
public sealed class TerminalTestRunner(
    IPaymentTerminal terminal,
    IReceiptPrinter printer,
    CardSlipFormatter formatter,
    HardwareLock hardwareLock,
    IServiceScopeFactory scopes,
    CertificationLogSink certificationLog,
    IOptionsMonitor<KioskOptions> kiosk,
    ILogger<TerminalTestRunner> logger) : ITerminalInteraction
{
    private static readonly string ResultsFile = Path.Combine(AppContext.BaseDirectory, "data", "cert-results.json");

    private readonly Lock _sync = new();
    private List<TerminalTestResult>? _results;
    private TerminalTestResult? _runResult;
    private TerminalTestStatus _status = TerminalTestStatus.Idle;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<TerminalReply>? _pendingPrompt;

    /// <summary>The last approved sale of the tests – the one "Unieważnij ostatnią" reverses.</summary>
    public CardTransaction? LastApproved { get; private set; }

    public TerminalTestStatus Status
    {
        get { lock (_sync) return _status; }
    }

    public event Action? Changed;

    /// <summary>Every test run so far (kept in data/cert-results.json), oldest first.</summary>
    public IReadOnlyList<TerminalTestResult> Results
    {
        get { lock (_sync) return [.. LoadResults()]; }
    }

    /// <summary>Starts the certification over: the summary and the per-test ECR↔POS logs are deleted.</summary>
    /// <returns>How many log files were deleted.</returns>
    public int ClearResults()
    {
        var deleted = 0;
        lock (_sync)
        {
            if (_status.IsRunning)
                return 0; // the running test is still writing its log
            _results = [];
            SaveResults();
            LastApproved = null;

            if (Directory.Exists(certificationLog.Directory))
                foreach (var file in Directory.EnumerateFiles(certificationLog.Directory, "test_*.log"))
                {
                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch (IOException ex)
                    {
                        logger.LogWarning(ex, "Certification log {File} could not be deleted", file);
                    }
                }
        }

        logger.LogInformation("Certification summary cleared, {Count} test logs deleted", deleted);
        Publish();
        return deleted;
    }

    public bool TryStart(string testNumber, TerminalTestOperation operation, decimal amount)
    {
        var number = new string(testNumber.Where(char.IsAsciiLetterOrDigit).ToArray());
        if (number.Length == 0)
            number = "x";
        if (number.Length == 1 && char.IsAsciiDigit(number[0]))
            number = "0" + number;
        // one file per test and day – the attempts of a test (e.g. the three cards of test 13) stay together
        var logFile = $"test_{number}_{DateTime.Now:yyyyMMdd}";

        lock (_sync)
        {
            if (_status.IsRunning)
                return false;
            _cts = new CancellationTokenSource();
            _status = new TerminalTestStatus(true, number, operation, null, null, null, logFile + ".log");
            _runResult = new TerminalTestResult(number, operation, DateTime.Now, "", logFile + ".log", "N/A", "N/A");
        }

        Publish();
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            string result;
            // flows into the terminal calls below – the protocol messages land in the test's own log
            using (LogContext.PushProperty(CertificationLogSink.Property, logFile))
            {
                try
                {
                    result = await RunAsync(operation, amount, token);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Certification test {Test} ({Operation}) failed", number, operation);
                    result = "Błąd: " + ex.Message;
                }
            }

            lock (_sync)
            {
                _pendingPrompt?.TrySetResult(TerminalReply.Cancel);
                _pendingPrompt = null;
                _status = _status with { IsRunning = false, TerminalMessage = null, Prompt = null, Result = result };
                _cts?.Dispose();
                _cts = null;
                if (_runResult is { } run)
                {
                    LoadResults().Add(run with { Outcome = run.Outcome.Length > 0 ? run.Outcome : FirstLine(result) });
                    SaveResults();
                }
            }

            Publish();
        }, CancellationToken.None);
        return true;
    }

    /// <summary>Aborts the transaction on the terminal (effective until the authorization is sent).</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            _pendingPrompt?.TrySetResult(TerminalReply.Cancel);
            _cts?.Cancel();
        }
    }

    public void Answer(TerminalReply reply)
    {
        TaskCompletionSource<TerminalReply>? pending;
        lock (_sync)
        {
            pending = _pendingPrompt;
            _pendingPrompt = null;
            _status = _status with { Prompt = null };
        }

        pending?.TrySetResult(reply);
        Publish();
    }

    private async Task<string> RunAsync(TerminalTestOperation operation, decimal amount, CancellationToken ct)
    {
        // the devices are shared with the kiosk and the health checks
        using var lease = await hardwareLock.TryAcquireAsync(TimeSpan.FromSeconds(10), ct);
        if (lease is null)
            return "Urządzenia są zajęte (transakcja klienta lub sprawdzanie połączeń) – spróbuj za chwilę.";

        switch (operation)
        {
            case TerminalTestOperation.Hello:
            {
                var info = await terminal.TestConnectionAsync(ct);
                return $"{info.Message}\nTID: {info.TerminalId}\nAkceptant: {info.MerchantName}\nStatus diagnostyczny: {info.DiagnosticStatus}";
            }

            case TerminalTestOperation.Sale:
                return await TransactionAsync(terminal.SaleAsync(new CardSaleRequest(amount, await NextEcrIdAsync()), this, ct));

            case TerminalTestOperation.Refund:
                return await TransactionAsync(terminal.RefundAsync(new CardRefundRequest(amount, await NextEcrIdAsync()), this, ct));

            case TerminalTestOperation.ReverseLast:
            {
                // the last approved sale of the tests; without one the terminal reverses its own last approved transaction
                var original = LastApproved;
                var request = new CardReversalRequest(original?.Amount ?? amount, original?.ReceiptNumber, await NextEcrIdAsync());
                return await TransactionAsync(terminal.ReverseAsync(request, this, ct));
            }

            case TerminalTestOperation.LastTransaction:
            {
                var tx = await terminal.GetLastTransactionAsync(ct);
                if (tx is null)
                    return "Brak ostatniej transakcji w terminalu.";
                await printer.PrintAsync(formatter.Format(tx, SlipCopy.Cardholder, printer.LineWidth, kiosk.CurrentValue.StationId), ct);
                return $"Ostatnia transakcja: {tx.ResultCode} – {tx.ResultDescription}\nWydrukowano potwierdzenie (kopia klienta).\n\n"
                       + formatter.Format(tx, SlipCopy.Cardholder, printer.LineWidth, kiosk.CurrentValue.StationId).ToPlainText(printer.LineWidth);
            }

            case TerminalTestOperation.UploadStatus:
                return await ReportAsync(terminal.GetUploadStatusAsync(ct), print: true, ct);

            case TerminalTestOperation.Report:
                return await ReportAsync(terminal.TransactionReportAsync(resetCounters: false, ct), print: true, ct);

            case TerminalTestOperation.ReportWithReset:
                return await ReportAsync(terminal.TransactionReportAsync(resetCounters: true, ct), print: true, ct);

            case TerminalTestOperation.CloseDay:
                return await ReportAsync(terminal.CloseDayAsync(ct), print: true, ct);

            default:
                return "Nieznana operacja.";
        }
    }

    /// <summary>Card transaction result with its printouts: both copies of an approved transaction, one of a decline.</summary>
    private async Task<string> TransactionAsync(Task<CardTransaction> operation)
    {
        var tx = await operation;
        var station = kiosk.CurrentValue.StationId;
        Record(r => r with { Outcome = $"{tx.Outcome} {tx.ResultCode}".Trim() });
        var text = new StringBuilder();
        text.AppendLine($"Wynik: {tx.Outcome} – {tx.ResultCode} {tx.ResultDescription}");
        if (tx.ResultCode == CardTransaction.NoConnectionCode)
            text.AppendLine("BRAK KOMUNIKACJI Z TERMINALEM PŁATNICZYM – sprawdź kabel sieciowy i zasilanie terminala.");
        if (!string.IsNullOrEmpty(tx.ReceiptNumber))
            text.AppendLine($"Rachunek terminala: {tx.ReceiptNumber}, kod autoryzacji: {tx.AuthorizationCode}");

        if (tx.IsApproved && tx.Kind == CardTransactionKind.Sale)
            LastApproved = tx;

        // nothing to print when the terminal did not answer or the card was not read
        if (tx.RawTags.Count == 0 || tx.Outcome == CardTransactionOutcome.Cancelled)
            return text.Append("Brak wydruku.").ToString();

        // both copies, as in the sheet's receipt templates (also for a decline)
        foreach (var copy in new[] { SlipCopy.Merchant, SlipCopy.Cardholder })
        {
            var slip = formatter.Format(tx, copy, printer.LineWidth, station);
            try
            {
                await printer.PrintAsync(slip);
                text.AppendLine($"Wydrukowano: {(copy == SlipCopy.Cardholder ? "kopia klienta" : "kopia sprzedawcy")}");
                var receipt = string.IsNullOrEmpty(tx.ReceiptNumber) ? "N/A" : tx.ReceiptNumber;
                Record(r => copy == SlipCopy.Merchant ? r with { MerchantReceipt = receipt } : r with { CardholderReceipt = receipt });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Test slip could not be printed");
                text.AppendLine($"Nie udało się wydrukować ({copy}): {ex.Message}");
            }

            text.AppendLine().AppendLine(slip.ToPlainText(printer.LineWidth));
        }

        return text.ToString();
    }

    private async Task<string> ReportAsync(Task<TerminalReport> operation, bool print, CancellationToken ct)
    {
        var report = await operation;
        if (!report.Success)
            return $"{report.Title}: {report.Error}";
        if (print)
            await printer.PrintAsync(new PrintDocument(PrintDocumentKind.Report,
                [PrintLine.Center(report.Title.ToUpperInvariant(), bold: true), PrintLine.Blank, .. report.Lines.Select(l => new PrintLine(l))]), ct);
        return report.Title + (print ? " (wydrukowano)" : "") + "\n\n" + string.Join("\n", report.Lines);
    }

    private void Record(Func<TerminalTestResult, TerminalTestResult> change)
    {
        lock (_sync)
            if (_runResult is { } run)
                _runResult = change(run);
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private List<TerminalTestResult> LoadResults()
    {
        if (_results is not null)
            return _results;
        try
        {
            _results = File.Exists(ResultsFile) ? JsonSerializer.Deserialize<List<TerminalTestResult>>(File.ReadAllText(ResultsFile)) ?? [] : [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Certification results could not be read");
            _results = [];
        }

        return _results;
    }

    private void SaveResults()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultsFile)!);
            File.WriteAllText(ResultsFile, JsonSerializer.Serialize(_results, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Certification results could not be saved");
        }
    }

    private async Task<string> NextEcrIdAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ITransactionJournal>().NextEcrTransactionNumberAsync()).ToString();
    }

    void ITerminalInteraction.ShowMessage(string text) => Update(s => s with { TerminalMessage = text });

    void ITerminalInteraction.ClearMessage() => Update(s => s with { TerminalMessage = null, Prompt = null });

    // every question goes to the operator – the certification checks the ECR side of the dialogue (BLIK question etc.)
    async Task<TerminalReply> ITerminalInteraction.AskAsync(TerminalPrompt prompt, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<TerminalReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _pendingPrompt?.TrySetResult(TerminalReply.Cancel);
            _pendingPrompt = tcs;
            _status = _status with { Prompt = prompt };
        }

        Publish();
        await using var registration = ct.Register(() => tcs.TrySetResult(TerminalReply.Cancel));
        var reply = await tcs.Task;
        Update(s => s with { Prompt = null });
        return reply;
    }

    private void Update(Func<TerminalTestStatus, TerminalTestStatus> change)
    {
        lock (_sync) _status = change(_status);
        Publish();
    }

    private void Publish()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Certification status subscriber failed");
        }
    }
}
