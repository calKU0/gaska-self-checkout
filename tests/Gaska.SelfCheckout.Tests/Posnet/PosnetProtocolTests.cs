using System.Text;
using Gaska.SelfCheckout.Devices.Posnet;

namespace Gaska.SelfCheckout.Tests.Posnet;

public class PosnetProtocolTests
{
    [Fact]
    public void Crc_matches_specification_check_value() =>
        Assert.Equal(0x31C3, PosnetProtocol.Crc16(Encoding.ASCII.GetBytes("123456789")));

    [Fact]
    public void Frame_matches_specification_example()
    {
        // [STX]trinit[TAB]bm0[TAB]#4825[ETX]
        var frame = PosnetProtocol.BuildFrame("trinit", [("bm", "0")]);
        Assert.Equal("\u0002trinit\tbm0\t#4825\u0003", Encoding.ASCII.GetString(frame));
    }

    [Fact]
    public void Parses_response_values_and_errors()
    {
        var ok = PosnetProtocol.ParseResponse(Encoding.ASCII.GetBytes("sdev\tds0\tcpT\tqeT\tpeN\ttl0\t#0C8C"));
        Assert.Equal("sdev", ok.Command);
        Assert.Equal("0", ok.Get("ds"));
        Assert.Null(ok.Error);

        var err = PosnetProtocol.ParseResponse(Encoding.ASCII.GetBytes("ERR\t?5\tcmrtcget\t#B2BF"));
        Assert.True(err.IsFrameError);
        Assert.Equal(5, err.Error);
    }

    [Fact]
    public void Sanitize_escapes_ampersand_and_removes_control_chars() =>
        Assert.Equal("A&&B C", PosnetProtocol.Sanitize("A&B\tC".Replace("\t", " ")));

    [Fact]
    public void Mazovia_encodes_polish_letters()
    {
        var bytes = MazoviaEncoding.Instance.GetBytes("Sprężyna Sworzeń Łódź");

        Assert.Equal(0x91, bytes[3]); // ę
        Assert.Equal(0xA7, bytes[4]); // ż
        Assert.Equal(0xA4, bytes[15]); // ń
        Assert.Equal("Sprężyna Sworzeń Łódź", MazoviaEncoding.Instance.GetString(bytes));
        Assert.Equal("Zażółć gęślą jaźń ĄĆĘŁŃÓŚŹŻ", MazoviaEncoding.Instance.GetString(MazoviaEncoding.Instance.GetBytes("Zażółć gęślą jaźń ĄĆĘŁŃÓŚŹŻ")));
    }

    [Fact]
    public void Frame_uses_the_printer_code_page()
    {
        var frame = PosnetProtocol.BuildFrame("formformattedline", [("s1", "ż")], PosnetProtocol.GetEncoding(PosnetCodePage.Mazovia));

        Assert.Contains((byte)0xA7, frame);
        Assert.Equal("ż", PosnetProtocol.ParseResponse(frame, MazoviaEncoding.Instance).Get("s1"));
    }
}
