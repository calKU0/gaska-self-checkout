using Gaska.SelfCheckout.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gaska.SelfCheckout.Application.Checkout;

/// <summary>Payment method chosen by the customer on the kiosk screen.</summary>
public enum PaymentChoice
{
    Card,
    Blik
}

/// <summary>Current payment state shown by the UI.</summary>
public sealed record CheckoutStatus(
    bool IsRunning,
    CheckoutStage Stage,
    string Message,
    string? TerminalMessage,
    TerminalPrompt? Prompt,
    CheckoutResult? Result)
{
    public static CheckoutStatus Idle { get; } = new(false, CheckoutStage.Preparing, "", null, null, null);
}

/// <summary>
/// Runs the payment process independently of the UI connection (a browser disconnect does not abort the payment)
/// and mediates customer ↔ terminal communication (messages, BLIK/DCC questions, cancellation).
/// </summary>
public sealed class CheckoutCoordinator(IServiceScopeFactory scopeFactory, ILogger<CheckoutCoordinator> logger) : ITerminalInteraction
{
    private readonly Lock _sync = new();
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<TerminalReply>? _pendingPrompt;
    private CheckoutStatus _status = CheckoutStatus.Idle;
    private PaymentChoice _choice = PaymentChoice.Card;

    public CheckoutStatus Status
    {
        get { lock (_sync) return _status; }
    }

    public event Action<CheckoutStatus>? StatusChanged;

    public bool TryStart(CheckoutRequest request, PaymentChoice choice)
    {
        lock (_sync)
        {
            if (_status.IsRunning)
                return false;
            _choice = choice;
            _cts = new CancellationTokenSource();
            _status = new CheckoutStatus(true, CheckoutStage.Preparing, "Przygotowujemy płatność…", null, null, null);
        }

        Publish();
        var token = _cts.Token;
        _ = Task.Run(() => RunAsync(request, token), CancellationToken.None);
        return true;
    }

    /// <summary>Customer cancellation – effective until the terminal has sent the authorization request.</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            _pendingPrompt?.TrySetResult(TerminalReply.Cancel);
            if (_status.IsRunning && _status.Stage == CheckoutStage.WaitingForPayment)
                _cts?.Cancel();
        }
    }

    public void Answer(TerminalReply reply)
    {
        TaskCompletionSource<TerminalReply>? pending;
        lock (_sync)
        {
            pending = _pendingPrompt;
            _pendingPrompt = null;
            _status = _status with { Prompt = null };
        }

        pending?.TrySetResult(reply);
        Publish();
    }

    /// <summary>The UI confirms the result has been shown – the coordinator returns to idle.</summary>
    public void Acknowledge()
    {
        lock (_sync)
        {
            if (_status.IsRunning)
                return;
            _status = CheckoutStatus.Idle;
        }

        Publish();
    }

    private async Task RunAsync(CheckoutRequest request, CancellationToken ct)
    {
        CheckoutResult? result = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<CheckoutService>();
            var progress = new SyncProgress<CheckoutProgress>(p =>
            {
                Update(s => s with { Stage = p.Stage, Message = p.Message, TerminalMessage = p.Stage == CheckoutStage.WaitingForPayment ? s.TerminalMessage : null });
            });
            result = await service.PayByCardAsync(request, this, progress, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected payment process failure");
            Update(s => s with { Stage = CheckoutStage.Failed, Message = ex.Message });
        }
        finally
        {
            lock (_sync)
            {
                _pendingPrompt?.TrySetResult(TerminalReply.Cancel);
                _pendingPrompt = null;
                _status = _status with
                {
                    IsRunning = false,
                    Prompt = null,
                    TerminalMessage = null,
                    Result = result,
                    Stage = result?.IsSuccess == true ? CheckoutStage.Completed : CheckoutStage.Failed,
                    Message = result?.Message ?? _status.Message
                };
                _cts?.Dispose();
                _cts = null;
            }

            Publish();
        }
    }

    void ITerminalInteraction.ShowMessage(string text) => Update(s => s with { TerminalMessage = text });

    void ITerminalInteraction.ClearMessage() => Update(s => s with { TerminalMessage = null, Prompt = null });

    async Task<TerminalReply> ITerminalInteraction.AskAsync(TerminalPrompt prompt, CancellationToken ct)
    {
        if (TryAutoAnswer(prompt) is { } auto)
        {
            logger.LogInformation("Automatic answer to terminal question \"{Text}\": {Value} (customer choice: {Choice})", prompt.Text, auto.Value, _choice);
            Update(s => s with { TerminalMessage = null });
            return auto;
        }

        var tcs = new TaskCompletionSource<TerminalReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _pendingPrompt?.TrySetResult(TerminalReply.Cancel);
            _pendingPrompt = tcs;
            _status = _status with { Prompt = prompt };
        }

        Publish();
        await using var registration = ct.Register(() => tcs.TrySetResult(TerminalReply.Cancel));
        var reply = await tcs.Task;
        Update(s => s with { Prompt = null });
        return reply;
    }

    /// <summary>
    /// The customer has already chosen the payment method on the kiosk screen, so the terminal question
    /// "Czy uzyc platnosci Blik?" (and the mobile payment system selection) is answered automatically.
    /// </summary>
    private TerminalReply? TryAutoAnswer(TerminalPrompt prompt)
    {
        var text = prompt.Text.ToUpperInvariant();
        if (prompt.Kind == TerminalPromptKind.YesNo && text.Contains("BLIK"))
            return TerminalReply.Of(_choice == PaymentChoice.Blik ? "0" : "1");

        if (prompt.Kind == TerminalPromptKind.Choice && _choice == PaymentChoice.Blik &&
            prompt.Options.FirstOrDefault(o => o.Label.Contains("BLIK", StringComparison.OrdinalIgnoreCase)) is { } blik)
            return TerminalReply.Of(blik.Value);

        return null;
    }

    private void Update(Func<CheckoutStatus, CheckoutStatus> change)
    {
        lock (_sync) _status = change(_status);
        Publish();
    }

    private void Publish()
    {
        var status = Status;
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Payment status subscriber failed");
        }
    }

    /// <summary>Progress reported synchronously (no context capture).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
