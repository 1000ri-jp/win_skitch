using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;

namespace WinSkitch;

public partial class App : Application
{
    private ResidentInstance? _instance;
    public static string LogPath => Path.Combine(Path.GetTempPath(), "winskitch.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Exercise the actual published entry point and embedded resources without
        // displaying a window, capturing the desktop, or changing the clipboard.
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
        {
            VerifyStartup(e.Args[1]);
            return;
        }
        LaunchOptions options;
        try { options = LaunchOptions.Parse(e.Args); }
        catch (ArgumentException error)
        {
            MessageBox.Show(error.Message, "WinSkitch", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        _instance = new ResidentInstance(() =>
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(new Action(() => (MainWindow as MainWindow)?.ShowEditor()));
        });
        if (!_instance.IsPrimary)
        {
            if (!options.StartInTray) _instance.RequestShow();
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            ReportError(args.Exception);
            args.Handled = true;
        };
        StartWindow(options);
    }

    private MainWindow StartWindow(LaunchOptions options)
    {
        var window = new MainWindow();
        MainWindow = window;
        if (options.StartInTray) window.StartInTray();
        else window.Show();
        if (options.ImagePath is not null && File.Exists(options.ImagePath)) window.OpenPath(options.ImagePath);
        return window;
    }

    public static void ReportError(Exception error)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:O}\n{error}\n\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (Current?.MainWindow is MainWindow window)
            window.ShowStatus($"エラー: {error.Message}（詳細: {LogPath}）", 8000);
        else MessageBox.Show(error.Message, "WinSkitch", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void VerifyStartup(string reportPath)
    {
        MainWindow? window = null;
        try
        {
            window = StartWindow(LaunchOptions.Parse(new[] { "--tray" }));
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || window.Icon is null)
                throw new InvalidOperationException("ウィンドウ、アイコンの初期化を確認できませんでした。");
            if (window.IsVisible) throw new InvalidOperationException("トレイ起動時に編集画面が表示されています。");
            File.WriteAllText(reportPath, $"PASS\nRuntime: {Environment.Version}\nBaseDirectory: {AppContext.BaseDirectory}\nTray startup stayed hidden. Window and icon resources loaded. Tray initialized and disposed.\nHotkeys available: {window.HotkeysRegistered} (another resident or application may already own them).\n");
            window.Exit();
        }
        catch (Exception error)
        {
            File.WriteAllText(reportPath, "FAIL\n" + error);
            window?.Exit();
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
