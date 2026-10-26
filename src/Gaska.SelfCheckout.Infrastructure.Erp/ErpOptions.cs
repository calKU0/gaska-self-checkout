namespace Gaska.SelfCheckout.Infrastructure.Erp;

public sealed class ErpOptions
{
    public const string Section = "Erp";

    /// <summary>Comarch ERP XL database connection (catalog, stock, prices, customers, WMS).</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Sale warehouses. The primary one is the self-service shop; the others are used in the listed order only when the
    /// primary runs out – such goods are collected by the customer and a pick-up ticket is printed.
    /// </summary>
    public List<WarehouseOptions> Warehouses { get; set; } = [];

    /// <summary>Retail gross price number (TwC_TwrLp).</summary>
    public int PriceNumber { get; set; }

    /// <summary>Root of the category tree shown at the kiosk (TwG_GIDNumer).</summary>
    public int CategoryRootId { get; set; }

    /// <summary>How often (seconds) the list of products in stock is refreshed.</summary>
    public int StockRefreshSeconds { get; set; } = 60;

    /// <summary>How often (minutes) the category tree (and the product → category links from the search index) is refreshed.</summary>
    public int CategoryRefreshMinutes { get; set; } = 10;

    /// <summary>
    /// False: search and categories show only products in stock in <see cref="Warehouses"/>. True: every product of the
    /// search index is shown – those out of stock greyed out ("chwilowo niedostępny") and never added to the cart.
    /// </summary>
    public bool ShowUnavailableProducts { get; set; }

    /// <summary>Product search index (search and category browsing; scanning always goes straight to XL).</summary>
    public ElasticsearchOptions Elasticsearch { get; set; } = new();

    public XlApiOptions XlApi { get; set; } = new();

    /// <summary>Products sold by weight / cut to length (hidden at the kiosk unless switched on).</summary>
    public SaleUnitsOptions Units { get; set; } = new();
    public CompanyRegistryOptions Registry { get; set; } = new();

    public WarehouseOptions PrimaryWarehouse =>
        Warehouses.FirstOrDefault(w => w.Primary) ?? throw new InvalidOperationException("No sale warehouse configured (Erp:Warehouses)");
}

public sealed class WarehouseOptions
{
    /// <summary>MAG_Kod – the warehouse id (MAG_GIDNumer) is looked up in cdn.Magazyny.</summary>
    public string Code { get; set; } = "";
    public bool Primary { get; set; }

    /// <summary>Kept for older configurations – disabled entries are ignored.</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class ElasticsearchOptions
{
    /// <summary>Elasticsearch node (7.x).</summary>
    public string Url { get; set; } = "";

    /// <summary>Index or alias with the products offered at the kiosk (document id = Twr_GIDNumer).</summary>
    public string Index { get; set; } = "";

    /// <summary>Search timeout – after it the kiosk searches the products in stock in memory.</summary>
    public int TimeoutSeconds { get; set; } = 5;
}

public sealed class XlApiOptions
{
    public int ApiVersion { get; set; } = 20251;

    /// <summary>Database name registered in XL (not the SQL database name).</summary>
    public string Database { get; set; } = "";

    public string Operator { get; set; } = "";
    public string Password { get; set; } = "";

    public string ProgramId { get; set; } = "Gaska Self-Checkout";
    public string? Server { get; set; }
    public string? ApiKey { get; set; }
    public string? LogFile { get; set; }

    /// <summary>XLLogin.TrybWsadowy – 1: never show XL windows. XL printouts (XLWykonajPodanyWydruk) may require 0.</summary>
    public int LoginBatchMode { get; set; } = 1;

    /// <summary>Receipt (PA) series.</summary>
    public string ReceiptSeries { get; set; } = "";

    /// <summary>Invoice (FS) series – businesses with a tax id only; invoices are not fiscal (sent to KSeF).</summary>
    public string InvoiceSeries { get; set; } = "";

    /// <summary>KSeF handling when confirming an invoice (XLZamknijDokument.KSeF): 0 – none, 1 – according to XL configuration.</summary>
    public int InvoiceKsefMode { get; set; } = 1;

    /// <summary>Customer acronym of anonymous receipts.</summary>
    public string CustomerAcronym { get; set; } = "";

    /// <summary>Document header description.</summary>
    public string DocumentDescription { get; set; } = "Kasa samoobsługowa";

    /// <summary>"Card" payment form (TrP_FormaNr).</summary>
    public int CardPaymentForm { get; set; }

    /// <summary>Print the receipt fiscally on confirmation (XLZamknijDokument Fiskalny=1).</summary>
    public bool PrintFiscal { get; set; } = true;

    /// <summary>0 – confirm, 1 – leave in the buffer (testing).</summary>
    public int CloseMode { get; set; }

    /// <summary>Group path of new customers created from the registry ("GROUP\|SUBGROUP"); empty = main group.</summary>
    public string NewCustomerGroupPath { get; set; } = "";

    /// <summary>Default price of new customers (TwC_TwrLp / TCN_RodzajCeny).</summary>
    public int NewCustomerPriceNumber { get; set; }
}

public sealed class CompanyRegistryOptions
{
    /// <summary>
    /// GUS REGON BIR 1.1 – the production key is issued by GUS (regon_bir@stat.gov.pl) and kept in appsettings.Local.json.
    /// Test environment: https://wyszukiwarkaregontest.stat.gov.pl/wsBIR/UslugaBIRzewnPubl.svc, key abcde12345abcde12345.
    /// </summary>
    public bool GusEnabled { get; set; } = true;
    public string GusUrl { get; set; } = "https://wyszukiwarkaregon.stat.gov.pl/wsBIR/UslugaBIRzewnPubl.svc";
    public string GusKey { get; set; } = "";

    /// <summary>Ministry of Finance VAT white list (no key) – fallback when GUS is unavailable.</summary>
    public bool WhiteListEnabled { get; set; } = true;
    public string WhiteListUrl { get; set; } = "https://wl-api.mf.gov.pl";

    public int TimeoutSeconds { get; set; } = 15;
}
