namespace Gaska.SelfCheckout.Infrastructure.Erp;

public enum SaleUnitKind
{
    Piece,
    /// <summary>Sold by weight (kg, g, t…) – weighed by the seller.</summary>
    Weighed,
    /// <summary>Cut to length (mb, m, cm…) – cut by the seller.</summary>
    Cut
}

/// <summary>
/// Products sold by weight or cut to length need the seller. XL has no dictionary that tells which units of measure
/// those are (Twr_Jm is free text), so the units are chosen in the workstation configuration (Erp:Units) from the units
/// used on the product cards. Hidden products are not offered at the kiosk and the customer is told to buy them at
/// the staffed checkout.
/// </summary>
public sealed class SaleUnitsOptions
{
    public static readonly IReadOnlyList<string> DefaultWeighedUnits = ["kg", "g", "dag", "t"];
    public static readonly IReadOnlyList<string> DefaultCutUnits = ["mb", "m", "cm", "mm"];

    /// <summary>Offer products sold by weight at the kiosk.</summary>
    public bool SellWeighed { get; set; }

    /// <summary>Offer products cut to length at the kiosk.</summary>
    public bool SellCut { get; set; }

    /// <summary>Units of products sold by weight; null / empty = <see cref="DefaultWeighedUnits"/>.</summary>
    public List<string>? WeighedUnits { get; set; }

    /// <summary>Units of products cut to length; null / empty = <see cref="DefaultCutUnits"/>.</summary>
    public List<string>? CutUnits { get; set; }

    public IReadOnlyList<string> Weighed => WeighedUnits is { Count: > 0 } list ? list : DefaultWeighedUnits;
    public IReadOnlyList<string> Cut => CutUnits is { Count: > 0 } list ? list : DefaultCutUnits;

    public SaleUnitKind KindOf(string? unit)
    {
        var u = unit?.Trim() ?? "";
        if (Weighed.Any(w => string.Equals(w.Trim(), u, StringComparison.OrdinalIgnoreCase)))
            return SaleUnitKind.Weighed;
        return Cut.Any(c => string.Equals(c.Trim(), u, StringComparison.OrdinalIgnoreCase)) ? SaleUnitKind.Cut : SaleUnitKind.Piece;
    }

    /// <summary>The product is not sold at the kiosk (its unit is weighed / cut and such products are switched off).</summary>
    public bool IsHidden(string? unit) => KindOf(unit) switch
    {
        SaleUnitKind.Weighed => !SellWeighed,
        SaleUnitKind.Cut => !SellCut,
        _ => false
    };

    /// <summary>Information for the customer about products to buy from the seller; null when everything is sold here.</summary>
    public string? CustomerNotice => (SellWeighed, SellCut) switch
    {
        (false, false) => "Towary sprzedawane na wagę oraz cięte na wymiar kupisz u sprzedawcy w sklepie.",
        (false, true) => "Towary sprzedawane na wagę kupisz u sprzedawcy w sklepie.",
        (true, false) => "Towary cięte na wymiar (sprzedawane na metry) kupisz u sprzedawcy w sklepie.",
        _ => null
    };
}
