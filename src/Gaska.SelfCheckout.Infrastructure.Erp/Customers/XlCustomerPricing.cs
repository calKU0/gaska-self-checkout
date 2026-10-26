using System.Collections.Concurrent;
using System.Globalization;
using cdn_api;
using Dapper;
using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Domain.Sales;
using Gaska.SelfCheckout.Infrastructure.Erp.Catalog;
using Gaska.SelfCheckout.Infrastructure.Erp.XlApi;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Gaska.SelfCheckout.Infrastructure.Erp.Customers;

/// <summary>
/// Prices calculated by XL itself for every sale – the identified customer or the one-off customer of an
/// anonymous receipt, in the session of the kiosk operator (operator discounts apply too). XLPrzeliczRabat simulates
/// adding the product to the document for a quantity: price type, discounts, promotions incl. quantity promotions.
/// One calculation takes ~0.7 s, so results are cached and browsing requests are low priority and dropped when the
/// customer moves on. Quantity tiers come from the XL discount matrix (cdn.MacierzRabatowa).
/// </summary>
public sealed class XlCustomerPricing(XlSession xl, CatalogCache catalog, ILogger<XlCustomerPricing> logger) : ICustomerPricing
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Tiers shown per product (each costs one XL calculation).</summary>
    private const int MaxBreaks = 2;

    private readonly ConcurrentDictionary<(int Customer, SaleDocumentKind Kind, int Product, decimal Quantity), (PriceQuote Quote, DateTime At)> _cache = new();
    private readonly ConcurrentDictionary<(int Customer, SaleDocumentKind Kind, int Product), (IReadOnlyList<QuantityBreak> Breaks, DateTime At)> _breaks = new();
    private int _browseGeneration;
    private int _breakGeneration;
    private int? _oneTimeCustomerId; // the one-off customer card may have GID 0 in XL

    public event Action<PricingContext, int>? PriceCalculated;

    public PriceQuote? TryGetCached(PricingContext context, int productId, decimal quantity = 1) =>
        _cache.TryGetValue((context.CustomerId, context.DocumentKind, productId, quantity), out var e) && Fresh(e.At) ? e.Quote : null;

    public IReadOnlyList<QuantityBreak>? TryGetCachedBreaks(PricingContext context, int productId) =>
        _breaks.TryGetValue((context.CustomerId, context.DocumentKind, productId), out var e) && Fresh(e.At) ? e.Breaks : null;

    public async Task<IReadOnlyDictionary<int, PriceQuote>> GetPricesAsync(
        PricingContext context, IReadOnlyCollection<PriceRequest> items, PricingPriority priority, CancellationToken ct = default)
    {
        var result = new Dictionary<int, PriceQuote>();
        var missing = new List<PriceRequest>();
        foreach (var item in items.DistinctBy(i => i.ProductId))
        {
            if (TryGetCached(context, item.ProductId, item.Quantity) is { } cached)
                result[item.ProductId] = cached;
            else
                missing.Add(item);
        }

        if (missing.Count == 0)
            return result;

        var codes = await ProductCodesAsync(missing.Select(m => m.ProductId).ToList(), ct);
        var high = priority == PricingPriority.High;
        // a new browsing request makes the previous (not yet calculated) one obsolete
        var generation = high ? 0 : Interlocked.Increment(ref _browseGeneration);

        foreach (var item in missing)
        {
            ct.ThrowIfCancellationRequested();
            if (!codes.TryGetValue(item.ProductId, out var code))
                continue;

            var quote = await xl.RunAsync(_ =>
                !high && generation != Volatile.Read(ref _browseGeneration)
                    ? null // obsolete browsing request
                    : Calculate(context, code, item.Quantity), high);
            if (quote is null)
                continue;

            Store(context, item.ProductId, item.Quantity, quote);
            result[item.ProductId] = quote;
            PriceCalculated?.Invoke(context, item.ProductId);
        }

        return result;
    }

    public async Task CalculateBreaksAsync(PricingContext context, IReadOnlyCollection<int> productIds, CancellationToken ct = default)
    {
        var missing = productIds.Distinct().Where(id => TryGetCachedBreaks(context, id) is null).ToList();
        if (missing.Count == 0)
            return;

        var generation = Interlocked.Increment(ref _breakGeneration);
        var customerId = context.CustomerId != 0 ? context.CustomerId : await OneTimeCustomerIdAsync(ct);
        var codes = await ProductCodesAsync(missing, ct);
        await using var conn = new SqlConnection(xl.Erp.ConnectionString);
        foreach (var id in missing.Where(codes.ContainsKey))
        {
            if (generation != Volatile.Read(ref _breakGeneration))
                return; // the customer moved on
            await CalculateBreaksAsync(conn, context, customerId, id, codes[id], generation, ct);
        }
    }

    private async Task CalculateBreaksAsync(SqlConnection conn, PricingContext context, int customerId, int id, string code, int generation, CancellationToken ct)
    {
        var thresholds = (await conn.QueryAsync<decimal>(new CommandDefinition(ThresholdsSql,
                new { kntID = customerId, twrID = id, @operator = xl.Settings.Operator }, commandTimeout: 15, cancellationToken: ct)))
            .Where(t => t > 1).Distinct().Order().Take(MaxBreaks).ToList();

        var breaks = new List<QuantityBreak>();
        if (thresholds.Count > 0)
        {
            var unit = TryGetCached(context, id) ?? await CalculateLowAsync(context, id, code, 1, generation);
            if (unit is null)
                return;
            // one calculation per tier: the thresholds of item promotions (TPr_Prog) are quantities – the matrix returns
            // no value thresholds for lines – so a tier is shown when it is cheaper than the previous one
            var previous = unit;
            foreach (var quantity in thresholds)
            {
                var quote = TryGetCached(context, id, quantity) ?? await CalculateLowAsync(context, id, code, quantity, generation);
                if (quote is null)
                    return;
                if (quote.Price >= previous.Price)
                    continue;
                // the tier is shown against the price before discounts of one piece
                breaks.Add(new QuantityBreak(quantity, quote with { BasePrice = unit.BasePrice }));
                previous = quote;
            }
        }

        _breaks[(context.CustomerId, context.DocumentKind, id)] = (breaks, DateTime.UtcNow);
        PriceCalculated?.Invoke(context, id);
    }

    private async Task<PriceQuote?> CalculateLowAsync(PricingContext context, int productId, string code, decimal quantity, int generation)
    {
        var quote = await xl.RunAsync(_ => generation != Volatile.Read(ref _breakGeneration) ? null : Calculate(context, code, quantity), highPriority: false);
        if (quote is not null)
            Store(context, productId, quantity, quote);
        return quote;
    }

    private PriceQuote? Calculate(PricingContext context, string productCode, decimal quantity)
    {
        var info = new XLRabatyComInfo_20251
        {
            Wersja = xl.ApiVersion,
            Typ = context.DocumentKind == SaleDocumentKind.Invoice ? XlSalesDocumentService.InvoiceType : XlSalesDocumentService.ReceiptType,
            FlagaNB = "B",
            Ilosc = quantity.ToString("0.####", CultureInfo.InvariantCulture),
            Akronim = context.CustomerAcronym ?? xl.Settings.CustomerAcronym,
            TowarKod = productCode
        };
        var rc = xl.CallWithRelogin(() => cdn_api.cdn_api.XLPrzeliczRabat(info));
        if (rc != 0 || !TryParse(info.CenaPoRabacie, out var price))
        {
            logger.LogWarning("XLPrzeliczRabat failed for {Customer}/{Product} x{Quantity}: code {Rc}", info.Akronim, productCode, quantity, rc);
            return null;
        }

        // CenaTowaru – the price of the customer's price type before discounts. The discount from the
        // customer card (Knt_Rabat, RabatGlobalny in 1/100 %, negative = mark-up) is part of the customer's own price:
        // only promotions (PRM) are shown as discounts.
        var typePrice = TryParse(info.CenaTowaru, out var b) && b > 0 ? b : price;
        var customerPrice = Round(typePrice * (1 - info.RabatGlobalny / 10000m));
        return new PriceQuote(Math.Max(customerPrice, Round(price)), Round(price));
    }

    private void Store(PricingContext context, int productId, decimal quantity, PriceQuote quote) =>
        _cache[(context.CustomerId, context.DocumentKind, productId, quantity)] = (quote, DateTime.UtcNow);

    private static bool Fresh(DateTime at) => DateTime.UtcNow - at < CacheLifetime;
    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static bool TryParse(string? text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    /// <summary>Customer card of anonymous receipts (its discounts apply to them).</summary>
    private async Task<int> OneTimeCustomerIdAsync(CancellationToken ct)
    {
        if (_oneTimeCustomerId is { } id)
            return id;
        await using var conn = new SqlConnection(xl.Erp.ConnectionString);
        var found = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT TOP 1 Knt_GIDNumer FROM cdn.KntKarty WHERE Knt_Akronim = @acronym", new { acronym = xl.Settings.CustomerAcronym },
            cancellationToken: ct)) ?? 0;
        _oneTimeCustomerId = found;
        return found;
    }

    private async Task<Dictionary<int, string>> ProductCodesAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var snapshot = catalog.Current;
        var codes = ids.Where(snapshot.Products.ContainsKey).ToDictionary(id => id, id => snapshot.Products[id].Code);
        var unknown = ids.Where(id => !codes.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
        {
            await using var conn = new SqlConnection(xl.Erp.ConnectionString);
            foreach (var (id, code) in await conn.QueryAsync<(int, string)>(new CommandDefinition(
                         "SELECT Twr_GIDNumer, Twr_Kod FROM cdn.TwrKarty WHERE Twr_GIDNumer IN @unknown", new { unknown }, cancellationToken: ct)))
                codes[id] = code.Trim();
        }

        return codes;
    }

    /// <summary>
    /// Quantity thresholds of the promotions applying to the customer and product in the company unit of the kiosk
    /// operator (XL discount matrix). Thresholds after
    /// the first promotion with "skip the following" (PRM_POMIN) are ignored – XL does not apply them.
    /// </summary>
    private const string ThresholdsSql = """
        SET NOCOUNT ON;
        DECLARE @promocjeProgi TABLE(
            LP int IDENTITY(1,1), PRM_ID int NOT NULL, PRM_AKRONIM varchar(200) NULL, PRM_TYP int NULL, PRM_PRIORYTET int NULL,
            PRM_PRIORYTETLP int, TWG_NUMER int NULL, TWR_NUMER int NULL, UPUST_TYP int NULL, UPUST_WARTOSC decimal(15,4) NULL,
            UPUST_WALUTA varchar(3) NULL, UPUST_FLAGANB varchar(1) NULL, UPUST_PROG decimal(15,4) NULL, KNT_NUMER int NULL,
            PRM_OPERACJA smallint NULL, PRM_POMIN smallint NULL, UPUST_ID int NULL, ZEST_NUMER int NULL, UPUST_SORT int,
            BUDZET_MAKS decimal(15,4), TPR_ID int, PRM_LIMITTYP tinyint, TPR_LIMITTYP tinyint, PRM_LIMITWARTOSC decimal(15,4),
            TPR_LIMITWARTOSC decimal(15,4), PRMPROGTYP tinyint, PRMPROGWARTOSC decimal(15,4), OKRESTRANSAKCJIOD int,
            OKRESTRANSAKCJIDO int, PROGTPR int, PRM_WarunekProgSQL varchar(2000), LIMITRODZAJ tinyint, LIMITWARTOSCKNT tinyint,
            CZYLIMITKNT tinyint);
        DECLARE @date int = DATEDIFF(ss, '1990-01-01T00:00:00', GETDATE());
        -- company unit of the kiosk operator: promotions can be limited to it (Struktura firmy → promocje centrum)
        DECLARE @frsId varchar(20) = ISNULL((SELECT TOP 1 CAST(Ope_FrsID AS varchar(20)) FROM cdn.OpeKarty WHERE Ope_Ident = @operator), '1');

        INSERT INTO @promocjeProgi
        EXEC CDN.MacierzRabatowa @lNagPoz = 2, @lRodzajDok = 1, @lData = @date, @lKntTyp = 32, @lKntNumer = @kntID,
            @lTwrTyp = 16, @lTwrNumer = @twrID, @lFormaNr = 0, @lFormaTermin = 0, @lFormaData = 0, @lSposobDostawy = 0,
            @lMagazyn = 1, @sFrsId = @frsId, @lZstNumer = 0, @lGIDTyp = 0, @lGIDNumer = 0, @lGIDLp = 1, @lPromocjaID = 0,
            @lPromocjaProgID = 0, @lPromocjaWTRID = 0, @lProgWTRID = 0, @sDsts = NULL;

        -- the first promotion with "skip the following": its last tier ends the list
        DECLARE @lpPomin int;
        SELECT @lpPomin = MAX(LP) FROM @promocjeProgi
        WHERE PRM_ID IN (SELECT PRM_ID FROM @promocjeProgi WHERE LP = (SELECT MIN(LP) FROM @promocjeProgi WHERE PRM_POMIN <> 0));

        SELECT UPUST_PROG FROM @promocjeProgi WHERE UPUST_PROG > 0 AND (@lpPomin IS NULL OR LP <= @lpPomin);
        """;
}
