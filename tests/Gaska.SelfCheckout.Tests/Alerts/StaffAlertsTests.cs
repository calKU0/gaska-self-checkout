using Gaska.SelfCheckout.Application.Alerts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gaska.SelfCheckout.Tests.Alerts;

public sealed class StaffAlertsTests
{
    private readonly StaffAlerts _alerts = new(NullLogger<StaffAlerts>.Instance);

    [Fact]
    public void Acknowledged_help_request_is_done()
    {
        _alerts.RequestHelp();
        Assert.True(_alerts.IsSounding);

        _alerts.Acknowledge(StaffAlerts.HelpKey);

        Assert.False(_alerts.IsSounding);
        Assert.Empty(_alerts.Active);
    }

    [Fact]
    public void Silenced_fault_stays_until_cleared_and_sounds_again_when_raised_again()
    {
        _alerts.Raise("health:Terminal", StaffAlertKind.Fault, "Brak połączenia: Terminal");
        _alerts.AcknowledgeAll();

        Assert.False(_alerts.IsSounding);
        Assert.True(Assert.Single(_alerts.Active).Silenced);

        // still failing – the next check must not wake the alarm up
        _alerts.Raise("health:Terminal", StaffAlertKind.Fault, "Brak połączenia: Terminal", "timeout");
        Assert.False(_alerts.IsSounding);
        Assert.Equal("timeout", _alerts.Active[0].Detail);

        _alerts.Clear("health:Terminal");
        Assert.Empty(_alerts.Active);

        _alerts.Raise("health:Terminal", StaffAlertKind.Fault, "Brak połączenia: Terminal");
        Assert.True(_alerts.IsSounding);
    }

    [Fact]
    public void Fault_sounds_before_help_request()
    {
        _alerts.RequestHelp();
        _alerts.Raise("scale", StaffAlertKind.Fault, "Błąd wagi");

        Assert.Equal("scale", _alerts.Sounding!.Key);
    }

    [Fact]
    public void Changed_is_raised_only_on_a_real_change()
    {
        var changes = 0;
        _alerts.Changed += () => changes++;

        _alerts.RequestHelp();
        _alerts.RequestHelp();
        _alerts.Clear("unknown");
        _alerts.AcknowledgeAll();
        _alerts.AcknowledgeAll();

        Assert.Equal(2, changes);
    }
}
