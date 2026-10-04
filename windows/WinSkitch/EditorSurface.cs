using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinSkitch;

public sealed class EditorSurface : Grid
{
    private readonly Canvas _textLayer = new();
    private BitmapSource? _rendered;
    private bool _dirty = true;
    private double _scale = 1;
    private Point _offset;
    private string? _dragMode;
    private string _handle = "";
    private Point _start, _last;
    private Annotation? _creating;
    private Annotation? _crop;
    private ImageDocument.Snapshot? _before;
    private bool _gestureChanged;
    private TextBox? _textBox;
    private Annotation? _editing;
    private Point _textOrigin;
    private double _textSize;
    private Color _textColor;
    private bool _commitInProgress;
    private bool _imeComposing;
    private static readonly double[] Sizes = { 3, 6, 10 };
    private static readonly double[] TextSizes = { 20, 32, 52 };
    private static readonly double[] StampSizes = { 18, 28, 42 };
    public ImageDocument Document { get; } = new();
    public Annotation? Selected { get; private set; }
    public string Tool { get; private set; } = "arrow";
    public Dictionary<string, string> Variants { get; } = new()
    {
        ["shape"] = "rect", ["pen"] = "marker", ["mosaic"] = "pixelate", ["stamp"] = "check"
    };
    public Color CurrentColor { get; private set; } = (Color)ColorConverter.ConvertFromString("#EA3323");
    public int SizeIndex { get; private set; } = 1;
    public bool IsEditingText => _textBox is not null;
    public event Action<string, int>? Status;
    public event Action? StateChanged;
    public event Action<string>? ImageInfo;

    public EditorSurface()
    {
        Background = new SolidColorBrush(Color.FromRgb(60, 60, 60));
        Focusable = true;
        ClipToBounds = true;
        Children.Add(_textLayer);
        SizeChanged += (_, _) => { Fit(); PositionText(); InvalidateVisual(); };
        Cursor = Cursors.Cross;
    }

    public void Load(BitmapSource image, string? name = null)
    {
        CommitText();
        Document.Load(image, name);
        Selected = null;
        SetTool("arrow");
        Refresh();
    }

    public void Open(string path)
    {
        CommitText();
        Document.Open(path);
        Selected = null;
        SetTool("arrow");
        Refresh();
    }

    public void Refresh()
    {
        _dirty = true;
        InvalidateVisual();
        StateChanged?.Invoke();
    }

    public void SetTool(string tool)
    {
        CommitText();
        Tool = tool;
        _crop = null;
        if (tool == "crop" && Document.Background is not null)
        {
            _crop = new Annotation { Kind = AnnotationKind.Rectangle, Start = new Point(),
                End = new Point(Document.Background.PixelWidth, Document.Background.PixelHeight) };
            Selected = null;
            Status?.Invoke("ハンドルで範囲を調整 → Enter で確定 / Esc でキャンセル（外側へ広げると余白を追加）", 0);
        }
        Cursor = tool == "text" ? Cursors.IBeam : Cursors.Cross;
        Refresh();
    }

    public void SetVariant(string tool, string variant)
    {
        Variants[tool] = variant;
        SetTool(tool);
    }

    public void SetColor(Color color)
    {
        CurrentColor = color;
        if (_textBox is not null)
        {
            _textColor = color;
            _textBox.Foreground = new SolidColorBrush(color);
            _textBox.CaretBrush = _textBox.Foreground;
            _textBox.Background = TextBackground(color);
        }
        else if (Selected is not null && Selected.Kind is not AnnotationKind.Pixelate and not AnnotationKind.Blur)
        {
            var before = Document.CaptureState();
            Selected.Color = color;
            Document.CommitHistory(before);
        }
        Refresh();
    }

