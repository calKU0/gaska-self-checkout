using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Infrastructure.Erp;

/// <summary>One choice of an XL dictionary: the value stored in the settings and its description.</summary>
public sealed record XlChoice(string Value, string Label);

/// <summary>XL dictionaries offered as choices in the workstation configuration.</summary>
public sealed record XlChoices(
    IReadOnlyList<XlChoice> Warehouses,
    IReadOnlyList<XlChoice> Series,
    IReadOnlyList<XlChoice> CustomerGroups,
    IReadOnlyList<XlChoice> PriceTypes,
    IReadOnlyList<XlChoice> PaymentForms,
    IReadOnlyList<XlChoice> Units);

/// <summary>
/// Reads warehouses, document series, customer groups, price types, payment forms and units of measure (XL has no unit
/// dictionary – the units used on product cards) from the XL database.
/// </summary>
public sealed class XlDictionaries(IOptionsMonitor<ErpOptions> options)
{
    /// <summary>XL configuration entry (cdn.Konfig) holding the payment forms, one row per form (Kon_Lp = TrP_FormaNr).</summary>
    private const int PaymentFormsConfigNumber = 736;

    public async Task<XlChoices> LoadAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(options.CurrentValue.ConnectionString);
        using var grid = await conn.QueryMultipleAsync(new CommandDefinition("""
            SELECT MAG_Kod FROM cdn.Magazyny WHERE MAG_Zablokowany = 0 ORDER BY MAG_Kod;

            SELECT SER_Nazwa FROM cdn.Serie WHERE SER_Korekta = 1 ORDER BY SER_Nazwa; -- sales series (2 = corrections)

            WITH g AS (
                SELECT KnG_GIDNumer AS Id, CAST(RTRIM(KnG_Akronim) AS varchar(1000)) AS Path
                FROM cdn.KntGrupy WHERE KnG_GIDTyp = -32 AND KnG_GrONumer = 0 AND KnG_GIDNumer <> 0
                UNION ALL
                SELECT k.KnG_GIDNumer, CAST(g.Path + '\|' + RTRIM(k.KnG_Akronim) AS varchar(1000))
                FROM cdn.KntGrupy k JOIN g ON k.KnG_GrONumer = g.Id
                WHERE k.KnG_GIDTyp = -32
            )
            SELECT Path FROM g ORDER BY Path;

            SELECT TCN_RodzajCeny, TCN_Nazwa FROM (
                SELECT TCN_RodzajCeny, TCN_Nazwa, ROW_NUMBER() OVER (PARTITION BY TCN_RodzajCeny ORDER BY TCN_DataOd DESC) AS n
                FROM cdn.TwrCenyNag) c
            WHERE n = 1 ORDER BY TCN_RodzajCeny;

            SELECT Kon_Lp, Kon_Wartosc FROM cdn.Konfig WHERE Kon_Numer = @forms AND Kon_Archiwalny = 0 ORDER BY Kon_Lp;

            SELECT RTRIM(Twr_Jm) AS Unit, COUNT(*) AS Products FROM cdn.TwrKarty
            WHERE Twr_Archiwalny = 0 AND RTRIM(Twr_Jm) <> '' GROUP BY RTRIM(Twr_Jm) ORDER BY COUNT(*) DESC;
            """, new { forms = PaymentFormsConfigNumber }, cancellationToken: ct));

        var warehouses = (await grid.ReadAsync<string>()).Select(Code).ToList();
        var series = (await grid.ReadAsync<string>()).Select(Code).ToList();
        var groups = (await grid.ReadAsync<string>()).Select(p => new XlChoice(p, p.Replace("\\|", " › "))).ToList();
        groups.Insert(0, new XlChoice("", "(grupa główna)"));
        var prices = (await grid.ReadAsync<(int Number, string Name)>())
            .Select(p => new XlChoice(p.Number.ToString(), p.Name.Trim())).ToList();
        var forms = (await grid.ReadAsync<(int Number, string Value)>())
            .Select(f => new XlChoice(f.Number.ToString(), PaymentFormName(f.Value))).ToList();

        var units = (await grid.ReadAsync<(string Unit, int Products)>())
            .Select(u => new XlChoice(u.Unit, $"{u.Unit} ({u.Products} tow.)")).ToList();

        return new XlChoices(warehouses, series, groups, prices, forms, units);
    }

    private static XlChoice Code(string code) => new(code.Trim(), code.Trim());

    /// <summary>The form name is the first 20-character column of the configuration value ("Karta               0  7 ...").</summary>
    private static string PaymentFormName(string value) => (value.Length > 20 ? value[..20] : value).Trim();
}
