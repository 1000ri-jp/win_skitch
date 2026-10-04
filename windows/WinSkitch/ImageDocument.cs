using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSkitch;

public sealed class ImageDocument
{
    public sealed record Snapshot(BitmapSource Background, List<Annotation> Annotations);
    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();
    public BitmapSource? Background { get; private set; }
    public List<Annotation> Annotations { get; } = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? FilePath { get; set; }
    public string FileName { get; private set; } = "Skitch.png";

    public void Load(BitmapSource image, string? name = null)
    {
        Background = Normalize(image);
        Annotations.Clear();
        _undo.Clear();
        _redo.Clear();
        FilePath = null;
        FileName = name ?? $"Skitch_{DateTime.Now:yyyy-MM-dd_HHmmss}.png";
    }

    public void Open(string path)
    {
        // OnLoad detaches the image from its file, allowing subsequent overwrite.
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Load(decoder.Frames[0], Path.GetFileNameWithoutExtension(path) + "_skitch.png");
    }

    public Snapshot CaptureState()
    {
        if (Background is null) throw new InvalidOperationException("画像がありません。");
        return new Snapshot(Background, Annotations.Select(a => a.Clone()).ToList());
    }

    public void CommitHistory(Snapshot before)
    {
        _undo.Add(before);
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private bool Restore(List<Snapshot> source, List<Snapshot> destination)
    {
        if (source.Count == 0 || Background is null) return false;
        destination.Add(CaptureState());
        var state = source[^1];
        source.RemoveAt(source.Count - 1);
        Background = state.Background;
        Annotations.Clear();
        Annotations.AddRange(state.Annotations.Select(a => a.Clone()));
        return true;
    }

    public bool Undo() => Restore(_undo, _redo);
    public bool Redo() => Restore(_redo, _undo);

    public BitmapSource Composite()
    {
        if (Background is null) throw new InvalidOperationException("画像がありません。");
        return AnnotationRenderer.Render(Background, Annotations);
    }

    public BitmapSource Flatten()
    {
        var source = Composite();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }
        var output = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        output.Render(visual);
        output.Freeze();
        return output;
    }

    public void Crop(Rect region)
    {
        if (Background is null) return;
        int x = (int)Math.Round(region.X), y = (int)Math.Round(region.Y);
        int w = (int)Math.Round(region.Width), h = (int)Math.Round(region.Height);
        if (w < 2 || h < 2) return;
        if (w > 32767 || h > 32767 || (long)w * h > 100_000_000)
            throw new InvalidOperationException("切り抜き範囲が大きすぎます。");
        var before = CaptureState();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawImage(Background, new Rect(-x, -y, Background.PixelWidth, Background.PixelHeight));
        }
        var image = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        Background = image;
        foreach (var a in Annotations) a.Move(-x, -y);
        CommitHistory(before);
    }

    public void Save(string path)
    {
        var extension = Path.GetExtension(path);
        BitmapEncoder encoder = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? new JpegBitmapEncoder { QualityLevel = 92 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Flatten()));
        using (var stream = File.Create(path)) encoder.Save(stream);
        FilePath = path;
    }

    public static BitmapSource Normalize(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Pbgra32 ? source :
            new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        var normalized = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
            PixelFormats.Pbgra32, null, pixels, stride);
        normalized.Freeze();
        return normalized;
    }
}
