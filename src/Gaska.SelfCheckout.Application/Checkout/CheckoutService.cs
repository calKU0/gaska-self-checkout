using System.Text.Json;
using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Application.Options;
using Gaska.SelfCheckout.Application.Printing;
using Gaska.SelfCheckout.Domain.Customers;
using Gaska.SelfCheckout.Domain.Payments;
using Gaska.SelfCheckout.Domain.Sales;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Application.Checkout;

public enum CheckoutStage
{
    Preparing,
    WaitingForPayment,
    CreatingDocument,
    PrintingConfirmation,
    Reversing,
    Completed,
    Failed
}

public sealed record CheckoutProgress(CheckoutStage Stage, string Message);

/// <summary>What the customer buys and which document they want.</summary>
public sealed record CheckoutRequest(CartSnapshot Cart, SaleDocumentKind DocumentKind, Customer? Customer);

public enum CheckoutResultKind
{
    Success,
    Declined,
    Cancelled,
    /// <summary>Payment was taken and reversed because the document could not be created.</summary>
    ReversedAfterError,
    /// <summary>Staff assistance is required.</summary>
    NeedsAttention
}

public sealed record CheckoutResult(
    CheckoutResultKind Kind,
    string Message,
    CheckoutRecord Record,
    CardTransaction? Card = null,
    bool RequiresPickup = false)
{
    public bool IsSuccess => Kind == CheckoutResultKind.Success;
}

