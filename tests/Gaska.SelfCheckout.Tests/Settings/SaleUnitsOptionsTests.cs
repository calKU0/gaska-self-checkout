using Gaska.SelfCheckout.Infrastructure.Erp;

namespace Gaska.SelfCheckout.Tests.Settings;

public class SaleUnitsOptionsTests
{
    [Fact]
    public void Weighed_and_cut_products_are_hidden_by_default()
    {
        var units = new SaleUnitsOptions();
        Assert.True(units.IsHidden("kg"));
        Assert.True(units.IsHidden(" MB "));
        Assert.False(units.IsHidden("szt."));
        Assert.NotNull(units.CustomerNotice);
    }

    [Fact]
    public void Units_and_switches_come_from_the_configuration()
    {
        var units = new SaleUnitsOptions { SellCut = true, WeighedUnits = ["kg"], CutUnits = ["m2"] };
        Assert.Equal(SaleUnitKind.Cut, units.KindOf("m2"));
        Assert.False(units.IsHidden("m2"));
        Assert.True(units.IsHidden("kg"));
        Assert.False(units.IsHidden("t")); // not on the configured list
        Assert.Equal("Towary sprzedawane na wagę kupisz u sprzedawcy w sklepie.", units.CustomerNotice);
    }

    [Fact]
    public void No_notice_when_everything_is_sold_at_the_kiosk()
    {
        Assert.Null(new SaleUnitsOptions { SellWeighed = true, SellCut = true }.CustomerNotice);
    }
}
