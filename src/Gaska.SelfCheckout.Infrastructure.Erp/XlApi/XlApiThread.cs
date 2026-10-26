using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Gaska.SelfCheckout.Infrastructure.Erp.XlApi;

/// <summary>
/// XL API (Clarion) must be called from a thread attached to the Clarion runtime (AttachThreadToClarion).
/// All API calls are therefore executed sequentially on one dedicated STA thread; high-priority work
/// (documents, cart prices) always runs before low-priority work (prices for browsing).
/// </summary>
internal sealed class XlApiThread : IDisposable
{
    private readonly ConcurrentQueue<Action> _high = new();
    private readonly ConcurrentQueue<Action> _low = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private Exception? _startupError;

    public XlApiThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "XL-API" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> func, bool highPriority = true)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        (highPriority ? _high : _low).Enqueue(() =>
        {
            try
            {
                if (_startupError is not null)
                    throw new InvalidOperationException("XL API could not be initialized: " + _startupError.Message, _startupError);
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        _signal.Release();
        return tcs.Task;
    }

    private void Run()
    {
        try
        {
            Native.Attach();
        }
        catch (Exception ex)
        {
            _startupError = ex;
        }

        try
        {
            while (true)
            {
                _signal.Wait(_stop.Token);
                if (_high.TryDequeue(out var action) || _low.TryDequeue(out action))
                    action();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(10));
    }

    private static class Native
    {
        [DllImport("ClaRUN.dll")]
        private static extern void AttachThreadToClarion(int flag);

        /// <summary>XL libraries (cdn_api.dll, ClaRUN.dll and dependencies) are found through the PATH of the workstation.</summary>
        public static void Attach()
        {
            if (Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("XL API requires a 32-bit (x86) process.");
            try
            {
                AttachThreadToClarion(1);
            }
            catch (DllNotFoundException ex)
            {
                throw new DllNotFoundException("ClaRUN.dll not found – add the Comarch ERP XL folder to the system PATH", ex);
            }
        }
    }
}
