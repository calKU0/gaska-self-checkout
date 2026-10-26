using Gaska.SelfCheckout.Domain.Catalog;

namespace Gaska.SelfCheckout.Infrastructure.Erp.Catalog;

/// <summary>Lightweight description of a product in stock (filtering, sorting and in-memory search).</summary>
public sealed record CatalogEntry(int Id, string Code, string Name, string? Ean, decimal Quantity, string Unit)
{
    /// <summary>Additional codes: EANs, supplier codes (TwrKody).</summary>
    public IReadOnlyList<string> ExtraCodes { get; init; } = [];

    private string? _text;
    private string? _compact;

    /// <summary>Normalized text: code, name, EAN, supplier codes.</summary>
    public string Text => _text ??= SearchText.Normalize($"{Code} {Name} {Ean} {string.Join(' ', ExtraCodes)}");

    /// <summary>Codes without separators (matches "00100100034" to "00.1001.000.34").</summary>
    public string CompactCodes => _compact ??= SearchText.Compact(SearchText.Normalize($"{Code} {Ean} {string.Join(' ', ExtraCodes)}"));

    public bool Matches(string[] tokens)
    {
        foreach (var t in tokens)
            if (!Text.Contains(t, StringComparison.Ordinal) && !(t.Length >= 3 && CompactCodes.Contains(SearchText.Compact(t), StringComparison.Ordinal)))
                return false;
        return true;
    }

    /// <summary>Match rank (lower is better).</summary>
    public int Rank(string normalizedQuery)
    {
        var code = SearchText.Normalize(Code);
        var compact = SearchText.Compact(normalizedQuery);
        if (code == normalizedQuery || Ean == normalizedQuery || ExtraCodes.Any(c => SearchText.Normalize(c) == normalizedQuery)) return 0;
        if (compact.Length > 0 && SearchText.Compact(code) == compact) return 1;
        if (code.StartsWith(normalizedQuery, StringComparison.Ordinal)) return 2;
        if (SearchText.Normalize(Name).StartsWith(normalizedQuery, StringComparison.Ordinal)) return 3;
        return 5;
    }
}

internal sealed record GroupNode(int Id, int ParentId, string Code, string Name);

/// <summary>Visible categories (non-empty subtrees) with their product counts and visible subcategories.</summary>
public sealed record CategoryTree(IReadOnlyDictionary<int, Category> Categories, IReadOnlyDictionary<int, List<int>> Children)
{
    public static CategoryTree Empty { get; } = new(new Dictionary<int, Category>(), new Dictionary<int, List<int>>());
}

/// <summary>
/// Immutable snapshot: products in stock + category tree counted twice – for products in stock and for every product of
/// the search index (option "show unavailable products").
/// </summary>
public sealed class CatalogSnapshot
{
    public static CatalogSnapshot Empty { get; } = new(new Dictionary<int, CatalogEntry>(), CategoryTree.Empty, CategoryTree.Empty,
        new Dictionary<int, HashSet<int>>(), [], 0, 0, DateTime.MinValue);

    internal CatalogSnapshot(
        IReadOnlyDictionary<int, CatalogEntry> products,
        CategoryTree available,
        CategoryTree all,
        IReadOnlyDictionary<int, HashSet<int>> productsInSubtree,
        int[] excludedFromIndex,
        int indexedProducts,
        int rootId,
        DateTime loadedAt)
    {
        Products = products;
        InStockIds = products.Keys.ToArray();
        Available = available;
        All = all;
        ProductsInSubtree = productsInSubtree;
        UnavailableExclusions = excludedFromIndex;
        IndexedProducts = indexedProducts;
        RootId = rootId;
        LoadedAt = loadedAt;
    }

    /// <summary>Products in stock in the sale warehouses (without products sold by weight / cut, unless switched on).</summary>
    public IReadOnlyDictionary<int, CatalogEntry> Products { get; }

    /// <summary>Ids of <see cref="Products"/> – the filter of "available" searches in the index.</summary>
    public IReadOnlyCollection<int> InStockIds { get; }

    /// <summary>Products left out of the "unavailable" part of a search: those in stock (listed first) and hidden units.</summary>
    public IReadOnlyCollection<int> UnavailableExclusions { get; }

    /// <summary>Categories of products in stock.</summary>
    public CategoryTree Available { get; }

    /// <summary>Categories of every product of the search index (= <see cref="Available"/> while the index links are unknown).</summary>
    public CategoryTree All { get; }

    public IReadOnlyDictionary<int, Category> Categories => Available.Categories;
    internal IReadOnlyDictionary<int, HashSet<int>> ProductsInSubtree { get; }

