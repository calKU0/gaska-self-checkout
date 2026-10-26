using Gaska.SelfCheckout.Web.Services;
using Microsoft.AspNetCore.Components;

namespace Gaska.SelfCheckout.Web.Components.Shared;

/// <summary>Component re-rendered on every kiosk session change (cart, notifications).</summary>
public abstract class SessionComponent : ComponentBase, IDisposable
{
    [Inject] protected KioskSession Session { get; set; } = default!;

    protected override void OnInitialized() => Session.Changed += OnSessionChanged;

    private void OnSessionChanged() => InvokeAsync(StateHasChanged);

    public virtual void Dispose() => Session.Changed -= OnSessionChanged;
}
