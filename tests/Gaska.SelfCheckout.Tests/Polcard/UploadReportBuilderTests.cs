using Gaska.SelfCheckout.Devices.Polcard.Protocol;

namespace Gaska.SelfCheckout.Tests.Polcard;

public class UploadReportBuilderTests
{
    [Fact]
    public void Cumulative_report_prints_the_summary_per_card_and_in_total()
    {
        // shortened from the terminal's answer to |2881311|2|3| (certification test 57)
        var tlv = Tlv.BuildPolcard([
            ("1A1C", "SKLEP TESTOWY SP. Z O.O."), ("1A1F", "12345678"), ("1A20", "87654321"),
            ("1B21", "Raport zamknięcia dnia nr 6"),
            ("1C14", "MasterCard$SPRZEDAŻ;6;10146$CASHBACK;0;0$UN. SPRZEDAŻ;1;100$RAZEM:;7;10046$Maestro$SPRZEDAŻ;1;100$RAZEM:;1;100$"),
            ("1C15", "ZESTAWIENIE RAZEM$SPRZEDAŻ;13;20446$UZNANIE;0;0$RAZEM:;14;20346$"),
            ("1C16", "2026/08/10 12:12:42")
        ]);

        var report = UploadReportBuilder.Build(Tlv.Parse(tlv), "0006", "000000");
        var text = string.Join("\n", report.Lines);

        Assert.Contains("Transakcji: 0", text);
        Assert.Contains("Liczniki od: 2026/08/10 12:12:42", text);
        Assert.Contains("MasterCard", text);
        Assert.Contains("ZESTAWIENIE RAZEM", text);
        Assert.Contains("203,46", text);
        Assert.DoesNotContain("CASHBACK", text); // rows without transactions are left out
    }
}
