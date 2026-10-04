using System;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace WinSkitch;

public sealed class WindowsHost : IDisposable
{
    private const int RegionHotkey = 1;
    private const int FullHotkey = 2;
    private readonly Window _owner;
    private readonly Action<string> _onSnap;
    private readonly Action _onShow;
    private readonly Action _onExit;
    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _icon;
    private readonly Forms.ToolStripMenuItem? _startupItem;
    private readonly bool _regionRegistered;
    private readonly bool _fullRegistered;
    private bool _hiddenHintShown;
    private bool _disposed;

    public bool FailedHotkeys => !_regionRegistered || !_fullRegistered;

    public WindowsHost(Window owner, Action<string> onSnap, Action onShow, Action onExit, Action? onToggleStartup = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(onSnap);
        ArgumentNullException.ThrowIfNull(onShow);
        ArgumentNullException.ThrowIfNull(onExit);
        _owner = owner;
        _onSnap = onSnap;
        _onShow = onShow;
        _onExit = onExit;
        _handle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("ウィンドウのハンドルを取得できませんでした。");
        _icon = CreateIcon();
        _menu = new Forms.ContextMenuStrip();
        try
        {
            AddSnap("範囲スナップ (Ctrl+Shift+5)", "region");
            AddSnap("全画面スナップ (Ctrl+Shift+6)", "full");
            AddSnap("タイマースナップ (5秒後)", "timer");
            AddSnap("前回の範囲をスナップ", "previous");
            _menu.Items.Add(new Forms.ToolStripSeparator());
            _menu.Items.Add("ウィンドウを表示", null, (_, _) => Dispatch(_onShow));
            if (onToggleStartup is not null)
            {
                _startupItem = new Forms.ToolStripMenuItem("Windowsログイン時に起動");
                _startupItem.Click += (_, _) => Dispatch(onToggleStartup);
                _menu.Items.Add(_startupItem);
                _menu.Opening += (_, _) => _startupItem.Checked = StartupRegistration.IsEnabled;
            }
            _menu.Items.Add("終了", null, (_, _) => Dispatch(_onExit));
            _tray = new Forms.NotifyIcon
            {
                Text = "WinSkitch",
                Icon = _icon,
                ContextMenuStrip = _menu
            };
            _tray.Visible = true;
            _tray.DoubleClick += (_, _) => Dispatch(_onShow);
            _source.AddHook(WindowMessage);
            uint modifiers = NativeMethods.ModControl | NativeMethods.ModShift | NativeMethods.ModNoRepeat;
            _regionRegistered = NativeMethods.RegisterHotKey(_handle, RegionHotkey, modifiers, 0x35);
            _fullRegistered = NativeMethods.RegisterHotKey(_handle, FullHotkey, modifiers, 0x36);
            _owner.Closed += OwnerClosed;
        }
        catch
        {
            if (_regionRegistered) NativeMethods.UnregisterHotKey(_handle, RegionHotkey);
            if (_fullRegistered) NativeMethods.UnregisterHotKey(_handle, FullHotkey);
            _source.RemoveHook(WindowMessage);
            _tray?.Dispose();
            _menu.Dispose();
            _icon.Dispose();
            throw;
        }
    }

    public void ShowHiddenHint()
    {
        if (_disposed || _hiddenHintShown) return;
        _hiddenHintShown = true;
        _tray.ShowBalloonTip(4000, "WinSkitch", "タスクトレイで動作中です。Ctrl+Shift+5 でスナップできます。", Forms.ToolTipIcon.Info);
    }

    public void ShowNotification(string message)
    {
        if (!_disposed) _tray.ShowBalloonTip(4000, "WinSkitch", message, Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Closed -= OwnerClosed;
        if (_regionRegistered) NativeMethods.UnregisterHotKey(_handle, RegionHotkey);
        if (_fullRegistered) NativeMethods.UnregisterHotKey(_handle, FullHotkey);
        _source.RemoveHook(WindowMessage);
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private void OwnerClosed(object? sender, EventArgs args) => Dispose();

    private void AddSnap(string label, string mode) =>
        _menu.Items.Add(label, null, (_, _) => Dispatch(() => _onSnap(mode)));

    private void Dispatch(Action action)
    {
        if (_disposed || _owner.Dispatcher.HasShutdownStarted) return;
        _owner.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != NativeMethods.WmHotkey || _disposed) return IntPtr.Zero;
        int id = wParam.ToInt32();
        if (id != RegionHotkey && id != FullHotkey) return IntPtr.Zero;
        handled = true;
        Dispatch(() => _onSnap(id == RegionHotkey ? "region" : "full"));
        return IntPtr.Zero;
    }

    private static Icon CreateIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/WinSkitch;component/Assets/WinSkitch.ico"))
            ?? throw new InvalidOperationException("WinSkitch のアイコンが見つかりませんでした。");
        using var stream = resource.Stream;
        using var icon = new Icon(stream, new System.Drawing.Size(32, 32));
        // NotifyIcon needs an independently owned icon after the resource stream closes.
        return (Icon)icon.Clone();
    }
}
