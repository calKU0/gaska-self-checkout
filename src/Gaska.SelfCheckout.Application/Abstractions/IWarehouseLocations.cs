namespace Gaska.SelfCheckout.Application.Abstractions;

/// <summary>Location of a product in the warehouse (WMS).</summary>
/// <param name="Building">Building / warehouse hall (e.g. "A" or "B").</param>
/// <param name="LocationCode">WMS location code (Lok_KodWMS).</param>
/// <param name="ErpCode">ERP location code (Lok_KodERP), if defined.</param>
public sealed record ProductLocation(int ProductId, string Building, string LocationCode, decimal Quantity, string? ErpCode = null);

/// <summary>Warehouse management system data used for the pick-up ticket.</summary>
public interface IWarehouseLocations
{
    Task<IReadOnlyDictionary<int, IReadOnlyList<ProductLocation>>> GetLocationsAsync(IReadOnlyCollection<int> productIds, CancellationToken ct = default);
}
