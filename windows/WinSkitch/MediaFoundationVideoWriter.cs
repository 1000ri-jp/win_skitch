using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinSkitch;

/// <summary>
/// Writes top-down BGR32 frames to an H.264 MP4 using the Windows encoder.
/// Create, write, finish, and dispose on the same thread. Finish publishes the
/// recording; disposing an unfinished writer leaves the destination untouched.
/// </summary>
public sealed class MediaFoundationVideoWriter : IDisposable
{
    public const int MaximumDimension = 4096;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly string _path;
    private readonly string _temporaryPath;
    private readonly int _inputWidth;
    private readonly int _inputHeight;
    private readonly int _inputLength;
    private readonly byte[]? _paddedFrame;
    private IntPtr _writer;
    private uint _stream;
    private bool _comStarted;
    private bool _mfStarted;
    private bool _disposed;
    private bool _finished;
    private bool _finalized;
    private bool _failed;
    private long _previousTimestamp = -1;

    public int Width { get; }
    public int Height { get; }
    public int FramesPerSecond { get; }

    public MediaFoundationVideoWriter(string path, int width, int height, int framesPerSecond = 15)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (width <= 0 || width > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(width), "録画範囲の幅は 1～4096 ピクセルにしてください。");
        if (height <= 0 || height > MaximumDimension)
            throw new ArgumentOutOfRangeException(nameof(height), "録画範囲の高さは 1～4096 ピクセルにしてください。");
        if (framesPerSecond <= 0 || framesPerSecond > 60)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        _inputWidth = width;
        _inputHeight = height;
        _inputLength = checked(width * height * 4);
        Width = checked((width + 1) & ~1);
        Height = checked((height + 1) & ~1);
        FramesPerSecond = framesPerSecond;
        int outputLength = checked(Width * Height * 4);
        _path = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(_path)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        _temporaryPath = Path.Combine(directory, $".winskitch-recording-{Guid.NewGuid():N}.mp4");

