using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSkitch;

public enum AnnotationKind
{
    Arrow, Text, Rectangle, RoundedRectangle, Ellipse, Line,
    Pen, Highlighter, Pixelate, Blur, Stamp
}

/// <summary>Editable annotation geometry, always expressed in source-image pixels.</summary>
public sealed class Annotation
{
    public AnnotationKind Kind { get; set; }
    public Color Color { get; set; } = System.Windows.Media.Color.FromRgb(234, 51, 35);
    public double Width { get; set; } = 6;
    public Point Start { get; set; }
    public Point End { get; set; }
    public List<Point> Points { get; set; } = new List<Point>();
    public string Text { get; set; } = string.Empty;
    public string Stamp { get; set; } = "check";
    public bool Hidden { get; set; }

    internal AnnotationDrawingCache? DrawingCache { get; set; }

    public Annotation Clone() => new Annotation
    {
        Kind = Kind, Color = Color, Width = Width, Start = Start, End = End,
        Points = new List<Point>(Points), Text = Text, Stamp = Stamp, Hidden = Hidden
    };

    public Rect Bounds
    {
        get
        {
            if (Kind == AnnotationKind.Text)
                return AnnotationRenderer.TextBounds(this);
            if (Kind == AnnotationKind.Stamp)
            {
                double radius = EffectiveWidth;
                return new Rect(Start.X - radius, Start.Y - radius, radius * 2, radius * 2);
            }
            if (Kind == AnnotationKind.Pen || Kind == AnnotationKind.Highlighter)
            {
                if (Points.Count == 0)
                    return new Rect(Start, Start);
                double left = Points[0].X, top = Points[0].Y;
                double right = left, bottom = top;
                foreach (Point point in Points)
                {
                    left = Math.Min(left, point.X);
                    top = Math.Min(top, point.Y);
                    right = Math.Max(right, point.X);
                    bottom = Math.Max(bottom, point.Y);
                }
                return new Rect(new Point(left, top), new Point(right, bottom));
            }
            return new Rect(Start, End);
        }
    }

    public void Move(double dx, double dy)
    {
        var delta = new Vector(dx, dy);
        Start += delta;
        End += delta;
        for (int i = 0; i < Points.Count; i++)
            Points[i] += delta;
    }

    public IReadOnlyList<(string Name, Point Position)> Handles
    {
        get
        {
            if (Kind == AnnotationKind.Arrow || Kind == AnnotationKind.Line)
                return new[] { ("p0", Start), ("p1", End) };
            if (Kind == AnnotationKind.Stamp)
            {
                double diagonal = EffectiveWidth / Math.Sqrt(2);
                return new[] { ("r", new Point(Start.X + diagonal, Start.Y + diagonal)) };
            }
            if (IsBox)
                return new[]
                {
                    ("x0y0", Start), ("x1y0", new Point(End.X, Start.Y)),
                    ("x0y1", new Point(Start.X, End.Y)), ("x1y1", End)
                };
            return Array.Empty<(string Name, Point Position)>();
        }
    }

    public void SetHandle(string name, Point position)
    {
        switch (name)
        {
            case "p0": Start = position; break;
            case "p1": End = position; break;
            case "r":
                if (Kind == AnnotationKind.Stamp)
                    Width = Math.Max(8, (position - Start).Length);
                break;
            case "x0y0": Start = position; break;
            case "x1y1": End = position; break;
            case "x1y0":
                Start = new Point(Start.X, position.Y);
                End = new Point(position.X, End.Y);
                break;
            case "x0y1":
                Start = new Point(position.X, Start.Y);
                End = new Point(End.X, position.Y);
                break;
        }
    }

