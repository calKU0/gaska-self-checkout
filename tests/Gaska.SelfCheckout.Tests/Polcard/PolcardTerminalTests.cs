using System.Threading.Channels;
using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Devices.Polcard;
using Gaska.SelfCheckout.Devices.Polcard.Protocol;
using Gaska.SelfCheckout.Devices.Polcard.Transport;
using Gaska.SelfCheckout.Domain.Payments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Tests.Polcard;

/// <summary>Scenariusze komunikacji z terminalem odtwarzane na atrapie połączenia.</summary>
public class PolcardTerminalTests
{
    private const string Hello = "|2840100|0|0000||87654321|0000||";

    [Fact]
    public async Task Sale_answers_blik_question_and_assembles_multipart_pos_finish()
    {
        var tlv = TestData.ApprovedTlv("5");
        var half = tlv.Length / 2;
        var factory = new ScriptedFactory(
            [Hello],
            [
                "|2830110|60|",
                "|2820010|024|Czy uzyc platnosci Blik?|0|1|120|",
                "<WAIT_ECR_DISPLAY>",
                $"|2810000|001|0|0000|{half:D4}|{tlv[..half]}|",
                $"|2810000|000|0|0000|{tlv.Length - half:D4}|{tlv[half..]}|"
            ]);
        var ui = new ScriptedInteraction(TerminalReply.Of("1"));
        var terminal = Create(factory);

        var tx = await terminal.SaleAsync(new CardSaleRequest(21m, "5"), ui);

        Assert.Equal(CardTransactionOutcome.Approved, tx.Outcome);
        Assert.Equal("0000000123", tx.ReceiptNumber);
        Assert.Equal("Czy uzyc platnosci Blik?", Assert.Single(ui.Prompts).Text);
        var sent = factory.Sent.Select(EcrMessage.Parse).ToList();
        var start = sent.Single(m => m.Id == EcrMessageIds.EcrStartExtended);
        Assert.Equal("00", start.Field(0));
        Assert.Equal("000000002100", start.Field(2));
        Assert.Equal("5", start.Field(21)); // pole 23: ECR_TRANSACTION_ID
        Assert.Contains(sent, m => m.ToWire() == "|2821000|01|001|1|");
    }

    [Fact]
    public async Task Cancellation_sends_abort_and_returns_cancelled()
    {
        var factory = new ScriptedFactory([Hello], ["<WAIT_ECR_DISPLAY>", "|2810000|000|8|4407|||"]);
        var terminal = Create(factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var tx = await terminal.SaleAsync(new CardSaleRequest(1m, "9"), new ScriptedInteraction(TerminalReply.Cancel), cts.Token);

        Assert.Equal(CardTransactionOutcome.Cancelled, tx.Outcome);
        Assert.Contains("|2821000|99|001|0|", factory.Sent);
    }

    [Fact]
    public async Task Lost_connection_recovers_result_from_last_transaction()
    {
        var factory = new ScriptedFactory([Hello], ["<CLOSE>"],
            [$"|2810200|000|0|0000|{TestData.ApprovedTlv("12").Length:D4}|{TestData.ApprovedTlv("12")}|"]);
        var terminal = Create(factory);

        var tx = await terminal.SaleAsync(new CardSaleRequest(21m, "12"), new ScriptedInteraction(TerminalReply.Cancel));

        Assert.Equal(CardTransactionOutcome.Approved, tx.Outcome);
        Assert.Contains("|2811211|", factory.Sent);
    }

    [Fact]
    public async Task Refund_starts_transaction_type_20()
    {
        var factory = new ScriptedFactory([Hello], ["|2810000|000|0|0000|0000||"]);
        var terminal = Create(factory);

        await terminal.RefundAsync(new CardRefundRequest(25.99m, "14"), new ScriptedInteraction(TerminalReply.Cancel));

        var start = factory.Sent.Select(EcrMessage.Parse).Single(m => m.Id == EcrMessageIds.EcrStartExtended);
        Assert.Equal("20", start.Field(0));
        Assert.Equal("000000002599", start.Field(2));
        Assert.Equal("0", start.Field(14)); // pole 16: REVERSAL FLAG
    }

    [Fact]
    public async Task Informational_report_does_not_upload_transactions()
    {
        var factory = new ScriptedFactory([Hello], ["|2880500|000|001|000000|0000||"]);
        var terminal = Create(factory);

        await terminal.TransactionReportAsync(resetCounters: true);

        Assert.Contains("|2881311|2|3|", factory.Sent);
    }

    [Fact]
    public async Task Unreachable_terminal_does_not_start_the_transaction()
    {
        var terminal = Create(new UnreachableFactory());

        var tx = await terminal.SaleAsync(new CardSaleRequest(13.33m, "15"), new ScriptedInteraction(TerminalReply.Cancel));

        Assert.Equal(CardTransactionOutcome.Cancelled, tx.Outcome);
        Assert.Equal(CardTransaction.NoConnectionCode, tx.ResultCode);
    }

    private sealed class UnreachableFactory : IEcrConnectionFactory
    {
        public Task<IEcrConnection> ConnectAsync(CancellationToken ct) => throw new TimeoutException("No connection to the terminal.");
    }

    private static PolcardTerminal Create(IEcrConnectionFactory factory) =>
        new(factory, new StaticOptions<PolcardOptions>(new PolcardOptions { TransactionIdleTimeoutSeconds = 5, ResponseTimeoutSeconds = 5 }), NullLogger<PolcardTerminal>.Instance);

    private sealed class ScriptedInteraction(TerminalReply reply) : ITerminalInteraction
    {
        public List<TerminalPrompt> Prompts { get; } = [];
        public void ShowMessage(string text) { }
        public void ClearMessage() { }
        public Task<TerminalReply> AskAsync(TerminalPrompt prompt, CancellationToken ct)
        {
            Prompts.Add(prompt);
            return Task.FromResult(reply);
        }
    }

    /// <summary>Każde kolejne połączenie dostaje kolejny skrypt odpowiedzi terminala.</summary>
    private sealed class ScriptedFactory(params string[][] scripts) : IEcrConnectionFactory
    {
        private int _index;
        public List<string> Sent { get; } = [];

        public Task<IEcrConnection> ConnectAsync(CancellationToken ct) =>
            Task.FromResult<IEcrConnection>(new ScriptedConnection(scripts[Math.Min(_index++, scripts.Length - 1)], Sent));
    }

    private sealed class ScriptedConnection(string[] script, List<string> sent) : IEcrConnection
    {
        private readonly Channel<string> _ecrDisplays = Channel.CreateUnbounded<string>();
        private int _pos;

        public Task SendAsync(EcrMessage message, CancellationToken ct)
        {
            lock (sent) sent.Add(message.ToWire());
            if (message.Id == EcrMessageIds.EcrDisplay)
                _ecrDisplays.Writer.TryWrite(message.ToWire());
            return Task.CompletedTask;
        }

        public async Task<EcrMessage?> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
        {
            while (_pos < script.Length)
            {
                var step = script[_pos++];
                switch (step)
                {
                    case "<CLOSE>":
                        return null;
                    case "<WAIT_ECR_DISPLAY>":
                        await _ecrDisplays.Reader.ReadAsync(ct);
                        continue;
                    default:
                        return EcrMessage.Parse(step);
                }
            }

            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
