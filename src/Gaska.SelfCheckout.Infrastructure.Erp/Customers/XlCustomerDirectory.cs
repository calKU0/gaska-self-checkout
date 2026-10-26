using cdn_api;
using Dapper;
using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Domain.Customers;
using Gaska.SelfCheckout.Infrastructure.Erp.XlApi;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Gaska.SelfCheckout.Infrastructure.Erp.Customers;

/// <summary>
/// Customer cards of Comarch ERP XL: lookup by tax id (NIP) or customer/loyalty card number (SQL)
/// and creation of new cards from registry data (XL API XLNowyKontrahent / XLZamknijKontrahenta).
/// </summary>
public sealed class XlCustomerDirectory(XlSession xl, ILogger<XlCustomerDirectory> logger) : ICustomerDirectory
{
    private const string SelectColumns = """
        SELECT Knt_GIDNumer AS Id, Knt_Akronim AS Acronym,
               LTRIM(RTRIM(CONCAT(Knt_Nazwa1, ' ', Knt_Nazwa2))) AS Name,
               COALESCE(NULLIF(Knt_Nip, ''), NULLIF(REPLACE(REPLACE(Knt_NipE, '-', ''), ' ', ''), '')) AS TaxId, NULLIF(LTRIM(RTRIM(CONCAT(Knt_Ulica, ' ', Knt_Adres))), '') AS Street,
               NULLIF(Knt_KodP, '') AS PostalCode, NULLIF(Knt_Miasto, '') AS City, NULLIF(RTRIM(Knt_Ean), '') AS CardNumber,
               CAST(CASE WHEN Knt_Status = 2 THEN 1 ELSE 0 END AS bit) AS IsPrivatePerson
        FROM cdn.KntKarty
        """;

    public async Task<IReadOnlyList<Customer>> FindByTaxIdAsync(string taxId, CancellationToken ct = default)
    {
        var nip = TaxId.Normalize(taxId);
        if (nip.Length == 0)
            return [];

        await using var conn = new SqlConnection(xl.Erp.ConnectionString);
        var rows = await conn.QueryAsync<Customer>(new CommandDefinition($"""
            {SelectColumns}
            WHERE Knt_Archiwalny = 0
              AND (REPLACE(REPLACE(Knt_Nip, '-', ''), ' ', '') = @nip OR REPLACE(REPLACE(Knt_NipE, '-', ''), ' ', '') = @nip)
            ORDER BY Knt_Akronim
            """, new { nip }, cancellationToken: ct));
        return rows.Select(Trim).ToList();
    }

    public async Task<IReadOnlyList<Customer>> FindByCardAsync(string cardNumber, CancellationToken ct = default)
    {
        var card = cardNumber.Trim();
        if (card.Length == 0)
            return [];

        await using var conn = new SqlConnection(xl.Erp.ConnectionString);
        var rows = await conn.QueryAsync<Customer>(new CommandDefinition($"""
            {SelectColumns}
            WHERE Knt_Archiwalny = 0
              AND Knt_Ean <> '' AND RTRIM(Knt_Ean) = @card -- the customer card number (EAN of the customer card)
            ORDER BY Knt_Akronim
            """, new { card }, cancellationToken: ct));
        return rows.Select(Trim).ToList();
    }

    public async Task<Customer?> GetByIdAsync(int customerId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(xl.Erp.ConnectionString);
        var customer = await conn.QuerySingleOrDefaultAsync<Customer>(new CommandDefinition(
            $"{SelectColumns} WHERE Knt_GIDNumer = @customerId", new { customerId }, cancellationToken: ct));
        return customer is null ? null : Trim(customer);
    }

    public async Task<Customer> CreateAsync(CompanyRegistryEntry company, CancellationToken ct = default)
    {
        var id = await xl.RunAsync(_ => Create(company));
        var customer = await GetByIdAsync(id, ct) ?? throw new InvalidOperationException($"Customer card {id} not found after creation.");
        logger.LogInformation("Customer {Acronym} (GID {Id}) created from {Source} for tax id {TaxId}", customer.Acronym, id, company.Source, company.TaxId);
        return customer;
    }

