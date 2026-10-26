using Gaska.SelfCheckout.Devices.Polcard.Protocol;
using Gaska.SelfCheckout.Domain.Payments;

namespace Gaska.SelfCheckout.Tests.Polcard;

public class PosFinishMapperTests
{
    [Fact]
    public void Maps_approved_transaction()
    {
        var tx = PosFinishMapper.Map(new PosFinish("0", "0000", new TlvBag(Tlv.Parse(TestData.ApprovedTlv()))), 21m, false, false);

        Assert.Equal(CardTransactionOutcome.Approved, tx.Outcome);
        Assert.Equal(21.00m, tx.Amount);
        Assert.Equal(new DateTime(2026, 10, 7, 13, 45, 12), tx.Timestamp);
        Assert.Equal("MASTERCARD", tx.CardName);
        Assert.Equal("************5029", tx.MaskedPan);
        Assert.Equal("A1B2C3", tx.AuthorizationCode);
        Assert.Equal("0000000123", tx.ReceiptNumber);
        Assert.Equal("A0000000041010", tx.Aid);
        Assert.Equal("7", tx.EcrTransactionId);
        Assert.True(tx.IsContactless);
        Assert.True(tx.PinVerified);
    }

    [Fact]
    public void Maps_declined_transaction_with_host_text()
    {
        var tx = PosFinishMapper.Map(new PosFinish("0", "1000", new TlvBag(Tlv.Parse(TestData.DeclinedTlv()))), 21m, false, false);
        Assert.Equal(CardTransactionOutcome.Declined, tx.Outcome);
        Assert.Equal("ODMOWA - BRAK SRODKOW", tx.ResultDescription);
    }

    [Fact]
    public void Maps_aborted_transaction_as_cancelled()
    {
        var tx = PosFinishMapper.Map(new PosFinish("8", "4407", new TlvBag([])), 1m, abortRequested: true, reversal: false);
        Assert.Equal(CardTransactionOutcome.Cancelled, tx.Outcome);
        Assert.Equal(1m, tx.Amount);
    }

    [Fact]
    public void Card_not_read_is_a_cancelled_attempt_even_without_abort_from_the_kiosk()
    {
        // red key on the PIN pad before a card was read: |2810000|000|8|4407|||
        var tx = PosFinishMapper.Map(new PosFinish("8", "4407", new TlvBag([])), 1m, abortRequested: false, reversal: false);

        Assert.Equal(CardTransactionOutcome.Cancelled, tx.Outcome);
        Assert.Equal("Nie odczytano karty (przerwano lub upłynął czas)", tx.ResultDescription);
    }
}
