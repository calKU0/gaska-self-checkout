using Gaska.SelfCheckout.Domain.Catalog;

namespace Gaska.SelfCheckout.Domain.Sales;

/// <summary>Type of the sales document issued for the purchase.</summary>
public enum SaleDocumentKind
{
    /// <summary>Fiscal receipt for an anonymous (one-off) customer.</summary>
    Receipt,
    /// <summary>VAT invoice for a selected customer (customer prices and discounts apply).</summary>
    Invoice
}

public sealed class CartLine
{
    internal CartLine(Product product, decimal quantity)
    {
        Product = product;
        Quantity = quantity;
    }

    public Product Product { get; internal set; }
    public decimal Quantity { get; internal set; }

    /// <summary>
    /// Price calculated by the ERP for the current quantity (customer / one-off customer discounts, quantity promotions);
    /// null while it is being calculated – cleared whenever the quantity changes.
    /// </summary>
    public PriceQuote? Quote { get; internal set; }

    public decimal UnitPrice => Quote?.Price ?? Product.GrossPrice;
    public decimal BasePrice => Quote?.BasePrice ?? Product.GrossPrice;
    public decimal Total => Math.Round(UnitPrice * Quantity, 2, MidpointRounding.AwayFromZero);

    /// <summary>Line value before discounts.</summary>
    public decimal ListTotal => Math.Round(BasePrice * Quantity, 2, MidpointRounding.AwayFromZero);

    public decimal Discount => Math.Max(0, ListTotal - Total);
    public int DiscountPercent => Quote?.DiscountPercent ?? 0;
    public IReadOnlyList<WarehouseAllocation> Allocations => Product.Allocate(Quantity);
}

public enum CartChangeResult
{
    Added,
    Increased,
    LimitedByStock,
    OutOfStock,
    Removed,
    Updated
}

/// <summary>Customer basket. Never allows selling more than the available stock.</summary>
public sealed class Cart
{
    private readonly List<CartLine> _lines = [];

    public IReadOnlyList<CartLine> Lines => _lines;
    public bool IsEmpty => _lines.Count == 0;
    public decimal Total => _lines.Sum(l => l.Total);
    public decimal Discount => _lines.Sum(l => l.Discount);
    public decimal ItemCount => _lines.Sum(l => l.Quantity);

    public event Action? Changed;

    public CartChangeResult Add(Product product, decimal quantity = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        if (!product.IsAvailable)
            return CartChangeResult.OutOfStock;

        var line = _lines.FirstOrDefault(l => l.Product.Id == product.Id);
        var current = line?.Quantity ?? 0;
        var target = Math.Min(current + quantity, product.AvailableQuantity);
        if (target <= current)
            return CartChangeResult.LimitedByStock;

        if (line is null)
            _lines.Insert(0, new CartLine(product, target));
        else
        {
            line.Product = product;
            line.Quantity = target;
            line.Quote = null; // quantity promotions – the price depends on the quantity
        }

        Changed?.Invoke();
        if (target < current + quantity)
            return CartChangeResult.LimitedByStock;
        return line is null ? CartChangeResult.Added : CartChangeResult.Increased;
    }

    public CartChangeResult SetQuantity(int productId, decimal quantity)
    {
        var line = _lines.FirstOrDefault(l => l.Product.Id == productId);
        if (line is null)
            return CartChangeResult.Removed;
        if (quantity <= 0)
            return Remove(productId);

        var limited = Math.Min(quantity, line.Product.AvailableQuantity);
        if (limited != line.Quantity)
            line.Quote = null;
        line.Quantity = limited;
        Changed?.Invoke();
        return limited < quantity ? CartChangeResult.LimitedByStock : CartChangeResult.Updated;
    }

    /// <summary>Sets the price calculated for <paramref name="quantity"/> – ignored when the quantity changed meanwhile.</summary>
    public bool SetQuote(int productId, decimal quantity, PriceQuote quote)
    {
        var line = _lines.FirstOrDefault(l => l.Product.Id == productId);
        if (line is null || line.Quantity != quantity)
            return false;
        if (line.Quote != quote)
        {
            line.Quote = quote;
            Changed?.Invoke();
        }

        return true;
    }

    /// <summary>All prices have to be calculated again (other customer or document type).</summary>
    public void ClearQuotes()
    {
        foreach (var line in _lines)
            line.Quote = null;
        Changed?.Invoke();
    }

    public CartChangeResult Remove(int productId)
    {
        _lines.RemoveAll(l => l.Product.Id == productId);
        Changed?.Invoke();
        return CartChangeResult.Removed;
    }

    /// <summary>Replaces product data (list price / stock) with current values – used right before payment.</summary>
    public IReadOnlyList<CartLine> Refresh(IReadOnlyDictionary<int, Product> current)
    {
        var changed = new List<CartLine>();
        foreach (var line in _lines.ToList())
        {
            if (!current.TryGetValue(line.Product.Id, out var fresh) || !fresh.IsAvailable)
            {
                changed.Add(line);
                _lines.Remove(line);
                continue;
            }

            var priceChanged = fresh.GrossPrice != line.Product.GrossPrice;
            var quantity = Math.Min(line.Quantity, fresh.AvailableQuantity);
            if (priceChanged || quantity != line.Quantity)
                changed.Add(line);
            if (quantity != line.Quantity)
                line.Quote = null;
            line.Product = fresh;
            line.Quantity = quantity;
        }

        if (changed.Count > 0)
            Changed?.Invoke();
        return changed;
    }

    public void Clear()
    {
        _lines.Clear();
        Changed?.Invoke();
    }

    public CartSnapshot Snapshot() =>
        new(_lines.Select(l => new CartSnapshotLine(l.Product.Id, l.Product.Code, l.Product.Name, l.Product.Unit,
            l.Quantity, l.UnitPrice, l.Total, l.Allocations)).ToList());
}

/// <summary>Immutable copy of the cart passed to the payment process and the ERP document.</summary>
public sealed record CartSnapshot(IReadOnlyList<CartSnapshotLine> Lines)
{
    public decimal Total => Lines.Sum(l => l.Total);

    /// <summary>True when any quantity is taken from a non-primary warehouse – the customer must collect it.</summary>
    public bool RequiresPickup => Lines.Any(l => l.Allocations.Any(a => !a.IsPrimary));
}

public sealed record CartSnapshotLine(
    int ProductId,
    string Code,
    string Name,
    string Unit,
    decimal Quantity,
    decimal UnitPrice,
    decimal Total,
    IReadOnlyList<WarehouseAllocation> Allocations);
