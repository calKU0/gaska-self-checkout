using System.Reflection;
using Gaska.SelfCheckout.Application.Checkout;

namespace Gaska.SelfCheckout.Web.Services;

/// <summary>
/// Update window requested by the deployment script (local endpoint <c>/maintenance</c>). The kiosk is never interrupted:
/// a customer finishes shopping and paying first. As soon as every kiosk screen is idle (start screen, empty cart, no
/// payment) it shows "update in progress" and stops taking customers; the script waits for <see cref="Ready"/>, replaces
/// the application and restarts the service. A request expires by itself, so a failed deployment cannot leave the kiosk
/// closed.
/// </summary>
public sealed class MaintenanceMode(CheckoutCoordinator coordinator, ILogger<MaintenanceMode> logger)
{
    /// <summary>How long the kiosk has to stay idle with the update screen shown before the update may start.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    private readonly Lock _sync = new();
    private readonly HashSet<KioskSession> _sessions = [];
    private DateTime? _expiresAt;
    private DateTime? _idleSince;

    public static string Version { get; } =
        typeof(MaintenanceMode).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(MaintenanceMode).Assembly.GetName().Version?.ToString() ?? "";

    public event Action? Changed;

    public bool Requested
    {
        get
        {
            lock (_sync)
            {
                if (_expiresAt is { } expires && expires <= DateTime.Now)
                {
                    _expiresAt = null;
                    logger.LogWarning("Update window expired – the kiosk is selling again");
                }

                return _expiresAt is not null;
            }
        }
    }

    /// <summary>No customer on any kiosk screen and no payment running (or waiting to show its result).</summary>
    public bool Idle
    {
        get
        {
            var status = coordinator.Status;
            if (status.IsRunning || status.Result is not null)
                return false;
            lock (_sync)
                return _sessions.All(s => s.IsIdle);
        }
    }

    /// <summary>The update screen is shown (or no kiosk screen is open) and the kiosk has stayed idle – safe to stop the service.</summary>
    public bool Ready
    {
        get
        {
            if (!Requested || !Idle)
            {
                lock (_sync)
                    _idleSince = null;
                return false;
            }

            lock (_sync)
            {
                _idleSince ??= DateTime.Now;
                return DateTime.Now - _idleSince >= SettleTime;
            }
        }
    }

    public void Request(TimeSpan duration)
    {
        lock (_sync)
            _expiresAt = DateTime.Now + duration;
        logger.LogInformation("Update requested – waiting for the kiosk to be idle (at most {Minutes:0} min)", duration.TotalMinutes);
        Changed?.Invoke();
    }

    public void Cancel()
    {
        lock (_sync)
        {
            if (_expiresAt is null)
                return;
            _expiresAt = null;
            _idleSince = null;
        }

        logger.LogInformation("Update window cancelled – the kiosk is selling again");
        Changed?.Invoke();
    }

    public object Snapshot() => new { version = Version, requested = Requested, idle = Idle, ready = Ready };

    internal void Register(KioskSession session)
    {
        lock (_sync)
            _sessions.Add(session);
    }

    internal void Unregister(KioskSession session)
    {
        lock (_sync)
            _sessions.Remove(session);
    }
}
