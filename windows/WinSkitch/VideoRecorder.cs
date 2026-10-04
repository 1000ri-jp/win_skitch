using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WinSkitch;

/// <summary>Records a physical desktop rectangle, including the cursor, without audio.</summary>
public sealed class VideoRecorder
{
    public const int FramesPerSecond = 15;
    private readonly string _path;
    private readonly TaskCompletionSource<VideoRecorder> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopRequested;
    private long _startedAt;
    private long _elapsedTicks = -1;

    public Int32Rect Bounds { get; }
    public int Width => (Bounds.Width + 1) & ~1;
    public int Height => (Bounds.Height + 1) & ~1;
    public Task Completion => _completion.Task;
    public TimeSpan Elapsed
    {
        get
        {
            long ticks = Interlocked.Read(ref _elapsedTicks);
            if (ticks >= 0) return TimeSpan.FromTicks(ticks);
            long started = Interlocked.Read(ref _startedAt);
            return started == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(started);
        }
    }

    private VideoRecorder(string path, Int32Rect bounds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (bounds.Width <= 0 || bounds.Height <= 0
            || bounds.Width > MediaFoundationVideoWriter.MaximumDimension
            || bounds.Height > MediaFoundationVideoWriter.MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(bounds), "録画範囲は幅・高さともに 1～4096 ピクセルにしてください。");
        _ = checked(bounds.Width * bounds.Height * 4);
        _path = Path.GetFullPath(path);
        Bounds = bounds;
        var worker = new Thread(Record) { IsBackground = true, Name = "WinSkitch screen recorder" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    public static VideoRecorder Start(string path, Int32Rect bounds) =>
        StartAsync(path, bounds).GetAwaiter().GetResult();

    public static Task<VideoRecorder> StartAsync(string path, Int32Rect bounds)
    {
        var recorder = new VideoRecorder(path, bounds);
        return recorder._ready.Task;
    }

    /// <summary>Stops capturing and completes only after the MP4 has been finalized and published.</summary>
    public Task StopAsync()
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        return Completion;
    }

    private void Record()
    {
        Exception? failure = null;
        try
        {
            using var writer = new MediaFoundationVideoWriter(_path, Bounds.Width, Bounds.Height, FramesPerSecond);
            using var capture = new DesktopFrameCapture(Bounds);
            byte[] pendingFrame = new byte[checked(Bounds.Width * Bounds.Height * 4)];
            byte[] nextFrame = new byte[pendingFrame.Length];
            capture.CopyFrame(pendingFrame);
            Interlocked.Exchange(ref _startedAt, Stopwatch.GetTimestamp());
            long pendingTimestamp = 0;
            long nextDue = TimeSpan.TicksPerSecond / FramesPerSecond;
            _ready.SetResult(this);

            while (Volatile.Read(ref _stopRequested) == 0)
            {
                // Poll briefly so StopAsync is responsive without retaining any event handles.
                long remaining = nextDue - Elapsed.Ticks;
                if (remaining > 0)
                {
                    Thread.Sleep((int)Math.Clamp(remaining / TimeSpan.TicksPerMillisecond, 1, 20));
                    continue;
                }
                capture.CopyFrame(nextFrame);
                long timestamp = Math.Max(pendingTimestamp + 1, Elapsed.Ticks);
                writer.WriteFrame(pendingFrame, pendingTimestamp, timestamp - pendingTimestamp);
                (pendingFrame, nextFrame) = (nextFrame, pendingFrame);
                pendingTimestamp = timestamp;
                // When encoding takes longer than a frame, advance directly to the next
                // interval. Actual timestamps retain elapsed time without a growing queue.
                nextDue += TimeSpan.TicksPerSecond / FramesPerSecond;
                long now = Elapsed.Ticks;
                if (nextDue < now) nextDue = now + TimeSpan.TicksPerSecond / FramesPerSecond;
            }

            long end = Math.Max(pendingTimestamp + 1, Elapsed.Ticks);
            Interlocked.Exchange(ref _elapsedTicks, end);
            writer.WriteFrame(pendingFrame, pendingTimestamp, end - pendingTimestamp);
            writer.Finish();
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            if (Interlocked.Read(ref _elapsedTicks) < 0)
                Interlocked.Exchange(ref _elapsedTicks, Elapsed.Ticks);
        }

        if (failure == null) _completion.SetResult();
        else
        {
            if (_ready.TrySetException(failure))
            {
                // StartAsync exposes the initialization failure, so no session is returned.
                // Complete its inaccessible completion task without another unobserved fault.
                _completion.SetResult();
            }
            else _completion.SetException(failure);
        }
    }

    private sealed class DesktopFrameCapture : IDisposable
    {
        private readonly Int32Rect _bounds;
        private readonly int _length;
        private IntPtr _screenDc;
        private IntPtr _imageDc;
        private IntPtr _bitmap;
        private IntPtr _previous;
        private IntPtr _pixels;

        internal DesktopFrameCapture(Int32Rect bounds)
        {
            _bounds = bounds;
            _length = checked(bounds.Width * bounds.Height * 4);
            try
            {
                _screenDc = CaptureNative.GetDC(IntPtr.Zero);
                if (_screenDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                _imageDc = CaptureNative.CreateCompatibleDC(_screenDc);
                if (_imageDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var info = new CaptureNative.BitmapInfo
                {
                    Header = new CaptureNative.BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<CaptureNative.BitmapInfoHeader>(),
                        Width = bounds.Width, Height = -bounds.Height, Planes = 1, BitCount = 32,
                        SizeImage = (uint)_length
                    }
                };
                _bitmap = CaptureNative.CreateDIBSection(_screenDc, ref info, 0, out _pixels, IntPtr.Zero, 0);
                if (_bitmap == IntPtr.Zero || _pixels == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                _previous = CaptureNative.SelectObject(_imageDc, _bitmap);
                if (_previous == IntPtr.Zero || _previous == new IntPtr(-1))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch { Dispose(); throw; }
        }

        internal void CopyFrame(byte[] frame)
        {
            if (!CaptureNative.BitBlt(_imageDc, 0, 0, _bounds.Width, _bounds.Height,
                _screenDc, _bounds.X, _bounds.Y, 0x00CC0020 | 0x40000000))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            DrawCursor();
            CaptureNative.GdiFlush();
            Marshal.Copy(_pixels, frame, 0, _length);
        }

        private void DrawCursor()
        {
            var cursor = new CaptureNative.CursorInfo { Size = Marshal.SizeOf<CaptureNative.CursorInfo>() };
            if (!CaptureNative.GetCursorInfo(ref cursor) || (cursor.Flags & 1) == 0) return;
            IntPtr icon = CaptureNative.CopyIcon(cursor.Cursor);
            if (icon == IntPtr.Zero) return;
            try
            {
                if (!CaptureNative.GetIconInfo(icon, out var info)) return;
                try
                {
                    CaptureNative.DrawIconEx(_imageDc, cursor.Position.X - _bounds.X - (int)info.HotspotX,
                        cursor.Position.Y - _bounds.Y - (int)info.HotspotY, icon, 0, 0, 0, IntPtr.Zero, 3);
                }
                finally
                {
                    if (info.Mask != IntPtr.Zero) CaptureNative.DeleteObject(info.Mask);
                    if (info.Color != IntPtr.Zero) CaptureNative.DeleteObject(info.Color);
                }
            }
            finally { CaptureNative.DestroyIcon(icon); }
        }

        public void Dispose()
        {
            if (_imageDc != IntPtr.Zero && _previous != IntPtr.Zero && _previous != new IntPtr(-1))
                CaptureNative.SelectObject(_imageDc, _previous);
            if (_bitmap != IntPtr.Zero) CaptureNative.DeleteObject(_bitmap);
            if (_imageDc != IntPtr.Zero) CaptureNative.DeleteDC(_imageDc);
            if (_screenDc != IntPtr.Zero) CaptureNative.ReleaseDC(IntPtr.Zero, _screenDc);
            _screenDc = _imageDc = _bitmap = _previous = _pixels = IntPtr.Zero;
        }
    }

    private static class CaptureNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfoHeader
        {
            internal uint Size;
            internal int Width, Height;
            internal ushort Planes, BitCount;
            internal uint Compression, SizeImage;
            internal int XPelsPerMeter, YPelsPerMeter;
            internal uint ColorsUsed, ColorsImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfo { internal BitmapInfoHeader Header; internal uint Color; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct CursorInfo { internal int Size; internal uint Flags; internal IntPtr Cursor; internal Point Position; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct IconInfo
        {
            internal int IsIcon;
            internal uint HotspotX, HotspotY;
            internal IntPtr Mask, Color;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage,
            out IntPtr pixels, IntPtr section, uint offset);
        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BitBlt(IntPtr target, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GdiFlush();
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("user32.dll")]
        internal static extern IntPtr CopyIcon(IntPtr icon);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height,
            uint step, IntPtr brush, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr icon);
    }
}
