using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinSkitch;

public static class CaptureOverlay
{
    public static Task<Int32Rect?> SelectAsync(DesktopCapture capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var session = new SelectionSession(capture, cancellationToken);
        return session.Start();
    }

    // Each HWND occupies one monitor. The shared selection always uses physical desktop
    // pixels; conversion to WPF units occurs only at drawing time on that monitor.
    private sealed class SelectionSession
    {
        private readonly DesktopCapture _capture;
        private readonly List<OverlayWindow> _windows = new();
        private readonly TaskCompletionSource<Int32Rect?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DispatcherTimer _timer;
        private readonly CancellationToken _cancellationToken;
        private CancellationTokenRegistration _cancellationRegistration;
        private NativeMethods.Point _cursor;
        private NativeMethods.Point? _start;
        private OverlayWindow? _capturingWindow;
        private bool _dragging;
        private bool _finished;
        internal Int32Rect? Selection { get; private set; }
        internal NativeMethods.Point Cursor => _cursor;
        internal bool Dragging => _dragging;

        internal SelectionSession(DesktopCapture capture, CancellationToken cancellationToken)
        {
            _capture = capture;
            _cancellationToken = cancellationToken;
            _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += Poll;
        }

        internal Task<Int32Rect?> Start()
        {
            Application.Current?.Dispatcher.VerifyAccess();
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                foreach (var monitor in ScreenCapture.Monitors())
                {
                    var bounds = ScreenCapture.Intersect(monitor, _capture.Bounds);
                    if (!bounds.IsEmpty) _windows.Add(new OverlayWindow(this, _capture, bounds));
                }
                if (_windows.Count == 0) throw new InvalidOperationException("画面を取得できませんでした。");
                foreach (var window in _windows) window.Show();
                Refresh(true);
                foreach (var window in _windows)
                {
                    if (!Contains(window.Bounds, _cursor)) continue;
                    window.Activate();
                    window.Focus();
                    break;
                }
                _timer.Start();
                var dispatcher = Dispatcher.CurrentDispatcher;
                _cancellationRegistration = _cancellationToken.Register(() =>
                    dispatcher.BeginInvoke(new Action(() => Finish(null))));
            }
            catch (Exception exception)
            {
                Finish(null, exception);
            }
            return _result.Task;
        }

        private void Poll(object? sender, EventArgs e) => Refresh(false);

        internal void Refresh(bool force)
        {
            if (_finished || !NativeMethods.GetCursorPos(out var cursor)) return;
            cursor.X = Math.Clamp(cursor.X, _capture.Bounds.X, _capture.Bounds.X + _capture.Bounds.Width);
            cursor.Y = Math.Clamp(cursor.Y, _capture.Bounds.Y, _capture.Bounds.Y + _capture.Bounds.Height);
            if (!force && _cursor.X == cursor.X && _cursor.Y == cursor.Y) return;
            _cursor = cursor;
            if (_start is { } start)
            {
                if (!_dragging && Math.Abs(cursor.X - start.X) + Math.Abs(cursor.Y - start.Y) >= 4)
                    _dragging = true;
                if (_dragging)
                {
                    int x = Math.Min(start.X, cursor.X), y = Math.Min(start.Y, cursor.Y);
                    int width = Math.Abs(start.X - cursor.X), height = Math.Abs(start.Y - cursor.Y);
                    Selection = width > 0 && height > 0 ? new Int32Rect(x, y, width, height) : null;
                }
            }
            else Selection = WindowAt(cursor);
            Invalidate();
        }

        internal void Press(OverlayWindow window)
        {
            Refresh(true);
            _start = _cursor;
            _dragging = false;
            _capturingWindow = window;
            Mouse.Capture(window, CaptureMode.SubTree);
        }

        internal void Release()
        {
            if (_start is null || _finished) return;
            Refresh(true);
            var selection = _dragging ? Selection : WindowAt(_cursor);
            _start = null;
            _dragging = false;
            Mouse.Capture(null);
            _capturingWindow = null;
            if (selection is { Width: >= 3, Height: >= 3 }) Finish(selection);
            else
            {
                Selection = WindowAt(_cursor);
                Invalidate();
            }
        }

        internal void LostCapture(OverlayWindow window)
        {
            if (_capturingWindow != window || _finished) return;
            _start = null;
            _dragging = false;
            _capturingWindow = null;
            Refresh(true);
        }

        internal void Finish(Int32Rect? selection, Exception? exception = null)
        {
            if (_finished) return;
            _finished = true;
            _cancellationRegistration.Dispose();
            _timer.Stop();
            _timer.Tick -= Poll;
            Mouse.Capture(null);
            foreach (var window in _windows) window.Close();
            if (exception is null) _result.TrySetResult(selection);
            else _result.TrySetException(exception);
        }

        private Int32Rect? WindowAt(NativeMethods.Point point)
        {
            // EnumWindows supplies topmost windows first.
            foreach (var bounds in _capture.Windows)
                if (Contains(bounds, point)) return bounds;
            return null;
        }