    public bool HitTest(Point point, double tolerance)
    {
        if (Hidden)
            return false;
        tolerance = Math.Max(0, tolerance);
        double width = EffectiveWidth;
        switch (Kind)
        {
            case AnnotationKind.Line:
                return SegmentDistance(point, Start, End) <= width / 2 + tolerance;
            case AnnotationKind.Arrow:
                Geometry arrow = AnnotationRenderer.ArrowGeometry(this);
                return arrow.FillContains(point) ||
                    arrow.StrokeContains(new Pen(Brushes.Black, 2 * tolerance + 4), point) ||
                    SegmentDistance(point, Start, End) <= width + tolerance;
            case AnnotationKind.Pen:
            case AnnotationKind.Highlighter:
                double reach = (Kind == AnnotationKind.Highlighter ? width * 3.5 : width) / 2 + tolerance;
                if (Points.Count == 0)
                    return (point - Start).Length <= reach;
                if (Points.Count == 1)
                    return (point - Points[0]).Length <= reach;
                for (int i = 1; i < Points.Count; i++)
                    if (SegmentDistance(point, Points[i - 1], Points[i]) <= reach)
                        return true;
                return false;
            case AnnotationKind.Stamp:
                return (point - Start).Length <= width + tolerance;
            case AnnotationKind.Rectangle:
            case AnnotationKind.RoundedRectangle:
            case AnnotationKind.Ellipse:
                Geometry shape = AnnotationRenderer.ShapeGeometry(this);
                return shape.StrokeContains(new Pen(Brushes.Black, width + tolerance * 2), point);
            default:
                Rect rect = Bounds;
                rect.Inflate(tolerance, tolerance);
                return rect.Contains(point);
        }
    }

    internal double EffectiveWidth => double.IsFinite(Width) ? Math.Clamp(Width, 1, 10000) : 6;

    private bool IsBox => Kind == AnnotationKind.Rectangle || Kind == AnnotationKind.RoundedRectangle ||
        Kind == AnnotationKind.Ellipse || Kind == AnnotationKind.Pixelate || Kind == AnnotationKind.Blur;

    private static double SegmentDistance(Point point, Point start, Point end)
    {
        Vector segment = end - start;
        double lengthSquared = segment.LengthSquared;
        double t = lengthSquared == 0 ? 0 : Math.Clamp(Vector.Multiply(point - start, segment) / lengthSquared, 0, 1);
        return (point - (start + segment * t)).Length;
    }
}

