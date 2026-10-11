using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace WinSkitch;

/// <summary>One resident process per session, with a show request from subsequent launches.</summary>
public sealed class ResidentInstance : IDisposable
{
    private readonly EventWaitHandle _showRequest;
    private readonly Mutex _mutex;
    private readonly MemoryMappedFile _ownerProcess;
    private readonly EventWaitHandle _ownerReady;
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
            _ownerReady = new EventWaitHandle(false, EventResetMode.ManualReset, name + ".OwnerReady");
            _ownerProcess = MemoryMappedFile.CreateOrOpen(name + ".OwnerProcess", sizeof(int));
            _mutex = new Mutex(false, name, out bool created);
            IsPrimary = created;
            if (IsPrimary)
            {
                _ownerReady.Reset();
                using (var owner = _ownerProcess.CreateViewAccessor()) owner.Write(0, Environment.ProcessId);
                _ownerReady.Set();
                _wait = ThreadPool.RegisterWaitForSingleObject(_showRequest, (_, _) =>
                {
                    if (!_disposed) onShowRequested();
                }, null, Timeout.Infinite, false);
            }
        }
        catch
        {
            _mutex?.Dispose();
            _ownerProcess?.Dispose();
            _ownerReady?.Dispose();
            _showRequest.Dispose();
            throw;
        }
    }

    public void RequestShow()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int processId = PrimaryProcessId;
        // Transfer the launching process's foreground permission before signaling
        // the resident. WPF Activate alone can be denied for a background process.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow
        if (processId > 0) NativeMethods.AllowSetForegroundWindow((uint)processId);
        _showRequest.Set();
    }

    internal int PrimaryProcessId
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_ownerReady.WaitOne(TimeSpan.FromSeconds(2))) return 0;
            using var owner = _ownerProcess.CreateViewAccessor();
            return owner.ReadInt32(0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wait?.Unregister(null);
        _showRequest.Dispose();
        _ownerProcess.Dispose();
        _ownerReady.Dispose();
        _mutex.Dispose();
    }
}
