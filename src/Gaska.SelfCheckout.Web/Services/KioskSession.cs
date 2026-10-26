using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Application.Checkout;
using Gaska.SelfCheckout.Domain.Catalog;
using Gaska.SelfCheckout.Domain.Customers;
using Gaska.SelfCheckout.Domain.Sales;
using Gaska.SelfCheckout.Infrastructure.Erp;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Web.Services;

public enum ToastKind
{
    Success,
    Info,
    Warning,
    Error
}

public sealed record Toast(Guid Id, ToastKind Kind, string Title, string? Text, DateTime ExpiresAt);

public enum ShoppingMode
{
    Scan,
    Search,
    Categories
}

/// <summary>
/// State of the kiosk for one UI connection: document type (receipt / invoice), the customer (invoice by tax id, or a
/// receipt customer identified by their card) and their prices,
/// cart, shopping mode, notifications and customer activity.
/// </summary>
public sealed class KioskSession : IDisposable
{
    private readonly IProductCatalog _catalog;
    private readonly ICustomerPricing _pricing;
    private readonly IOptionsMonitor<ErpOptions> _erp;
    private readonly ILogger<KioskSession> _logger;
    private readonly MaintenanceMode _maintenance;
    private readonly List<Toast> _toasts = [];
    private readonly HashSet<int> _pricingInProgress = [];

    public KioskSession(IProductCatalog catalog, ICustomerPricing pricing, IOptionsMonitor<ErpOptions> erp, MaintenanceMode maintenance,
        ILogger<KioskSession> logger)
    {
        _catalog = catalog;
        _pricing = pricing;
        _erp = erp;
        _maintenance = maintenance;
        _logger = logger;
        _pricing.PriceCalculated += OnPriceCalculated;
        _maintenance.Register(this);
    }

    public Cart Cart { get; } = new();
    public ShoppingMode Mode { get; private set; } = ShoppingMode.Scan;

    /// <summary>Null until the customer chooses receipt or invoice on the start screen.</summary>
    public SaleDocumentKind? DocumentKind { get; private set; }

    /// <summary>Invoice customer, or the customer who identified themselves with a card on a receipt.</summary>
    public Customer? Customer { get; private set; }
    public bool WelcomeVisible => DocumentKind is null;
    public bool IsInvoice => DocumentKind == SaleDocumentKind.Invoice;

    /// <summary>Nobody is shopping on this screen: start screen with an empty cart.</summary>
    public bool IsIdle => WelcomeVisible && Cart.IsEmpty;

    /// <summary>Prices are calculated by XL for the customer (or the one-off customer) and the document type.</summary>
    public PricingContext Pricing => PricingContext.For(Customer, DocumentKind ?? SaleDocumentKind.Receipt);
    public int? LastAddedProductId { get; private set; }
    public DateTime LastActivity { get; private set; } = DateTime.Now;
    public IReadOnlyList<Toast> Toasts => _toasts;

    /// <summary>Cart lines whose price (for the current quantity) is still being calculated.</summary>
    public bool PricesPending => Cart.Lines.Any(l => l.Quote is null);

    public event Action? Changed;

    public void Touch() => LastActivity = DateTime.Now;

    public void SetMode(ShoppingMode mode)
    {
        Mode = mode;
        Touch();
        Notify();
    }

    /// <summary>Receipt for an anonymous customer.</summary>
    public void StartReceipt(ShoppingMode mode = ShoppingMode.Scan)
    {
        DocumentKind = SaleDocumentKind.Receipt;
        Customer = null;
        Cart.ClearQuotes();
        Mode = mode;
        Touch();
        Notify();
        _ = PriceCartAsync();
    }

    /// <summary>Invoice for the selected customer – the cart is repriced with the customer's prices.</summary>
    public void StartInvoice(Customer customer)
    {
        DocumentKind = SaleDocumentKind.Invoice;
        Customer = customer;
        Cart.ClearQuotes();
        Touch();
        Notify();
        _ = PriceCartAsync();
    }

    /// <summary>Receipt customer identified by their card – the cart is repriced with the customer's prices.</summary>
    public void IdentifyCustomer(Customer customer)
    {
        DocumentKind ??= SaleDocumentKind.Receipt;
        Customer = customer;
        Cart.ClearQuotes();
        Touch();
        Notify();
        _ = PriceCartAsync();
    }

    /// <summary>Receipt instead of an invoice – the customer stays identified; prices are calculated for the receipt.</summary>
    public void SwitchToReceipt()
    {
        DocumentKind = SaleDocumentKind.Receipt;
        Cart.ClearQuotes();
        Touch();
        Notify();
        _ = PriceCartAsync();
    }

