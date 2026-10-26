using Gaska.SelfCheckout.Devices.Polcard.Protocol;
using Gaska.SelfCheckout.Domain.Catalog;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Tests;

internal static class TestData
{
    public static Product Product(int id = 1, decimal price = 10m, decimal stock = 5m) =>
        new(id, $"KOD-{id}", $"Produkt {id}", null, "szt.", price, 23m, [new WarehouseStock("MAG1", stock, true)]);

    /// <summary>Blok TLV odpowiedzi pos_finish dla zaakceptowanej transakcji (wartości jak z terminala).</summary>
    public static string ApprovedTlv(string ecrId = "7") =>
        Emv("9F21", "134512") + Emv("9A", "261007") +
        Tlv.BuildPolcard([
            ("1A04", "MASTERCARD"), ("1A05", "************5029"), ("1A06", "M"), ("1A07", "1"), ("1A08", "A1B2C3"),
            ("1A1B", "PLN"), ("1A1C", "SKLEP TESTOWY SP. Z O.O."), ("1A1D", "UL. PRZYKLADOWA 1"), ("1A1E", "00-001 WARSZAWA PL"),
            ("1A1F", "12345678"), ("1A20", "87654321"), ("1B01", "0000000123"), ("1B08", "0010000"), ("1B12", "00"), ("1C06", ecrId)
        ]) +
        Emv("4F", "A0000000041010") + Emv("9F02", "000000002100") + Emv("8A", "3030");

    public static string DeclinedTlv() =>
        Emv("9F21", "134512") + Emv("9A", "261007") +
        Tlv.BuildPolcard([("1A04", "VISA"), ("1A05", "************7280"), ("1B03", "ODMOWA - BRAK SRODKOW"), ("1A20", "87654321")]) +
        Emv("9F02", "000000002100");

    private static string Emv(string tag, string hexValue) => tag + (hexValue.Length / 2).ToString("X2") + hexValue;
}

/// <summary>Options monitor with a fixed value (no reloads).</summary>
internal sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