/// <summary>
/// Shared preview/export renderer. Vector annotations are batched; only effect regions
/// are rasterized before the final image, preserving annotation order for redaction.
/// </summary>
public static class AnnotationRenderer
{
    private static readonly Typeface TextTypeface = new Typeface(
        new FontFamily("Yu Gothic, Meiryo, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface SymbolTypeface = new Typeface(
        new FontFamily("Segoe UI Symbol, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public static BitmapSource Render(BitmapSource background, IReadOnlyList<Annotation> annotations)
    {
        if (background == null)
            throw new ArgumentNullException(nameof(background));
        if (annotations == null)
            throw new ArgumentNullException(nameof(annotations));
        int width = background.PixelWidth, height = background.PixelHeight;
        var imageBounds = new Rect(0, 0, width, height);
        var document = new DrawingGroup();
        document.Children.Add(new ImageDrawing(background, imageBounds));

        foreach (Annotation annotation in annotations)
        {
            if (annotation.Hidden)
                continue;
            if (annotation.Kind == AnnotationKind.Pixelate || annotation.Kind == AnnotationKind.Blur)
            {
                Int32Rect region = PixelRegion(annotation.Bounds, width, height);
                if (region.Width < 2 || region.Height < 2)
                    continue;
                BitmapSource current = Rasterize(document, region);
                int stride = checked(region.Width * 4);
                var pixels = new byte[checked(stride * region.Height)];
                current.CopyPixels(pixels, stride, 0);
                if (annotation.Kind == AnnotationKind.Blur)
                    Blur(pixels, region.Width, region.Height, 4, Math.Max(6, annotation.EffectiveWidth * 2));
                else
                    Pixelate(pixels, region.Width, region.Height, Math.Max(6, annotation.EffectiveWidth * 2));
                BitmapSource effect = BitmapSource.Create(region.Width, region.Height, 96, 96,
                    PixelFormats.Pbgra32, null, pixels, stride);
                effect.Freeze();
                document.Children.Add(new ImageDrawing(effect, new Rect(region.X, region.Y, region.Width, region.Height)));
                continue;
            }

            AnnotationDrawingCache cache = GetDrawing(annotation);
            if (!cache.Drawing.Bounds.IntersectsWith(imageBounds))
                continue;
            if (annotation.Kind != AnnotationKind.Highlighter)
            {
                cache.EnsureShadow(imageBounds);
                if (cache.Shadow != null)
                    document.Children.Add(new ImageDrawing(cache.Shadow, cache.ShadowBounds));
            }
            document.Children.Add(cache.Drawing);
        }

        return Rasterize(document, new Int32Rect(0, 0, width, height));
    }

    internal static Rect TextBounds(Annotation annotation)
    {
        FormattedText text = MakeText(annotation.Text.Length == 0 ? " " : annotation.Text,
            annotation.EffectiveWidth, TextTypeface);
        // Keep blank lines and trailing whitespace selectable as well as actual glyphs.
        var bounds = new Rect(annotation.Start, new Size(Math.Max(1, text.WidthIncludingTrailingWhitespace), text.Height));
        bounds.Inflate(TextOutline(annotation), TextOutline(annotation));
        return bounds;
    }

    internal static Geometry ShapeGeometry(Annotation annotation)
    {
        Rect rect = new Rect(annotation.Start, annotation.End);
        if (annotation.Kind == AnnotationKind.Ellipse)
            return new EllipseGeometry(rect);
        double radius = annotation.Kind == AnnotationKind.RoundedRectangle
            ? Math.Max(12, annotation.EffectiveWidth * 2.5) : annotation.EffectiveWidth * 0.6;
        return new RectangleGeometry(rect, radius, radius);
    }

    internal static Geometry ArrowGeometry(Annotation annotation)
    {
        Point start = annotation.Start, end = annotation.End;
        Vector direction = end - start;
        double length = direction.Length;
        if (length < 0.001)
            return new EllipseGeometry(start, Math.Max(0.8, annotation.EffectiveWidth * 0.2), Math.Max(0.8, annotation.EffectiveWidth * 0.2));
        direction /= length;
        var normal = new Vector(-direction.Y, direction.X);
        double width = annotation.EffectiveWidth;
        double head = Math.Min(length * 0.6, 12 + width * 3.6);
        double halfHead = head * 0.64;
        double back = head * 0.2;
        double shaft = Math.Max(1.2, width * 0.55);
        double tail = Math.Max(0.8, width * 0.2);
        Point neck = end - direction * head;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(start + normal * tail, true, true);
            context.PolyLineTo(new[]
            {
                neck + normal * shaft,
                neck - direction * back + normal * halfHead,
                end,
                neck - direction * back - normal * halfHead,
                neck - normal * shaft,
                start - normal * tail
            }, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static AnnotationDrawingCache GetDrawing(Annotation annotation)
    {
        if (annotation.DrawingCache == null || !annotation.DrawingCache.Matches(annotation))
            annotation.DrawingCache = new AnnotationDrawingCache(annotation, CreateDrawing(annotation));
        return annotation.DrawingCache;
    }

    private static DrawingGroup CreateDrawing(Annotation annotation)
    {
        var drawing = new DrawingGroup();
        var brush = new SolidColorBrush(annotation.Color);
        brush.Freeze();
        double width = annotation.EffectiveWidth;
        using (DrawingContext context = drawing.Open())
        {
            switch (annotation.Kind)
            {
                case AnnotationKind.Arrow:
                    Geometry arrow = ArrowGeometry(annotation);
                    context.DrawGeometry(brush, Stroke(Brushes.White, 4), arrow);
                    context.DrawEllipse(Brushes.White, null, annotation.Start, Math.Max(0.8, width * 0.2) + 2, Math.Max(0.8, width * 0.2) + 2);
                    context.DrawGeometry(brush, null, arrow);
                    context.DrawEllipse(brush, null, annotation.Start, Math.Max(0.8, width * 0.2), Math.Max(0.8, width * 0.2));
                    break;
                case AnnotationKind.Line:
                    context.DrawLine(Stroke(Brushes.White, width + 4), annotation.Start, annotation.End);
                    context.DrawLine(Stroke(brush, width), annotation.Start, annotation.End);
                    break;
                case AnnotationKind.Rectangle:
                case AnnotationKind.RoundedRectangle:
                case AnnotationKind.Ellipse:
                    Geometry shape = ShapeGeometry(annotation);
                    context.DrawGeometry(null, Stroke(Brushes.White, width + 4), shape);
                    context.DrawGeometry(null, Stroke(brush, width), shape);
                    break;
                case AnnotationKind.Pen:
                case AnnotationKind.Highlighter:
                    double thickness = annotation.Kind == AnnotationKind.Highlighter ? width * 3.5 : width;
                    Geometry stroke = FreehandGeometry(annotation);
                    if (annotation.Kind == AnnotationKind.Highlighter)
                        context.PushOpacity(0.45);
                    else
                        DrawFreehand(context, stroke, annotation, Brushes.White, thickness + 4);
                    DrawFreehand(context, stroke, annotation, brush, thickness);
                    if (annotation.Kind == AnnotationKind.Highlighter)
                        context.Pop();
                    break;
                case AnnotationKind.Text:
                    Geometry glyphs = MakeText(annotation.Text, width, TextTypeface).BuildGeometry(annotation.Start);
                    Brush outline = IsLight(annotation.Color) ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 34, 34)) : Brushes.White;
                    // WPF paints a stroke over its fill. Draw the outline first so
                    // thick borders do not hide narrow Japanese or Latin glyphs.
                    context.DrawGeometry(null, Stroke(outline, TextOutline(annotation) * 2), glyphs);
                    context.DrawGeometry(brush, null, glyphs);
                    break;
                case AnnotationKind.Stamp:
                    context.DrawEllipse(Brushes.White, null, annotation.Start, width, width);
                    double inner = Math.Max(0, width - Math.Max(2, width * 0.09));
                    context.DrawEllipse(brush, null, annotation.Start, inner, inner);
                    string symbol = StampGlyph(annotation.Stamp);
                    FormattedText stampText = MakeText(symbol, width * (symbol == "?" || symbol == "!" ? 1.2 : 1),
                        symbol == "?" || symbol == "!" ? TextTypeface : SymbolTypeface);
                    Geometry stampGlyph = stampText.BuildGeometry(new Point(0, 0));
                    Rect glyphBounds = stampGlyph.Bounds;
                    context.PushTransform(new TranslateTransform(annotation.Start.X - glyphBounds.X - glyphBounds.Width / 2,
                        annotation.Start.Y - glyphBounds.Y - glyphBounds.Height / 2));
                    context.DrawGeometry(IsLight(annotation.Color) ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 34, 34)) : Brushes.White,
                        null, stampGlyph);
                    context.Pop();
                    break;
            }
        }
        drawing.Freeze();
        return drawing;
    }

    private static FormattedText MakeText(string text, double size, Typeface typeface) => new FormattedText(
        text.Replace("\r\n", "\n").Replace('\r', '\n'), CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight, typeface, size, Brushes.Black, 1);

    private static double TextOutline(Annotation annotation) => Math.Max(2, Math.Round(annotation.EffectiveWidth / 7));

    private static bool IsLight(Color color) => color.R * 0.299 + color.G * 0.587 + color.B * 0.114 > 186;

    private static string StampGlyph(string stamp) => stamp switch
    {
        "check" => "✔", "cross" => "✖", "question" => "?", "exclaim" => "!", "star" => "★", "heart" => "♥", _ => "?"
    };

    private static Pen Stroke(Brush brush, double width)
    {
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static Geometry FreehandGeometry(Annotation annotation)
    {
        var geometry = new StreamGeometry();
        if (annotation.Points.Count > 1)
        {
            using StreamGeometryContext context = geometry.Open();
            context.BeginFigure(annotation.Points[0], false, false);
            for (int i = 1; i < annotation.Points.Count; i++)
                context.LineTo(annotation.Points[i], true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void DrawFreehand(DrawingContext context, Geometry geometry, Annotation annotation, Brush brush, double thickness)
    {
        if (annotation.Points.Count > 1)
            context.DrawGeometry(null, Stroke(brush, thickness), geometry);
        else
            context.DrawEllipse(brush, null, annotation.Points.Count == 1 ? annotation.Points[0] : annotation.Start, thickness / 2, thickness / 2);
    }

    internal static BitmapSource Rasterize(Drawing drawing, Int32Rect region)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(-region.X, -region.Y));
            context.DrawDrawing(drawing);
            context.Pop();
        }
        var image = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }

    private static Int32Rect PixelRegion(Rect rect, int width, int height)
    {
        if (rect.IsEmpty)
            return Int32Rect.Empty;
        int left = (int)Math.Clamp(Math.Round(rect.Left), 0, width);
        int top = (int)Math.Clamp(Math.Round(rect.Top), 0, height);
        int right = (int)Math.Clamp(Math.Round(rect.Right), 0, width);
        int bottom = (int)Math.Clamp(Math.Round(rect.Bottom), 0, height);
        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static void Pixelate(byte[] pixels, int width, int height, double strength)
    {
        int columns = Math.Max(1, (int)(width / strength));
        int rows = Math.Max(1, (int)(height / strength));
        int stride = width * 4;
        for (int by = 0; by < rows; by++)
        {
            int top = (int)((long)by * height / rows);
            int bottom = (int)((long)(by + 1) * height / rows);
            for (int bx = 0; bx < columns; bx++)
            {
                int left = (int)((long)bx * width / columns);
                int right = (int)((long)(bx + 1) * width / columns);
                long blue = 0, green = 0, red = 0, alpha = 0;
                for (int y = top; y < bottom; y++)
                {
                    int index = y * stride + left * 4;
                    for (int x = left; x < right; x++, index += 4)
                    {
                        blue += pixels[index]; green += pixels[index + 1];
                        red += pixels[index + 2]; alpha += pixels[index + 3];
                    }
                }
                long count = (long)(right - left) * (bottom - top);
                byte b = (byte)(blue / count), g = (byte)(green / count), r = (byte)(red / count), a = (byte)(alpha / count);
                for (int y = top; y < bottom; y++)
                {
                    int index = y * stride + left * 4;
                    for (int x = left; x < right; x++, index += 4)
                    {
                        pixels[index] = b; pixels[index + 1] = g;
                        pixels[index + 2] = r; pixels[index + 3] = a;
                    }
                }
            }
        }
    }

    /// <summary>Three separable box passes approximate a Gaussian in linear time.</summary>
    internal static void Blur(byte[] pixels, int width, int height, int channels, double sigma)
    {
        var scratch = new byte[pixels.Length];
        double ideal = Math.Sqrt(4 * sigma * sigma + 1);
        int lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0)
            lower--;
        lower = Math.Max(1, lower);
        int upper = lower + 2;
        int lowerPasses = (int)Math.Round((12 * sigma * sigma - 3 * lower * lower - 12 * lower - 9) / (-4.0 * lower - 4));
        lowerPasses = Math.Clamp(lowerPasses, 0, 3);
        for (int pass = 0; pass < 3; pass++)
        {
            int radius = ((pass < lowerPasses ? lower : upper) - 1) / 2;
            HorizontalBox(pixels, scratch, width, height, channels, radius);
            VerticalBox(scratch, pixels, width, height, channels, radius);
        }
    }

    private static void HorizontalBox(byte[] source, byte[] target, int width, int height, int channels, int radius)
    {
        radius = Math.Min(radius, width);
        int span = radius * 2 + 1;
        int stride = width * channels;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int channel = 0; channel < channels; channel++)
            {
                int sum = source[row + channel] * (radius + 1);
                for (int x = 1; x <= radius; x++)
                    sum += source[row + Math.Min(x, width - 1) * channels + channel];
                for (int x = 0; x < width; x++)
                {
                    target[row + x * channels + channel] = (byte)((sum + span / 2) / span);
                    sum += source[row + Math.Min(width - 1, x + radius + 1) * channels + channel]
                        - source[row + Math.Max(0, x - radius) * channels + channel];
                }
            }
        }
    }

