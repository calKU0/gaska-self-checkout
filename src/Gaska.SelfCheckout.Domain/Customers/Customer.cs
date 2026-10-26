namespace Gaska.SelfCheckout.Domain.Customers;

/// <summary>ERP customer (contractor card) an invoice is issued for.</summary>
public sealed record Customer(
    int Id,
    string Acronym,
    string Name,
    string? TaxId,
    string? Street,
    string? PostalCode,
    string? City,
    string? CardNumber,
    bool IsPrivatePerson = false)
{
    /// <summary>
    /// The kiosk issues invoices only to businesses with a tax id; private persons are served at the staffed checkout.
    /// </summary>
    public bool CanReceiveInvoice => !string.IsNullOrWhiteSpace(TaxId) && !IsPrivatePerson;

    public string AddressLine => string.Join(", ", new[] { Street, $"{PostalCode} {City}".Trim() }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>Company data from a public registry (GUS REGON / VAT white list) used to create a new customer card.</summary>
public sealed record CompanyRegistryEntry(
    string TaxId,
    string Name,
    string? Regon,
    string? Street,
    string? BuildingNumber,
    string? ApartmentNumber,
    string? PostalCode,
    string? City,
    string? PostOffice,
    string? Province,
    string? District,
    string? Commune,
    string Source)
{
    public string StreetLine
    {
        get
        {
            var number = string.IsNullOrWhiteSpace(ApartmentNumber) ? BuildingNumber : $"{BuildingNumber}/{ApartmentNumber}";
            return string.IsNullOrWhiteSpace(Street) ? (City is null ? "" : $"{City} {number}").Trim() : $"{Street} {number}".Trim();
        }
    }
}
