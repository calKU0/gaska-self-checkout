using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Infrastructure.Erp.Catalog;

/// <summary>
/// Keeps in memory the products in stock in the kiosk sale warehouses (a few thousand items) with their codes, and the
/// category tree. The products in stock filter the search index ("only available" searches) and are the in-memory
/// fallback search when the index does not answer. Products sold by weight or cut to length are left out unless switched
/// on (<see cref="SaleUnitsOptions"/>). The product → category links of the whole search index are refreshed by a
/// separate loop (every <see cref="ErpOptions.CategoryRefreshMinutes"/>), so the stock refresh never waits for them.
/// </summary>
public sealed class CatalogCache(IOptionsMonitor<ErpOptions> options, ElasticProductIndex index, ILogger<CatalogCache> logger) : BackgroundService
{
    private static readonly TimeSpan IndexRetry = TimeSpan.FromMinutes(1);

    private readonly TaskCompletionSource _firstLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private IReadOnlyDictionary<int, CatalogEntry> _products = new Dictionary<int, CatalogEntry>();
    private IReadOnlyList<GroupNode> _groups = [];
    private IReadOnlySet<int> _hidden = new HashSet<int>();
    private DateTime _groupsLoadedAt = DateTime.MinValue;
    private string _groupsKey = "";
    private Dictionary<int, int[]>? _indexLinks;
    private bool _indexFailed;
    private Dictionary<int, int[]>? _xlLinks;
    private string _warehouseKey = "";

    public CatalogSnapshot Current { get; private set; } = CatalogSnapshot.Empty;

