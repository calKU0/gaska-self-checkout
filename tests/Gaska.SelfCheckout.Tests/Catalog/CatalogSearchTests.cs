using System.Text.Json;
using Gaska.SelfCheckout.Domain.Catalog;
using Gaska.SelfCheckout.Domain.Sales;
using Gaska.SelfCheckout.Infrastructure.Erp.Catalog;

namespace Gaska.SelfCheckout.Tests.Catalog;

public class CatalogSearchTests
{
    private const int Root = 1;

    // 1 (root) ─ 10 ─ 100
    //          └ 20
    // 99 – outside the shown tree
    private static readonly GroupNode[] Groups =
    [
        new(Root, 0, "1", "Kategorie"),
        new(10, Root, "A", "Filtry"),
        new(100, 10, "A1", "Filtry oleju"),
        new(20, Root, "B", "Pompy"),
        new(99, 0, "X", "Części według zastosowania")
    ];

    private static CatalogEntry InStock(int id) => new(id, $"K{id}", $"Towar {id}", null, 5, "szt.");

    [Fact]
    public void Available_tree_counts_products_in_stock_and_the_full_tree_every_indexed_product()
    {
        var links = new Dictionary<int, int[]>
        {
            [1001] = [100, 10, Root, 99], // the index lists the whole path – counted once per category
            [1002] = [20],
            [1003] = [100], // not in stock
            [1004] = [20] // hidden unit (sold by weight)
        };
        var snapshot = CatalogSnapshot.Build(
            new Dictionary<int, CatalogEntry> { [1001] = InStock(1001), [1002] = InStock(1002) },
            Groups, links, new HashSet<int> { 1004 }, Root, indexed: true);

        Assert.Equal(2, snapshot.Available.Categories[Root].ProductCount);
        Assert.Equal(1, snapshot.Available.Categories[10].ProductCount);
        Assert.Equal(3, snapshot.All.Categories[Root].ProductCount);
        Assert.Equal(2, snapshot.All.Categories[100].ProductCount);
        Assert.Equal(1, snapshot.All.Categories[20].ProductCount);
        Assert.False(snapshot.Available.Categories.ContainsKey(99));
        Assert.Equal(3, snapshot.IndexedProducts);
        Assert.Equal([1001], snapshot.ProductIdsIn(100));
        Assert.Equal([1001, 1002, 1004], snapshot.UnavailableExclusions.Order());
    }

    [Fact]
    public void Categories_without_products_in_stock_are_hidden_unless_unavailable_products_are_shown()
    {
        var snapshot = CatalogSnapshot.Build(
            new Dictionary<int, CatalogEntry> { [1002] = InStock(1002) },
            Groups, new Dictionary<int, int[]> { [1001] = [100], [1002] = [20] }, new HashSet<int>(), Root, indexed: true);

        Assert.Equal([20], snapshot.Tree(showUnavailable: false).Children[Root]);
        Assert.Equal([10, 20], snapshot.Tree(showUnavailable: true).Children[Root].Order());
        Assert.True(snapshot.Tree(showUnavailable: true).Categories[10].HasChildren);
    }

    [Fact]
    public void Without_the_index_both_trees_come_from_the_products_in_stock()
    {
        var snapshot = CatalogSnapshot.Build(
            new Dictionary<int, CatalogEntry> { [1001] = InStock(1001) },
            Groups, new Dictionary<int, int[]> { [1001] = [100] }, new HashSet<int>(), Root, indexed: false);

        Assert.Same(snapshot.Available, snapshot.All);
        Assert.Equal(0, snapshot.IndexedProducts);
    }

    [Fact]
    public void Search_request_matches_every_word_in_the_index_fields_and_filters_by_category_and_products()
    {
        var body = ElasticProductIndex.BuildSearch(new IndexQuery("  filtr oleju ", 100, [1, 2], [3], 24, 24));
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        Assert.Equal(24, root.GetProperty("from").GetInt32());
        Assert.Equal(24, root.GetProperty("size").GetInt32());
        var query = root.GetProperty("query").GetProperty("bool");
        var multiMatch = query.GetProperty("must")[0].GetProperty("bool").GetProperty("should")[0].GetProperty("multi_match");
        Assert.Equal("filtr oleju", multiMatch.GetProperty("query").GetString());
        Assert.Equal("and", multiMatch.GetProperty("operator").GetString());
        Assert.Equal("whitespace_ascii", multiMatch.GetProperty("analyzer").GetString());
        Assert.Equal(100, query.GetProperty("filter")[0].GetProperty("nested").GetProperty("query").GetProperty("term").GetProperty("categories.id").GetInt32());
        Assert.Equal([1, 2], query.GetProperty("filter")[1].GetProperty("terms").GetProperty("id").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal([3], query.GetProperty("must_not")[0].GetProperty("terms").GetProperty("id").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal("_score", root.GetProperty("sort")[0].GetString());
    }

    [Fact]
    public void Browsing_a_category_has_no_text_query_and_an_empty_product_filter_matches_nothing()
    {
        using var json = JsonDocument.Parse(ElasticProductIndex.BuildSearch(new IndexQuery(null, 100, [], null, 0, 24)));
        var query = json.RootElement.GetProperty("query").GetProperty("bool");

        Assert.False(query.TryGetProperty("must", out _));
        Assert.False(query.TryGetProperty("must_not", out _));
        Assert.Equal(0, query.GetProperty("filter")[1].GetProperty("terms").GetProperty("id").GetArrayLength());
        Assert.True(json.RootElement.GetProperty("sort")[0].TryGetProperty("Order", out _));
    }

    [Fact]
    public void A_product_out_of_stock_is_never_added_to_the_cart()
    {
        var product = new Product(1, "K1", "Towar", "5900000000001", "szt.", 10m, 23m, [new WarehouseStock("MAG1", 0, true)]);
        var cart = new Cart();

        Assert.False(product.IsAvailable);
        Assert.Equal(CartChangeResult.OutOfStock, cart.Add(product));
        Assert.True(cart.IsEmpty);
    }
}