    public void SetSize(int index)
    {
        SizeIndex = Math.Clamp(index, 0, 2);
        if (_textBox is not null)
        {
            _textSize = TextSizes[SizeIndex];
            PositionText();
        }
        else if (Selected is not null)
        {
            var before = Document.CaptureState();
            Selected.Width = Selected.Kind switch
            {
                AnnotationKind.Text => TextSizes[SizeIndex], AnnotationKind.Stamp => StampSizes[SizeIndex],
                _ => Sizes[SizeIndex]
            };
            Document.CommitHistory(before);
        }
        Refresh();
    }

    private void Fit()
    {
        if (Document.Background is null) return;
        int w = Document.Background.PixelWidth, h = Document.Background.PixelHeight;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // 100% means one source-image pixel per physical display pixel.
        _scale = Math.Max(0.001, Math.Min(1 / dpi, Math.Min(Math.Max(1, ActualWidth - 48) / w,
            Math.Max(1, ActualHeight - 48) / h)));
        _offset = new Point((ActualWidth - w * _scale) / 2, (ActualHeight - h * _scale) / 2);
    }

    private Point ImagePoint(Point p) => new((p.X - _offset.X) / _scale, (p.Y - _offset.Y) / _scale);
    private Point DisplayPoint(Point p) => new(p.X * _scale + _offset.X, p.Y * _scale + _offset.Y);
    private Rect DisplayRect(Rect r) => new(DisplayPoint(r.TopLeft), DisplayPoint(r.BottomRight));
    private static Pen AccentPen(double width = 1) => new(new SolidColorBrush(Color.FromRgb(47, 140, 242)), width);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Document.Background is null)
        {
            var text = FormatText("Ctrl + Shift + 5 で画面をスナップ\n\n画像をドロップ / Ctrl+V で貼り付け / Ctrl+O で開く", 15,
                new SolidColorBrush(Color.FromRgb(170, 170, 170)));
            text.TextAlignment = TextAlignment.Center;
            dc.DrawText(text, new Point(ActualWidth / 2, Math.Max(0, (ActualHeight - text.Height) / 2)));
            ImageInfo?.Invoke("");
            return;
        }
        Fit();
        if (_dirty || _rendered is null)
        {
            _rendered = Document.Composite();
            _dirty = false;
        }
        var imageRect = new Rect(_offset, new Size(_rendered.PixelWidth * _scale, _rendered.PixelHeight * _scale));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(42, 42, 42)), null,
            new Rect(imageRect.X + 2, imageRect.Y + 3, imageRect.Width, imageRect.Height));
        dc.DrawRectangle(Brushes.White, null, imageRect);
        dc.DrawImage(_rendered, imageRect);
        if (Selected is not null && Document.Annotations.Contains(Selected) && !Selected.Hidden)
        {
            if (Selected.Handles.Count == 0 || Selected.Kind is AnnotationKind.Pixelate or AnnotationKind.Blur or AnnotationKind.Stamp)
            {
                var box = DisplayRect(Selected.Bounds);
                box.Inflate(4, 4);
                var pen = AccentPen();
                pen.DashStyle = DashStyles.Dash;
                dc.DrawRectangle(null, pen, box);
            }
            DrawHandles(dc, Selected);
        }
        if (_crop is not null)
        {
            var r = DisplayRect(_crop.Bounds);
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)), new RectangleGeometry(r));
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(125, 0, 0, 0)), null, outside);
            var pen = new Pen(Brushes.White, 1) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(null, pen, r);
            DrawHandles(dc, _crop);
            var label = FormatText($"{Math.Round(_crop.Bounds.Width)} × {Math.Round(_crop.Bounds.Height)}   Enter で確定", 12, Brushes.White);
            dc.DrawText(label, new Point(r.Left + (r.Width - label.Width) / 2, r.Bottom + 8));
        }
        ImageInfo?.Invoke($"{_rendered.PixelWidth} × {_rendered.PixelHeight}  ·  {Math.Round(_scale * VisualTreeHelper.GetDpi(this).DpiScaleX * 100)}%");
    }

    private FormattedText FormatText(string text, double size, Brush color) => new(text,
        System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface("Yu Gothic UI"), size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawHandles(DrawingContext dc, Annotation annotation)
    {
        foreach (var handle in annotation.Handles)
            dc.DrawEllipse(Brushes.White, AccentPen(1.5), DisplayPoint(handle.Position), 4.5, 4.5);
    }

    private string? HandleAt(Annotation? annotation, Point p)
    {
        if (annotation is null) return null;
        double tolerance = 8 / _scale;
        foreach (var handle in annotation.Handles)
            if (Math.Abs(p.X - handle.Position.X) <= tolerance && Math.Abs(p.Y - handle.Position.Y) <= tolerance)
                return handle.Name;
        return null;
    }

    private Annotation? AnnotationAt(Point p) => Document.Annotations.LastOrDefault(a => !a.Hidden && a.HitTest(p, 6 / _scale));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Document.Background is null) return;
        if (_textBox is not null && e.OriginalSource is DependencyObject origin &&
            (ReferenceEquals(origin, _textBox) || _textBox.IsAncestorOf(origin))) return;
        if (_textBox is not null) { CommitText(); e.Handled = true; return; }
        Focus();
        var p = ImagePoint(e.GetPosition(this));
        var hit = AnnotationAt(p);
        if (e.ClickCount == 2 && hit?.Kind == AnnotationKind.Text)
        {
            StartText(hit.Start, hit);
            e.Handled = true;
            return;
        }
        _start = _last = p;
        _gestureChanged = false;
        _before = Document.CaptureState();
        _creating = null;
        if (Tool == "crop" && _crop is not null)
        {
            var handle = HandleAt(_crop, p);
            if (handle is not null) { _dragMode = "crop_handle"; _handle = handle; }
            else if (_crop.Bounds.Contains(p)) _dragMode = "crop_move";
            else
            {
                _crop = new Annotation { Kind = AnnotationKind.Rectangle, Start = p, End = p };
                _dragMode = "crop_handle"; _handle = "x1y1";
            }
        }
        else if (HandleAt(Selected, p) is string selectedHandle)
        {
            _dragMode = "handle";
            _handle = selectedHandle;
        }
        else if (hit is not null)
        {
            Selected = hit;
            if (Tool == "text" && hit.Kind == AnnotationKind.Text)
            {
                StartText(hit.Start, hit);
                e.Handled = true;
                return;
            }
            _dragMode = "move";
        }
        else
        {
            Selected = null;
            if (Tool == "text")
            {
                StartText(p);
                e.Handled = true;
                return;
            }
            _creating = CreateAnnotation(p);
            if (_creating is not null)
            {
                Document.Annotations.Add(_creating);
                _dragMode = "create";
            }
        }
        if (_dragMode is not null) CaptureMouse();
        Refresh();
        e.Handled = true;
    }

    private Annotation? CreateAnnotation(Point p)
    {
        var kind = Tool switch
        {
            "arrow" => AnnotationKind.Arrow,
            "shape" => Variants["shape"] switch
            {
                "rrect" => AnnotationKind.RoundedRectangle, "oval" => AnnotationKind.Ellipse,
                "line" => AnnotationKind.Line, _ => AnnotationKind.Rectangle
            },
            "pen" => Variants["pen"] == "highlighter" ? AnnotationKind.Highlighter : AnnotationKind.Pen,
            "mosaic" => Variants["mosaic"] == "blur" ? AnnotationKind.Blur : AnnotationKind.Pixelate,
            "stamp" => AnnotationKind.Stamp,
            _ => (AnnotationKind?)null
        };
        if (kind is null) return null;
        return new Annotation
        {
            Kind = kind.Value, Color = CurrentColor, Width = kind == AnnotationKind.Stamp ? StampSizes[SizeIndex] : Sizes[SizeIndex],
            Start = p, End = p, Points = new List<Point> { p }, Stamp = Variants["stamp"]
        };
    }

    private static Point Constrain(Point start, Point end, bool square)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        if (square)
        {
            double m = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new Point(start.X + Math.CopySign(m, dx == 0 ? 1 : dx), start.Y + Math.CopySign(m, dy == 0 ? 1 : dy));
        }
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * Math.PI / 4;
        double length = Math.Sqrt(dx * dx + dy * dy);
        return new Point(start.X + length * Math.Cos(angle), start.Y + length * Math.Sin(angle));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Document.Background is null || _textBox is not null) return;
        var p = ImagePoint(e.GetPosition(this));
        if (_dragMode is null)
        {
            var target = Tool == "crop" ? _crop : Selected;
            Cursor = HandleAt(target, p) is not null ? Cursors.SizeAll :
                (Tool == "crop" ? _crop?.Bounds.Contains(p) == true : AnnotationAt(p) is not null) ? Cursors.Hand :
                Tool == "text" ? Cursors.IBeam : Cursors.Cross;
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if ((p - _last).Length < 0.001) return;
        switch (_dragMode)
        {
            case "create" when _creating is not null:
                var a = _creating;
                if (a.Kind is AnnotationKind.Pen or AnnotationKind.Highlighter)
                {
                    if ((p - a.Points[^1]).Length >= 1.5 / _scale) a.Points.Add(p);
                }
                else if (a.Kind == AnnotationKind.Stamp) a.Width = Math.Max(StampSizes[SizeIndex], (p - _start).Length);
                else
                {
                    if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                        p = Constrain(_start, p, a.Kind is AnnotationKind.Rectangle or AnnotationKind.RoundedRectangle or
                            AnnotationKind.Ellipse or AnnotationKind.Pixelate or AnnotationKind.Blur);
                    a.SetHandle(a.Handles[^1].Name, p);
                }
                break;
            case "handle" when Selected is not null: Selected.SetHandle(_handle, p); break;
            case "move" when Selected is not null: Selected.Move(p.X - _last.X, p.Y - _last.Y); break;
            case "crop_handle" when _crop is not null: _crop.SetHandle(_handle, p); break;
            case "crop_move" when _crop is not null: _crop.Move(p.X - _last.X, p.Y - _last.Y); break;
        }
        _gestureChanged = true;
        _last = p;
        Refresh();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragMode is null) return;
        FinishGesture();
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragMode is not null) FinishGesture();
    }

    private void FinishGesture()
    {
        bool changed = _gestureChanged && _dragMode is "handle" or "move";
        if (_dragMode == "create" && _creating is not null)
        {
            var a = _creating;
            bool tiny = a.Kind switch
            {
                AnnotationKind.Arrow or AnnotationKind.Line => (a.End - a.Start).Length < 4,
                AnnotationKind.Rectangle or AnnotationKind.RoundedRectangle or AnnotationKind.Ellipse or
                    AnnotationKind.Pixelate or AnnotationKind.Blur => a.Bounds.Width < 4 || a.Bounds.Height < 4,
                _ => false
            };
            if (tiny) Document.Annotations.Remove(a);
            else { Selected = a; changed = true; }
        }
        if (changed && _before is not null) Document.CommitHistory(_before);
        _dragMode = null;
        _creating = null;
        _before = null;
        Refresh();
    }

    public void Undo()
    {
        CommitText();
        if (Document.Undo()) { Selected = null; if (Tool == "crop") SetTool("crop"); Refresh(); }
    }

    public void Redo()
    {
        CommitText();
        if (Document.Redo()) { Selected = null; if (Tool == "crop") SetTool("crop"); Refresh(); }
    }

    public void DeleteSelected()
    {
        if (Selected is null) return;
        var before = Document.CaptureState();
        Document.Annotations.Remove(Selected);
        Document.CommitHistory(before);
        Selected = null;
        Refresh();
    }

    public void Escape()
    {
        if (Tool == "crop") SetTool("arrow");
        else { Selected = null; Refresh(); }
    }

    public void ApplyCrop()
    {
        if (_crop is null || _crop.Bounds.Width < 2 || _crop.Bounds.Height < 2) return;
        Document.Crop(_crop.Bounds);
        SetTool("arrow");
        Status?.Invoke($"切り抜きました ({Document.Background!.PixelWidth} × {Document.Background.PixelHeight})", 3000);
    }

    public void Nudge(double dx, double dy)
    {
        if (Selected is null) return;
        var before = Document.CaptureState();
        Selected.Move(dx, dy);
        Document.CommitHistory(before);
        Refresh();
    }

    private static Brush TextBackground(Color c) => c.R * 0.299 + c.G * 0.587 + c.B * 0.114 > 185
        ? new SolidColorBrush(Color.FromRgb(51, 51, 51)) : Brushes.White;

    public void StartText(Point origin, Annotation? annotation = null)
    {
        CommitText();
        _editing = annotation;
        _textOrigin = origin;
        _textSize = annotation?.Width ?? TextSizes[SizeIndex];
        _textColor = annotation?.Color ?? CurrentColor;
        if (annotation is not null) annotation.Hidden = true;
        Selected = null;
        _textBox = new TextBox
        {
            Text = annotation?.Text ?? "", AcceptsReturn = true, AcceptsTab = false,
            TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Yu Gothic UI"), FontWeight = FontWeights.Bold,
            Padding = new Thickness(2), BorderThickness = new Thickness(1), BorderBrush = AccentPen().Brush,
            Foreground = new SolidColorBrush(_textColor), CaretBrush = new SolidColorBrush(_textColor),
            Background = TextBackground(_textColor), MinWidth = 45, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, IsUndoEnabled = true
        };
        _textBox.PreviewKeyDown += (_, e) =>
        {
            // The first Enter belongs to IME conversion; only a subsequent Enter commits.
            if (_imeComposing) return;
            if (e.Key == Key.Escape || (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0))
            { CommitText(); e.Handled = true; }
        };
        _imeComposing = false;
        TextCompositionManager.AddPreviewTextInputStartHandler(_textBox, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(_textBox, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(_textBox, (_, _) => _imeComposing = false);
        _textLayer.Children.Add(_textBox);
        PositionText();
        _textBox.Focus();
        _textBox.CaretIndex = _textBox.Text.Length;
        Status?.Invoke("Enter で確定 / Shift+Enter で改行", 0);
        Refresh();
    }

    private void PositionText()
    {
        if (_textBox is null) return;
        var display = DisplayPoint(_textOrigin);
        Canvas.SetLeft(_textBox, display.X);
        Canvas.SetTop(_textBox, display.Y);
        _textBox.FontSize = Math.Max(1, _textSize * _scale);
    }

    public void CommitText()
    {
        if (_textBox is null || _commitInProgress) return;
        _commitInProgress = true;
        try
        {
            string text = _textBox.Text.TrimEnd();
            _textLayer.Children.Remove(_textBox);
            _textBox = null;
            _imeComposing = false;
            if (_editing is not null) _editing.Hidden = false;
            if (Document.Background is not null)
            {
                var before = Document.CaptureState();
                bool changed = false;
                if (_editing is not null)
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        Document.Annotations.Remove(_editing);
                        changed = true;
                    }
                    else
                    {
                        changed = text != _editing.Text || _textColor != _editing.Color || _textSize != _editing.Width;
                        _editing.Text = text; _editing.Color = _textColor; _editing.Width = _textSize;
                        Selected = _editing;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    Selected = new Annotation { Kind = AnnotationKind.Text, Start = _textOrigin, End = _textOrigin,
                        Width = _textSize, Color = _textColor, Text = text };
                    Document.Annotations.Add(Selected);
                    changed = true;
                }
                if (changed) Document.CommitHistory(before);
            }
            _editing = null;
            Status?.Invoke("", 0);
            Focus();
            Refresh();
        }
        finally { _commitInProgress = false; }
    }
}