    /// <summary>Products of the search index shown at the kiosk (0 = index links not loaded).</summary>
    public int IndexedProducts { get; }

    public int RootId { get; }
    public DateTime LoadedAt { get; }
    public bool IsLoaded => LoadedAt > DateTime.MinValue;

    public CategoryTree Tree(bool showUnavailable) => showUnavailable ? All : Available;

    public IReadOnlyCollection<int> ProductIdsIn(int? categoryId) =>
        categoryId is { } id
            ? ProductsInSubtree.TryGetValue(id, out var set) ? set : []
            : InStockIds;

    /// <param name="products">Products in stock.</param>
    /// <param name="links">Product → its product groups: every product of the search index (<paramref name="indexed"/>),
    /// or – when the index is unavailable – the products in stock from XL.</param>
    /// <param name="hidden">Products never shown (sold by weight / cut to length).</param>
    internal static CatalogSnapshot Build(
        IReadOnlyDictionary<int, CatalogEntry> products,
        IReadOnlyList<GroupNode> groups,
        IReadOnlyDictionary<int, int[]> links,
        IReadOnlySet<int> hidden,
        int rootId,
        bool indexed)
    {
        // Subtree of the root
        var childrenAll = groups.GroupBy(g => g.ParentId).ToDictionary(g => g.Key, g => g.Select(x => x.Id).Distinct().ToList());
        var byId = groups.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());
        var parentOf = new Dictionary<int, int>();
        var inTree = new HashSet<int> { rootId };
        var queue = new Queue<int>([rootId]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!childrenAll.TryGetValue(id, out var kids)) continue;
            foreach (var k in kids.Where(inTree.Add))
            {
                parentOf[k] = id;
                queue.Enqueue(k);
            }
        }

        // The group and its ancestors up to the root (empty for groups outside the shown tree)
        var ancestors = new Dictionary<int, int[]>();
        int[] AncestorsOf(int groupId)
        {
            if (ancestors.TryGetValue(groupId, out var path))
                return path;
            var list = new List<int>();
            var g = groupId;
            while (inTree.Contains(g) && list.Count < 64)
            {
                list.Add(g);
                if (g == rootId || !parentOf.TryGetValue(g, out g)) break;
            }

            return ancestors[groupId] = list.ToArray();
        }

        // Products in subtrees (a product in a group counts for all its ancestors, once)
        var inStock = new Dictionary<int, HashSet<int>>();
        var allCounts = new Dictionary<int, int>();
        var indexedProducts = 0;
        var seen = new HashSet<int>();
        foreach (var (productId, productGroups) in links)
        {
            if (hidden.Contains(productId)) continue;
            var available = products.ContainsKey(productId);
            if (!available && !indexed) continue;
            indexedProducts++;
            seen.Clear();
            foreach (var groupId in productGroups)
                seen.UnionWith(AncestorsOf(groupId));
            foreach (var c in seen)
            {
                allCounts[c] = allCounts.GetValueOrDefault(c) + 1;
                if (!available) continue;
                if (!inStock.TryGetValue(c, out var set)) inStock[c] = set = [];
                set.Add(productId);
            }
        }

        CategoryTree Tree(Func<int, int> count)
        {
            var categories = new Dictionary<int, Category>();
            var children = new Dictionary<int, List<int>>();
            foreach (var id in inTree)
            {
                var n = count(id);
                if (n == 0) continue; // empty categories are hidden
                var node = byId.GetValueOrDefault(id) ?? new GroupNode(id, 0, "", "");
                var visibleKids = childrenAll.GetValueOrDefault(id)?
                    .Where(k => parentOf.GetValueOrDefault(k) == id && count(k) > 0)
                    .ToList() ?? [];
                children[id] = visibleKids;
                categories[id] = new Category(id, parentOf.GetValueOrDefault(id), node.Code, Pretty(node), n, visibleKids.Count > 0);
            }

            return new CategoryTree(categories, children);
        }

        var availableTree = Tree(id => inStock.TryGetValue(id, out var set) ? set.Count : 0);
        var allTree = indexed ? Tree(id => allCounts.GetValueOrDefault(id)) : availableTree;
        var excluded = products.Keys.Concat(hidden).Distinct().ToArray();
        return new CatalogSnapshot(products, availableTree, allTree, inStock, excluded, indexed ? indexedProducts : 0, rootId, DateTime.Now);
    }

    /// <summary>Display name of a group – TwG_Nazwa (e.g. "Pompki paliwa"), or the code when empty.</summary>
    private static string Pretty(GroupNode node) => string.IsNullOrWhiteSpace(node.Name) ? node.Code : node.Name.Trim();
}
