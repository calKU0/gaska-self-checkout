using System.Globalization;
using System.Text.Json;
using Gaska.SelfCheckout.Application;
using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Application.Options;
using Gaska.SelfCheckout.Application.Printing;
using Gaska.SelfCheckout.Devices.Polcard;
using Gaska.SelfCheckout.Devices.Posnet;
using Gaska.SelfCheckout.Domain.Catalog;
using Gaska.SelfCheckout.Domain.Customers;
using Gaska.SelfCheckout.Domain.Payments;
using Gaska.SelfCheckout.Domain.Sales;
using Gaska.SelfCheckout.Infrastructure;
using Gaska.SelfCheckout.Infrastructure.Erp;
using Gaska.SelfCheckout.Infrastructure.Erp.Catalog;
using Gaska.SelfCheckout.Infrastructure.Imaging;
using Gaska.SelfCheckout.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Service tool for testing every integration separately (terminal, printer, XL, catalog, registries).
Console.OutputEncoding = System.Text.Encoding.UTF8;
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);
builder.Configuration.AddJsonFile(StationSettingsStore.FilePath, optional: true);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Services
    .AddKasaApplication(builder.Configuration)
    .AddKasaInfrastructure(builder.Configuration)
    .AddComarchXl(builder.Configuration)
    .AddPolcardTerminal(builder.Configuration)
    .AddPosnetPrinter(builder.Configuration);

using var host = builder.Build();
var sp = host.Services;
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var command = args.FirstOrDefault() ?? "help";
var arg = args.Skip(1).ToArray();
var inv = CultureInfo.InvariantCulture;

