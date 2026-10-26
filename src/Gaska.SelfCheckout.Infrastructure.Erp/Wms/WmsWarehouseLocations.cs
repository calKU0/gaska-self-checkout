using Dapper;
using Gaska.SelfCheckout.Application.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Infrastructure.Erp.Wms;

/// <summary>
/// Product locations from Comarch WMS (wms.TwrStany + wms.Lokalizacje). The building (A / B) is taken from the zones the
/// location belongs to (wms.Strefy.Str_Magazyn), falling back to the first letter of the WMS location code.
/// Only picking / storage locations of the non-primary warehouses are listed (no receiving or dispatch buffers).
/// </summary>
public sealed class WmsWarehouseLocations(IOptionsMonitor<ErpOptions> options) : IWarehouseLocations
{
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<ProductLocation>>> GetLocationsAsync(
        IReadOnlyCollection<int> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0)
            return new Dictionary<int, IReadOnlyList<ProductLocation>>();

        var o = options.CurrentValue;
        var warehouses = o.Warehouses.Where(w => !w.Primary).Select(w => w.Code).ToList();
        if (warehouses.Count == 0)
            return new Dictionary<int, IReadOnlyList<ProductLocation>>();

        await using var conn = new SqlConnection(o.ConnectionString);
        var rows = await conn.QueryAsync<(int ProductId, string LocationCode, decimal Quantity, string? Building, string? ErpCode)>(new CommandDefinition("""
            SELECT s.TWS_TwrNumer, l.Lok_KodWMS, SUM(s.TWS_Ilosc),
                   COALESCE(
                       (SELECT TOP 1 st.Str_Magazyn FROM wms.StrefyLokalizacje sl
                        JOIN wms.Strefy st ON st.Str_ID = sl.SL_StrefaId
                        WHERE sl.SL_LokalizacjaId = l.Lok_Id AND st.Str_Magazyn IS NOT NULL AND st.Str_Magazyn <> ''),
                       CASE WHEN LEFT(l.Lok_KodWMS, 1) IN ('A', 'B') THEN LEFT(l.Lok_KodWMS, 1) END),
                   NULLIF(l.Lok_KodERP, '')
            FROM wms.TwrStany s
            JOIN wms.Lokalizacje l ON l.Lok_Id = s.TWS_LokalizacjaId
            WHERE s.TWS_TwrNumer IN @productIds
              AND s.TWS_Ilosc > 0
              AND l.Lok_Aktywna = 1
              AND l.Lok_Magazyn IN @warehouses
              AND (l.Lok_TypProcesu LIKE '%kompletowania%' OR l.Lok_TypProcesu LIKE '%przechowywania%')
            GROUP BY s.TWS_TwrNumer, l.Lok_KodWMS, l.Lok_Id, l.Lok_KodERP
            """, new { productIds, warehouses }, cancellationToken: ct));

        return rows
            .GroupBy(r => r.ProductId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ProductLocation>)g
                    .Select(r => new ProductLocation(r.ProductId, r.Building?.Trim() ?? "", r.LocationCode.Trim(), r.Quantity, r.ErpCode?.Trim()))
                    .OrderByDescending(l => l.Quantity)
                    .ToList());
    }
}
