using Gaska.SelfCheckout.Application.Alerts;
using Gaska.SelfCheckout.Application.Checkout;
using Gaska.SelfCheckout.Application.Options;
using Gaska.SelfCheckout.Application.Printing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gaska.SelfCheckout.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddKasaApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KioskOptions>(configuration.GetSection(KioskOptions.Section));
        services.Configure<CheckoutOptions>(configuration.GetSection(CheckoutOptions.Section));
        services.Configure<PickupOptions>(configuration.GetSection(PickupOptions.Section));

        services.AddSingleton<StaffAlerts>();
        services.AddSingleton<HardwareLock>();
        services.AddSingleton<CardSlipFormatter>();
        services.AddSingleton<PickupTicketBuilder>();
        services.AddSingleton<CheckoutCoordinator>();
        services.AddScoped<CheckoutService>();
        return services;
    }
}
