using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSkitch;

public static class ClipboardService
{
    private static readonly uint PngFormat = NativeMethods.RegisterClipboardFormat("PNG");

    public static bool SetImage(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        IntPtr dibMemory = IntPtr.Zero;
        IntPtr pngMemory = IntPtr.Zero;
        HwndSource? temporaryOwner = null;
        bool opened = false;
        try
        {
            // CF_DIB provides broad compatibility; PNG retains the source's alpha channel.
            var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
            opaque.Freeze();
            var bmpEncoder = new BmpBitmapEncoder();
            bmpEncoder.Frames.Add(BitmapFrame.Create(opaque));
            using var bmp = new MemoryStream();
            bmpEncoder.Save(bmp);
            byte[] bmpBytes = bmp.ToArray();
            var dibBytes = new byte[bmpBytes.Length - 14];
            Buffer.BlockCopy(bmpBytes, 14, dibBytes, 0, dibBytes.Length);
            var pngEncoder = new PngBitmapEncoder();
            pngEncoder.Frames.Add(BitmapFrame.Create(image));
            using var png = new MemoryStream();
            pngEncoder.Save(png);

            dibMemory = Allocate(dibBytes);
            pngMemory = Allocate(png.ToArray());
            if (dibMemory == IntPtr.Zero || pngMemory == IntPtr.Zero || PngFormat == 0) return false;

            IntPtr owner = IntPtr.Zero;
            if (Application.Current?.MainWindow is { } window)
                owner = new WindowInteropHelper(window).EnsureHandle();
            if (owner == IntPtr.Zero)
            {
                temporaryOwner = new HwndSource(new HwndSourceParameters("WinSkitch Clipboard")
                {
                    ParentWindow = new IntPtr(-3), // HWND_MESSAGE, never visible.
                    WindowStyle = 0,
                    Width = 0,
                    Height = 0
                });
                owner = temporaryOwner.Handle;
            }

            opened = TryOpen(owner);
            if (!opened || !NativeMethods.EmptyClipboard()) return false;
            bool dibWritten = NativeMethods.SetClipboardData(NativeMethods.CfDib, dibMemory) != IntPtr.Zero;
            if (dibWritten) dibMemory = IntPtr.Zero; // Ownership passes to Windows.
            bool pngWritten = NativeMethods.SetClipboardData(PngFormat, pngMemory) != IntPtr.Zero;
            if (pngWritten) pngMemory = IntPtr.Zero;
            return dibWritten && pngWritten;
        }
        catch (Exception exception) when (exception is ExternalException or IOException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (opened) NativeMethods.CloseClipboard();
            if (dibMemory != IntPtr.Zero) NativeMethods.GlobalFree(dibMemory);
            if (pngMemory != IntPtr.Zero) NativeMethods.GlobalFree(pngMemory);
            temporaryOwner?.Dispose();
        }
    }

    public static BitmapSource? GetImage()
    {
        byte[]? png = null;
        byte[]? dib = null;
        BitmapSource? bitmap = null;
        if (TryOpen(IntPtr.Zero))
        {
            try
            {
                if (PngFormat != 0 && NativeMethods.IsClipboardFormatAvailable(PngFormat))
                    png = ReadMemory(NativeMethods.GetClipboardData(PngFormat));
                if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfDibV5))
                    dib = ReadMemory(NativeMethods.GetClipboardData(NativeMethods.CfDibV5));
                if (dib is null && NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfDib))
                    dib = ReadMemory(NativeMethods.GetClipboardData(NativeMethods.CfDib));
                // CF_BITMAP is owned by the clipboard. Detach a WPF copy before closing it.
                if (NativeMethods.IsClipboardFormatAvailable(2))
                {
                    IntPtr handle = NativeMethods.GetClipboardData(2);
                    if (handle != IntPtr.Zero)
                    {
                        try
                        {
                            bitmap = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                            bitmap.Freeze();
                        }
                        catch (Exception exception) when (exception is ExternalException or ArgumentException or InvalidOperationException)
                        {
                            bitmap = null;
                        }
                    }
                }
            }
            finally { NativeMethods.CloseClipboard(); }
        }
        // Decode after closing the clipboard so image work never locks other applications out.
        if (png is not null && Decode(png) is { } pngImage) return pngImage;
        if (dib is not null)
        {
            try
            {
                if (Decode(DibToBitmap(dib)) is { } dibImage) return dibImage;
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException or InvalidDataException)
            {
                // The device-dependent bitmap, if supplied, remains available as a fallback.
            }
        }
        return bitmap;
    }

    private static bool TryOpen(IntPtr owner)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (NativeMethods.OpenClipboard(owner)) return true;
            if (attempt < 3) Thread.Sleep(10);
        }
        return false;
    }

    private static IntPtr Allocate(byte[] bytes)
    {
        IntPtr memory = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, new UIntPtr((uint)bytes.Length));
        if (memory == IntPtr.Zero) return IntPtr.Zero;
        IntPtr pointer = NativeMethods.GlobalLock(memory);
        if (pointer == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(memory);
            return IntPtr.Zero;
        }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
        finally { NativeMethods.GlobalUnlock(memory); }
        return memory;
    }

    private static byte[]? ReadMemory(IntPtr memory)
    {
        if (memory == IntPtr.Zero) return null;
        ulong size = NativeMethods.GlobalSize(memory).ToUInt64();
        if (size == 0 || size > 512UL * 1024 * 1024) return null;
        IntPtr pointer = NativeMethods.GlobalLock(memory);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { NativeMethods.GlobalUnlock(memory); }
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            var image = decoder.Frames[0];
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException or InvalidOperationException or FormatException or ExternalException)
        {
            return null;
        }
    }

    private static byte[] DibToBitmap(byte[] dib)
    {
        if (dib.Length < 12) throw new InvalidDataException("DIB header is missing.");
        uint headerSize = BitConverter.ToUInt32(dib, 0);
        int pixelOffset;
        if (headerSize == 12)
        {
            int bits = BitConverter.ToUInt16(dib, 10);
            pixelOffset = 12 + (bits <= 8 ? (1 << bits) * 3 : 0);
        }
        else
        {
            if (headerSize < 40 || headerSize > dib.Length) throw new InvalidDataException("DIB header is invalid.");
            int bits = BitConverter.ToUInt16(dib, 14);
            uint compression = BitConverter.ToUInt32(dib, 16);
            uint paletteColors = BitConverter.ToUInt32(dib, 32);
            if (paletteColors == 0 && bits <= 8) paletteColors = (uint)(1 << bits);
            int masks = headerSize == 40 ? compression == 3 ? 12 : compression == 6 ? 16 : 0 : 0;
            pixelOffset = checked((int)headerSize + masks + (int)paletteColors * 4);
        }
        if (pixelOffset >= dib.Length) throw new InvalidDataException("DIB pixel data is missing.");
        var bytes = new byte[checked(dib.Length + 14)];
        using var writer = new BinaryWriter(new MemoryStream(bytes, true));
        writer.Write((ushort)0x4D42);
        writer.Write(bytes.Length);
        writer.Write(0);
        writer.Write(pixelOffset + 14);
        writer.Write(dib);
        return bytes;
    }
}