    private static void VerticalBox(byte[] source, byte[] target, int width, int height, int channels, int radius)
    {
        radius = Math.Min(radius, height);
        int span = radius * 2 + 1;
        int stride = width * channels;
        for (int x = 0; x < width; x++)
        {
            int column = x * channels;
            for (int channel = 0; channel < channels; channel++)
            {
                int sum = source[column + channel] * (radius + 1);
                for (int y = 1; y <= radius; y++)
                    sum += source[Math.Min(y, height - 1) * stride + column + channel];
                for (int y = 0; y < height; y++)
                {
                    target[y * stride + column + channel] = (byte)((sum + span / 2) / span);
                    sum += source[Math.Min(height - 1, y + radius + 1) * stride + column + channel]
                        - source[Math.Max(0, y - radius) * stride + column + channel];
                }
            }
        }
    }
}

/// <summary>Retains expensive glyph and shadow work until mutable annotation values change.</summary>
internal sealed class AnnotationDrawingCache
{
    private readonly Annotation _snapshot;
    private Rect _shadowClip = Rect.Empty;
    public DrawingGroup Drawing { get; }
    public BitmapSource? Shadow { get; private set; }
    public Rect ShadowBounds { get; private set; }

    public AnnotationDrawingCache(Annotation source, DrawingGroup drawing)
    {
        _snapshot = source.Clone();
        Drawing = drawing;
    }

