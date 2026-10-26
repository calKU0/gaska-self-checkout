using Gaska.SelfCheckout.Devices.Polcard.Protocol;

namespace Gaska.SelfCheckout.Tests.Polcard;

public class EcrMessageTests
{
    [Fact]
    public void Serializes_with_leading_and_trailing_separators()
    {
        var msg = new EcrMessage("2821000", "99", "001", "0");
        Assert.Equal("|2821000|99|001|0|", msg.ToWire());
    }

    [Fact]
    public void Parses_message_with_empty_fields()
    {
        var msg = EcrMessage.Parse("|2810000|000|8|4407|||");
        Assert.Equal("2810000", msg.Id);
        Assert.Equal("000", msg.Field(0));
        Assert.Equal("8", msg.Field(1));
        Assert.Equal("4407", msg.Field(2));
        Assert.Equal("", msg.Field(4));
    }

    [Theory]
    [InlineData(1.00, "000000000100")]
    [InlineData(418.00, "000000041800")]
    [InlineData(0.05, "000000000005")]
    public void Formats_amount_as_n12_minor_units(decimal amount, string expected) =>
        Assert.Equal(expected, EcrMessage.Amount(amount));

    [Fact]
    public void Parses_pinpad_display_yes_no()
    {
        var d = PinpadDisplay.From(EcrMessage.Parse("|2820010|024|Czy uzyc platnosci Blik?|0|1|120|"));
        Assert.Equal("Czy uzyc platnosci Blik?", d.Text);
        Assert.Equal(1, d.KeyboardOptions);
        Assert.Equal(120, d.TimeoutSeconds);
    }

    [Fact]
    public void Parses_mobile_payment_options()
    {
        var d = new PinpadDisplay("<1>BLIK$<2>IKO$<3>MOBILE$", 0, 5, null);
        var (header, options) = d.ParseOptions();
        Assert.Null(header);
        Assert.Equal(["1", "2", "3"], options.Select(o => o.Value));
        Assert.Equal("BLIK", options[0].Label);
    }

    [Fact]
    public void Parses_dcc_currency_selection()
    {
        var d = new PinpadDisplay("<D1>Wybierz walutę transakcji:$<1>PLN$<2>EUR$", 0, 8, null);
        var (header, options) = d.ParseOptions();
        Assert.Equal("Wybierz walutę transakcji:", header);
        Assert.Equal(["PLN", "EUR"], options.Select(o => o.Label));
        Assert.Equal("Wybierz walutę transakcji:", d.DisplayText);
    }

    [Fact]
    public void Decodes_hex_encoded_display_text()
    {
        var hex = Convert.ToHexString(EcrEncoding.Text.GetBytes("PIN OK"));
        Assert.Equal("PIN OK", PinpadDisplay.DecodeText("006", hex));
        Assert.Equal("PIN OK", PinpadDisplay.DecodeText("006", "PIN OK"));
    }
}
