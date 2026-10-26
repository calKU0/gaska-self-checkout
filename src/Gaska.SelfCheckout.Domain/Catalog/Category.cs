namespace Gaska.SelfCheckout.Domain.Catalog;

/// <summary>ERP product group (category tree node) with the number of available products in the whole subtree.</summary>
public sealed record Category(int Id, int ParentId, string Code, string Name, int ProductCount, bool HasChildren);

/// <summary>A page of product search / browse results.</summary>
public sealed record ProductPage(IReadOnlyList<Product> Items, int TotalCount, int Page, int PageSize)
{
    public static ProductPage Empty(int pageSize) => new([], 0, 1, pageSize);
    public int PageCount => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasMore => Page < PageCount;
}
