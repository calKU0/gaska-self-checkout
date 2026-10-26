using Gaska.SelfCheckout.Application.Alerts;
using Gaska.SelfCheckout.Application.Options;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Web.Services;

/// <summary>
/// Watches the payment terminal, fiscal printer, XL API and database in the background. When one of them fails
/// <see cref="KioskOptions.HealthFailuresBeforeStop"/> times in a row the kiosk stops selling (the cart is blocked by an
/// out-of-service screen; the service panel stays available) and resumes by itself after the next successful check.
/// Checks run only while the devices are free – a customer payment is never interrupted. Every component that stops the
/// sale also sounds the staff alarm (<see cref="StaffAlerts"/>) until it works again.
/// </summary>
public sealed class KioskHealthMonitor(
    IServiceScopeFactory scopes, StaffAlerts alerts, IOptionsMonitor<KioskOptions> options, ILogger<KioskHealthMonitor> logger)
    : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly Lock _sync = new();
    private readonly Dictionary<string, int> _failures = [];
    private IReadOnlyList<DeviceStatus> _problems = [];

    /// <summary>Failed connections that stop the sale (empty = the kiosk works).</summary>
    public IReadOnlyList<DeviceStatus> Problems
    {
        get { lock (_sync) return _problems; }
    }

    public bool OutOfService => Problems.Count > 0;

    public event Action? Changed;

    /// <summary>Result of a check (background or from the service panel).</summary>
    public void Report(IReadOnlyList<DeviceStatus> statuses)
    {
        var threshold = Math.Max(1, options.CurrentValue.HealthFailuresBeforeStop);
        IReadOnlyList<DeviceStatus> before, after;
        lock (_sync)
        {
            before = _problems;
            var problems = new List<DeviceStatus>();
            foreach (var s in statuses.Where(s => s.Blocking))
            {
                var failures = _failures[s.Name] = s.Ok ? 0 : _failures.GetValueOrDefault(s.Name) + 1;
                // a component already stopping the sale stays a problem until it works again
                if (!s.Ok && (failures >= threshold || before.Any(p => p.Name == s.Name)))
                    problems.Add(s);
            }

            _problems = after = problems;
        }

        foreach (var p in after)
            alerts.Raise(AlertKey(p), StaffAlertKind.Fault, $"Brak połączenia: {p.Name}", p.Message);
        foreach (var p in before.Where(b => after.All(a => a.Name != b.Name)))
            alerts.Clear(AlertKey(p));

        if (before.Select(p => p.Name).SequenceEqual(after.Select(p => p.Name)))
            return;

        if (after.Count > 0)
            logger.LogError("Kiosk out of service: {Problems}", string.Join("; ", after.Select(p => $"{p.Name}: {p.Message}")));
        else
            logger.LogInformation("Kiosk back in service – all connections work");
        Changed?.Invoke();
    }

    private static string AlertKey(DeviceStatus status) => $"health:{status.Name}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // after the start-up device initialization (DeviceWarmupService)
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = options.CurrentValue.HealthCheckSeconds;
            if (interval > 0)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    // devices busy (payment, service operation) – skip this round
                    await scope.ServiceProvider.GetRequiredService<ServiceOperations>().CheckAsync(TimeSpan.Zero, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Health check failed");
                }
            }
            else if (OutOfService)
            {
                Report([]); // checks turned off – do not keep the kiosk stopped
            }

            bool suspicious;
            lock (_sync) suspicious = _failures.Values.Any(f => f > 0);
            var delay = interval > 0 && suspicious ? RetryDelay : TimeSpan.FromSeconds(Math.Max(15, interval > 0 ? interval : 60));
            await Task.Delay(delay, stoppingToken);
        }
    }
}
