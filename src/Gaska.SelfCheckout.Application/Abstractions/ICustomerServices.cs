using Gaska.SelfCheckout.Domain.Customers;
using Gaska.SelfCheckout.Domain.Sales;

namespace Gaska.SelfCheckout.Application.Abstractions;

/// <summary>ERP customer directory.</summary>
public interface ICustomerDirectory
{
    /// <summary>Finds active (non-archived) customers by tax id (NIP) – invoices.</summary>
    Task<IReadOnlyList<Customer>> FindByTaxIdAsync(string taxId, CancellationToken ct = default);

    /// <summary>Finds active (non-archived) customers by customer / loyalty card number – customer prices on a receipt.</summary>
    Task<IReadOnlyList<Customer>> FindByCardAsync(string cardNumber, CancellationToken ct = default);

    /// <summary>Customer card by its ERP id (null when it does not exist).</summary>
    Task<Customer?> GetByIdAsync(int customerId, CancellationToken ct = default);

    /// <summary>Creates a new customer card in the ERP from public registry data.</summary>
    Task<Customer> CreateAsync(CompanyRegistryEntry company, CancellationToken ct = default);
}

/// <summary>Public company registry (GUS REGON BIR, VAT white list).</summary>
public interface ICompanyRegistry
{
    Task<CompanyRegistryEntry?> FindByTaxIdAsync(string taxId, CancellationToken ct = default);
}

public enum PricingPriority
{
    /// <summary>Prices needed to pay (cart) – processed before anything else.</summary>
    High,
    /// <summary>Prices shown while browsing – may be superseded by newer requests.</summary>
    Low
}

/// <summary>
/// Whom and on which document the price is calculated for. CustomerId 0 = anonymous receipt (the one-off customer
/// configured in the ERP – it may have its own discounts).
/// </summary>
public readonly record struct PricingContext(int CustomerId, string? CustomerAcronym, SaleDocumentKind DocumentKind)
{
    public static PricingContext For(Customer? customer, SaleDocumentKind kind) => new(customer?.Id ?? 0, customer?.Acronym, kind);
}

public sealed record PriceRequest(int ProductId, decimal Quantity);

/// <summary>
/// Prices calculated by the ERP (XLPrzeliczRabat – price type, customer and operator discounts, promotions including
/// quantity promotions), for every sale – also without a customer account.
/// </summary>
public interface ICustomerPricing
{
    /// <summary>Cached price for the quantity or null when not calculated yet.</summary>
    PriceQuote? TryGetCached(PricingContext context, int productId, decimal quantity = 1);

    /// <summary>Calculates gross unit prices for the requested quantities (keyed by product).</summary>
    Task<IReadOnlyDictionary<int, PriceQuote>> GetPricesAsync(PricingContext context, IReadOnlyCollection<PriceRequest> items, PricingPriority priority, CancellationToken ct = default);

    /// <summary>Cached quantity discount tiers (empty = none) or null when not calculated yet.</summary>
    IReadOnlyList<QuantityBreak>? TryGetCachedBreaks(PricingContext context, int productId);

    /// <summary>Calculates (low priority) the quantity discount tiers of the products.</summary>
    Task CalculateBreaksAsync(PricingContext context, IReadOnlyCollection<int> productIds, CancellationToken ct = default);

    /// <summary>Raised whenever a price or tier is calculated (lets the UI update progressively).</summary>
    event Action<PricingContext, int>? PriceCalculated;
}
