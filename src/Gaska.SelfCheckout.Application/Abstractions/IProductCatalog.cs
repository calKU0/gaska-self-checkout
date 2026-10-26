using Gaska.SelfCheckout.Domain.Catalog;

namespace Gaska.SelfCheckout.Application.Abstractions;

public sealed record ProductQuery(string? Text, int? CategoryId, int Page = 1, int PageSize = 24);

/// <summary>Catalog of products available in the kiosk sale warehouses.</summary>
public interface IProductCatalog
{
    /// <summary>Exact lookup by a scanned/typed code (EAN, product code, supplier code).</summary>
    Task<Product?> FindByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>Full-text search (name, code, EAN, supplier codes, OEM numbers), optionally limited to a category.</summary>
    Task<ProductPage> SearchAsync(ProductQuery query, CancellationToken ct = default);

    /// <summary>Subcategories of a category (null = configured root). Only categories containing available products.</summary>
    Task<IReadOnlyList<Category>> GetCategoriesAsync(int? parentId, CancellationToken ct = default);

    Task<IReadOnlyList<Category>> GetCategoryPathAsync(int categoryId, CancellationToken ct = default);

    /// <summary>Current data (list price, stock) of the given products.</summary>
    Task<IReadOnlyDictionary<int, Product>> GetProductsAsync(IEnumerable<int> ids, CancellationToken ct = default);
}
