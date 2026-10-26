using System.Text;
using System.Threading.Channels;
using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Devices.Polcard.Protocol;
using Gaska.SelfCheckout.Devices.Polcard.Transport;
using Gaska.SelfCheckout.Domain.Payments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Devices.Polcard;

/// <summary>
/// Polcard/Fiserv PIN pad integration – ECR-POS/PINPAD protocol v4.75.
/// Scope: sale (including DCC and BLIK mobile payments), reversal, refund, ECR_HELLO_EXTENDED (1C10, 1C11, 1C12, 1C19),
/// ECR_REPORT_START (end of day, informational report), ECR_PROMPT/PINPAD_PROMPT type 04, ECR_GET_LAST_TRANSACTION.
/// </summary>
public sealed class PolcardTerminal : IPaymentTerminal
{
    private readonly IEcrConnectionFactory _connections;
    private readonly IOptionsMonitor<PolcardOptions> _optionsMonitor;
    private readonly ILogger<PolcardTerminal> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TerminalInfo? _session;

    public PolcardTerminal(IEcrConnectionFactory connections, IOptionsMonitor<PolcardOptions> options, ILogger<PolcardTerminal> logger)
    {
        _connections = connections;
        _optionsMonitor = options;
        // new terminal address or parameters → a new session must be initialized
        options.OnChange(_ => _session = null);
        _logger = logger;
    }

    public TerminalInfo? Session => _session;

    private PolcardOptions Settings => _optionsMonitor.CurrentValue;

    #region Session

    // RESPONSE TYPE = 3 → test connection: the terminal shows "Test zakończony pomyślnie. Kliknij przycisk…"
    public Task<TerminalInfo> TestConnectionAsync(CancellationToken ct = default) => HelloExclusiveAsync(responseType: 3, ct);

    // RESPONSE TYPE = 1 → ordinary session hello (pos_hello_extended), nothing shown on the terminal
    public Task<TerminalInfo> CheckAsync(CancellationToken ct = default) => HelloExclusiveAsync(responseType: 1, ct);

    private Task<TerminalInfo> HelloExclusiveAsync(int responseType, CancellationToken ct) =>
        Exclusive(async () =>
        {
            try
            {
                var info = await HelloAsync(responseType, ct);
                _session = info.Connected ? info : _session;
                return info;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Terminal connection test failed");
                return new TerminalInfo(false, null, null, -1, ex.Message);
            }
        }, ct);

    private async Task EnsureSessionAsync(CancellationToken ct, bool force = false)
    {
        if (_session is { Connected: true } && !force)
            return;

        TerminalInfo? info = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            info = await HelloAsync(responseType: 1, ct);
            // 0 – OK, 2/3 – warnings but work can continue; 1 – error → retry up to 2 times
            if (info.Connected)
            {
                _session = info;
                return;
            }
        }

