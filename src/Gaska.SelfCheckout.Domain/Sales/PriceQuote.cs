namespace Gaska.SelfCheckout.Domain.Sales;

/// <summary>
/// Gross unit price calculated by the ERP for a quantity: before discounts (the price of the customer's price type,
/// XLPrzeliczRabat.CenaTowaru) and after discounts and promotions (CenaPoRabacie).
/// </summary>
public sealed record PriceQuote(decimal BasePrice, decimal Price)
{
    public static PriceQuote ListPrice(decimal price) => new(price, price);

    public bool IsDiscounted => Price < BasePrice;

    /// <summary>Discount in percent of the base price, rounded.</summary>
    public int DiscountPercent => BasePrice > 0 && Price < BasePrice
        ? (int)Math.Round((1 - Price / BasePrice) * 100, MidpointRounding.AwayFromZero)
        : 0;
}

/// <summary>Quantity discount tier: unit price from the given quantity (e.g. "od 5 szt.").</summary>
public sealed record QuantityBreak(decimal Quantity, PriceQuote Quote);
