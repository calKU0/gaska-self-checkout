using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Application.Printing;
using Gaska.SelfCheckout.Devices.Polcard.Protocol;
using Gaska.SelfCheckout.Domain.Payments;

namespace Gaska.SelfCheckout.Tests.Checkout;

public class CardSlipFormatterTests
{
    private static readonly Gaska.SelfCheckout.Domain.Payments.CardTransaction Approved =
        PosFinishMapper.Map(new PosFinish("0", "0000", new TlvBag(Tlv.Parse(TestData.ApprovedTlv()))), 21m, false, false);

    [Fact]
    public void Cardholder_slip_contains_required_elements()
    {
        var doc = new CardSlipFormatter().Format(Approved, SlipCopy.Cardholder, 40, "SCO1");
        var text = doc.ToPlainText(40);

        Assert.Equal(PrintDocumentKind.CardPayment, doc.Kind);
        Assert.Contains("SKLEP TESTOWY SP. Z O.O.", text);
        Assert.Contains("MID: 12345678", text);
        Assert.Contains("TID: 87654321", text);
        Assert.Contains("RACH. NR: 0000000123", text);
        Assert.Contains("************5029", text);
        Assert.Contains("21,00 PLN", text);
        Assert.Contains("KOD PIN ZGODNY", text);
        Assert.Contains("KOD AUTORYZACJI: (1) A1B2C3", text);
        Assert.Contains("AID: A0000000041010", text);
        Assert.Contains("RACHUNEK DLA KLIENTA", text);
        Assert.Contains("CONTACTLESS", text);
        Assert.DoesNotContain("PROSZĘ OBCIĄŻYĆ MOJE KONTO", text);
        Assert.All(text.Split(Environment.NewLine).SelectMany(l => l.Split('\n')), l => Assert.True(l.Length <= 40, l));
    }

    [Fact]
    public void Merchant_slip_contains_debit_statement()
    {
        var text = new CardSlipFormatter().Format(Approved, SlipCopy.Merchant, 40, "SCO1").ToPlainText(40);
        Assert.Contains("PROSZĘ OBCIĄŻYĆ MOJE KONTO", text);
        Assert.Contains("KOPIA DLA SPRZEDAWCY", text);
    }

    [Fact]
    public void Declined_slip_shows_reason()
    {
        var declined = PosFinishMapper.Map(new PosFinish("0", "1000", new TlvBag(Tlv.Parse(TestData.DeclinedTlv()))), 21m, false, false);
        var doc = new CardSlipFormatter().Format(declined, SlipCopy.Cardholder, 40, "SCO1");
        Assert.Equal(PrintDocumentKind.CardDeclined, doc.Kind);
        Assert.Contains("ODMOWA - BRAK SRODKOW", doc.ToPlainText(40));
    }

    [Fact]
    public void Decline_does_not_print_the_response_code()
    {
        // Fiserv's decline receipts show the decline and the host's text only
        var tx = new CardTransaction
        {
            Outcome = CardTransactionOutcome.Declined, ResultCode = "1000", AuthorizationResponseCode = "06", DeclineText = "ODMOWA", Amount = 150.51m
        };

        var text = new CardSlipFormatter().Format(tx, SlipCopy.Cardholder, 40, "SCO1").ToPlainText(40);

        Assert.Contains("TRANSAKCJA ODRZUCONA", text);
        Assert.Contains("ODMOWA", text);
        Assert.DoesNotContain("06", text.Replace("2026", ""));
        Assert.DoesNotContain("KOD ODPOWIEDZI", text);
    }

    [Fact]
    public void Dcc_decline_is_printed_in_english_with_the_conversion()
    {
        // certification test 13, card MAESTRO DECLINE – Fiserv's sample receipt
        var tx = new CardTransaction
        {
            Outcome = CardTransactionOutcome.Declined, Kind = CardTransactionKind.Sale, ResultCode = "1000", AuthorizationResponseCode = "06",
            DeclineText = "ODMOWA", Amount = 15m, CardName = "Maestro", ReadMethod = "C", MaskedPan = "**** **** **** 2436",
            ReceiptNumber = "008013", Timestamp = new DateTime(2026, 10, 29, 14, 6, 20),
            Dcc = new DccInfo(0.247979m, "EUR", "3,72", "8,44", "I have been offered a choice of currencies…", "0")
        };

        var text = new CardSlipFormatter().Format(tx, SlipCopy.Cardholder, 40, "SCO1").ToPlainText(40);

        Assert.Contains("SALE AMOUNT:", text);
        Assert.Contains("1 PLN = 0.247979 EUR", text);
        Assert.Contains("CURRENCY CONVERSION FEE:", text);
        Assert.Contains("TRANSACTION CURRENCY:", text);
        Assert.Contains("TOTAL TRANSACTION AMOUNT:", text);
        Assert.Contains("3,72 EUR", text);
        Assert.Contains("TRANSACTION DECLINE", text);
        Assert.Contains("ODMOWA", text);
        Assert.Contains("RECEIPT NO: 008013", text);
        Assert.Contains("DATE: 2026-10-29", text);
        Assert.Contains("THANKS FOR YOUR PURCHASE!", text);
        Assert.DoesNotContain("I have been offered", text); // the DCC statement belongs to an approved transaction
        foreach (var polish in new[] { "SPRZEDAŻ", "ODRZUCONA", "KASA:", "DATA:", "GODZINA", "RACHUNEK", "KOD ODPOWIEDZI" })
            Assert.DoesNotContain(polish, text);
    }