    /// <summary>Logs the customer out: receipt with the one-off customer's prices.</summary>
    public void ForgetCustomer()
    {
        DocumentKind = SaleDocumentKind.Receipt;
        Customer = null;
        Cart.ClearQuotes();
        Touch();
        Notify();
        _ = PriceCartAsync();
    }

    /// <summary>
    /// Price of one piece shown on product cards – always calculated by XL (also for the one-off customer); null while
    /// calculating. The list price is never shown, it would change in front of the customer a moment later.
    /// </summary>
    public PriceQuote? DisplayPrice(Product product) => _pricing.TryGetCached(Pricing, product.Id);

    /// <summary>Quantity discount tiers of a product (empty while not calculated).</summary>
    public IReadOnlyList<QuantityBreak> QuantityBreaks(int productId) => _pricing.TryGetCachedBreaks(Pricing, productId) ?? [];

    /// <summary>Starts (low priority) calculation of prices and quantity tiers for products shown on screen.</summary>
    public void RequestBrowsePrices(IEnumerable<int> productIds)
    {
        var context = Pricing;
        var ids = productIds.ToList();
        var missing = ids.Where(id => _pricing.TryGetCached(context, id) is null).Select(id => new PriceRequest(id, 1)).ToList();
        _ = SafeAsync(async () =>
        {
            if (missing.Count > 0)
                await _pricing.GetPricesAsync(context, missing, PricingPriority.Low);
            await _pricing.CalculateBreaksAsync(context, ids);
        });
    }

    /// <summary>Handles a scanned code (EAN, product code, supplier code).</summary>
    public async Task<bool> ScanAsync(string code)
    {
        Touch();
        DocumentKind ??= SaleDocumentKind.Receipt; // scanning on the start screen starts a receipt
        code = code.Trim();
        if (code.Length == 0)
            return false;

        try
        {
            var product = await _catalog.FindByCodeAsync(code);
            if (product is null)
            {
                ShowToast(ToastKind.Warning, "Nie rozpoznano kodu", $"Kod {code} nie został znaleziony. Wyszukaj produkt ręcznie.");
                return false;
            }

            var units = _erp.CurrentValue.Units;
            if (units.IsHidden(product.Unit))
            {
                ShowToast(ToastKind.Info, units.KindOf(product.Unit) == SaleUnitKind.Weighed ? "Towar na wagę" : "Towar cięty na wymiar",
                    $"„{product.Name}” kupisz u sprzedawcy w sklepie.", seconds: 6);
                return false;
            }

            return Add(product);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Code lookup failed for {Code}", code);
            ShowToast(ToastKind.Error, "Chwilowy problem", "Nie udało się sprawdzić produktu. Spróbuj ponownie.");
            return false;
        }
    }

    public bool Add(Product product, decimal quantity = 1)
    {
        Touch();
        DocumentKind ??= SaleDocumentKind.Receipt;
        var result = Cart.Add(product, quantity);
        switch (result)
        {
            case CartChangeResult.Added:
            case CartChangeResult.Increased:
                LastAddedProductId = product.Id;
                _ = PriceCartAsync();
                RequestBreaks(product.Id);
                ShowToast(ToastKind.Success, "Dodano do koszyka", product.Name);
                return true;
            case CartChangeResult.LimitedByStock:
                LastAddedProductId = product.Id;
                _ = PriceCartAsync();
                ShowToast(ToastKind.Warning, "Brak większej ilości",
                    $"Dostępne: {product.AvailableQuantity:0.##} {product.Unit} – „{product.Name}”.");
                return false;
            default:
                ShowToast(ToastKind.Warning, "Towar chwilowo niedostępny", $"„{product.Name}” – brak na stanie, nie można go teraz kupić na tej kasie.", seconds: 6);
                return false;
        }
    }

    public void SetQuantity(int productId, decimal quantity)
    {
        Touch();
        if (Cart.SetQuantity(productId, quantity) == CartChangeResult.LimitedByStock)
            ShowToast(ToastKind.Warning, "Brak większej ilości", "Osiągnięto dostępny stan magazynowy.");
        Notify();
        _ = PriceCartAsync(); // quantity promotions – XL recalculates the price for the new quantity
    }

    public void Remove(int productId)
    {
        Touch();
        Cart.Remove(productId);
        Notify();
    }

    /// <summary>Refreshes stock right before payment (and prices of changed quantities). Returns the changes to show to the customer.</summary>
    public async Task<IReadOnlyList<string>> RefreshBeforePaymentAsync()
    {
        var fresh = await _catalog.GetProductsAsync(Cart.Lines.Select(l => l.Product.Id));
        var before = Cart.Lines.ToDictionary(l => l.Product.Id, l => (l.Product.GrossPrice, l.Quantity, l.Product.Name));
        var changed = Cart.Refresh(fresh);
        var messages = new List<string>();
        foreach (var line in changed)
        {
            var old = before[line.Product.Id];
            if (!Cart.Lines.Contains(line))
                messages.Add($"„{old.Name}” – produkt jest już niedostępny i został usunięty z koszyka.");
            else if (line.Quantity != old.Quantity)
                messages.Add($"„{old.Name}” – dostępna ilość zmniejszona do {line.Quantity:0.##}.");
        }

        await PriceCartAsync();
        Notify();
        return messages;
    }

