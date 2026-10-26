using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Infrastructure.Erp.Catalog;
using Gaska.SelfCheckout.Infrastructure.Erp.Customers;
using Gaska.SelfCheckout.Infrastructure.Erp.Registry;
using Gaska.SelfCheckout.Infrastructure.Erp.Wms;
using Gaska.SelfCheckout.Infrastructure.Erp.XlApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gaska.SelfCheckout.Infrastructure.Erp;

public static class DependencyInjection
{
    public static IServiceCollection AddComarchXl(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ErpOptions>(configuration.GetSection(ErpOptions.Section));
        services.PostConfigure<ErpOptions>(o =>
        {
            // drop incomplete, disabled and repeated entries, exactly one primary warehouse
            o.Warehouses = o.Warehouses
                .Where(w => !string.IsNullOrWhiteSpace(w.Code) && w.Enabled)
                .DistinctBy(w => w.Code.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (o.Warehouses.Count == 0)
                return;
            var primary = o.Warehouses.FirstOrDefault(w => w.Primary) ?? o.Warehouses[0];
            o.Warehouses.ForEach(w => w.Primary = w == primary);
        });

        // catalog: search index (Elasticsearch) + products in stock from XL
        services.AddHttpClient(ElasticProductIndex.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan) // per request (ElasticsearchOptions.TimeoutSeconds)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) });
        services.AddSingleton<ElasticProductIndex>();
        services.AddSingleton<CatalogCache>();
        services.AddHostedService(sp => sp.GetRequiredService<CatalogCache>());
        services.AddScoped<IProductCatalog, XlProductCatalog>();
        services.AddSingleton<IProductImages, XlProductImages>();

        // XL API (one session / one Clarion thread for the whole process)
        services.AddSingleton<XlSession>();
        services.AddSingleton<ISalesDocumentService, XlSalesDocumentService>();
        services.AddSingleton<ICustomerDirectory, XlCustomerDirectory>();
        services.AddSingleton<ICustomerPricing, XlCustomerPricing>();

        // public company registries
        services.AddHttpClient<GusBirClient>();
        services.AddHttpClient<VatWhiteListClient>();
        services.AddSingleton<ICompanyRegistry, CompanyRegistry>();

        // dictionaries for the workstation configuration
        services.AddSingleton<XlDictionaries>();

        // WMS
        services.AddSingleton<IWarehouseLocations, WmsWarehouseLocations>();
        return services;
    }
}
