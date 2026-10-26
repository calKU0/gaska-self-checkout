using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Application.Options;
using Gaska.SelfCheckout.Domain.Sales;

namespace Gaska.SelfCheckout.Application.Printing;

/// <summary>
/// Pick-up ticket for goods sold from a non-primary warehouse: QR code with the document number,
/// packing list grouped by warehouse building (WMS locations) and the site map.
/// </summary>
public sealed class PickupTicketBuilder
{
    /// <summary>First printer graphic slot holding the site map.</summary>
    public const int MapImageSlot = 1;

    public PrintDocument Build(
        string documentNumber,
        CartSnapshot cart,
        IReadOnlyDictionary<int, IReadOnlyList<ProductLocation>> locations,
        PickupOptions options,
        int width)
    {
        // kept short: header, QR, document number, item codes per building, map
        var lines = new List<PrintLine> { PrintLine.Center("ODBIÓR TOWARU", bold: true, wide: true), PrintLine.Qr(documentNumber) };
        lines.Add(PrintLine.Center(documentNumber, bold: true));
        lines.Add(PrintLine.Separator);

        foreach (var group in GroupByBuilding(cart, locations).OrderBy(g => g.Key == "" ? "~" : g.Key))
        {
            var title = group.Key == ""
                ? "LOKALIZACJA DO USTALENIA"
                : $"MAGAZYN {group.Key}";
            lines.Add(PrintLine.Center(title, bold: true, wide: true));
            if (group.Key == "")
                lines.Add(PrintLine.Center("zapytaj obsługę"));

            foreach (var item in group)
            {
                lines.Add(new PrintLine(CardSlipFormatter.Cols(item.Code, $"{item.Quantity:0.##} {item.Unit}", width), Bold: true));
                // XL names often repeat the code ("JAG98-0146 Sworzeń") – the code is already on the line above
                var name = item.Name.StartsWith(item.Code + " ", StringComparison.OrdinalIgnoreCase) ? item.Name[(item.Code.Length + 1)..] : item.Name;
                foreach (var nameLine in CardSlipFormatter.Wrap(name, width))
                    lines.Add(new PrintLine(nameLine));
            }

            lines.Add(PrintLine.Separator);
        }

        for (var i = 0; i < Math.Max(1, options.MapImageParts); i++)
            lines.Add(PrintLine.Image(MapImageSlot + i));

        return new PrintDocument(PrintDocumentKind.PickupTicket, lines);
    }

    private sealed record PickupItem(string Code, string Name, string Unit, decimal Quantity, IReadOnlyList<ProductLocation> Locations);

    /// <summary>
    /// Assigns each quantity taken from a non-primary warehouse to a building: the one with the most stock of the product
    /// (locations of that building are listed, biggest quantities first).
    /// </summary>
    private static IEnumerable<IGrouping<string, PickupItem>> GroupByBuilding(
        CartSnapshot cart, IReadOnlyDictionary<int, IReadOnlyList<ProductLocation>> locations)
    {
        var items = new List<(string Building, PickupItem Item)>();
        foreach (var line in cart.Lines)
        {
            var quantity = line.Allocations.Where(a => !a.IsPrimary).Sum(a => a.Quantity);
            if (quantity <= 0)
                continue;

            var productLocations = locations.GetValueOrDefault(line.ProductId) ?? [];
            var building = productLocations
                .GroupBy(l => l.Building)
                .OrderByDescending(g => g.Sum(l => l.Quantity) >= quantity)
                .ThenByDescending(g => g.Sum(l => l.Quantity))
                .Select(g => g.Key)
                .FirstOrDefault() ?? "";
            var list = productLocations.Where(l => l.Building == building).OrderByDescending(l => l.Quantity).ToList();
            items.Add((building, new PickupItem(line.Code, line.Name, line.Unit, quantity, list)));
        }

        return items.GroupBy(i => i.Building, i => i.Item);
    }
}
