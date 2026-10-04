using System;
using System.Threading;

namespace WinSkitch;

/// <summary>One resident process per session, with a show request from subsequent launches.</summary>
public sealed class ResidentInstance : IDisposable
{
    private readonly EventWaitHandle _showRequest;
    private readonly Mutex _mutex;
    private readonly RegisteredWaitHandle? _wait;
    private volatile bool _disposed;
    public bool IsPrimary { get; }

    public ResidentInstance(Action onShowRequested, string name = "WinSkitch-single-instance")
    {
        ArgumentNullException.ThrowIfNull(onShowRequested);
        // An early request stays signaled until the primary listener is registered.
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".ShowWindow");
        try
        {
            _mutex = new Mutex(false, name, out bool created);
            IsPrimary = created;
            if (IsPrimary)
                _wait = ThreadPool.RegisterWaitForSingleObject(_showRequest, (_, _) =>
                {
                    if (!_disposed) onShowRequested();
                }, null, Timeout.Infinite, false);
        }
        catch
        {
            _mutex?.Dispose();
            _showRequest.Dispose();
            throw;
        }
    }

    public void RequestShow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _showRequest.Set();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wait?.Unregister(null);
        _showRequest.Dispose();
        _mutex.Dispose();
    }
}