    private int Create(CompanyRegistryEntry company)
    {
        var s = xl.Settings;
        var nip = TaxId.Normalize(company.TaxId);
        var (name1, name2, name3) = SplitName(company.Name);
        var (street, address) = SplitStreet(company.StreetLine);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var acronym = attempt == 0 ? nip : $"{nip}-{attempt}";
            var info = new XLKontrahentInfo_20251
            {
                Wersja = s.ApiVersion,
                Tryb = 2, // batch mode
                Typ = 16, // customer (recipient)
                Status = 1, // business entity
                PlatnikVat = 1,
                ExpoKraj = 1, // domestic
                Akronim = acronym,
                Nazwa1 = name1,
                Nazwa2 = name2,
                Nazwa3 = name3,
                Ulica = street,
                Adres = address,
                KodP = company.PostalCode ?? "",
                Miasto = Truncate(company.City ?? "", 30),
                Kraj = "PL",
                NipPrefiks = "PL",
                NipE = TaxId.Format(nip),
                Regon = company.Regon ?? "",
                Wojewodztwo = company.Province ?? "",
                Powiat = company.District ?? "",
                Gmina = company.Commune ?? "",
                GrupaSciezka = s.NewCustomerGroupPath,
                Cena = s.NewCustomerPriceNumber, // default price type
                Opis = $"Utworzony przez kasę samoobsługową na podstawie danych z {company.Source} ({DateTime.Now:yyyy-MM-dd})"
            };

            var customerId = 0;
            var rc = xl.CallWithRelogin(() => cdn_api.cdn_api.XLNowyKontrahent(xl.SessionId, ref customerId, info));
            if (rc == 48) // acronym already exists
                continue;
            if (rc != 0)
                throw new InvalidOperationException($"Customer card could not be created (XL {rc}: {xl.Describe(XlSession.FnNewCustomer, rc)})");

            var close = new XLZamkniecieKontrahentaInfo_20251 { Wersja = s.ApiVersion, Tryb = 0 };
            rc = cdn_api.cdn_api.XLZamknijKontrahenta(customerId, close);
            if (rc != 0)
                throw new InvalidOperationException($"Customer card could not be saved (XL {rc}: {xl.Describe(XlSession.FnCloseCustomer, rc)})");

            return close.GidNumer != 0 ? close.GidNumer : info.GIDNumer;
        }

        throw new InvalidOperationException($"No free acronym for tax id {nip}.");
    }

    /// <summary>XL name lines: 50 + 50 + 250 characters.</summary>
    private static (string, string, string) SplitName(string name)
    {
        name = name.Trim();
        if (name.Length <= 50)
            return (name, "", "");
        var cut = name.LastIndexOf(' ', 50);
        if (cut < 20) cut = 50;
        var rest = name[cut..].Trim();
        if (rest.Length <= 50)
            return (name[..cut].Trim(), rest, "");
        var cut2 = rest.LastIndexOf(' ', 50);
        if (cut2 < 20) cut2 = 50;
        return (name[..cut].Trim(), rest[..cut2].Trim(), Truncate(rest[cut2..].Trim(), 250));
    }

    /// <summary>XL street (30) + address continuation (30).</summary>
    private static (string, string) SplitStreet(string street)
    {
        street = street.Trim();
        if (street.Length <= 30)
            return (street, "");
        var cut = street.LastIndexOf(' ', 30);
        if (cut < 10) cut = 30;
        return (street[..cut].Trim(), Truncate(street[cut..].Trim(), 30));
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static Customer Trim(Customer c) => c with
    {
        Acronym = c.Acronym.Trim(),
        Name = c.Name.Trim(),
        TaxId = c.TaxId?.Trim(),
        Street = c.Street?.Trim(),
        PostalCode = c.PostalCode?.Trim(),
        City = c.City?.Trim(),
        CardNumber = c.CardNumber?.Trim()
    };
}
