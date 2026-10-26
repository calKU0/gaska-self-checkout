using Dapper;
using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Domain.Catalog;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Infrastructure.Erp.Catalog;

/// <summary>
/// Kiosk catalog: search and category browsing in the Elasticsearch product index (filtered by the products in stock
/// from <see cref="CatalogCache"/>; in-memory search when the index does not answer) + SQL (current retail price and
/// per-warehouse stock when shown / added to the cart). Scanned codes are looked up only in XL.
/// </summary>
public sealed class XlProductCatalog(CatalogCache cache, ElasticProductIndex index, IOptionsMonitor<ErpOptions> options,
    ILogger<XlProductCatalog> logger) : IProductCatalog
{
    public async Task<Product?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        code = code.Trim();
        if (code.Length == 0)
            return null;

        await using var conn = new SqlConnection(options.CurrentValue.ConnectionString);
        var ids = (await conn.QueryAsync<int>(new CommandDefinition("""
            SELECT Twr_GIDNumer FROM cdn.TwrKarty WHERE Twr_Ean = @code OR Twr_Kod = @code
            UNION
            SELECT TwK_TwrNumer FROM cdn.TwrKody WHERE TwK_Kod = @code
            """, new { code }, cancellationToken: ct))).Distinct().ToList();
        if (ids.Count == 0)
            return null;

        var products = await LoadAsync(conn, ids, ct);
        // with several matches prefer an available product, then an EAN match, then a code match
        return products.Values
            .OrderByDescending(p => p.IsAvailable)
            .ThenByDescending(p => p.Ean == code)
            .ThenByDescending(p => p.Code == code)
            .FirstOrDefault();
    }

    public async Task<ProductPage> SearchAsync(ProductQuery query, CancellationToken ct = default)
    {
        var snapshot = await cache.GetAsync(ct: ct);
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);
        var text = query.Text?.Trim() ?? "";
        var showUnavailable = options.CurrentValue.ShowUnavailableProducts;

        IReadOnlyList<int> pageIds;
        int total;
        try
        {
            (pageIds, total) = await SearchIndexAsync(snapshot, text, query.CategoryId, page, size, showUnavailable, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Search index unavailable ({Error}) – products in stock searched in memory", ex.Message);
            showUnavailable = false;
            (pageIds, total) = SearchInMemory(snapshot, text, query.CategoryId, page, size);
        }

        if (pageIds.Count == 0)
            return new ProductPage([], total, page, size);

        await using var conn = new SqlConnection(options.CurrentValue.ConnectionString);
        var products = await LoadAsync(conn, pageIds, ct);
        // stock / price checked again in XL: an unavailable product is shown greyed out (option) or left out
        var items = pageIds.Where(products.ContainsKey).Select(id => products[id]).Where(p => showUnavailable || p.IsAvailable).ToList();
        return new ProductPage(items, total, page, size);
    }

    /// <summary>
    /// Search index: first the products in stock, then (option "show unavailable") the others – one result list paged
    /// across both parts.
    /// </summary>
    private async Task<(IReadOnlyList<int> Ids, int Total)> SearchIndexAsync(
        CatalogSnapshot snapshot, string text, int? categoryId, int page, int size, bool showUnavailable, CancellationToken ct)
    {
        if (text.Length == 0 && categoryId is null)
            return ([], 0);

        var start = (page - 1) * size;
        var available = snapshot.InStockIds.Count == 0
            ? new IndexHits([], 0)
            : await index.SearchAsync(new IndexQuery(text, categoryId, snapshot.InStockIds, null, start, size), ct);
        if (!showUnavailable)
            return (available.Ids, available.Total);

        var rest = await index.SearchAsync(new IndexQuery(text, categoryId, null, snapshot.UnavailableExclusions,
            Math.Max(0, start - available.Total), size - available.Ids.Count), ct);
        return ([.. available.Ids, .. rest.Ids], available.Total + rest.Total);
    }

    /// <summary>Fallback without the index: products in stock, every word in the name or codes, exact codes first.</summary>
    private static (IReadOnlyList<int> Ids, int Total) SearchInMemory(CatalogSnapshot snapshot, string text, int? categoryId, int page, int size)
    {
        var allowed = snapshot.ProductIdsIn(categoryId);
        IEnumerable<CatalogEntry> ordered;
        if (text.Length == 0)
            ordered = allowed.Select(id => snapshot.Products[id]).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);
        else
        {
            var normalized = SearchText.Normalize(text);
            var tokens = SearchText.Tokens(text);
            ordered = allowed.Select(id => snapshot.Products[id])
                .Where(p => p.Matches(tokens))
                .OrderBy(p => p.Rank(normalized))
                .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        var ids = ordered.Select(p => p.Id).ToList();
        return (ids.Skip((page - 1) * size).Take(size).ToList(), ids.Count);
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(int? parentId, CancellationToken ct = default)
    {
        var snapshot = await cache.GetAsync(ct: ct);
        var tree = snapshot.Tree(options.CurrentValue.ShowUnavailableProducts);
        var parent = parentId ?? snapshot.RootId;
        return tree.Children.TryGetValue(parent, out var kids)
            ? kids.Select(k => tree.Categories[k]).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            : [];
    }

    public async Task<IReadOnlyList<Category>> GetCategoryPathAsync(int categoryId, CancellationToken ct = default)
    {
        var snapshot = await cache.GetAsync(ct: ct);
        var categories = snapshot.Tree(options.CurrentValue.ShowUnavailableProducts).Categories;
        var path = new List<Category>();
        var id = categoryId;
        while (id != snapshot.RootId && categories.TryGetValue(id, out var c) && path.Count < 32)
        {
            path.Insert(0, c);
            id = c.ParentId;
        }

        return path;
    }

    public async Task<IReadOnlyDictionary<int, Product>> GetProductsAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(options.CurrentValue.ConnectionString);
        return await LoadAsync(conn, ids.Distinct().ToList(), ct);
    }

    private sealed record ProductRow(int Id, string Code, string Name, string? Ean, string Unit, decimal GrossPrice, decimal VatRate);

    private async Task<Dictionary<int, Product>> LoadAsync(SqlConnection conn, IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        var o = options.CurrentValue;
        var warehouses = o.Warehouses;
        var warehouseCodes = warehouses.Select(w => w.Code.Trim()).ToList();

        using var grid = await conn.QueryMultipleAsync(new CommandDefinition("""
            SELECT t.Twr_GIDNumer AS Id, t.Twr_Kod AS Code, t.Twr_Nazwa AS Name, NULLIF(t.Twr_Ean, '') AS Ean,
                   t.Twr_Jm AS Unit,
                   CAST(ISNULL(c.TwC_Wartosc, 0) AS decimal(18, 2)) AS GrossPrice,
                   CAST(ISNULL(t.Twr_StawkaPodSpr, 0) AS decimal(9, 2)) AS VatRate
            FROM cdn.TwrKarty t
            LEFT JOIN cdn.TwrCeny c ON c.TwC_TwrNumer = t.Twr_GIDNumer AND c.TwC_TwrLp = @price
            WHERE t.Twr_GIDNumer IN @ids AND t.Twr_Archiwalny = 0;

            SELECT z.TwZ_TwrNumer, m.MAG_Kod, SUM(z.TwZ_Ilosc)
            FROM cdn.TwrZasoby z
            JOIN cdn.Magazyny m ON m.MAG_GIDNumer = z.TwZ_MagNumer
            WHERE z.TwZ_TwrNumer IN @ids AND m.MAG_Kod IN @warehouseCodes
            GROUP BY z.TwZ_TwrNumer, m.MAG_Kod;
            """, new { ids, price = o.PriceNumber, warehouseCodes }, cancellationToken: ct));

        var rows = (await grid.ReadAsync<ProductRow>()).ToList();
        var stock = (await grid.ReadAsync<(int ProductId, string WarehouseCode, decimal Quantity)>()).ToLookup(s => s.ProductId);

        var result = new Dictionary<int, Product>();
        foreach (var r in rows)
        {
            var perWarehouse = warehouses
                .Select(w => new WarehouseStock(w.Code, stock[r.Id].Where(s => string.Equals(s.WarehouseCode.Trim(), w.Code.Trim(), StringComparison.OrdinalIgnoreCase)).Sum(s => s.Quantity), w.Primary))
                .ToList();
            result.TryAdd(r.Id, new Product(r.Id, r.Code.Trim(), r.Name.Trim(), r.Ean?.Trim(), r.Unit.Trim(), r.GrossPrice, r.VatRate, perWarehouse));
        }

        return result;
    }
}
