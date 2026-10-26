using System.Text.Json.Nodes;
using Gaska.SelfCheckout.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gaska.SelfCheckout.Tests.Settings;

public sealed class StationSettingsStoreTests : IDisposable
{
    private sealed class SampleOptions
    {
        public string Host { get; set; } = "10.0.0.1";
        public int Port { get; set; } = 8282;
        public int ReportTimeoutSeconds { get; set; } = 300;
    }

    private readonly StationSettingsStore _store = new(NullLogger<StationSettingsStore>.Instance);

    [Fact]
    public void Only_listed_properties_are_stored()
    {
        _store.Save("Sample", new SampleOptions(), nameof(SampleOptions.Host), nameof(SampleOptions.Port));

        var section = JsonNode.Parse(File.ReadAllText(StationSettingsStore.FilePath))!["Sample"]!.AsObject();
        Assert.Equal("10.0.0.1", (string?)section["Host"]);
        Assert.Equal(8282, (int?)section["Port"]);
        Assert.False(section.ContainsKey("ReportTimeoutSeconds")); // keeps coming from appsettings.json
    }

    [Fact]
    public void Single_values_are_stored_under_their_path()
    {
        _store.Save("Erp:XlApi:ReceiptSeries", "PAR");

        Assert.Equal("PAR", (string?)JsonNode.Parse(File.ReadAllText(StationSettingsStore.FilePath))!["Erp"]!["XlApi"]!["ReceiptSeries"]);
    }

    public void Dispose() => File.Delete(StationSettingsStore.FilePath);
}