switch (command)
{
    case "hello":
        Print(await sp.GetRequiredService<IPaymentTerminal>().TestConnectionAsync());
        break;

    case "ping":
        Print(await sp.GetRequiredService<IPaymentTerminal>().CheckAsync());
        break;

    case "last":
        Print(await sp.GetRequiredService<IPaymentTerminal>().GetLastTransactionAsync());
        break;

    case "upload-status":
        Print(await sp.GetRequiredService<IPaymentTerminal>().GetUploadStatusAsync());
        break;

    case "close-day":
        Print(await sp.GetRequiredService<IPaymentTerminal>().CloseDayAsync());
        break;

    case "sale":
    {
        // sale <amount> [abort_after_s|0] [card|blik] – with card/blik the terminal question
        // "Czy uzyc platnosci Blik?" is answered automatically (no console input needed)
        var amount = decimal.Parse(arg.ElementAtOrDefault(0) ?? "1.00", inv);
        using var cts = arg.ElementAtOrDefault(1) is { } s && s != "0" ? new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(s))) : new CancellationTokenSource();
        ITerminalInteraction interaction = arg.ElementAtOrDefault(2) is { } mode ? new AutoInteraction(mode == "blik") : new ConsoleInteraction();
        var tx = await sp.GetRequiredService<IPaymentTerminal>().SaleAsync(
            new CardSaleRequest(amount, (await sp.GetRequiredService<ITransactionJournal>().NextEcrTransactionNumberAsync()).ToString()),
            interaction, cts.Token);
        Print(tx);
        PrintSlip(tx);
        break;
    }

    case "reverse":
    {
        var amount = decimal.Parse(arg.ElementAtOrDefault(0) ?? "1.00", inv);
        Print(await sp.GetRequiredService<IPaymentTerminal>().ReverseAsync(
            new CardReversalRequest(amount, arg.ElementAtOrDefault(1), (await sp.GetRequiredService<ITransactionJournal>().NextEcrTransactionNumberAsync()).ToString()),
            new ConsoleInteraction()));
        break;
    }

    case "printer-status":
        Print(await sp.GetRequiredService<IReceiptPrinter>().GetStatusAsync());
        break;

    case "print-test":
    {
        var printer = sp.GetRequiredService<IReceiptPrinter>();
        await printer.PrintAsync(new PrintDocument(PrintDocumentKind.Other,
        [
            PrintLine.Center("TEST WYDRUKU", bold: true),
            new PrintLine("Zażółć gęślą jaźń ZAŻÓŁĆ GĘŚLĄ JAŹŃ"),
            new PrintLine(CardSlipFormatter.Cols("Lewa", "Prawa", printer.LineWidth)),
            PrintLine.Separator,
            PrintLine.Qr("PA-1/26/TEST"),
            new PrintLine("Szeroki", Wide: true),
            new PrintLine("Do prawej", PrintAlign.Right)
        ]));
        Console.WriteLine($"Printed (line width {printer.LineWidth}).");
        break;
    }

    case "image":
    {
        // image <productId> [output.jpg] – first product photo as the kiosk shows it (thumbnail)
        var original = await sp.GetRequiredService<IProductImages>().GetFirstImageAsync(int.Parse(arg[0]));
        if (original is null)
        {
            Console.WriteLine("No photo.");
            break;
        }

        var output = arg.ElementAtOrDefault(1) ?? $"product-{arg[0]}.jpg";
        File.WriteAllBytes(output, Thumbnail.ToJpeg(original, 360));
        Console.WriteLine($"Photo {original.Length} B -> thumbnail {new FileInfo(output).Length} B: {Path.GetFullPath(output)}");
        break;
    }

    case "posnet":
        // posnet <command> [name=value...] – raw POSNET command, e.g. posnet pccfgget pi=2
        Print(await ((PosnetPrinter)sp.GetRequiredService<IReceiptPrinter>()).ExecuteAsync(arg[0],
            arg.Skip(1).Select(a => a.Split('=', 2)).Select(a => (a[0], a.ElementAtOrDefault(1) ?? "")).ToArray()));
        break;

    case "upload-map":
    {
        // upload-map [file] [slot]
        var file = arg.ElementAtOrDefault(0) ?? Path.Combine(AppContext.BaseDirectory, "pickup-map.png");
        var slot = arg.Length > 1 ? int.Parse(arg[1]) : PickupTicketBuilder.MapImageSlot;
        await using var stream = File.OpenRead(file);
        var parts = await PrinterImageUploader.UploadAsync(sp.GetRequiredService<IReceiptPrinter>(), stream, slot);
        sp.GetRequiredService<StationSettingsStore>().Save($"{PickupOptions.Section}:MapImageParts", parts.Count);
        Console.WriteLine($"Image {parts[0].Width}x{parts.Sum(p => p.Height)} stored in slots {slot}-{slot + parts.Count - 1}.");
        break;
    }

    case "map-preview":
    {
        // map-preview [file] [output.png] [norotate] – renders what the printer will print
        await using var stream = File.OpenRead(arg.ElementAtOrDefault(0) ?? Path.Combine(AppContext.BaseDirectory, "pickup-map.png"));
        var image = MonochromeImageConverter.Convert(stream, 432, rotateLandscape: arg.ElementAtOrDefault(2) != "norotate");
        var output = arg.ElementAtOrDefault(1) ?? "map-preview.png";
        MonochromeImageConverter.SavePreview(image, output);
        Console.WriteLine($"{image.Width}x{image.Height} ({MonochromeImageConverter.Split(image).Count} slots) -> {Path.GetFullPath(output)}");
        break;
    }

    case "pickup-test":
    {
        // pickup-test <productId...> – prints a pick-up ticket for products from the non-primary warehouse
        var ids = arg.Select(int.Parse).ToList();
        var products = await sp.CreateScope().ServiceProvider.GetRequiredService<IProductCatalog>().GetProductsAsync(ids);
        var erp = sp.GetRequiredService<IOptionsMonitor<ErpOptions>>().CurrentValue;
        var pickupWarehouse = erp.Warehouses.FirstOrDefault(w => !w.Primary)?.Code ?? "";
        var cart = new CartSnapshot(products.Values.Select(p => new CartSnapshotLine(p.Id, p.Code, p.Name, p.Unit, 1, p.GrossPrice, p.GrossPrice,
            [new WarehouseAllocation(pickupWarehouse, 1, false)])).ToList());
        await PrintPickupTicket($"PA-TEST/{DateTime.Now:yy}/{erp.XlApi.ReceiptSeries}", cart);
        break;
    }

    case "xl-login":
        Print(await sp.GetRequiredService<ISalesDocumentService>().GetStatusAsync());
        break;

    case "xl-receipt":
    case "xl-invoice":
    {
        // xl-receipt <productId> [quantity] | <productId>:<qty>...   |   xl-invoice <tax id or card> <same>
        var invoice = command == "xl-invoice";
        Customer? customer = null;
        if (!invoice && arg.Length > 0 && arg[0].StartsWith("card="))
        {
            // xl-receipt card=<number> ... – receipt for a customer identified by their card (customer prices)
            customer = (await sp.GetRequiredService<ICustomerDirectory>().FindByCardAsync(arg[0][5..])).FirstOrDefault()
                       ?? throw new InvalidOperationException("Customer card not found.");
            arg = arg[1..];
        }
        else if (invoice)
        {
            // "<code>:<n>" picks the n-th match (several cards may share a tax id or card number)
            // "gid:<Knt_GIDNumer>" uses a customer card that cannot be found by tax id / card number
            var parts = arg[0].Split(':');
            if (parts[0] == "gid")
            {
                customer = await sp.GetRequiredService<ICustomerDirectory>().GetByIdAsync(int.Parse(parts[1]))
                           ?? throw new InvalidOperationException("Customer not found.");
            }
            else
            {
                customer = (await FindCustomer(parts[0]))
                           .ElementAtOrDefault(parts.Length > 1 ? int.Parse(parts[1]) : 0)
                           ?? throw new InvalidOperationException("Customer not found.");
            }
            arg = arg[1..];
        }

        // "<productId> [qty]" or several "<productId>:<qty>"
        var items = arg.Length == 2 && !arg[1].Contains(':')
            ? [(Id: int.Parse(arg[0]), Qty: decimal.Parse(arg[1], inv))]
            : arg.Select(a => a.Split(':')).Select(a => (Id: int.Parse(a[0]), Qty: a.Length > 1 ? decimal.Parse(a[1], inv) : 1m)).ToList();
        var ids = items.Select(i => i.Id).ToList();
        var products = await sp.CreateScope().ServiceProvider.GetRequiredService<IProductCatalog>().GetProductsAsync(ids);
        var quotes = await sp.GetRequiredService<ICustomerPricing>().GetPricesAsync(
            PricingContext.For(customer, invoice ? SaleDocumentKind.Invoice : SaleDocumentKind.Receipt),
            items.Select(i => new PriceRequest(i.Id, i.Qty)).ToList(), PricingPriority.High);
        var prices = ids.ToDictionary(id => id, id => quotes.TryGetValue(id, out var q) ? q.Price : products[id].GrossPrice);
        var cart = new CartSnapshot(items.Select(i =>
        {
            var p = products[i.Id];
            var price = prices[i.Id];
            return new CartSnapshotLine(p.Id, p.Code, p.Name, p.Unit, i.Qty, price, Math.Round(price * i.Qty, 2), p.Allocate(i.Qty));
        }).ToList());
        foreach (var line in cart.Lines)
            Console.WriteLine($"{line.Code} x{line.Quantity}: {string.Join(", ", line.Allocations.Select(a => $"{a.WarehouseCode} {a.Quantity}"))}");
        var attach = !invoice && cart.RequiresPickup;
        if (attach) // pick-up ticket printed under the receipt, without the printer header
            await sp.GetRequiredService<IReceiptPrinter>().ContinueAfterNextFiscalDocumentAsync(true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await sp.GetRequiredService<ISalesDocumentService>().CreateAsync(new SalesDocumentRequest(Guid.NewGuid(),
            invoice ? SaleDocumentKind.Invoice : SaleDocumentKind.Receipt, customer, cart, SalePaymentMethod.Card, cart.Total, "TEST DevTool"));
        Print(result);
        Console.WriteLine($"Time: {sw.ElapsedMilliseconds} ms");
        if (attach)
            await sp.GetRequiredService<IReceiptPrinter>().ContinueAfterNextFiscalDocumentAsync(false);
        if (result.Success && cart.RequiresPickup)
            await PrintPickupTicket(result.DocumentNumber!, cart);
        break;
    }

    case "customer":
        Print(await FindCustomer(arg[0]));
        break;

    case "registry":
        Console.WriteLine($"Valid tax id: {TaxId.IsValid(arg[0])}");
        Print(await sp.GetRequiredService<ICompanyRegistry>().FindByTaxIdAsync(arg[0]));
        break;

    case "create-customer":
    {
        // create-customer <taxId> – registry lookup + new XL customer card
        var company = await sp.GetRequiredService<ICompanyRegistry>().FindByTaxIdAsync(arg[0]) ?? throw new InvalidOperationException("Not found in registries.");
        Print(company);
        Print(await sp.GetRequiredService<ICustomerDirectory>().CreateAsync(company));
        break;
    }

    case "price-raw":
    {
        // price-raw <acronym | -> <product code> [qty] [FS|PA] – all fields returned by XLPrzeliczRabat
        var xl = sp.GetRequiredService<Gaska.SelfCheckout.Infrastructure.Erp.XlApi.XlSession>();
        var info = new cdn_api.XLRabatyComInfo_20251
        {
            Wersja = xl.ApiVersion,
            Typ = arg.ElementAtOrDefault(3) == "PA" ? 2034 : 2033,
            FlagaNB = "B",
            Ilosc = arg.ElementAtOrDefault(2) ?? "1",
            Akronim = arg[0] == "-" ? xl.Settings.CustomerAcronym : arg[0],
            TowarKod = arg[1]
        };
        var rc = await xl.RunAsync(_ => xl.CallWithRelogin(() => cdn_api.cdn_api.XLPrzeliczRabat(info)));
        Console.WriteLine($"rc = {rc}");
        foreach (var field in info.GetType().GetFields())
            Console.WriteLine($"{field.Name} = {field.GetValue(info)}");
        break;
    }

    case "price":
    {
        // price <tax id | card | -> <productId[:qty]...>   ("-" = anonymous receipt, the one-off customer)
        await host.StartAsync();
        await sp.GetRequiredService<CatalogCache>().GetAsync(TimeSpan.FromSeconds(60));
        var customer = arg[0] == "-" ? null : (await FindCustomer(arg[0])).First();
        var context = PricingContext.For(customer, SaleDocumentKind.Receipt);
        var pricing = sp.GetRequiredService<ICustomerPricing>();
        var requests = arg.Skip(1).Select(a => a.Split(':'))
            .Select(a => new PriceRequest(int.Parse(a[0]), a.Length > 1 ? decimal.Parse(a[1], CultureInfo.InvariantCulture) : 1m)).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Print(await pricing.GetPricesAsync(context, requests, PricingPriority.High));
        Console.WriteLine($"{customer?.Acronym ?? "one-off customer"}: {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        await pricing.CalculateBreaksAsync(context, requests.Select(r => r.ProductId).ToList());
        foreach (var r in requests)
            Print(new { r.ProductId, Breaks = pricing.TryGetCachedBreaks(context, r.ProductId) });
        Console.WriteLine($"Quantity tiers: {sw.ElapsedMilliseconds} ms");
        break;
    }

    case "find":
        await host.StartAsync();
        Print(await sp.CreateScope().ServiceProvider.GetRequiredService<IProductCatalog>().FindByCodeAsync(arg[0]));
        break;

    case "search":
    {
        await host.StartAsync();
        var snapshot = await sp.GetRequiredService<CatalogCache>().GetAsync(TimeSpan.FromSeconds(60));
        Console.WriteLine($"Catalog: {snapshot.Products.Count} products in stock, {snapshot.Categories.Count} categories, " +
                          $"show unavailable: {sp.GetRequiredService<IOptionsMonitor<ErpOptions>>().CurrentValue.ShowUnavailableProducts}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var page = await sp.CreateScope().ServiceProvider.GetRequiredService<IProductCatalog>().SearchAsync(new ProductQuery(string.Join(' ', arg), null));
        Console.WriteLine($"Result in {sw.ElapsedMilliseconds} ms, total {page.TotalCount}");
        foreach (var p in page.Items)
            Console.WriteLine($"{p.Id,8} {p.Code,-22} {p.GrossPrice,10:N2} {string.Join(",", p.Stock.Select(s => $"{s.WarehouseCode}:{s.Quantity:0.##}")),-16} {(p.IsAvailable ? "" : "[niedostępny] ")}{p.Name}");
        break;
    }

    case "xl-choices":
    {
        var choices = await sp.GetRequiredService<XlDictionaries>().LoadAsync();
        foreach (var (name, list) in new[] { ("Warehouses", choices.Warehouses), ("Series", choices.Series), ("Customer groups", choices.CustomerGroups),
                     ("Price types", choices.PriceTypes), ("Payment forms", choices.PaymentForms) })
            Console.WriteLine($"{name} ({list.Count}):\n  " + string.Join("\n  ", list.Select(c => $"{c.Value,-24} {c.Label}")));
        break;
    }

    case "categories":
    {
        await host.StartAsync();
        var cache = sp.GetRequiredService<CatalogCache>();
        await cache.GetAsync(TimeSpan.FromSeconds(60));
        for (var i = 0; i < 60 && cache.Current.IndexedProducts == 0; i++) // product → category links of the search index
            await Task.Delay(500);
        Console.WriteLine($"Search index: {cache.Current.IndexedProducts} products with categories");
        var catalog = sp.CreateScope().ServiceProvider.GetRequiredService<IProductCatalog>();
        foreach (var c in await catalog.GetCategoriesAsync(arg.Length > 0 ? int.Parse(arg[0]) : null))
            Console.WriteLine($"{c.Id,8} {c.ProductCount,6} {(c.HasChildren ? "+" : " ")} {c.Name}");
        break;
    }

    default:
        Console.WriteLine("""
            Usage: DevTool <command>
              terminal:  hello | last | upload-status | close-day | sale <amount> [abort_after_s] | reverse <amount> [receipt_no]
              printer:   printer-status | print-test | upload-map [file] [slot] | pickup-test <productId...>
              XL:        xl-login | xl-receipt <productId> [qty] | xl-invoice <taxId|card> <productId> [qty]
              customers: customer <taxId|card> | registry <taxId> | create-customer <taxId> | price <taxId|card> <productId...>
              catalog:   find <code> | search <text> | categories [id] | xl-choices
            """);
        break;
}

return;


async Task PrintPickupTicket(string documentNumber, CartSnapshot cart)
{
    var ids = cart.Lines.Where(l => l.Allocations.Any(a => !a.IsPrimary)).Select(l => l.ProductId).ToList();
    var locations = await sp.GetRequiredService<IWarehouseLocations>().GetLocationsAsync(ids);
    var printer = sp.GetRequiredService<IReceiptPrinter>();
    await printer.GetStatusAsync();
    var ticket = sp.GetRequiredService<PickupTicketBuilder>().Build(documentNumber, cart, locations,
        sp.GetRequiredService<IOptionsMonitor<PickupOptions>>().CurrentValue, printer.LineWidth);
    Console.WriteLine(ticket.ToPlainText(printer.LineWidth));
    await printer.PrintAsync(ticket);
}

// tax id when valid, otherwise a customer card number
Task<IReadOnlyList<Customer>> FindCustomer(string code) => TaxId.IsValid(code)
    ? sp.GetRequiredService<ICustomerDirectory>().FindByTaxIdAsync(code)
    : sp.GetRequiredService<ICustomerDirectory>().FindByCardAsync(code);

void Print(object? o) => Console.WriteLine(o is null ? "(null)" : JsonSerializer.Serialize(o, o.GetType(), json));

void PrintSlip(CardTransaction tx)
{
    var doc = sp.GetRequiredService<CardSlipFormatter>().Format(tx, SlipCopy.Cardholder, 40, sp.GetRequiredService<IOptionsMonitor<KioskOptions>>().CurrentValue.StationId);
    Console.WriteLine(new string('-', 40));
    Console.WriteLine(doc.ToPlainText(40));
    Console.WriteLine(new string('-', 40));
}

/// <summary>Answers the BLIK question like the kiosk does; any other question is cancelled.</summary>
sealed class AutoInteraction(bool blik) : ITerminalInteraction
{
    public void ShowMessage(string text) => Console.WriteLine($"[TERMINAL] {text}");
    public void ClearMessage() => Console.WriteLine("[TERMINAL] (clear)");

    public Task<TerminalReply> AskAsync(TerminalPrompt prompt, CancellationToken ct)
    {
        Console.WriteLine($"[QUESTION {prompt.Kind}] {prompt.Text}: {string.Join(", ", prompt.Options.Select(o => $"{o.Value}={o.Label}"))}");
        if (prompt.Kind == TerminalPromptKind.YesNo && prompt.Text.Contains("BLIK", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(TerminalReply.Of(blik ? "0" : "1"));
        if (blik && prompt.Options.FirstOrDefault(o => o.Label.Contains("BLIK", StringComparison.OrdinalIgnoreCase)) is { } option)
            return Task.FromResult(TerminalReply.Of(option.Value));
        Console.WriteLine("[ANSWER] cancel");
        return Task.FromResult(TerminalReply.Cancel);
    }
}

sealed class ConsoleInteraction : ITerminalInteraction
{
    public void ShowMessage(string text) => Console.WriteLine($"[TERMINAL] {text}");
    public void ClearMessage() => Console.WriteLine("[TERMINAL] (clear)");

    public Task<TerminalReply> AskAsync(TerminalPrompt prompt, CancellationToken ct)
    {
        Console.WriteLine($"[QUESTION {prompt.Kind}] {prompt.Text}: {string.Join(", ", prompt.Options.Select(o => $"{o.Value}={o.Label}"))} (Enter = cancel)");
        return Task.Run(() =>
        {
            var line = Console.ReadLine();
            return string.IsNullOrWhiteSpace(line) ? TerminalReply.Cancel : TerminalReply.Of(line.Trim());
        }, ct);
    }
}