        private void Invalidate()
        {
            foreach (var window in _windows) window.Surface.InvalidateVisual();
        }

        internal static bool Contains(Int32Rect bounds, NativeMethods.Point point) =>
            point.X >= bounds.X && point.X < bounds.X + bounds.Width
            && point.Y >= bounds.Y && point.Y < bounds.Y + bounds.Height;
    }

    private sealed class OverlayWindow : Window
    {
        private readonly SelectionSession _session;
        internal Int32Rect Bounds { get; }
        internal SelectionSurface Surface { get; }

        internal OverlayWindow(SelectionSession session, DesktopCapture capture, Int32Rect bounds)
        {
            _session = session;
            Bounds = bounds;
            Title = "WinSkitch — 範囲を選択";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Black;
            Cursor = Cursors.Cross;
            Width = bounds.Width;
            Height = bounds.Height;
            Surface = new SelectionSurface(session, bounds, ScreenCapture.Crop(capture, bounds));
            Content = Surface;
            SourceInitialized += (_, _) => Place();
            Loaded += (_, _) => Place();
            SizeChanged += (_, _) => Surface.InvalidateVisual();
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                session.Finish(null);
            };
            MouseLeftButtonDown += (_, e) => { e.Handled = true; session.Press(this); };
            MouseLeftButtonUp += (_, e) => { e.Handled = true; session.Release(); };
            MouseRightButtonDown += (_, e) => { e.Handled = true; session.Finish(null); };
            MouseMove += (_, _) => session.Refresh(false);
            LostMouseCapture += (_, _) => session.LostCapture(this);
            Closed += (_, _) => session.Finish(null);
        }

        private void Place()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (!NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, Bounds.X, Bounds.Y,
                Bounds.Width, Bounds.Height, NativeMethods.SwpNoActivate))
                _session.Finish(null, new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
        }
    }

    private sealed class SelectionSurface : FrameworkElement
    {
        private static readonly Brush DimBrush = FrozenBrush(Color.FromArgb(115, 0, 0, 0));
        private static readonly Brush Accent = FrozenBrush(Color.FromRgb(47, 140, 242));
        private static readonly Brush LabelBackground = FrozenBrush(Color.FromArgb(235, 28, 31, 36));
        private readonly SelectionSession _session;
        private readonly Int32Rect _bounds;
        private readonly BitmapSource _image;

        internal SelectionSurface(SelectionSession session, Int32Rect bounds, BitmapSource image)
        {
            _session = session;
            _bounds = bounds;
            _image = image;
            ClipToBounds = true;
            SnapsToDevicePixels = true;
        }

        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            double sx = ActualWidth / _bounds.Width, sy = ActualHeight / _bounds.Height;
            var local = new Rect(0, 0, ActualWidth, ActualHeight);
            drawing.DrawImage(_image, local);
            drawing.DrawRectangle(DimBrush, null, local);

            if (_session.Selection is { } selection)
            {
                var rect = new Rect((selection.X - _bounds.X) * sx, (selection.Y - _bounds.Y) * sy,
                    selection.Width * sx, selection.Height * sy);
                drawing.PushClip(new RectangleGeometry(rect));
                drawing.DrawImage(_image, local);
                drawing.Pop();
                drawing.DrawRectangle(null, new Pen(Accent, 2), rect);
            }

            var cursor = _session.Cursor;
            double x = (cursor.X - _bounds.X) * sx, y = (cursor.Y - _bounds.Y) * sy;
            var crosshair = new Pen(Brushes.White, 1) { DashStyle = DashStyles.Dash };
            if (y >= 0 && y <= ActualHeight) drawing.DrawLine(crosshair, new Point(0, y), new Point(ActualWidth, y));
            if (x >= 0 && x <= ActualWidth) drawing.DrawLine(crosshair, new Point(x, 0), new Point(x, ActualHeight));
            if (!SelectionSession.Contains(_bounds, cursor)) return;

            string dimensions = _session.Selection is { } box
                ? $"{box.Width} × {box.Height}" : $"{cursor.X}, {cursor.Y}";
            string hint = _session.Dragging ? dimensions : dimensions + "  •  ドラッグ / クリックで選択  •  Esc で中止";
            var text = new FormattedText(hint, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double labelWidth = Math.Min(text.Width + 16, ActualWidth);
            double labelHeight = text.Height + 10;
            double labelX = Math.Clamp(x + 18, 0, Math.Max(0, ActualWidth - labelWidth));
            double labelY = y + 18 + labelHeight <= ActualHeight ? y + 18 : Math.Max(0, y - labelHeight - 18);
            drawing.DrawRoundedRectangle(LabelBackground, null, new Rect(labelX, labelY, labelWidth, labelHeight), 4, 4);
            drawing.PushClip(new RectangleGeometry(new Rect(labelX, labelY, labelWidth, labelHeight)));
            drawing.DrawText(text, new Point(labelX + 8, labelY + 5));
            drawing.Pop();
        }

        private static Brush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