    // templates of the certification sheet (sheet "printouts")

    [Fact]
    public void Blik_slip_shows_the_code_and_reference_number()
    {
        var tx = new CardTransaction
        {
            Outcome = CardTransactionOutcome.Approved, Amount = 1.10m, CardName = "BLIK", ReadMethod = "K",
            BlikCode = "******", BlikEntryMode = "1", MobileReference = "123456789012", AuthorizationCode = "B00590"
        };

        var text = new CardSlipFormatter().Format(tx, SlipCopy.Cardholder, 40, "SCO1").ToPlainText(40);

        Assert.Contains("PŁATNOŚĆ BLIK:", text);
        Assert.Contains("NR REFERENCYJNY:", text);
        Assert.Contains("123456789012", text);
    }

    [Fact]
    public void Dcc_slip_is_printed_in_english()
    {
        var tx = Approved with { Dcc = new DccInfo(0.249221m, "EUR", "2,49", "8,44", "I have been offered a choice of currencies…", null) };

        var text = new CardSlipFormatter().Format(tx, SlipCopy.Merchant, 40, "SCO1").ToPlainText(40);

        Assert.Contains("SALE AMOUNT:", text);
        Assert.Contains("EXCHANGE RATE:", text);
        Assert.Contains("1 PLN = 0.249221 EUR", text);
        Assert.Contains("CURRENCY CONVERSION FEE:", text);
        Assert.Contains("TOTAL TRANSACTION AMOUNT:", text);
        Assert.Contains("PLEASE DEBIT MY ACCOUNT", text);
        Assert.Contains("I have been offered", text);
        Assert.Contains("AUTH CODE: (1) A1B2C3", text);
        Assert.DoesNotContain("SPRZEDAŻ", text);
    }

    [Fact]
    public void Reversal_slip_names_the_reversed_receipt()
    {
        var tx = Approved with { Kind = CardTransactionKind.Reversal, OriginalReceiptNumber = "005011" };

        var doc = new CardSlipFormatter().Format(tx, SlipCopy.Cardholder, 40, "SCO1");
        var text = doc.ToPlainText(40);

        Assert.Equal(PrintDocumentKind.CardReversal, doc.Kind);
        Assert.Contains("UNIEWAŻNIENIE", text);
        Assert.Contains("DLA RACHUNKU: 005011", text);
        Assert.Contains("TRANSAKCJA UNIEWAŻNIONA", text);
    }

    [Fact]
    public void Reversal_slip_has_no_cardholder_verification()
    {
        // the terminal answers a reversal with 1B08 = 0011000 (PIN + signature flags of the original sale)
        var tx = Approved with { Kind = CardTransactionKind.Reversal, OriginalReceiptNumber = "008018", CvmList = "0011000" };

        var text = new CardSlipFormatter().Format(tx, SlipCopy.Merchant, 40, "SCO1").ToPlainText(40);

        Assert.DoesNotContain("KOD PIN ZGODNY", text);
        Assert.DoesNotContain("PODPIS", text);
        Assert.Contains("KOD AUTORYZACJI", text);
    }

    [Fact]
    public void Refund_merchant_slip_asks_to_credit_the_account()
    {
        var tx = Approved with { Kind = CardTransactionKind.Refund };

        var text = new CardSlipFormatter().Format(tx, SlipCopy.Merchant, 40, "SCO1").ToPlainText(40);

        Assert.Contains("UZNANIE:", text);
        Assert.Contains("PROSZĘ UZNAĆ MOJE KONTO", text);
        Assert.DoesNotContain("KOD PIN ZGODNY", text);
    }
}
