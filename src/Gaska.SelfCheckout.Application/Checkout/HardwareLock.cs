namespace Gaska.SelfCheckout.Application.Checkout;

/// <summary>
/// Exclusive access to the devices (terminal + printer + ERP fiscal printing) – a customer transaction
/// and service operations must never interleave.
/// </summary>
public sealed class HardwareLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool IsBusy => _semaphore.CurrentCount == 0;

    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        return new Releaser(_semaphore);
    }

    public async Task<IDisposable?> TryAcquireAsync(TimeSpan timeout, CancellationToken ct = default) =>
        await _semaphore.WaitAsync(timeout, ct) ? new Releaser(_semaphore) : null;

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                semaphore.Release();
        }
    }
}