        IntPtr outputType = IntPtr.Zero;
        IntPtr inputType = IntPtr.Zero;
        try
        {
            uint apartment = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA ? 2u : 0u;
            Mf.Check(Mf.CoInitializeEx(IntPtr.Zero, apartment));
            _comStarted = true;
            Mf.Check(Mf.MFStartup(0x00020070, 0));
            _mfStarted = true;
            Mf.Check(Mf.MFCreateSinkWriterFromURL(_temporaryPath, IntPtr.Zero, IntPtr.Zero, out _writer));
            Mf.Check(Mf.MFCreateMediaType(out outputType));
            ConfigureType(outputType, Mf.H264);
            // About 0.15 bits/pixel/frame keeps text legible without oversized files.
            uint bitrate = (uint)Math.Clamp((long)Width * Height * framesPerSecond / 6,
                1_000_000L, 30_000_000L);
            Mf.SetUInt32(outputType, Mf.AvgBitrate, bitrate);
            Mf.Check(Mf.Method<Mf.AddStream>(_writer, 3)(_writer, outputType, out _stream));

            Mf.Check(Mf.MFCreateMediaType(out inputType));
            ConfigureType(inputType, Mf.Rgb32);
            Mf.SetUInt32(inputType, Mf.DefaultStride, checked((uint)Width * 4));
            Mf.Check(Mf.Method<Mf.SetInputType>(_writer, 4)(_writer, _stream, inputType, IntPtr.Zero));
            Mf.Check(Mf.Method<Mf.NoArguments>(_writer, 5)(_writer));
            if (width != Width || height != Height) _paddedFrame = new byte[outputLength];
        }
        catch
        {
            Mf.Release(ref inputType);
            Mf.Release(ref outputType);
            Dispose();
            throw;
        }
        finally
        {
            Mf.Release(ref inputType);
            Mf.Release(ref outputType);
        }
    }

    private void ConfigureType(IntPtr type, Guid subtype)
    {
        Mf.SetGuid(type, Mf.MajorType, Mf.Video);
        Mf.SetGuid(type, Mf.Subtype, subtype);
        Mf.SetUInt32(type, Mf.InterlaceMode, 2); // MFVideoInterlace_Progressive
        Mf.SetUInt64(type, Mf.FrameSize, Mf.Pair((uint)Width, (uint)Height));
        Mf.SetUInt64(type, Mf.FrameRate, Mf.Pair((uint)FramesPerSecond, 1));
        Mf.SetUInt64(type, Mf.PixelAspectRatio, Mf.Pair(1, 1));
    }

    public void WriteFrame(byte[] bgr32, long timestamp100ns, long duration100ns)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(bgr32);
        if (bgr32.Length != _inputLength)
            throw new ArgumentException("フレームのサイズが録画サイズと一致しません。", nameof(bgr32));
        if (timestamp100ns < 0 || timestamp100ns <= _previousTimestamp)
            throw new ArgumentOutOfRangeException(nameof(timestamp100ns));
        if (duration100ns <= 0 || timestamp100ns > long.MaxValue - duration100ns)
            throw new ArgumentOutOfRangeException(nameof(duration100ns));

        byte[] frame = bgr32;
        if (_paddedFrame != null)
        {
            // The extra right column and bottom row stay black. Never capture pixels
            // beyond the selected rectangle just to satisfy H.264's even dimensions.
            for (int row = 0; row < _inputHeight; row++)
                Buffer.BlockCopy(bgr32, row * _inputWidth * 4, _paddedFrame, row * Width * 4, _inputWidth * 4);
            frame = _paddedFrame;
        }

        IntPtr buffer = IntPtr.Zero;
        IntPtr sample = IntPtr.Zero;
        bool locked = false;
        try
        {
            Mf.Check(Mf.MFCreateMemoryBuffer((uint)frame.Length, out buffer));
            Mf.Check(Mf.Method<Mf.LockBuffer>(buffer, 3)(buffer, out IntPtr data, out _, out _));
            locked = true;
            Marshal.Copy(frame, 0, data, frame.Length);
            Mf.Check(Mf.Method<Mf.NoArguments>(buffer, 4)(buffer));
            locked = false;
            Mf.Check(Mf.Method<Mf.SetLength>(buffer, 6)(buffer, (uint)frame.Length));
            Mf.Check(Mf.MFCreateSample(out sample));
            Mf.Check(Mf.Method<Mf.PointerArgument>(sample, 42)(sample, buffer));
            Mf.Check(Mf.Method<Mf.TimeArgument>(sample, 36)(sample, timestamp100ns));
            Mf.Check(Mf.Method<Mf.TimeArgument>(sample, 38)(sample, duration100ns));
            // Default sink-writer throttling provides back pressure; frames are never queued in managed memory.
            Mf.Check(Mf.Method<Mf.WriteSample>(_writer, 6)(_writer, _stream, sample));
            _previousTimestamp = timestamp100ns;
        }
        catch
        {
            _failed = true;
            throw;
        }
        finally
        {
            if (locked) Mf.Method<Mf.NoArguments>(buffer, 4)(buffer);
            Mf.Release(ref sample);
            Mf.Release(ref buffer);
        }
    }

    public void Finish()
    {
        VerifyThread();
        if (_finished) return;
        EnsureWritable();
        if (_previousTimestamp < 0) throw new InvalidOperationException("録画するフレームがありません。");
        try
        {
            Mf.Check(Mf.Method<Mf.NoArguments>(_writer, 11)(_writer));
            _finalized = true;
            Mf.Release(ref _writer); // Close all file handles before replacing the destination.
            try { File.Move(_temporaryPath, _path, overwrite: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"動画は保存できましたが、指定の保存先へ移動できませんでした。動画: {_temporaryPath}", error);
            }
            _finished = true;
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    private void EnsureWritable()
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished || _failed) throw new InvalidOperationException("この録画ファイルへの書き込みは終了しています。");
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("動画エンコーダーは作成したスレッドで使用してください。");
    }

    public void Dispose()
    {
        VerifyThread();
        if (_disposed) return;
        _disposed = true;
        Mf.Release(ref _writer);
        if (_mfStarted) { Mf.MFShutdown(); _mfStarted = false; }
        if (_comStarted) { Mf.CoUninitialize(); _comStarted = false; }
        if (!_finalized)
        {
            try { File.Delete(_temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Minimal native ABI binding. Slots include IUnknown (0..2) and IMFAttributes
    // (3..32), as declared in Windows SDK mfobjects.h and mfreadwrite.h:
    // https://github.com/microsoft/win32metadata/tree/main/generation/WinSDK/RecompiledIdlHeaders/um
    // Encoding follows Microsoft's sink writer tutorial:
    // https://learn.microsoft.com/windows/win32/medfound/tutorial--using-the-sink-writer-to-encode-video
    private static class Mf
    {
        internal static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        internal static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        internal static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
        internal static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
        internal static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
        internal static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        internal static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        internal static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
        internal static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        internal static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        internal static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

        internal static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
        internal static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
        internal static ulong Pair(uint high, uint low) => ((ulong)high << 32) | low;
        internal static void Release(ref IntPtr instance)
        {
            if (instance == IntPtr.Zero) return;
            Marshal.Release(instance);
            instance = IntPtr.Zero;
        }
        internal static void SetGuid(IntPtr type, Guid key, Guid value) =>
            Check(Method<GuidAttribute>(type, 24)(type, ref key, ref value));
        internal static void SetUInt32(IntPtr type, Guid key, uint value) =>
            Check(Method<UIntAttribute>(type, 21)(type, ref key, value));
        internal static void SetUInt64(IntPtr type, Guid key, ulong value) =>
            Check(Method<LongAttribute>(type, 22)(type, ref key, value));

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int GuidAttribute(IntPtr self, ref Guid key, ref Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int UIntAttribute(IntPtr self, ref Guid key, uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int LongAttribute(IntPtr self, ref Guid key, ulong value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int AddStream(IntPtr self, IntPtr type, out uint stream);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int SetInputType(IntPtr self, uint stream, IntPtr type, IntPtr attributes);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int WriteSample(IntPtr self, uint stream, IntPtr sample);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int NoArguments(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int LockBuffer(IntPtr self, out IntPtr data, out uint maximumLength, out uint currentLength);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int SetLength(IntPtr self, uint length);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int PointerArgument(IntPtr self, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int TimeArgument(IntPtr self, long value);

        [DllImport("ole32.dll", ExactSpelling = true)]
        internal static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll", ExactSpelling = true)]
        internal static extern void CoUninitialize();
        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFStartup(uint version, uint flags);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFCreateMemoryBuffer(uint length, out IntPtr buffer);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFCreateSample(out IntPtr sample);
        [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IntPtr writer);
    }
}
