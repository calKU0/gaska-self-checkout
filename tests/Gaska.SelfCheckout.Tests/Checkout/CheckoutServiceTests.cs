using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Application.Checkout;
using Gaska.SelfCheckout.Application.Options;
using Gaska.SelfCheckout.Application.Printing;
using Gaska.SelfCheckout.Devices.Polcard.Protocol;
using Gaska.SelfCheckout.Domain.Catalog;
using Gaska.SelfCheckout.Domain.Customers;
using Gaska.SelfCheckout.Domain.Payments;
using Gaska.SelfCheckout.Domain.Sales;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gaska.SelfCheckout.Tests.Checkout;

public class CheckoutServiceTests
{
    private static readonly CardTransaction Approved =
        PosFinishMapper.Map(new PosFinish("0", "0000", new TlvBag(Tlv.Parse(TestData.ApprovedTlv()))), 21m, false, false);

    private static readonly Customer Company =
        new(7, "5261040828", "GŁÓWNY URZĄD STATYSTYCZNY", "5261040828", "Test-Krucza 208", "00-925", "Warszawa", null);

    private static readonly IReadOnlyList<WarehouseAllocation> FromPrimary = [new("MAG1", 1, true)];

    private readonly FakeTerminal _terminal = new();
    private readonly FakeErp _erp = new();
    private readonly FakePrinter _printer = new();
    private readonly FakeLocations _locations = new();
    private readonly InMemoryJournal _journal = new();

    private CheckoutService CreateService(bool askToPrint = false) => new(_terminal, _erp, _printer, _locations, _journal,
        new CardSlipFormatter(), new PickupTicketBuilder(), new HardwareLock(),
        new StaticOptions<KioskOptions>(new()),
        new StaticOptions<CheckoutOptions>(new() { ErpRetries = 1, AskToPrintConfirmation = askToPrint, PrintQuestionSeconds = 5 }),
        new StaticOptions<PickupOptions>(new()), NullLogger<CheckoutService>.Instance);

    private static CartSnapshot Cart(IReadOnlyList<WarehouseAllocation>? allocations = null, decimal quantity = 1) =>
        new([new CartSnapshotLine(1, "WFO-05.219", "Filtr oleju", "szt.", quantity, 21m, 21m * quantity, allocations ?? FromPrimary)]);

    private Task<CheckoutResult> Pay(CheckoutRequest? request = null) =>
        CreateService().PayByCardAsync(request ?? new CheckoutRequest(Cart(), SaleDocumentKind.Receipt, null),
            SilentTerminalInteraction.Instance, new Progress<CheckoutProgress>(), CancellationToken.None);

    [Fact]
    public async Task Approved_payment_creates_receipt_and_prints_confirmation()
    {
        _terminal.SaleResult = Approved;

        var result = await Pay();

        Assert.Equal(CheckoutResultKind.Success, result.Kind);
        Assert.Equal(CheckoutState.Completed, result.Record.State);
        Assert.Equal("PA-1/26/PAR", result.Record.DocumentNumber);
        Assert.True(result.Record.SlipPrinted);
        Assert.NotNull(result.Record.MerchantSlip);
        Assert.Equal(PrintDocumentKind.CardPayment, Assert.Single(_printer.Printed).Kind);
        var request = Assert.Single(_erp.Requests);
        Assert.Equal(result.Record.Id, request.TransactionId);
        Assert.Equal(1, _erp.Prepared);
        // TID + number as in the Fiserv settlement report
        Assert.Equal("TID 87654321 NR 123 AUT A1B2C3 MASTERCARD ************5029", request.PaymentReference);
        Assert.Equal(21m, request.PaidAmount);
    }

    [Theory]
    [InlineData("0", true)]   // "Tak, drukuj"
    [InlineData("1", false)]  // "Nie, dziękuję"
    [InlineData(null, false)] // no answer (customer left)
    public async Task Customer_decides_whether_the_confirmation_is_printed(string? answer, bool printed)
    {
        _terminal.SaleResult = Approved;
        var interaction = new AnsweringInteraction(answer);

        var result = await CreateService(askToPrint: true).PayByCardAsync(new CheckoutRequest(Cart(), SaleDocumentKind.Receipt, null),
            interaction, new Progress<CheckoutProgress>(), CancellationToken.None);

        Assert.Equal(CheckoutResultKind.Success, result.Kind);
        Assert.Equal("Wydrukować potwierdzenie płatności kartą?", Assert.Single(interaction.Questions));
        Assert.Equal(printed, result.Record.SlipPrinted);
        Assert.Equal(printed ? 1 : 0, _printer.Printed.Count(d => d.Kind == PrintDocumentKind.CardPayment));
        Assert.NotNull(result.Record.MerchantSlip); // merchant copy kept electronically either way
    }

