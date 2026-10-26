using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Infrastructure.Journal;
using Gaska.SelfCheckout.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gaska.SelfCheckout.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddKasaInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JournalOptions>(configuration.GetSection(JournalOptions.Section));
        services.AddSingleton<ITransactionJournal, JsonTransactionJournal>();
        services.AddSingleton<StationSettingsStore>();
        return services;
    }
}