    /// <summary>Waits for the first load (at most <paramref name="timeout"/>).</summary>
    public async Task<CatalogSnapshot> GetAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!Current.IsLoaded)
            await Task.WhenAny(_firstLoad.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(15), ct));
        return Current;
    }

    /// <summary>Forces a refresh of the stock (e.g. after a sale or a configuration change).</summary>
    public Task RefreshAsync(CancellationToken ct = default) => LoadAsync(ct);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(StockLoopAsync(stoppingToken), IndexLoopAsync(stoppingToken));

    private async Task StockLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await LoadAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Product catalog refresh failed");
            }

            if (!await DelayAsync(TimeSpan.FromSeconds(Math.Max(10, options.CurrentValue.StockRefreshSeconds)), ct))
                return;
        }
    }

    /// <summary>Product → category links of every product in the search index (both category trees are counted from them).</summary>
    private async Task IndexLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var key = IndexKey();
            var next = IndexRetry;
            try
            {
                var started = DateTime.Now;
                var links = await index.LoadCategoryLinksAsync(ct);
                if (links.Count == 0)
                    throw new InvalidOperationException($"index {options.CurrentValue.Elasticsearch.Index} is empty");
                _indexLinks = links;
                _indexFailed = false;
                _xlLinks = null;
                logger.LogInformation("Search index: {Count} products with categories loaded in {Seconds:0.0} s", links.Count, (DateTime.Now - started).TotalSeconds);
                await RebuildAsync(ct);
                next = TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.CategoryRefreshMinutes));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Search index categories could not be loaded – {State}",
                    _indexLinks is null ? "categories from XL (products in stock only)" : "previous data kept");
                if (_indexLinks is null && !_indexFailed)
                {
                    _indexFailed = true;
                    await RebuildAsync(ct); // fallback: XL links of the products in stock
                }
            }

            // wait – or reload at once when the index is changed in the configuration
            var until = DateTime.Now + next;
            while (DateTime.Now < until && key == IndexKey())
                if (!await DelayAsync(TimeSpan.FromSeconds(15), ct))
                    return;
        }
    }

    private string IndexKey() => $"{options.CurrentValue.Elasticsearch.Url}|{options.CurrentValue.Elasticsearch.Index}";

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        await _loadGate.WaitAsync(ct);
        try
        {
            var o = options.CurrentValue;
            var codes = o.Warehouses.Select(w => w.Code.Trim()).ToList();
            var warehouseKey = string.Join(',', codes);

            await using var conn = new SqlConnection(o.ConnectionString);
            await conn.OpenAsync(ct);

            var found = (await conn.QueryAsync<(int Id, string Code)>(new CommandDefinition(
                "SELECT MAG_GIDNumer, MAG_Kod FROM cdn.Magazyny WHERE MAG_Kod IN @codes",
                new { codes }, cancellationToken: ct))).ToList();
            var warehouses = found.Select(m => m.Id).ToList();
            if (warehouseKey != _warehouseKey)
                foreach (var missing in codes.Where(c => !found.Any(m => string.Equals(m.Code.Trim(), c, StringComparison.OrdinalIgnoreCase))))
                    logger.LogWarning("Warehouse {Code} not found in cdn.Magazyny – its stock is not shown", missing);

            var products = (await conn.QueryAsync<CatalogEntry>(new CommandDefinition("""
                SELECT t.Twr_GIDNumer AS Id, t.Twr_Kod AS Code, t.Twr_Nazwa AS Name, NULLIF(t.Twr_Ean, '') AS Ean, s.Qty AS Quantity,
                       RTRIM(t.Twr_Jm) AS Unit
                FROM cdn.TwrKarty t
                JOIN (SELECT TwZ_TwrNumer AS Id, SUM(TwZ_Ilosc) AS Qty
                      FROM cdn.TwrZasoby WHERE TwZ_MagNumer IN @warehouses
                      GROUP BY TwZ_TwrNumer HAVING SUM(TwZ_Ilosc) > 0) s ON s.Id = t.Twr_GIDNumer
                WHERE t.Twr_Archiwalny = 0
                """, new { warehouses }, cancellationToken: ct, commandTimeout: 120)))
                .Where(p => !o.Units.IsHidden(p.Unit)) // sold by weight / cut to length – at the staffed checkout
                .ToDictionary(p => p.Id);

            var extraCodes = (await conn.QueryAsync<(int Id, string Code)>(new CommandDefinition("""
                SELECT k.TwK_TwrNumer, k.TwK_Kod FROM cdn.TwrKody k
                WHERE k.TwK_TypKodu IN (1, 2, 3) AND EXISTS (SELECT 1 FROM cdn.TwrZasoby z
                    WHERE z.TwZ_TwrNumer = k.TwK_TwrNumer AND z.TwZ_MagNumer IN @warehouses AND z.TwZ_Ilosc > 0)
                """, new { warehouses }, cancellationToken: ct, commandTimeout: 120)))
                .GroupBy(c => c.Id)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Code.Trim()).Where(c => c.Length > 0).Distinct().ToList());
            foreach (var (id, list) in extraCodes)
                if (products.TryGetValue(id, out var entry))
                    products[id] = entry with { ExtraCodes = list };

            // category tree and products never shown at the kiosk (their units)
            var hiddenUnits = o.Units.Weighed.Where(_ => !o.Units.SellWeighed).Concat(o.Units.Cut.Where(_ => !o.Units.SellCut))
                .Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var groupsKey = string.Join(',', hiddenUnits);
            var refreshTree = groupsKey != _groupsKey || DateTime.Now - _groupsLoadedAt > TimeSpan.FromMinutes(o.CategoryRefreshMinutes);
            if (refreshTree)
            {
                _groups = (await conn.QueryAsync<GroupNode>(new CommandDefinition(
                    "SELECT TwG_GIDNumer AS Id, TwG_GrONumer AS ParentId, TwG_Kod AS Code, TwG_Nazwa AS Name FROM cdn.TwrGrupy WHERE TwG_GIDTyp = -16",
                    cancellationToken: ct))).ToList();
                _hidden = hiddenUnits.Count == 0
                    ? new HashSet<int>()
                    : (await conn.QueryAsync<int>(new CommandDefinition(
                        "SELECT Twr_GIDNumer FROM cdn.TwrKarty WHERE RTRIM(Twr_Jm) IN @hiddenUnits",
                        new { hiddenUnits }, cancellationToken: ct, commandTimeout: 120))).ToHashSet();
                _groupsLoadedAt = DateTime.Now;
                _groupsKey = groupsKey;
            }

            // the search index does not answer – categories of the products in stock from XL
            if (_indexLinks is null && _indexFailed && (_xlLinks is null || refreshTree || warehouseKey != _warehouseKey))
            {
                _xlLinks = (await conn.QueryAsync<(int ProductId, int GroupId)>(new CommandDefinition("""
                    SELECT g.TwG_GIDNumer, g.TwG_GrONumer FROM cdn.TwrGrupy g
                    WHERE g.TwG_GIDTyp = 16 AND EXISTS (SELECT 1 FROM cdn.TwrZasoby z
                        WHERE z.TwZ_TwrNumer = g.TwG_GIDNumer AND z.TwZ_MagNumer IN @warehouses AND z.TwZ_Ilosc > 0)
                    """, new { warehouses }, cancellationToken: ct, commandTimeout: 180)))
                    .GroupBy(l => l.ProductId)
                    .ToDictionary(g => g.Key, g => g.Select(l => l.GroupId).ToArray());
            }

            _warehouseKey = warehouseKey;
            _products = products;
            Build();
            _firstLoad.TrySetResult();
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>New snapshot with the last loaded stock (after the index links changed).</summary>
    private async Task RebuildAsync(CancellationToken ct)
    {
        if (!Current.IsLoaded)
            return; // the first stock load builds it
        if (_indexLinks is null)
        {
            await LoadAsync(ct); // XL links are loaded with the stock
            return;
        }

        await _loadGate.WaitAsync(ct);
        try
        {
            Build();
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private void Build()
    {
        var o = options.CurrentValue;
        var links = (IReadOnlyDictionary<int, int[]>?)_indexLinks ?? _xlLinks ?? new Dictionary<int, int[]>();
        Current = CatalogSnapshot.Build(_products, _groups, links, _hidden, o.CategoryRootId, indexed: _indexLinks is not null);
        logger.LogDebug("Catalog: {Products} products in stock ({Warehouses}), {Indexed} in the search index, {Categories} categories",
            _products.Count, _warehouseKey, Current.IndexedProducts, Current.Categories.Count);
    }
}