    [Fact]
    public async Task Invoice_is_created_for_customer()
    {
        _terminal.SaleResult = Approved;
        _erp.Number = "FS-1/26/FAK";

        var result = await Pay(new CheckoutRequest(Cart(), SaleDocumentKind.Invoice, Company));

        Assert.Equal(CheckoutResultKind.Success, result.Kind);
        var request = Assert.Single(_erp.Requests);
        Assert.Equal(SaleDocumentKind.Invoice, request.Kind);
        Assert.Equal(Company, request.Customer);
        Assert.Equal("FS-1/26/FAK", result.Record.DocumentNumber);
        Assert.Equal(PrintDocumentKind.CardPayment, Assert.Single(_printer.Printed).Kind); // nothing else is printed
    }

    [Fact]
    public async Task Goods_from_secondary_warehouse_print_pickup_ticket()
    {
        _terminal.SaleResult = Approved;
        _locations.Locations[1] = [new ProductLocation(1, "A", "A-01-02", 5)];

        var result = await Pay(new CheckoutRequest(
            Cart([new("MAG1", 2, true), new("MAG2", 1, false)], 3), SaleDocumentKind.Receipt, null));

        Assert.Equal(CheckoutResultKind.Success, result.Kind);
        Assert.True(result.RequiresPickup);
        var ticket = Assert.Single(_printer.Printed, d => d.Kind == PrintDocumentKind.PickupTicket);
        Assert.Contains(ticket.Lines, l => l.Kind == PrintLineKind.QrCode && l.Text == "PA-1/26/PAR");
        var text = ticket.ToPlainText(40);
        Assert.Contains("MAGAZYN A", text);
        Assert.Contains("WFO-05.219", text);
        Assert.Contains("Filtr oleju", text); // name under the code
        Assert.DoesNotContain("A-01-02", text); // no locations by default
    }

    [Fact]
    public async Task Goods_from_primary_warehouse_only_print_no_pickup_ticket()
    {
        _terminal.SaleResult = Approved;

        var result = await Pay();

        Assert.False(result.RequiresPickup);
        Assert.DoesNotContain(_printer.Printed, d => d.Kind == PrintDocumentKind.PickupTicket);
    }

    [Fact]
    public async Task Invoice_for_private_person_is_refused_before_payment()
    {
        _terminal.SaleResult = Approved;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Pay(new CheckoutRequest(Cart(), SaleDocumentKind.Invoice, Company with { IsPrivatePerson = true })));

