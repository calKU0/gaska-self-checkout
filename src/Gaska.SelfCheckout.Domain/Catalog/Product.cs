namespace Gaska.SelfCheckout.Domain.Catalog;

/// <summary>Stock of a product in a single ERP warehouse.</summary>
/// <param name="IsPrimary">The point-of-sale warehouse; other warehouses are used only when it runs out.</param>
public sealed record WarehouseStock(string WarehouseCode, decimal Quantity, bool IsPrimary);

/// <summary>
/// A product available for sale at the kiosk: ERP product card + retail gross price + stock in the sale warehouses.
/// </summary>
public sealed record Product(
    int Id,
    string Code,
    string Name,
    string? Ean,
    string Unit,
    decimal GrossPrice,
    decimal VatRate,
    IReadOnlyList<WarehouseStock> Stock)
{
    public decimal AvailableQuantity => Stock.Sum(s => Math.Max(0, s.Quantity));

    public bool IsAvailable => AvailableQuantity > 0 && GrossPrice > 0;

    /// <summary>True when part of the stock is held outside the primary (point-of-sale) warehouse.</summary>
    public bool HasSecondaryStock => Stock.Any(s => !s.IsPrimary && s.Quantity > 0);

    /// <summary>
    /// Splits the requested quantity between warehouses – the primary warehouse first, then the others.
    /// </summary>
    public IReadOnlyList<WarehouseAllocation> Allocate(decimal quantity)
    {
        var result = new List<WarehouseAllocation>();
        var remaining = quantity;
        foreach (var stock in Stock.OrderByDescending(s => s.IsPrimary).Where(s => s.Quantity > 0))
        {
            if (remaining <= 0)
                break;
            var take = Math.Min(remaining, stock.Quantity);
            result.Add(new WarehouseAllocation(stock.WarehouseCode, take, stock.IsPrimary));
            remaining -= take;
        }

        return result;
    }
}

/// <summary>Quantity of a cart line taken from a given warehouse.</summary>
public sealed record WarehouseAllocation(string WarehouseCode, decimal Quantity, bool IsPrimary);