        throw new InvalidOperationException($"Terminal session could not be initialized: {info?.Message}");
    }

    private async Task<TerminalInfo> HelloAsync(int responseType, CancellationToken ct)
    {
        var tlv = Tlv.BuildPolcard([
            ("1C10", Settings.EcrName),
            ("1C11", Settings.EcrVersion),
            ("1C12", Settings.EcrModificationDate),
            ("1C19", Settings.ProtocolVersion)
        ]);
        // |2841111|e|a|dddd|LLLL|key|b|llll|TLV| – unencrypted session (the card is read by the PIN pad)
        var hello = new EcrMessage(EcrMessageIds.EcrHelloExtended,
            "0", "", "", "", "", responseType.ToString(), EcrMessage.Num(tlv.Length, 4), tlv);

        await using var conn = await _connections.ConnectAsync(ct);
        await conn.SendAsync(hello, ct);
        var response = await conn.ReceiveAsync(TimeSpan.FromSeconds(Settings.ResponseTimeoutSeconds), ct)
                       ?? throw new IOException("The terminal closed the connection without answering ecr_hello_extended.");

        if (response.Id is not (EcrMessageIds.PosHelloExtended or EcrMessageIds.PosHello))
            throw new IOException($"Unexpected terminal response: {response.Id}");

        _ = int.TryParse(response.Field(0), out var diag);
        var tid = response.Field(3);
        var tags = new TlvBag(Tlv.Parse(response.Field(5)));
        var connected = diag is 0 or 2 or 3;
        var message = diag switch
        {
            0 => "Połączenie z terminalem poprawne",
            1 => "Błąd terminala blokujący dalszą pracę",
            2 => "Terminal wymaga pobrania aplikacji (praca możliwa)",
            3 => "Terminal wymaga wysłania danych (praca możliwa)",
            4 => "Terminal wymaga natychmiastowego pobrania aplikacji",
            5 => "Terminal wymaga natychmiastowej wysyłki danych",
            _ => $"Status diagnostyczny {diag}"
        };
        _logger.LogInformation("pos_hello_extended: diag={Diag}, TID={Tid}, external_device={Ext}, services={Services}",
            diag, tid, tags.Text("1C08"), tags.Text("1C09"));
        return new TerminalInfo(connected, string.IsNullOrEmpty(tid) ? tags.Text("1A20") : tid, tags.Text("1A1C"), diag, message);
    }

    #endregion

    #region Transactions

    public Task<CardTransaction> SaleAsync(CardSaleRequest request, ITerminalInteraction interaction, CancellationToken ct = default) =>
        Exclusive(() => StartTransactionAsync(Settings.SaleTransactionType, request.Amount, request.EcrTransactionId, false, null, interaction, ct), CancellationToken.None);

    public Task<CardTransaction> ReverseAsync(CardReversalRequest request, ITerminalInteraction interaction, CancellationToken ct = default) =>
        Exclusive(() => StartTransactionAsync(Settings.SaleTransactionType, request.Amount, request.EcrTransactionId, true, request.OriginalReceiptNumber, interaction, ct), CancellationToken.None);

    public Task<CardTransaction> RefundAsync(CardRefundRequest request, ITerminalInteraction interaction, CancellationToken ct = default) =>
        Exclusive(() => StartTransactionAsync(RefundTransactionType, request.Amount, request.EcrTransactionId, false, null, interaction, ct, request.AuthorizationCode), CancellationToken.None);

    /// <summary>Transaction type of a refund ("uznanie").</summary>
    private const string RefundTransactionType = "20";

    private async Task<CardTransaction> StartTransactionAsync(
        string type, decimal amount, string ecrTransactionId, bool reversal, string? originalReceipt,
        ITerminalInteraction interaction, CancellationToken ct, string? authorizationCode = null)
    {
        try
        {
            await EnsureSessionAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            // nothing sent yet – the transaction did not start (requirement: ECR-POS communication error message)
            _logger.LogError(ex, "No connection with the terminal – the transaction was not started");
            return new CardTransaction
            {
                Outcome = CardTransactionOutcome.Cancelled,
                Amount = amount,
                ResultCode = CardTransaction.NoConnectionCode,
                ResultDescription = "Brak komunikacji z terminalem płatniczym",
                Kind = reversal ? CardTransactionKind.Reversal : type == RefundTransactionType ? CardTransactionKind.Refund : CardTransactionKind.Sale
            };
        }

        var message = BuildStartExtended(type, amount, ecrTransactionId, reversal, originalReceipt, authorizationCode);
        _logger.LogInformation("{Op}: amount {Amount}, ECR_TRANSACTION_ID {EcrId}",
            reversal ? "Reversal" : type == RefundTransactionType ? "Refund" : "Sale", amount, ecrTransactionId);

        TransactionOutcome outcome;
        try
        {
            outcome = await RunTransactionAsync(message, interaction, ct);
            if (outcome.PosError)
            {
                // pos_error at transaction start → a new session is required (FAQ 5); retry once.
                _logger.LogWarning("pos_error – re-initializing the session and retrying the transaction");
                await EnsureSessionAsync(CancellationToken.None, force: true);
                outcome = await RunTransactionAsync(message, interaction, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.Net.Sockets.SocketException)
        {
            _logger.LogError(ex, "Communication lost during the transaction");
            outcome = TransactionOutcome.Lost(ex.Message);
        }

        if (outcome.Finish is { } finish)
            return PosFinishMapper.Map(finish, amount, outcome.AbortRequested, reversal) with { OriginalReceiptNumber = reversal ? originalReceipt : null };

        if (outcome.PosError)
            return new CardTransaction
            {
                Outcome = CardTransactionOutcome.Declined,
                Amount = amount,
                ResultCode = "POS_ERROR",
                ResultDescription = "Terminal zgłosił błąd uniemożliwiający transakcję.",
                Kind = reversal ? CardTransactionKind.Reversal : CardTransactionKind.Sale
            };

        // No pos_finish → check the terminal's last transaction (requirement: ECR_GET_LAST_TRANSACTION).
        return await RecoverFromLastTransactionAsync(amount, ecrTransactionId, reversal, outcome);
    }

    private async Task<CardTransaction> RecoverFromLastTransactionAsync(decimal amount, string ecrTransactionId, bool reversal, TransactionOutcome outcome)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2 + attempt * 3));
                var last = await QueryLastTransactionAsync(CancellationToken.None);
                if (last is null)
                    break;
                var lastTx = PosFinishMapper.Map(last, amount, outcome.AbortRequested, reversal);
                if (string.Equals(lastTx.EcrTransactionId?.TrimStart('0'), ecrTransactionId.TrimStart('0'), StringComparison.Ordinal))
                {
                    _logger.LogWarning("Transaction result determined from ECR_GET_LAST_TRANSACTION: {Code}", lastTx.ResultCode);
                    return lastTx;
                }

                _logger.LogWarning("The terminal's last transaction has a different ECR_TRANSACTION_ID ({Last}) – the transaction was not performed", lastTx.EcrTransactionId);
                return new CardTransaction
                {
                    Outcome = outcome.AbortRequested ? CardTransactionOutcome.Cancelled : CardTransactionOutcome.Declined,
                    Amount = amount,
                    ResultCode = "NO_TRANSACTION",
                    ResultDescription = "Transakcja nie została przeprowadzona."
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the last transaction (attempt {Attempt})", attempt + 1);
            }
        }

        return new CardTransaction
        {
            Outcome = CardTransactionOutcome.Unknown,
            Amount = amount,
            ResultCode = "UNKNOWN",
            ResultDescription = outcome.Error ?? "Brak odpowiedzi terminala."
        };
    }

    private EcrMessage BuildStartExtended(
        string type, decimal amount, string ecrTransactionId, bool reversal, string? originalReceipt, string? authorizationCode)
    {
        // |2811111|tt|nnn|amount|amount2|amount3|amount4|amount5|ccc|LLL|carddata|cardtype|ordertype|LLL|track|r|cc|chid|clerk|profile|maxcb|auth|ecrid|...|origid|
        var fields = new List<string>
        {
            type,                           // 2  TRANSACTION TYPE
            "000",                          // 3  MESSAGE PART NUMBER
            EcrMessage.Amount(amount),      // 4  AMOUNT
            "", "", "", "",                 // 5-8 AMOUNT2..5 (not used)
            "985",                          // 9  CURRENCY – PLN (ISO 4217), as in Fiserv's frame examples
            "", "", "", "",                 // 10-13 card data from an ECR reader (not used)
            "", "",                         // 14-15 TRACK
            reversal ? "1" : "0",           // 16 REVERSAL FLAG
            "",                             // 17 COMMERCIAL CODE
            "",                             // 18 CARDHOLDER ID
            "",                             // 19 CLERK_ID
            "",                             // 20 PROFILE_ID
            "",                             // 21 MAX_CB_AMOUNT
            authorizationCode ?? "",        // 22 AUTHORIZATION_CODE
            ecrTransactionId                // 23 ECR_TRANSACTION_ID
        };

        if (reversal && !string.IsNullOrEmpty(originalReceipt))
        {
            // 24 PRIVATE CARD NAME, 25 EXTERNAL_TXN_ID, 26 CH_PHONE, 27 CH_EMAIL, 28 PARTIAL APPROVAL, 29 CARDHOLDER DATA,
            // 30 CARDHOLDER LANGUAGE, 31 SPLIT_PROFILE_ID, 32-34 LIBRE12-14, 35 ORIGINAL TRANSACTION ID
            fields.AddRange(Enumerable.Repeat("", 11));
            fields.Add(EcrMessage.Num(long.TryParse(originalReceipt, out var orig) ? orig : 0, 10));
        }

        return new EcrMessage(EcrMessageIds.EcrStartExtended, fields);
    }

    private sealed record TransactionOutcome(PosFinish? Finish, bool PosError, bool AbortRequested, string? Error)
    {
        public static TransactionOutcome Lost(string error) => new(null, false, false, error);
    }

    /// <summary>
    /// Message loop during a transaction: pinpad_display / pinpad_stop_display / pinpad_wait messages,
    /// ecr_display replies, ECR abort (reply type 99) and assembling a multi-part pos_finish.
    /// </summary>
    private async Task<TransactionOutcome> RunTransactionAsync(EcrMessage start, ITerminalInteraction interaction, CancellationToken ct)
    {
        await using var conn = await _connections.ConnectAsync(CancellationToken.None);
        await conn.SendAsync(start, CancellationToken.None);

        var incoming = Channel.CreateUnbounded<EcrMessage?>();
        using var readerCts = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            try
            {
                while (!readerCts.IsCancellationRequested)
                {
                    var msg = await conn.ReceiveAsync(TimeSpan.FromHours(1), readerCts.Token);
                    await incoming.Writer.WriteAsync(msg, CancellationToken.None);
                    if (msg is null)
                        break;
                }

                incoming.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                incoming.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        var cancelSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cancelReg = ct.Register(() => cancelSignal.TrySetResult());

        var idleTimeout = TimeSpan.FromSeconds(Settings.TransactionIdleTimeoutSeconds);
        var abortRequested = false;
        var tlv = new StringBuilder();
        string? cardReadType = null, result = null;
        Task<EcrMessage?>? readTask = null;
        Task<TerminalReply>? promptTask = null;
        PinpadDisplay? promptSource = null;
        CancellationTokenSource? promptCts = null;

        try
        {
            while (true)
            {
                readTask ??= incoming.Reader.ReadAsync(CancellationToken.None).AsTask();
                var waits = new List<Task> { readTask };
                if (promptTask is not null) waits.Add(promptTask);
                if (!abortRequested) waits.Add(cancelSignal.Task);

                var timeout = Task.Delay(idleTimeout, CancellationToken.None);
                waits.Add(timeout);
                var completed = await Task.WhenAny(waits);

                if (completed == timeout)
                {
                    if (!abortRequested)
                    {
                        _logger.LogWarning("No terminal messages for {Timeout} – aborting the transaction", idleTimeout);
                        await SendAbortAsync(conn);
                        abortRequested = true;
                        idleTimeout = TimeSpan.FromSeconds(Settings.ResponseTimeoutSeconds);
                        continue;
                    }

                    return new TransactionOutcome(null, false, abortRequested, "Terminal response timeout.");
                }

                if (completed == cancelSignal.Task)
                {
                    _logger.LogInformation("Transaction abort requested by the customer");
                    abortRequested = true;
                    promptCts?.Cancel();
                    promptTask = null;
                    await SendAbortAsync(conn);
                    idleTimeout = TimeSpan.FromSeconds(Math.Max(Settings.ResponseTimeoutSeconds, 60));
                    continue;
                }

                if (promptTask is not null && completed == promptTask)
                {
                    var reply = await promptTask;
                    promptTask = null;
                    if (promptSource is not null)
                        await SendPromptReplyAsync(conn, promptSource, reply);
                    if (reply.Cancelled && promptSource?.KeyboardOptions is 2 or 3 or 4)
                        abortRequested = true;
                    promptSource = null;
                    continue;
                }

                // message from the terminal
                EcrMessage? msg;
                try
                {
                    msg = await readTask;
                }
                catch (ChannelClosedException ex) when (ex.InnerException is not null)
                {
                    throw ex.InnerException;
                }

                readTask = null;
                if (msg is null)
                    return new TransactionOutcome(null, false, abortRequested, "The terminal closed the connection before the transaction finished.");

                switch (msg.Id)
                {
                    case EcrMessageIds.PinpadDisplay:
                    {
                        var display = PinpadDisplay.From(msg);
                        interaction.ShowMessage(display.DisplayText);
                        if (display.KeyboardOptions != 0)
                        {
                            promptCts?.Cancel();
                            promptCts = new CancellationTokenSource();
                            promptSource = display;
                            promptTask = interaction.AskAsync(ToPrompt(display), promptCts.Token);
                        }

                        if (display.TimeoutSeconds is { } t && TimeSpan.FromSeconds(t + 30) > idleTimeout)
                            idleTimeout = TimeSpan.FromSeconds(t + 30);
                        break;
                    }

                    case EcrMessageIds.PinpadStopDisplay:
                        promptCts?.Cancel();
                        promptTask = null;
                        promptSource = null;
                        interaction.ClearMessage();
                        break;

                    case EcrMessageIds.PinpadWait:
                        if (int.TryParse(msg.Field(0), out var wait))
                            idleTimeout = TimeSpan.FromSeconds(Math.Max(Settings.TransactionIdleTimeoutSeconds, wait + 15));
                        break;

                    case EcrMessageIds.PosError:
                        return new TransactionOutcome(null, true, abortRequested, "pos_error");

                    case EcrMessageIds.PosFinish:
                    case EcrMessageIds.PosLastTransaction:
                    {
                        // |2810000|nnn|a|cccc|LLLL|TLV|
                        cardReadType ??= msg.Field(1);
                        result ??= msg.Field(2);
                        tlv.Append(msg.Field(4));
                        if (int.TryParse(msg.Field(0), out var part) && part > 0)
                            break; // more parts follow
                        interaction.ClearMessage();
                        return new TransactionOutcome(
                            new PosFinish(cardReadType ?? "", result ?? "", new TlvBag(Tlv.Parse(tlv.ToString()))),
                            false, abortRequested, null);
                    }

                    default:
                        _logger.LogWarning("Unsupported terminal message {Id} – ignored", msg.Id);
                        break;
                }
            }
        }
        finally
        {
            promptCts?.Cancel();
            await readerCts.CancelAsync();
            try { await reader; } catch { /* closing */ }
        }
    }

    private static TerminalPrompt ToPrompt(PinpadDisplay d)
    {
        TimeSpan? timeout = d.TimeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : null;
        switch (d.KeyboardOptions)
        {
            case 1:
                return new TerminalPrompt(TerminalPromptKind.YesNo, d.DisplayText,
                    [new("0", "TAK"), new("1", "NIE")], timeout);
            case 5:
            case 6:
            case 8:
            {
                var (header, options) = d.ParseOptions();
                return new TerminalPrompt(TerminalPromptKind.Choice, header ?? "Wybierz opcję", options, timeout);
            }
            case 2:
            case 3:
            case 4:
                return new TerminalPrompt(TerminalPromptKind.Numeric, d.DisplayText, [], timeout);
            default:
                return new TerminalPrompt(TerminalPromptKind.Acknowledge, d.DisplayText, [new("1", "OK")], timeout);
        }
    }

    private static Task SendPromptReplyAsync(IEcrConnection conn, PinpadDisplay source, TerminalReply reply)
    {
        string type, value;
        switch (source.KeyboardOptions)
        {
            case 1:
                type = "01";
                value = reply.Cancelled ? "1" : reply.Value ?? "1";
                break;
            case 2:
            case 3:
            case 4:
                if (reply.Cancelled || string.IsNullOrEmpty(reply.Value))
                    return SendAbortAsync(conn);
                type = "0" + source.KeyboardOptions;
                value = reply.Value;
                break;
            case 5:
            case 8:
                type = "05";
                value = reply.Cancelled ? "0" : reply.Value ?? "0";
                break;
            case 6:
                type = "06";
                value = reply.Cancelled ? "0" : reply.Value ?? "0";
                break;
            default:
                type = "07";
                value = "1";
                break;
        }

        return conn.SendAsync(new EcrMessage(EcrMessageIds.EcrDisplay, type, EcrMessage.Num(value.Length, 3), value), CancellationToken.None);
    }

    /// <summary>&lt;ecr_display&gt; reply type 99, message 0 – transaction abort (effective until authorization is sent).</summary>
    private static Task SendAbortAsync(IEcrConnection conn) =>
        conn.SendAsync(new EcrMessage(EcrMessageIds.EcrDisplay, "99", "001", "0"), CancellationToken.None);

    #endregion

    #region Service operations

    public Task<CardTransaction?> GetLastTransactionAsync(CancellationToken ct = default) =>
        Exclusive(async () =>
        {
            var finish = await QueryLastTransactionAsync(ct);
            return finish is null ? null : PosFinishMapper.Map(finish, 0m, false, false);
        }, ct);

    private async Task<PosFinish?> QueryLastTransactionAsync(CancellationToken ct)
    {
        await EnsureSessionAsync(ct);
        await using var conn = await _connections.ConnectAsync(ct);
        await conn.SendAsync(new EcrMessage(EcrMessageIds.EcrGetLastTransaction), ct);

        var tlv = new StringBuilder();
        string? readType = null, result = null;
        while (true)
        {
            var msg = await conn.ReceiveAsync(TimeSpan.FromSeconds(Settings.ResponseTimeoutSeconds), ct);
            if (msg is null)
                throw new IOException("The terminal closed the connection without answering ecr_get_last_transaction.");
            if (msg.Id == EcrMessageIds.PosError)
            {
                _session = null;
                throw new IOException("The terminal reported an error (pos_error).");
            }

            if (msg.Id is not (EcrMessageIds.PosLastTransaction or EcrMessageIds.PosFinish))
                continue;

            readType ??= msg.Field(1);
            result ??= msg.Field(2);
            tlv.Append(msg.Field(4));
            if (int.TryParse(msg.Field(0), out var part) && part > 0)
                continue;
            if (result == "9000")
                return null;
            return new PosFinish(readType ?? "", result ?? "", new TlvBag(Tlv.Parse(tlv.ToString())));
        }
    }

    public Task<TerminalReport> GetUploadStatusAsync(CancellationToken ct = default) =>
        Exclusive(async () =>
        {
            await EnsureSessionAsync(ct);
            await using var conn = await _connections.ConnectAsync(ct);
            // <ecr_prompt> type 04 – last transaction upload status
            await conn.SendAsync(new EcrMessage(EcrMessageIds.EcrPrompt, "04"), ct);
            while (true)
            {
                var msg = await conn.ReceiveAsync(TimeSpan.FromSeconds(Settings.ResponseTimeoutSeconds), ct)
                          ?? throw new IOException("The terminal closed the connection without a response.");
                if (msg.Id == EcrMessageIds.PosError)
                    return new TerminalReport(false, "Status wysyłki", [], "Terminal zgłosił błąd (pos_error).");
                if (msg.Id != EcrMessageIds.PinpadPrompt)
                    continue;

                // |2820200|04|LLL|Status$LastUploadDateTime$TransactionCount$|
                if (msg.Field(0) == "99")
                    return new TerminalReport(false, "Status wysyłki", [], "Dane niedostępne.");
                var content = PinpadDisplay.DecodeText(msg.Field(1), msg.Field(2));
                var parts = content.Split('$');
                var lines = new List<string>
                {
                    $"Status ostatniej wysyłki: {Part(parts, 0)}",
                    $"Data ostatniej udanej wysyłki: {Part(parts, 1)}",
                    $"Liczba transakcji w terminalu: {Part(parts, 2)}"
                };
                return new TerminalReport(true, "Status wysyłki transakcji", lines, null);
            }
        }, ct);

    private static string Part(string[] parts, int i) => i < parts.Length ? parts[i] : "-";

    public Task<TerminalReport> CloseDayAsync(CancellationToken ct = default) =>
        ReportAsync(Settings.CloseDayReportType, Settings.CloseDayAdditionalAction, "Raport wysyłki / zamknięcie dnia", ct);

    // type 2 – cumulative report; action 2 – report only, 3 – report and counter reset (no upload for settlement)
    public Task<TerminalReport> TransactionReportAsync(bool resetCounters, CancellationToken ct = default) =>
        ReportAsync(2, resetCounters ? 3 : 2,
            resetCounters ? "Raport informacyjny – z zerowaniem liczników" : "Raport informacyjny – bez wysyłki", ct);

    private Task<TerminalReport> ReportAsync(int type, int action, string title, CancellationToken ct) =>
        Exclusive(async () =>
        {
            await EnsureSessionAsync(ct);
            await using var conn = await _connections.ConnectAsync(ct);
            await conn.SendAsync(new EcrMessage(EcrMessageIds.EcrReportStart, type.ToString(), action.ToString()), ct);

            var tlv = new StringBuilder();
            string? batch = null, count = null;
            while (true)
            {
                var msg = await conn.ReceiveAsync(TimeSpan.FromSeconds(Settings.ReportTimeoutSeconds), ct)
                          ?? throw new IOException("The terminal closed the connection before sending the report.");
                switch (msg.Id)
                {
                    case EcrMessageIds.PosError:
                        return new TerminalReport(false, title, [],
                            "Terminal zgłosił błąd (pos_error). Sprawdź połączenie terminala z hostem i spróbuj ponownie.");
                    case EcrMessageIds.PinpadDisplay:
                    case EcrMessageIds.PinpadWait:
                    case EcrMessageIds.PinpadStopDisplay:
                        continue;
                    case EcrMessageIds.PinpadReportFinish:
                    case EcrMessageIds.PinpadCutoffFinish:
                        batch ??= msg.Field(1);
                        count ??= msg.Field(2);
                        tlv.Append(msg.Field(4));
                        if (int.TryParse(msg.Field(0), out var part) && part > 0)
                            continue;
                        return UploadReportBuilder.Build(Tlv.Parse(tlv.ToString()), batch, count) with { Title = title };
                }
            }
        }, ct);

    #endregion

    private async Task<T> Exclusive<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }
}