    /// <summary>
    /// Calculates (high priority) the prices of cart lines without a price for their current quantity – cached prices
    /// are applied at once. Completes when the calculation started now has finished.
    /// </summary>
    public Task PriceCartAsync()
    {
        var context = Pricing;
        var requests = new List<PriceRequest>();
        foreach (var line in Cart.Lines.Where(l => l.Quote is null).ToList())
        {
            if (_pricing.TryGetCached(context, line.Product.Id, line.Quantity) is { } cached)
                Cart.SetQuote(line.Product.Id, line.Quantity, cached);
            else if (_pricingInProgress.Add(line.Product.Id))
                requests.Add(new PriceRequest(line.Product.Id, line.Quantity));
        }

        return requests.Count == 0 ? Task.CompletedTask : PriceLinesAsync(context, requests);
    }

    /// <summary>
    /// Prices of cart lines. A line whose price XL cannot calculate gets the list price, so the payment is never blocked;
    /// results for another customer / document type or an outdated quantity are dropped and calculated again.
    /// </summary>
    private async Task PriceLinesAsync(PricingContext context, IReadOnlyList<PriceRequest> requests)
    {
        IReadOnlyDictionary<int, PriceQuote> prices;
        try
        {
            prices = await _pricing.GetPricesAsync(context, requests, PricingPriority.High);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prices could not be calculated for {Customer}", context.CustomerAcronym ?? "one-off customer");
            prices = new Dictionary<int, PriceQuote>();
        }
        finally
        {
            foreach (var r in requests)
                _pricingInProgress.Remove(r.ProductId);
        }

        if (Pricing == context)
        {
            var listPrices = 0;
            foreach (var r in requests)
            {
                var line = Cart.Lines.FirstOrDefault(l => l.Product.Id == r.ProductId);
                if (line is null)
                    continue;
                if (prices.TryGetValue(r.ProductId, out var quote))
                    Cart.SetQuote(r.ProductId, r.Quantity, quote);
                else if (Cart.SetQuote(r.ProductId, r.Quantity, PriceQuote.ListPrice(line.Product.GrossPrice)))
                    listPrices++;
            }

            if (listPrices > 0)
            {
                _logger.LogWarning("{Count} price(s) unavailable for {Customer} – list prices used", listPrices, context.CustomerAcronym ?? "one-off customer");
                ShowToast(ToastKind.Info, "Cena katalogowa", "Dla części produktów obowiązuje cena katalogowa.");
            }
        }

        // quantities / customer changed while calculating
        await PriceCartAsync();
        Notify();
    }

    public CheckoutRequest CreateCheckoutRequest() =>
        new(Cart.Snapshot(), DocumentKind ?? SaleDocumentKind.Receipt, Customer);

    public void Reset()
    {
        Cart.Clear();
        Mode = ShoppingMode.Scan;
        DocumentKind = null;
        Customer = null;
        LastAddedProductId = null;
        _toasts.Clear();
        Notify();
    }

    public void ShowToast(ToastKind kind, string title, string? text = null, int seconds = 4)
    {
        _toasts.Add(new Toast(Guid.NewGuid(), kind, title, text, DateTime.Now.AddSeconds(seconds)));
        while (_toasts.Count > 3)
            _toasts.RemoveAt(0);
        Notify();
    }

    public void ExpireToasts()
    {
        if (_toasts.RemoveAll(t => t.ExpiresAt <= DateTime.Now) > 0)
            Notify();
    }

    public void Notify() => Changed?.Invoke();

    /// <summary>Quantity tiers of a product added to the cart (the cart shows the next tier).</summary>
    private void RequestBreaks(int productId)
    {
        var context = Pricing;
        if (_pricing.TryGetCachedBreaks(context, productId) is null)
            _ = SafeAsync(() => _pricing.CalculateBreaksAsync(context, [productId]));
    }

    // Raised by the shared pricing service on any thread: only refresh the view (prices shown on product cards come
    // from the pricing cache); cart lines are priced by PriceLinesAsync on this session's own context.
    private void OnPriceCalculated(PricingContext context, int productId)
    {
        if (context == Pricing)
            Notify();
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background pricing failed");
        }
    }

    public void Dispose()
    {
        _pricing.PriceCalculated -= OnPriceCalculated;
        _maintenance.Unregister(this);
    }
}
