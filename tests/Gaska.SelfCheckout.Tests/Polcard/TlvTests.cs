using Gaska.SelfCheckout.Devices.Polcard.Protocol;

namespace Gaska.SelfCheckout.Tests.Polcard;

public class TlvTests
{
    [Fact]
    public void Parses_polcard_tag_from_specification_example()
    {
        // przykład ze specyfikacji: 1A05 "************3732"
        var items = Tlv.Parse("1A0500102A2A2A2A2A2A2A2A2A2A2A2A33373332");
        var item = Assert.Single(items);
        Assert.Equal("1A05", item.Tag);
        Assert.Equal("************3732", item.Text);
    }

    [Fact]
    public void Parses_mixed_emv_and_polcard_tags()
    {
        var bag = new TlvBag(Tlv.Parse(TestData.ApprovedTlv()));
        Assert.Equal("134512", bag.Hex("9F21"));
        Assert.Equal("A0000000041010", bag.Hex("4F"));
        Assert.Equal("000000002100", bag.Hex("9F02"));
        Assert.Equal("SKLEP TESTOWY SP. Z O.O.", bag.Text("1A1C"));
        Assert.Equal("0000000123", bag.Text("1B01"));
    }

    [Fact]
    public void Decodes_terminal_hello_response_with_cp1250()
    {
        // pos_hello_extended response fragment in CP1250 (0xA3 = 'Ł', 0xA5 = 'Ą')
        var bag = new TlvBag(Tlv.Parse("1A1C000FA3A54B412053502E205A204F2E4F2E1C080001331C2C000131"));
        Assert.Equal("ŁĄKA SP. Z O.O.", bag.Text("1A1C"));
        Assert.Equal("3", bag.Text("1C08"));
    }

    [Fact]
    public void Builds_polcard_block()
    {
        Assert.Equal("1C19000730303437353133", Tlv.BuildPolcard([("1C19", "0047513")]));
    }
}
