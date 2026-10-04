using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WinSkitch;

public sealed record DesktopCapture(BitmapSource Image, Int32Rect Bounds, IReadOnlyList<Int32Rect> Windows);

public static class ScreenCapture
{
    public static DesktopCapture CaptureDesktop()
    {
        var bounds = new Int32Rect(NativeMethods.GetSystemMetrics(76), NativeMethods.GetSystemMetrics(77),
            NativeMethods.GetSystemMetrics(78), NativeMethods.GetSystemMetrics(79));
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("画面のサイズを取得できませんでした。");

        IntPtr screenDc = IntPtr.Zero;
        IntPtr imageDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        BitmapSource image;
        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            imageDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (imageDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
            if (bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = NativeMethods.SelectObject(imageDc, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!NativeMethods.BitBlt(imageDc, 0, 0, bounds.Width, bounds.Height, screenDc,
                bounds.X, bounds.Y, NativeMethods.Srccopy | NativeMethods.CaptureBlt))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
        }
        finally
        {
            if (imageDc != IntPtr.Zero && previous != IntPtr.Zero && previous != new IntPtr(-1))
                NativeMethods.SelectObject(imageDc, previous);
            if (bitmap != IntPtr.Zero) NativeMethods.DeleteObject(bitmap);
            if (imageDc != IntPtr.Zero) NativeMethods.DeleteDC(imageDc);
            if (screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }

        var windows = new List<Int32Rect>();
        NativeMethods.EnumWindowsProc callback = (window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window)
                || NativeMethods.GetWindowTextLength(window) == 0) return true;
            if (NativeMethods.DwmGetWindowInt(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            if (NativeMethods.DwmGetWindowRect(window, 9, out var rect, Marshal.SizeOf<NativeMethods.Rect>()) != 0
                && !NativeMethods.GetWindowRect(window, out rect)) return true;
            if (rect.Right - rect.Left < 20 || rect.Bottom - rect.Top < 20) return true;
            var visible = Intersect(bounds, new Int32Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            if (!visible.IsEmpty) windows.Add(visible);
            return true;
        };
        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return new DesktopCapture(image, bounds, windows.AsReadOnly());
    }

    public static Int32Rect MonitorAtCursor()
    {
        if (!NativeMethods.GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var monitor = NativeMethods.MonitorFromPoint(point, 2);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return ToRect(info.Monitor);
    }

    public static BitmapSource Crop(DesktopCapture capture, Int32Rect globalBounds)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var bounds = Intersect(capture.Bounds, globalBounds);
        if (bounds.IsEmpty) throw new ArgumentOutOfRangeException(nameof(globalBounds), "選択範囲が画面の外にあります。");
        var image = new CroppedBitmap(capture.Image, new Int32Rect(bounds.X - capture.Bounds.X,
            bounds.Y - capture.Bounds.Y, bounds.Width, bounds.Height));
        image.Freeze();
        return image;
    }

    internal static IReadOnlyList<Int32Rect> Monitors()
    {
        var monitors = new List<Int32Rect>();
        NativeMethods.MonitorEnumProc callback = (IntPtr monitor, IntPtr dc, ref NativeMethods.Rect rect, IntPtr parameter) =>
        {
            monitors.Add(ToRect(rect));
            return true;
        };
        if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(callback);
        return monitors;
    }

    internal static Int32Rect Intersect(Int32Rect first, Int32Rect second)
    {
        int left = Math.Max(first.X, second.X);
        int top = Math.Max(first.Y, second.Y);
        int right = Math.Min(first.X + first.Width, second.X + second.Width);
        int bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);
        return right > left && bottom > top ? new Int32Rect(left, top, right - left, bottom - top) : Int32Rect.Empty;
    }

    private static Int32Rect ToRect(NativeMethods.Rect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
}
