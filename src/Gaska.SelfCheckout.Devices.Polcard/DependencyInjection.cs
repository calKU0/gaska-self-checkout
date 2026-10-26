using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Devices.Polcard.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Devices.Polcard;

public static class DependencyInjection
{
    /// <summary>Log category with the full ECR↔POS message trace (to be handed over to Fiserv).</summary>
    public const string ProtocolLogCategory = "Polcard.Protocol";

    public static IServiceCollection AddPolcardTerminal(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PolcardOptions>(configuration.GetSection(PolcardOptions.Section));
        services.AddSingleton<IEcrConnectionFactory, TcpEcrConnectionFactory>();
        services.AddSingleton<PolcardTerminal>();
        services.AddSingleton<IPaymentTerminal>(sp => sp.GetRequiredService<PolcardTerminal>());
        return services;
    }
}

public sealed class TcpEcrConnectionFactory(IOptionsMonitor<PolcardOptions> options, ILoggerFactory loggerFactory) : IEcrConnectionFactory
{
    private readonly ILogger _protocolLog = loggerFactory.CreateLogger(DependencyInjection.ProtocolLogCategory);

    public async Task<IEcrConnection> ConnectAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        return await EcrTcpConnection.ConnectAsync(o.Host, o.Port, TimeSpan.FromSeconds(o.ConnectTimeoutSeconds), _protocolLog, ct);
    }
}
