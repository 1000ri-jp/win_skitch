using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinSkitch;

public sealed class RecordingControlsWindow : Window
{
    private readonly TextBlock _elapsed = new() { Text = "00:00", FontSize = 20,
        FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _stop;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Action _onStop;
    private bool _allowClose;

    public RecordingControlsWindow(Func<TimeSpan> elapsed, Action onStop)
    {
        _onStop = onStop;
        Title = "WinSkitch — 録画中";
        Width = 360; Height = 120;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(38, 38, 38));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Yu Gothic UI");
        FontSize = 13;
        var panel = new StackPanel { Margin = new Thickness(14, 10, 14, 10) };
        var row = new DockPanel();
        var label = new TextBlock { Text = "● 録画中", Foreground = new SolidColorBrush(Color.FromRgb(245, 85, 85)),
            Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        _stop = new Button { Content = "■ 停止して保存", Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(12, 0, 0, 0), Cursor = Cursors.Hand };
        _stop.Click += (_, _) => _onStop();
        DockPanel.SetDock(_stop, Dock.Right);
        row.Children.Add(_stop);
        row.Children.Add(_elapsed);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = "Ctrl + Shift + 7 でも停止できます", FontSize = 11,
            Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 0, 0) });
        Content = panel;
        _timer.Tick += (_, _) => _elapsed.Text = FormatElapsed(elapsed());
        SourceInitialized += (_, _) =>
        {
            // Exclude recording controls from supported Windows desktop capture APIs.
            if (!SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11))
            {
                Height = 156;
                panel.Children.Add(new TextBlock { Text = "操作パネルも録画されるため、範囲外へ移動してください",
                    FontSize = 10, TextWrapping = TextWrapping.Wrap });
            }
        };
        Loaded += (_, _) => _timer.Start();
        Closing += OnClosing;
        Closed += (_, _) => _timer.Stop();
    }

    public static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
        : $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";

    public void BeginSaving()
    {
        _stop.IsEnabled = false;
        _stop.Content = "保存中…";
        Title = "WinSkitch — 動画を保存中";
    }

    public void CloseAfterRecording()
    {
        _allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_stop.IsEnabled) _onStop();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
