using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Application.Checkout;

namespace Gaska.SelfCheckout.Web.Services;

/// <summary>
/// On start-up: terminal session initialization (ecr_hello_extended), printer paper width detection and XL API login –
/// the first customer transaction does not wait for them. Transactions requiring attention are reported to the log.
/// </summary>
public sealed class DeviceWarmupService(IServiceProvider services, HardwareLock hardwareLock, ILogger<DeviceWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        using (await hardwareLock.AcquireAsync(stoppingToken))
        {
            await Try("Terminal", async () =>
            {
                var info = await services.GetRequiredService<IPaymentTerminal>().CheckAsync(stoppingToken);
                return $"connected={info.Connected}, diagnostic status {info.DiagnosticStatus}, TID {info.TerminalId}";
            });
            await Try("Printer", async () => { var s = await services.GetRequiredService<IReceiptPrinter>().GetStatusAsync(stoppingToken); return $"online={s.Online}, paper ok={s.PaperOk}"; });
            await Try("XL API", async () => { var s = await services.GetRequiredService<ISalesDocumentService>().GetStatusAsync(stoppingToken); return s.Connected ? "logged in" : "not connected: " + s.Message; });
        }

        var attention = await services.GetRequiredService<ITransactionJournal>().GetRequiringAttentionAsync(stoppingToken);
        if (attention.Count > 0)
            logger.LogWarning("The journal contains {Count} transactions requiring a check in the service panel", attention.Count);
    }

    private async Task Try(string name, Func<Task<string>> action)
    {
        try
        {
            logger.LogInformation("{Name}: {Status}", name, await action());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Name}: initialization failed", name);
        }
    }
}
