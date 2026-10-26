using Microsoft.Extensions.Logging;

namespace Gaska.SelfCheckout.Application.Alerts;

public enum StaffAlertKind
{
    /// <summary>The customer called for help – handled (and removed) once staff acknowledge it.</summary>
    HelpRequest,

    /// <summary>
    /// A device or connection fault (terminal, printer, XL, database, scale…). Staff can silence it, but it stays listed
    /// until its source clears it; raised again after that it sounds again.
    /// </summary>
    Fault
}

/// <param name="Key">Identifies the source, e.g. "help" or "health:Terminal płatniczy" – raising the same key again updates the alert.</param>
/// <param name="Silenced">Acknowledged by staff – listed, but no longer sounding.</param>
public sealed record StaffAlert(string Key, StaffAlertKind Kind, string Title, string? Detail, DateTime RaisedAt, bool Silenced)
{
    public bool Sounding => !Silenced;
}

/// <summary>
/// Alerts that call the staff to the kiosk with a sound (help button, lost connections, device errors). Any part of the
/// application raises and clears them by key; the kiosk screen plays the alarm while one of them is
/// <see cref="StaffAlert.Sounding"/> and staff silence it after entering the PIN.
/// </summary>
public sealed class StaffAlerts(ILogger<StaffAlerts> logger)
{
    public const string HelpKey = "help";

    private readonly Lock _sync = new();
    private readonly Dictionary<string, StaffAlert> _alerts = [];

    public event Action? Changed;

    /// <summary>Active alerts, oldest first.</summary>
    public IReadOnlyList<StaffAlert> Active
    {
        get { lock (_sync) return [.. _alerts.Values.OrderBy(a => a.RaisedAt)]; }
    }

    /// <summary>The alarm sounds (an alert no one has silenced yet).</summary>
    public bool IsSounding => Sounding is not null;

    /// <summary>The alert the alarm sounds for – a fault before a help request.</summary>
    public StaffAlert? Sounding
    {
        get
        {
            lock (_sync)
                return _alerts.Values.Where(a => a.Sounding).OrderBy(a => a.Kind == StaffAlertKind.Fault ? 0 : 1).ThenBy(a => a.RaisedAt).FirstOrDefault();
        }
    }

    public bool IsActive(string key)
    {
        lock (_sync) return _alerts.ContainsKey(key);
    }

    /// <summary>
    /// Raises the alert (or updates the text of the active one with the same key – an alert already silenced stays silent).
    /// </summary>
    public void Raise(string key, StaffAlertKind kind, string title, string? detail = null)
    {
        lock (_sync)
        {
            if (_alerts.TryGetValue(key, out var existing))
            {
                if (existing.Title == title && existing.Detail == detail)
                    return;
                _alerts[key] = existing with { Title = title, Detail = detail };
            }
            else
            {
                _alerts[key] = new StaffAlert(key, kind, title, detail, DateTime.Now, Silenced: false);
                logger.LogWarning("Staff alert raised: {Title} {Detail}", title, detail);
            }
        }

        Changed?.Invoke();
    }

    public void RequestHelp() => Raise(HelpKey, StaffAlertKind.HelpRequest, "Klient prosi o pomoc");

    /// <summary>The source of the alert works again (or the request was withdrawn).</summary>
    public void Clear(string key)
    {
        bool removed;
        lock (_sync) removed = _alerts.Remove(key);
        if (!removed)
            return;
        logger.LogInformation("Staff alert cleared: {Key}", key);
        Changed?.Invoke();
    }

    /// <summary>Staff took over: a help request is done, a fault is silenced until its source clears it.</summary>
    public void Acknowledge(string key)
    {
        lock (_sync)
        {
            if (!_alerts.TryGetValue(key, out var alert) || !Acknowledge(alert))
                return;
        }

        Changed?.Invoke();
    }

    /// <summary>Staff came to the kiosk (PIN entered) – every alert is acknowledged.</summary>
    public void AcknowledgeAll()
    {
        var changed = false;
        lock (_sync)
        {
            foreach (var alert in _alerts.Values.ToList())
                changed |= Acknowledge(alert);
        }

        if (changed)
            Changed?.Invoke();
    }

    private bool Acknowledge(StaffAlert alert)
    {
        if (alert.Kind == StaffAlertKind.HelpRequest)
            _alerts.Remove(alert.Key);
        else if (!alert.Silenced)
            _alerts[alert.Key] = alert with { Silenced = true };
        else
            return false;

        logger.LogInformation("Staff alert acknowledged: {Title}", alert.Title);
        return true;
    }
}