        Assert.False(_terminal.SaleCalled);
        Assert.Empty(_erp.Requests);
    }

    [Fact]
    public async Task Document_created_despite_failed_confirmation_is_not_issued_twice()
    {
        _terminal.SaleResult = Approved;
        _erp.CommitThrows = true; // e.g. XL printed the receipt, then the call failed
        _erp.Existing = new SalesDocumentResult(true, "PA-7/26/PAR", 7, null, 2034);

        var result = await Pay();

        Assert.Equal(CheckoutResultKind.Success, result.Kind);
        Assert.Equal("PA-7/26/PAR", result.Record.DocumentNumber);
        Assert.Empty(_erp.Requests); // no second document
    }

    [Fact]
    public async Task Receipt_failure_reverses_payment()
    {
        _terminal.SaleResult = Approved;
        _terminal.ReversalResult = Approved with { Kind = CardTransactionKind.Reversal };
        _erp.Fail = true;

        var result = await Pay();

        Assert.Equal(CheckoutResultKind.ReversedAfterError, result.Kind);
        Assert.Equal(CheckoutState.DocumentFailedReversed, result.Record.State);
        Assert.Equal("0000000123", _terminal.LastReversal?.OriginalReceiptNumber);
        Assert.Equal(PrintDocumentKind.CardReversal, Assert.Single(_printer.Printed).Kind);
    }

    [Fact]
    public async Task Failed_reversal_requires_attention()
    {
        _terminal.SaleResult = Approved;
        _terminal.ReversalResult = new CardTransaction { Outcome = CardTransactionOutcome.Declined, ResultCode = "4401" };
        _erp.Fail = true;

        var result = await Pay();

        Assert.Equal(CheckoutResultKind.NeedsAttention, result.Kind);
        Assert.Equal(CheckoutState.NeedsAttention, result.Record.State);
    }

    [Fact]
    public async Task Declined_payment_does_not_create_receipt()
    {
        _terminal.SaleResult = PosFinishMapper.Map(new PosFinish("0", "1000", new TlvBag(Tlv.Parse(TestData.DeclinedTlv()))), 21m, false, false);

        var result = await Pay();

        Assert.Equal(CheckoutResultKind.Declined, result.Kind);
        Assert.Empty(_erp.Requests);
        await Task.Delay(100);
        Assert.Equal(1, _erp.Aborted); // the document prepared during the payment is deleted
        Assert.Equal(PrintDocumentKind.CardDeclined, Assert.Single(_printer.Printed).Kind);
    }

    [Fact]
    public async Task Unknown_payment_result_requires_attention_without_receipt()
    {
        _terminal.SaleResult = new CardTransaction { Outcome = CardTransactionOutcome.Unknown, ResultCode = "UNKNOWN" };

        var result = await Pay();

        Assert.Equal(CheckoutResultKind.NeedsAttention, result.Kind);
        Assert.Empty(_erp.Requests);
    }

    private sealed class FakeTerminal : IPaymentTerminal
    {
        public CardTransaction SaleResult { get; set; } = null!;
        public CardTransaction ReversalResult { get; set; } = null!;
        public CardReversalRequest? LastReversal { get; private set; }

        public bool SaleCalled { get; private set; }

        public Task<CardTransaction> SaleAsync(CardSaleRequest request, ITerminalInteraction interaction, CancellationToken ct = default)
        {
            SaleCalled = true;
            return Task.FromResult(SaleResult);
        }

        public Task<CardTransaction> ReverseAsync(CardReversalRequest request, ITerminalInteraction interaction, CancellationToken ct = default)
        {
            LastReversal = request;
            return Task.FromResult(ReversalResult);
        }

        public Task<CardTransaction?> GetLastTransactionAsync(CancellationToken ct = default) => Task.FromResult<CardTransaction?>(null);
        public Task<TerminalInfo> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(new TerminalInfo(true, "T", "M", 0, "OK"));
        public Task<TerminalReport> CloseDayAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TerminalReport> GetUploadStatusAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CardTransaction> RefundAsync(CardRefundRequest request, ITerminalInteraction interaction, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TerminalReport> TransactionReportAsync(bool resetCounters, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeErp : ISalesDocumentService
    {
        public bool Fail { get; set; }
        public string Number { get; set; } = "PA-1/26/PAR";
        public bool CommitThrows { get; set; }
        public SalesDocumentResult? Existing { get; set; }
        public bool Fiscal { get; set; }
        public List<SalesDocumentRequest> Requests { get; } = [];
        public int Prepared { get; private set; }
        public int Aborted { get; private set; }

        public Task<SalesDocumentResult> CreateAsync(SalesDocumentRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Fail
                ? SalesDocumentResult.Failed("XL 2: error")
                : new SalesDocumentResult(true, Number, 1, null, request.Kind == SaleDocumentKind.Invoice ? 2033 : 2034, Fiscal));
        }

        public Task<SalesDocumentResult?> FindByTransactionIdAsync(Guid transactionId, CancellationToken ct = default) =>
            Task.FromResult(Existing);

        public Task<IPreparedDocument> PrepareAsync(SalesDocumentRequest request, CancellationToken ct = default)
        {
            Prepared++;
            return Task.FromResult<IPreparedDocument>(new Document(this, request));
        }

        private sealed class Document(FakeErp erp, SalesDocumentRequest request) : IPreparedDocument
        {
            public Task<SalesDocumentResult> CommitAsync(decimal paidAmount, string? paymentReference) =>
                erp.CommitThrows
                    ? throw new InvalidOperationException("XL call failed")
                    : erp.CreateAsync(request with { PaidAmount = paidAmount, PaymentReference = paymentReference });

            public Task AbortAsync()
            {
                erp.Aborted++;
                return Task.CompletedTask;
            }
        }

        public Task<ErpStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(new ErpStatus(true, "OK"));
    }

    private sealed class FakePrinter : IReceiptPrinter
    {
        public List<PrintDocument> Printed { get; } = [];
        public int LineWidth => 40;
        public int MaxImageWidth => 432;

        public Task PrintAsync(PrintDocument document, CancellationToken ct = default)
        {
            Printed.Add(document);
            return Task.CompletedTask;
        }

        public Task<PrinterStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(new PrinterStatus(true, true, "OK"));
        public Task StoreImageAsync(int slot, MonochromeImage image, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class AnsweringInteraction(string? answer) : ITerminalInteraction
    {
        public List<string> Questions { get; } = [];
        public void ShowMessage(string text) { }
        public void ClearMessage() { }

        public async Task<TerminalReply> AskAsync(TerminalPrompt prompt, CancellationToken ct)
        {
            Questions.Add(prompt.Text);
            if (answer is not null)
                return TerminalReply.Of(answer);
            await Task.Delay(Timeout.Infinite, ct); // never answers – the kiosk gives up after the timeout
            return TerminalReply.Cancel;
        }
    }

    private sealed class FakeLocations : IWarehouseLocations
    {
        public Dictionary<int, IReadOnlyList<ProductLocation>> Locations { get; } = [];

        public Task<IReadOnlyDictionary<int, IReadOnlyList<ProductLocation>>> GetLocationsAsync(
            IReadOnlyCollection<int> productIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<ProductLocation>>>(Locations);
    }

    private sealed class InMemoryJournal : ITransactionJournal
    {
        private readonly Dictionary<Guid, CheckoutRecord> _records = [];
        private int _counter;

        public Task AddAsync(CheckoutRecord record, CancellationToken ct = default)
        {
            _records[record.Id] = record;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(CheckoutRecord record, CancellationToken ct = default)
        {
            _records[record.Id] = record;
            return Task.CompletedTask;
        }

        public Task<CheckoutRecord?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_records.GetValueOrDefault(id));
        public Task<IReadOnlyList<CheckoutRecord>> GetRecentAsync(int count, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CheckoutRecord>>(_records.Values.ToList());
        public Task<IReadOnlyList<CheckoutRecord>> GetRequiringAttentionAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CheckoutRecord>>([]);
        public Task<int> NextEcrTransactionNumberAsync(CancellationToken ct = default) => Task.FromResult(++_counter);
    }
}
