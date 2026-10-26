using Gaska.SelfCheckout.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gaska.SelfCheckout.Devices.Posnet;

public static class DependencyInjection
{
    public static IServiceCollection AddPosnetPrinter(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PosnetOptions>(configuration.GetSection(PosnetOptions.Section));
        services.AddSingleton<PosnetPrinter>();
        services.AddSingleton<IReceiptPrinter>(sp => sp.GetRequiredService<PosnetPrinter>());
        return services;
    }
}