/// <summary>
/// Purchase process: payment on the terminal → ERP document (receipts and fiscal invoices are printed by the ERP,
/// other invoices go to KSeF only) → card payment confirmation → optional pick-up ticket.
/// Every step is written to the journal so the state of a transaction can always be determined after a failure.
/// </summary>
public sealed class CheckoutService(
    IPaymentTerminal terminal,
    ISalesDocumentService erp,
    IReceiptPrinter printer,
    IWarehouseLocations warehouseLocations,
    ITransactionJournal journal,
    CardSlipFormatter slipFormatter,
    PickupTicketBuilder pickupBuilder,
    HardwareLock hardwareLock,
    IOptionsMonitor<KioskOptions> kioskOptions,
    IOptionsMonitor<CheckoutOptions> checkoutOptions,
    IOptionsMonitor<PickupOptions> pickupOptions,
    ILogger<CheckoutService> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task<CheckoutResult> PayByCardAsync(
        CheckoutRequest checkout,
        ITerminalInteraction interaction,
        IProgress<CheckoutProgress> progress,
        CancellationToken ct)
    {
        var kiosk = kioskOptions.CurrentValue;
        var options = checkoutOptions.CurrentValue;
        var cart = checkout.Cart;
        if (cart.Lines.Count == 0 || cart.Total <= 0)
            throw new InvalidOperationException("Koszyk jest pusty.");
        if (cart.Total > kiosk.MaxTransactionAmount)
            throw new InvalidOperationException($"Kwota przekracza limit kasy ({kiosk.MaxTransactionAmount:N2} zł).");
        // checked before the card is charged (XL would refuse the document only after the payment)
        if (checkout.DocumentKind == SaleDocumentKind.Invoice && checkout.Customer is not { CanReceiveInvoice: true })
            throw new InvalidOperationException("Fakturę na kasie wystawiamy tylko firmie z numerem NIP.");

        using var deviceLease = await hardwareLock.AcquireAsync(ct);

        var record = new CheckoutRecord
        {
            State = CheckoutState.Started,
            DocumentKind = checkout.DocumentKind.ToString(),
            CustomerAcronym = checkout.Customer?.Acronym,
            CustomerId = checkout.Customer?.Id,
            Amount = cart.Total,
            CartJson = JsonSerializer.Serialize(cart.Lines, Json),
            EcrTransactionId = (await journal.NextEcrTransactionNumberAsync(ct)).ToString()
        };
        await journal.AddAsync(record, ct);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CheckoutId"] = record.Id });
        logger.LogInformation("Transaction {Id} started: {Kind}, amount {Amount}, {Count} lines, customer {Customer}",
            record.Id, checkout.DocumentKind, cart.Total, cart.Lines.Count, checkout.Customer?.Acronym ?? "-");

        // The ERP document is prepared in parallel with the payment (creating it in XL takes several seconds).
        var prepared = options.PrepareDocumentDuringPayment ? PrepareDocument(record, checkout) : null;

        // 1. Payment
        progress.Report(new(CheckoutStage.WaitingForPayment, "Postępuj zgodnie z instrukcjami na terminalu"));
        CardTransaction card;
        try
        {
            card = await terminal.SaleAsync(new CardSaleRequest(cart.Total, record.EcrTransactionId!), interaction, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Terminal communication failure");
            card = new CardTransaction { Outcome = CardTransactionOutcome.Unknown, Amount = cart.Total, ResultDescription = ex.Message };
        }

        record.CardJson = JsonSerializer.Serialize(card, Json);
        record.CardResultCode = card.ResultCode;
        record.CardReceiptNumber = card.ReceiptNumber;

        if (!card.IsApproved && prepared is not null)
            _ = AbortPrepared(prepared);

        if (card.Outcome == CardTransactionOutcome.Unknown)
        {
            record.State = CheckoutState.NeedsAttention;
            record.Error = "No payment result from the terminal. " + card.ResultDescription;
            await Save(record);
            return new(CheckoutResultKind.NeedsAttention,
                "Nie udało się potwierdzić wyniku płatności. Prosimy o kontakt z obsługą.", record, card);
        }

        if (!card.IsApproved)
        {
            record.State = card.Outcome == CardTransactionOutcome.Cancelled ? CheckoutState.PaymentCancelled : CheckoutState.PaymentDeclined;
            record.Error = card.ResultDescription;
            await Save(record);
            if (card.Outcome == CardTransactionOutcome.Declined && options.PrintDeclinedSlip && card.RawTags.Count > 0)
                await TryPrintSlips(record, card, merchantCopy: false);
            return card.Outcome == CardTransactionOutcome.Cancelled
                ? new(CheckoutResultKind.Cancelled, card.ResultCode == CardTransaction.NoConnectionCode
                    ? "Brak komunikacji z terminalem płatniczym. Nie pobrano żadnych środków – poproś obsługę o pomoc."
                    : "Nie pobrano żadnych środków. Możesz spróbować ponownie lub wrócić do koszyka.", record, card)
                : new(CheckoutResultKind.Declined, card.ResultDescription ?? "Transakcja odrzucona.", record, card);
        }

        record.State = CheckoutState.PaymentApproved;
        await Save(record);
        logger.LogInformation("Payment approved, receipt no {Receipt}, auth code {Auth}", card.ReceiptNumber, card.AuthorizationCode);

        // 2. ERP document
        progress.Report(new(CheckoutStage.CreatingDocument, checkout.DocumentKind == SaleDocumentKind.Invoice
            ? "Płatność zaakceptowana. Wystawiamy fakturę…"
            : "Płatność zaakceptowana. Drukujemy paragon…"));
        // pick-up ticket glued under the fiscal receipt (no printer header, one strip)
        var pickup = cart.RequiresPickup;
        var attachPickup = pickup && checkout.DocumentKind == SaleDocumentKind.Receipt;
        if (attachPickup)
            attachPickup = await TrySetReceiptContinuation(true);

        var document = await CreateDocument(record, checkout, card, prepared);

        if (!document.Success)
        {
            if (attachPickup)
                await TrySetReceiptContinuation(false);
            logger.LogError("Document not created: {Error}. Reversing the payment.", document.Error);
            record.Error = document.Error;
            progress.Report(new(CheckoutStage.Reversing, "Wystąpił problem z dokumentem sprzedaży. Anulujemy płatność…"));
            var reversed = await TryReverse(record, card, interaction);
            record.State = reversed ? CheckoutState.DocumentFailedReversed : CheckoutState.NeedsAttention;
            await Save(record);
            return reversed
                ? new(CheckoutResultKind.ReversedAfterError,
                    "Nie udało się wystawić dokumentu sprzedaży. Płatność została anulowana – środki nie zostaną pobrane.", record, card)
                : new(CheckoutResultKind.NeedsAttention,
                    "Nie udało się wystawić dokumentu sprzedaży ani anulować płatności. Prosimy o kontakt z obsługą.", record, card);
        }

        record.State = CheckoutState.DocumentCreated;
        record.DocumentNumber = document.DocumentNumber;
        record.DocumentId = document.DocumentId;
        await Save(record);

        // 3. Pick-up ticket for goods from a non-primary warehouse – right after the receipt when attached to it
        if (pickup && attachPickup)
        {
            // the receipt already ended without a cut – the ticket itself has to end normally (feed + cut)
            await TrySetReceiptContinuation(false);
            progress.Report(new(CheckoutStage.PrintingConfirmation, "Drukujemy informację o odbiorze towaru…"));
            await TryPrintPickupTicket(record, cart, document.DocumentNumber ?? record.Id.ToString()[..8]);
        }

        // 4. Card payment confirmation (non-fiscal printout on the fiscal printer) – on request of the customer
        var printCardholderCopy = await AskToPrintConfirmation(card, interaction, progress, options);
        if (printCardholderCopy)
            progress.Report(new(CheckoutStage.PrintingConfirmation, "Drukujemy potwierdzenie płatności kartą…"));
        await TryPrintSlips(record, card, merchantCopy: options.PrintMerchantCopy, cardholderCopy: printCardholderCopy);

        if (pickup && !attachPickup)
        {
            progress.Report(new(CheckoutStage.PrintingConfirmation, "Drukujemy informację o odbiorze towaru…"));
            await TryPrintPickupTicket(record, cart, document.DocumentNumber ?? record.Id.ToString()[..8]);
        }

        record.State = CheckoutState.Completed;
        await Save(record);
        progress.Report(new(CheckoutStage.Completed, "Dziękujemy za zakupy!"));
        logger.LogInformation("Transaction completed, document {Doc}", record.DocumentNumber);
        return new(CheckoutResultKind.Success, "Dziękujemy za zakupy!", record, card, pickup);
    }

    private static SalesDocumentRequest BuildRequest(CheckoutRecord record, CheckoutRequest checkout, decimal amount, string? reference) =>
        new(record.Id, checkout.DocumentKind, checkout.Customer, checkout.Cart, SalePaymentMethod.Card, amount, reference);

    private Task<IPreparedDocument?> PrepareDocument(CheckoutRecord record, CheckoutRequest checkout) =>
        Task.Run(async () =>
        {
            try
            {
                return await erp.PrepareAsync(BuildRequest(record, checkout, checkout.Cart.Total, null));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not prepare the document during payment – it will be created after authorization");
                return (IPreparedDocument?)null;
            }
        });

    private async Task AbortPrepared(Task<IPreparedDocument?> prepared)
    {
        try
        {
            if (await prepared is { } document)
                await document.AbortAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete the prepared document");
        }
    }

    /// <summary>
    /// Card payment description stored in the ERP document: "TID 12345678 NR 5007 AUT B00582 VISA ************1234".
    /// TID + NR identify the transaction in the Fiserv settlement report (each terminal numbers its transactions on its
    /// own; the report shows the number without leading zeros).
    /// </summary>
    public static string PaymentReference(CardTransaction card) =>
        string.Join(" ", new[]
            {
                string.IsNullOrEmpty(card.TerminalId) ? null : "TID " + card.TerminalId,
                string.IsNullOrEmpty(card.ReceiptNumber) ? null : "NR " + (card.ReceiptNumber.TrimStart('0') is { Length: > 0 } n ? n : "0"),
                string.IsNullOrEmpty(card.AuthorizationCode) ? null : "AUT " + card.AuthorizationCode,
                card.CardName, card.MaskedPan
            }
            .Where(s => !string.IsNullOrEmpty(s)));

    private async Task<SalesDocumentResult> CreateDocument(CheckoutRecord record, CheckoutRequest checkout, CardTransaction card, Task<IPreparedDocument?>? prepared)
    {
        if (prepared is not null && await prepared is { } document)
        {
            try
            {
                var committed = await document.CommitAsync(card.Amount, PaymentReference(card));
                if (committed.Success)
                    return committed;
                logger.LogWarning("Confirming the prepared document failed: {Error} – retrying", committed.Error);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Confirming the prepared document failed – retrying");
            }
        }

        return await CreateDocumentWithRetry(record, checkout, card);
    }

    private async Task<SalesDocumentResult> CreateDocumentWithRetry(CheckoutRecord record, CheckoutRequest checkout, CardTransaction card)
    {
        var request = BuildRequest(record, checkout, card.Amount, PaymentReference(card));
        var result = SalesDocumentResult.Failed("Not attempted.");
        var retries = checkoutOptions.CurrentValue.ErpRetries;
        for (var attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                // A failed attempt (or a failed confirmation of the prepared document) may have created and printed
                // the document anyway – never issue a second fiscal receipt for one payment.
                var existing = await erp.FindByTransactionIdAsync(record.Id, CancellationToken.None);
                if (existing is { Success: true })
                    return existing;
                if (existing is { DocumentId: { } buffered } && record.Error?.Contains($"GID {buffered}") != true)
                {
                    // an earlier attempt left an unconfirmed document (its deletion failed) – it holds the stock
                    logger.LogWarning("Document {Id} of this transaction is left in the XL buffer", buffered);
                    AppendError(record, $"Unconfirmed document (GID {buffered}) left in the XL buffer – delete it in XL");
                }

                // The payment is already taken – the customer can no longer cancel the document.
                result = await erp.CreateAsync(request, CancellationToken.None);
                if (result.Success)
                    return result;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Exception while creating the document (attempt {Attempt})", attempt + 1);
                result = SalesDocumentResult.Failed(ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(1 + attempt), CancellationToken.None);
        }

        return await erp.FindByTransactionIdAsync(record.Id, CancellationToken.None) is { Success: true } found ? found : result;
    }

    private async Task<bool> TryReverse(CheckoutRecord record, CardTransaction card, ITerminalInteraction interaction)
    {
        try
        {
            var reversal = await terminal.ReverseAsync(
                new CardReversalRequest(card.Amount, card.ReceiptNumber, (await journal.NextEcrTransactionNumberAsync()).ToString()),
                interaction, CancellationToken.None);
            if (reversal.IsApproved)
            {
                await TryPrintSlips(record, reversal with { Kind = CardTransactionKind.Reversal }, merchantCopy: false);
                return true;
            }

            logger.LogError("Reversal declined: {Code} {Description}", reversal.ResultCode, reversal.ResultDescription);
            AppendError(record, $"Reversal declined: {reversal.ResultCode} {reversal.ResultDescription}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Payment reversal failed");
            AppendError(record, "Reversal failed: " + ex.Message);
        }

        return false;
    }

    private async Task<bool> TrySetReceiptContinuation(bool enabled)
    {
        try
        {
            await printer.ContinueAfterNextFiscalDocumentAsync(enabled);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Receipt continuation could not be {Mode}", enabled ? "enabled" : "disabled");
            return false;
        }
    }

    /// <summary>Asks the customer whether to print the payment confirmation (always yes for DCC or when not configured).</summary>
    private async Task<bool> AskToPrintConfirmation(CardTransaction card, ITerminalInteraction interaction,
        IProgress<CheckoutProgress> progress, CheckoutOptions options)
    {
        if (!options.AskToPrintConfirmation || card.Dcc is not null)
            return true;

        progress.Report(new(CheckoutStage.PrintingConfirmation, "Płatność przyjęta"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, options.PrintQuestionSeconds)));
        try
        {
            var reply = await interaction.AskAsync(new TerminalPrompt(TerminalPromptKind.YesNo, "Wydrukować potwierdzenie płatności kartą?",
                [new("0", "Tak, drukuj"), new("1", "Nie, dziękuję")]), timeout.Token);
            var print = reply is { Cancelled: false, Value: "0" };
            logger.LogInformation("Customer {Answer} the payment confirmation printout", print ? "requested" : reply.Cancelled ? "did not answer – skipped" : "declined");
            return print;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task TryPrintSlips(CheckoutRecord record, CardTransaction card, bool merchantCopy, bool cardholderCopy = true)
    {
        var width = printer.LineWidth;
        var station = kioskOptions.CurrentValue.StationId;
        var merchant = slipFormatter.Format(card, SlipCopy.Merchant, width, station);
        record.MerchantSlip = merchant.ToPlainText(width); // kept electronically in the journal
        try
        {
            if (cardholderCopy)
                await printer.PrintAsync(slipFormatter.Format(card, SlipCopy.Cardholder, width, station));
            if (merchantCopy)
                await printer.PrintAsync(merchant);
            record.SlipPrinted = cardholderCopy;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Card payment confirmation could not be printed");
            AppendError(record, "Confirmation print: " + ex.Message);
        }

        await Save(record);
    }

    private async Task TryPrintPickupTicket(CheckoutRecord record, CartSnapshot cart, string documentNumber)
    {
        try
        {
            var productIds = cart.Lines.Where(l => l.Allocations.Any(a => !a.IsPrimary)).Select(l => l.ProductId).ToList();
            var locations = await warehouseLocations.GetLocationsAsync(productIds);
            var ticket = pickupBuilder.Build(documentNumber, cart, locations, pickupOptions.CurrentValue, printer.LineWidth);
            await printer.PrintAsync(ticket);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pick-up ticket could not be printed");
            AppendError(record, "Pick-up ticket print: " + ex.Message);
        }
    }

    private static void AppendError(CheckoutRecord record, string error) =>
        record.Error = string.IsNullOrEmpty(record.Error) ? error : record.Error + " | " + error;

    private Task Save(CheckoutRecord record)
    {
        record.UpdatedAt = DateTime.Now;
        return journal.UpdateAsync(record, CancellationToken.None);
    }
}