    public bool Matches(Annotation annotation)
    {
        if (_snapshot.Kind != annotation.Kind || _snapshot.Color != annotation.Color ||
            _snapshot.Width != annotation.Width || _snapshot.Start != annotation.Start || _snapshot.End != annotation.End ||
            _snapshot.Text != annotation.Text || _snapshot.Stamp != annotation.Stamp || _snapshot.Points.Count != annotation.Points.Count)
            return false;
        for (int i = 0; i < annotation.Points.Count; i++)
            if (_snapshot.Points[i] != annotation.Points[i])
                return false;
        return true;
    }

    public void EnsureShadow(Rect imageBounds)
    {
        if (_shadowClip == imageBounds)
            return;
        _shadowClip = imageBounds;
        Shadow = null;
        Rect sourceBounds = Drawing.Bounds;
        if (sourceBounds.IsEmpty)
            return;
        sourceBounds.Inflate(10, 10);
        Rect available = imageBounds;
        available.Inflate(12, 12);
        sourceBounds.Intersect(available);
        if (sourceBounds.IsEmpty)
            return;
        int left = (int)Math.Floor(sourceBounds.Left), top = (int)Math.Floor(sourceBounds.Top);
        int width = Math.Max(1, (int)Math.Ceiling(sourceBounds.Right) - left);
        int height = Math.Max(1, (int)Math.Ceiling(sourceBounds.Bottom) - top);
        BitmapSource mask = AnnotationRenderer.Rasterize(Drawing, new Int32Rect(left, top, width, height));
        var pixels = new byte[checked(width * height * 4)];
        mask.CopyPixels(pixels, width * 4, 0);
        var alpha = new byte[checked(width * height)];
        for (int i = 0; i < alpha.Length; i++)
            alpha[i] = pixels[i * 4 + 3];
        AnnotationRenderer.Blur(alpha, width, height, 1, 3);
        Array.Clear(pixels);
        for (int i = 0; i < alpha.Length; i++)
            pixels[i * 4 + 3] = (byte)Math.Round(alpha[i] * 0.45);
        Shadow = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        Shadow.Freeze();
        ShadowBounds = new Rect(left + 1, top + 2, width, height);
    }
}
