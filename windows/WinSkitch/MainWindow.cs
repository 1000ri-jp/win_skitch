using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinSkitch;

public sealed class MainWindow : Window
{
    public EditorSurface Editor { get; } = new();
    private WindowsHost? _host;
    private readonly SavedFileHistory _savedFileHistory;
    private SaveHistoryWindow? _historyWindow;
    private readonly List<Control> _historyActions = new();
    internal bool HotkeysRegistered => _host is { FailedHotkeys: false };
    private bool _allowExit, _snapping;
    private bool _recordingBusy, _exitRequested;
    private bool _recordingSaveDialogOpen;
    private VideoRecorder? _recorder;
    private RecordingControlsWindow? _recordingControls;
    private CancellationTokenSource? _recordingCancellation;
    private Task? _recordingTask;
    private readonly List<Control> _captureActions = new();
    private readonly List<Control> _recordingStartActions = new();
    private MenuItem? _stopRecordingItem;
    private Int32Rect? _previous;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = GrayBrush };
    private readonly DispatcherTimer _statusTimer = new();
    private readonly Dictionary<string, Button> _tools = new();
    private readonly Dictionary<Color, Button> _swatches = new();
    private readonly List<Button> _sizes = new();
    private readonly List<Control> _requiresImage = new();
    private MenuItem? _undoItem, _redoItem;
    private MenuItem? _startupItem;
    private Point? _exportStart;
    private static Brush GrayBrush => new SolidColorBrush(Color.FromRgb(160, 160, 160));
    private static Brush BarBrush => new SolidColorBrush(Color.FromRgb(48, 48, 48));
    private static Brush ActiveBrush => new SolidColorBrush(Color.FromRgb(85, 85, 85));
    private static readonly string[] Palette = { "#EA3323", "#F03C96", "#F7A12B", "#FDE23A", "#58C43C", "#2F8CF2", "#8A3FD1", "#000000", "#FFFFFF" };
    private static readonly (string Tool, string Tip)[] ToolList =
    {
        ("arrow", "矢印 (A)"), ("text", "テキスト (T)"), ("shape", "図形 (R)"),
        ("pen", "ペン (M) / 蛍光ペン (H)"), ("mosaic", "モザイク (P)"), ("stamp", "スタンプ (S)"), ("crop", "切り抜き (C)")
    };
    private static readonly Dictionary<string, (string Key, string Label)[]> VariantList = new()
    {
        ["shape"] = new[] { ("rect", "四角形"), ("rrect", "角丸四角形"), ("oval", "楕円"), ("line", "直線") },
        ["pen"] = new[] { ("marker", "マーカー"), ("highlighter", "蛍光ペン") },
        ["mosaic"] = new[] { ("pixelate", "モザイク"), ("blur", "ぼかし") },
        ["stamp"] = new[] { ("check", "✔ OK"), ("cross", "✖ NG"), ("question", "? 質問"), ("exclaim", "! 注意"), ("star", "★ スター"), ("heart", "♥ ハート") }
    };

    public MainWindow(SavedFileHistory? history = null)
    {
        _savedFileHistory = history ?? new SavedFileHistory();
        Title = "WinSkitch";
        Width = 1000; Height = 700; MinWidth = 480; MinHeight = 360;
        FontFamily = new FontFamily("Yu Gothic UI");
        FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(38, 38, 38));
        Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230));
        Icon = new BitmapImage(new Uri("pack://application:,,,/WinSkitch;component/Assets/WinSkitch.png"));
        BuildLayout();
        Editor.Status += ShowStatus;
        Editor.StateChanged += UpdateControls;
        Editor.ImageInfo += text => _info.Text = text;
        _statusTimer.Tick += (_, _) => { _status.Text = ""; _statusTimer.Stop(); };
        SourceInitialized += (_, _) =>
        {
            _host = new WindowsHost(this, mode => _ = SnapAsync(mode), ShowEditor, Exit, ToggleStartup,
                mode => _ = StartRecordingAsync(mode), () => _ = StopRecordingAsync(), ShowSaveHistory);
            UpdateRecordingControls();
            if (_host.FailedHotkeys) ShowStatus("⚠ ホットキーを登録できませんでした（他のアプリが使用中の可能性）", 0);
        };
        Closing += OnClosing;
        Closed += (_, _) => { _host?.Dispose(); _statusTimer.Stop(); };
        PreviewKeyDown += OnKeyDown;
        AllowDrop = true;
        PreviewDragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) OpenPath(files[0]);
            e.Handled = true;
        };
        UpdateControls();
        UpdateRecordingControls();
    }

    private void BuildLayout()
    {
        var root = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);
        var top = new DockPanel { Background = BarBrush, LastChildFill = true, MinHeight = 44 };
        DockPanel.SetDock(top, Dock.Top);
        var actions = new WrapPanel { Margin = new Thickness(8, 5, 8, 5) };
        var snap = Button("📷  スナップ", () => _ = SnapAsync("region"));
        snap.Background = new SolidColorBrush(Color.FromRgb(61, 111, 182));
        actions.Children.Add(snap);
        var more = Button("▾", () => { });
        more.Background = snap.Background;
        more.Click += (_, _) => ShowSnapMenu(more);
        actions.Children.Add(more);
        _captureActions.Add(snap); _captureActions.Add(more);
        var record = Button("● 録画", () => _ = StartRecordingAsync("region"));
        record.Background = new SolidColorBrush(Color.FromRgb(160, 54, 54));
        record.ToolTip = "範囲を選んでMP4動画を録画 (Ctrl+Shift+7)";
        actions.Children.Add(record);
        var recordMore = Button("▾", () => { });
        recordMore.Background = record.Background;
        recordMore.Click += (_, _) => ShowRecordingMenu(recordMore);
        actions.Children.Add(recordMore);
        _recordingStartActions.Add(record); _recordingStartActions.Add(recordMore);
        actions.Children.Add(Button("開く", OpenFile));
        var save = Button("保存", () => Save(false));
        var copy = Button("コピー", CopyImage);
        actions.Children.Add(save); actions.Children.Add(copy);
        _requiresImage.Add(save); _requiresImage.Add(copy);
        var history = Button("履歴", ShowSaveHistory);
        history.ToolTip = "保存した画像・動画のファイルや保存先を開きます";
        actions.Children.Add(history);
        _historyActions.Add(history);
        DockPanel.SetDock(actions, Dock.Left);
        top.Children.Add(actions);
        _info.Margin = new Thickness(0, 0, 12, 0);
        _info.HorizontalAlignment = HorizontalAlignment.Right;
        top.Children.Add(_info);
        root.Children.Add(top);

        var footer = new DockPanel { Background = BarBrush, MinHeight = 36 };
        DockPanel.SetDock(footer, Dock.Bottom);
        var export = new Border { Background = new SolidColorBrush(Color.FromRgb(58, 58, 58)),
            Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 5, 8, 5), Cursor = Cursors.Hand,
            Child = new TextBlock { Text = "⠿  ここをドラッグして書き出し", VerticalAlignment = VerticalAlignment.Center },
            ToolTip = "編集した画像をPNGファイルとして他のアプリへドラッグできます" };
        export.PreviewMouseLeftButtonDown += (_, e) => _exportStart = e.GetPosition(export);
        export.MouseLeftButtonUp += (_, _) => _exportStart = null;
        export.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _exportStart is null) return;
            var delta = e.GetPosition(export) - _exportStart.Value;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _exportStart = null;
            Safe(() => ExportDrag(export));
        };
        DockPanel.SetDock(export, Dock.Left);
        footer.Children.Add(export);
        _status.Foreground = GrayBrush;
        _status.Margin = new Thickness(0, 0, 8, 0);
        footer.Children.Add(_status);
        root.Children.Add(footer);

        var work = new Grid();
        work.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        work.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var side = new StackPanel { Margin = new Thickness(5, 3, 5, 6) };
        foreach (var (tool, tip) in ToolList)
        {
            var button = new Button { Width = 60, Height = 41, Margin = new Thickness(0, 3, 0, 0),
                Padding = new Thickness(9, 5, 9, 5), ToolTip = tip, Cursor = Cursors.Hand };
            button.Click += (_, _) =>
            {
                if (Editor.Tool == tool && VariantList.ContainsKey(tool)) ShowVariants(tool, button);
                else Editor.SetTool(tool);
            };
            button.MouseRightButtonUp += (_, e) => { if (VariantList.ContainsKey(tool)) ShowVariants(tool, button); e.Handled = true; };
            _tools[tool] = button;
            side.Children.Add(button);
        }
        side.Children.Add(new Separator { Margin = new Thickness(4, 10, 4, 8) });
        var swatches = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Rows = 3, Margin = new Thickness(6, 0, 6, 0) };
        foreach (string value in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            var swatch = new Button { Background = new SolidColorBrush(color), Width = 19, Height = 19,
                Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(0), BorderThickness = new Thickness(2), ToolTip = value };
            // A local template keeps hover styles from replacing the selected color.
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            swatch.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            swatch.Click += (_, _) => Editor.SetColor(color);
            _swatches[color] = swatch;
            swatches.Children.Add(swatch);
        }
        side.Children.Add(swatches);
        side.Children.Add(new Separator { Margin = new Thickness(4, 10, 4, 8) });
        var sizes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            var size = new Button { Width = 22, Height = 23, Padding = new Thickness(0), Margin = new Thickness(0),
                Content = new System.Windows.Shapes.Ellipse { Width = new[] { 4d, 8d, 14d }[i], Height = new[] { 4d, 8d, 14d }[i], Fill = Foreground },
                ToolTip = new[] { "細い / 小さい", "標準", "太い / 大きい" }[i] };
            size.Click += (_, _) => Editor.SetSize(index);
            _sizes.Add(size); sizes.Children.Add(size);
        }
        side.Children.Add(sizes);
        work.Children.Add(new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(Editor, 1);
        work.Children.Add(Editor);
        root.Children.Add(work);
        Content = root;
    }

    private Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand };
        button.Click += (_, _) => Safe(action);
        return button;
    }

    private MenuItem MenuAction(string title, Action action, string shortcut = "")
    {
        var item = new MenuItem { Header = title, InputGestureText = shortcut };
        item.Click += (_, _) => Safe(action);
        return item;
    }

    private Menu BuildMenu()
    {
        var menu = new Menu();
        var file = new MenuItem { Header = "ファイル" };
        file.Items.Add(CaptureMenuAction("範囲スナップ", () => _ = SnapAsync("region"), "Ctrl+Shift+5"));
        file.Items.Add(CaptureMenuAction("全画面スナップ", () => _ = SnapAsync("full"), "Ctrl+Shift+6"));
        file.Items.Add(new Separator());
        file.Items.Add(MenuAction("開く…", OpenFile, "Ctrl+O"));
        var save = MenuAction("保存", () => Save(false), "Ctrl+S");
        var saveAs = MenuAction("名前を付けて保存…", () => Save(true), "Ctrl+Shift+S");
        file.Items.Add(save); file.Items.Add(saveAs);
        _requiresImage.Add(save); _requiresImage.Add(saveAs);
        var history = MenuAction("保存履歴…", ShowSaveHistory);
        file.Items.Add(history);
        _historyActions.Add(history);
        file.Items.Add(new Separator());
        file.Items.Add(MenuAction("閉じる（トレイに格納）", HideEditor));
        file.Items.Add(MenuAction("終了", Exit));
        menu.Items.Add(file);
        var edit = new MenuItem { Header = "編集" };
        _undoItem = MenuAction("元に戻す", Editor.Undo, "Ctrl+Z");
        _redoItem = MenuAction("やり直し", Editor.Redo, "Ctrl+Y");
        edit.Items.Add(_undoItem); edit.Items.Add(_redoItem);
        edit.Items.Add(new Separator());
        var copy = MenuAction("画像をコピー", CopyImage, "Ctrl+C");
        edit.Items.Add(copy); _requiresImage.Add(copy);
        edit.Items.Add(MenuAction("貼り付け", PasteImage, "Ctrl+V"));
        edit.Items.Add(MenuAction("選択を削除", Editor.DeleteSelected, "Delete"));
        menu.Items.Add(edit);
        var snap = new MenuItem { Header = "スナップ" };
        foreach (var item in SnapItems(true)) snap.Items.Add(item);
        menu.Items.Add(snap);
        var recording = new MenuItem { Header = "動画" };
        recording.Items.Add(RecordingMenuAction("範囲を録画…", "region", "Ctrl+Shift+7"));
        recording.Items.Add(RecordingMenuAction("モニター全体を録画…", "full"));
        _stopRecordingItem = MenuAction("停止して保存", () => _ = StopRecordingAsync(), "Ctrl+Shift+7");
        recording.Items.Add(_stopRecordingItem);
        menu.Items.Add(recording);
        var settings = new MenuItem { Header = "設定" };
        _startupItem = new MenuItem { Header = "Windowsログイン時に起動", IsCheckable = true };
        _startupItem.Click += (_, _) => ToggleStartup();
        settings.Items.Add(_startupItem);
        settings.SubmenuOpened += (_, _) => _startupItem.IsChecked = StartupRegistration.IsEnabled;
        menu.Items.Add(settings);
        return menu;
    }

    private IEnumerable<MenuItem> SnapItems(bool trackControls = false)
    {
        var items = new[]
        {
            MenuAction("範囲スナップ", () => _ = SnapAsync("region"), "Ctrl+Shift+5"),
            MenuAction("全画面スナップ", () => _ = SnapAsync("full"), "Ctrl+Shift+6"),
            MenuAction("タイマースナップ（5秒後）", () => _ = SnapAsync("timer")),
            MenuAction("前回の範囲でスナップ", () => _ = SnapAsync("previous"))
        };
        foreach (var item in items)
        {
            if (trackControls) _captureActions.Add(item);
            yield return item;
        }
    }

    private MenuItem CaptureMenuAction(string title, Action action, string shortcut = "")
    {
        var item = MenuAction(title, action, shortcut);
        _captureActions.Add(item);
        return item;
    }

    private MenuItem RecordingMenuAction(string title, string mode, string shortcut = "")
    {
        var item = MenuAction(title, () => _ = StartRecordingAsync(mode), shortcut);
        _recordingStartActions.Add(item);
        return item;
    }

    private void ShowRecordingMenu(Button owner)
    {
        var menu = new ContextMenu { PlacementTarget = owner, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuAction("範囲を録画…", () => _ = StartRecordingAsync("region"), "Ctrl+Shift+7"));
        menu.Items.Add(MenuAction("モニター全体を録画…", () => _ = StartRecordingAsync("full")));
        menu.IsOpen = true;
    }

    private void UpdateRecordingControls()
    {
        bool active = _recordingTask is not null;
        foreach (var item in _captureActions) item.IsEnabled = !active && !_snapping;
        foreach (var item in _recordingStartActions) item.IsEnabled = !active && !_snapping;
        foreach (var item in _historyActions) item.IsEnabled = !active && !_snapping;
        if (_stopRecordingItem is not null) _stopRecordingItem.IsEnabled = _recorder is not null && !_recordingBusy;
        _host?.SetRecordingState(_recorder is not null, _recordingBusy || _snapping);
    }

    private void ShowSnapMenu(Button owner)
    {
        var menu = new ContextMenu { PlacementTarget = owner, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var item in SnapItems()) menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private void ShowVariants(string tool, Button owner)
    {
        var menu = new ContextMenu { PlacementTarget = owner, Placement = System.Windows.Controls.Primitives.PlacementMode.Right };
        foreach (var (key, label) in VariantList[tool])
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = Editor.Variants[tool] == key };
            item.Click += (_, _) => Editor.SetVariant(tool, key);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void UpdateControls()
    {
        foreach (var (tool, button) in _tools)
        {
            button.Background = Editor.Tool == tool ? ActiveBrush : Background;
            var icon = ToolIcons.Create(tool, Editor.Variants.GetValueOrDefault(tool));
            if (button.Content is not Image image || !ReferenceEquals(image.Source, icon))
                button.Content = new Image { Source = icon, Width = 28, Height = 28 };
        }
        foreach (var (color, button) in _swatches) button.BorderBrush = color == Editor.CurrentColor ? Brushes.White : Background;
        for (int i = 0; i < _sizes.Count; i++) _sizes[i].Background = i == Editor.SizeIndex ? ActiveBrush : Background;
        foreach (var item in _requiresImage) item.IsEnabled = Editor.Document.Background is not null;
        if (_undoItem is not null) _undoItem.IsEnabled = Editor.Document.CanUndo;
        if (_redoItem is not null) _redoItem.IsEnabled = Editor.Document.CanRedo;
    }

    public void ShowStatus(string text, int milliseconds = 3000)
    {
        _statusTimer.Stop();
        _status.Text = text;
        _status.ToolTip = text;
        if (milliseconds > 0)
        {
            _statusTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            _statusTimer.Start();
        }
    }

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception error) { App.ReportError(error); }
    }

    private void ToggleStartup()
    {
        Safe(() =>
        {
            bool enabled = !StartupRegistration.IsEnabled;
            StartupRegistration.SetEnabled(enabled);
            string message = enabled ? "次回のWindowsログインからトレイに常駐します" : "Windowsログイン時の自動起動を解除しました";
            ShowStatus(message);
            if (!IsVisible) _host?.ShowNotification(message);
        });
        if (_startupItem is not null) _startupItem.IsChecked = StartupRegistration.IsEnabled;
    }

    public void OpenPath(string path) => Safe(() => { Editor.Open(path); ShowEditor(); });

    public void ShowSaveHistory()
    {
        if (_exitRequested || _snapping || _recordingTask is not null) return;
        ShowEditor();
        if (_historyWindow is null)
        {
            var history = new SaveHistoryWindow(_savedFileHistory, text => ShowStatus(text, 8000)) { Owner = this };
            _historyWindow = history;
            history.Closed += (_, _) => { if (ReferenceEquals(_historyWindow, history)) _historyWindow = null; };
            history.Show();
        }
        else
        {
            _historyWindow.Refresh();
            if (_historyWindow.WindowState == WindowState.Minimized) _historyWindow.WindowState = WindowState.Normal;
            _historyWindow.Activate();
        }
    }

    private void CloseSaveHistory() => _historyWindow?.Close();

    private string RememberSavedFile(string path)
    {
        try
        {
            if (_savedFileHistory.Add(path)) return "";
            return $"（履歴の保存に失敗: {_savedFileHistory.LastError}）";
        }
        catch (Exception error)
        {
            // A history problem must never turn a completed image/video save into a failure.
            return $"（履歴を記録できませんでした: {error.Message}）";
        }
    }

    private void OpenFile()
    {
        Editor.CommitText();
        var dialog = new OpenFileDialog { Filter = "画像|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|すべてのファイル|*.*", InitialDirectory = PicturesDirectory() };
        if (dialog.ShowDialog(this) == true) OpenPath(dialog.FileName);
    }

    private static string PicturesDirectory()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Directory.Exists(path) ? path : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void Save(bool saveAs)
    {
        Editor.CommitText();
        if (Editor.Document.Background is null) return;
        string? path = Editor.Document.FilePath;
        if (saveAs || path is null)
        {
            var dialog = new SaveFileDialog { Filter = "PNG|*.png|JPEG|*.jpg;*.jpeg", DefaultExt = ".png", AddExtension = true,
                FileName = Path.GetFileName(path ?? Editor.Document.FileName),
                InitialDirectory = path is null ? PicturesDirectory() : Path.GetDirectoryName(path) };
            if (dialog.ShowDialog(this) != true) return;
            path = dialog.FileName;
        }
        Editor.Document.Save(path);
        string warning = RememberSavedFile(path);
        ShowStatus($"保存しました: {path}{warning}", warning.Length > 0 ? 10000 : 3000);
    }

    private void CopyImage()
    {
        Editor.CommitText();
        if (Editor.Document.Background is null) return;
        if (ClipboardService.SetImage(Editor.Document.Flatten())) ShowStatus("クリップボードにコピーしました");
        else ShowStatus("クリップボードを使用できませんでした。もう一度お試しください。");
    }

    private void PasteImage()
    {
        var image = ClipboardService.GetImage();
        if (image is not null) Editor.Load(image);
        else if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Count > 0) OpenPath(Clipboard.GetFileDropList()[0]!);
    }

    private void ExportDrag(DependencyObject source)
    {
        Editor.CommitText();
        if (Editor.Document.Background is null) return;
        string directory = Path.Combine(Path.GetTempPath(), "WinSkitch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Editor.Document.FileName);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Editor.Document.Flatten()));
        using (var stream = File.Create(path)) encoder.Save(stream);
        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Editor.IsEditingText || Keyboard.FocusedElement is TextBox) return;
        var modifiers = Keyboard.Modifiers;
        bool control = (modifiers & ModifierKeys.Control) != 0, shift = (modifiers & ModifierKeys.Shift) != 0;
        Action? action = null;
        if (control && (modifiers & ModifierKeys.Alt) == 0)
        {
            action = e.Key switch
            {
                Key.O => OpenFile, Key.S => () => Save(shift), Key.C => CopyImage, Key.V => PasteImage,
                Key.Z => shift ? Editor.Redo : Editor.Undo, Key.Y => Editor.Redo, _ => null
            };
        }
        else if (modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            action = e.Key switch
            {
                Key.Delete or Key.Back => Editor.DeleteSelected, Key.Escape => Editor.Escape, Key.Enter => Editor.ApplyCrop,
                Key.A => () => Editor.SetTool("arrow"), Key.T => () => Editor.SetTool("text"), Key.R => () => Editor.SetTool("shape"),
                Key.M => () => Editor.SetVariant("pen", "marker"), Key.H => () => Editor.SetVariant("pen", "highlighter"),
                Key.P => () => Editor.SetTool("mosaic"), Key.S => () => Editor.SetTool("stamp"), Key.C => () => Editor.SetTool("crop"),
                Key.Left => () => Editor.Nudge(shift ? -10 : -1, 0), Key.Right => () => Editor.Nudge(shift ? 10 : 1, 0),
                Key.Up => () => Editor.Nudge(0, shift ? -10 : -1), Key.Down => () => Editor.Nudge(0, shift ? 10 : 1), _ => null
            };
        }
        if (action is not null) { Safe(action); e.Handled = true; }
    }

    public async Task SnapAsync(string mode)
    {
        if (_snapping || _recordingTask is not null || _exitRequested) return;
        CloseSaveHistory();
        _snapping = true;
        UpdateRecordingControls();
        bool wasVisible = IsVisible;
        Editor.CommitText();
        Hide();
        try
        {
            if (mode == "timer") await CountdownAsync();
            else await Task.Delay(250);
            var capture = ScreenCapture.CaptureDesktop();
            Int32Rect? bounds;
            if (mode == "full") bounds = ScreenCapture.MonitorAtCursor();
            else if (mode == "previous" && _previous is not null) bounds = _previous;
            else bounds = await CaptureOverlay.SelectAsync(capture);
            if (bounds is null) { if (wasVisible) ShowEditor(); return; }
            var image = ScreenCapture.Crop(capture, bounds.Value);
            if (mode != "full") _previous = bounds;
            Editor.Load(image);
            ShowEditor();
        }
        catch (Exception error)
        {
            if (wasVisible) ShowEditor();
            App.ReportError(error);
        }
        finally { _snapping = false; UpdateRecordingControls(); }
    }

    public Task StartRecordingAsync(string mode)
    {
        if (_snapping || _recordingTask is not null || _exitRequested) return Task.CompletedTask;
        CloseSaveHistory();
        _recordingBusy = true;
        _recordingCancellation = new CancellationTokenSource();
        _recordingTask = RecordAsync(mode, _recordingCancellation);
        UpdateRecordingControls();
        return _recordingTask;
    }

    private async Task RecordAsync(string mode, CancellationTokenSource cancellation)
    {
        // Publish the session task before modal dialogs or other dispatcher work can reenter.
        await Task.Yield();
        bool wasVisible = IsVisible;
        string? savedPath = null;
        Exception? failure = null;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            // Remember the target monitor before the save dialog moves the cursor.
            Int32Rect? bounds = mode == "full" ? ScreenCapture.MonitorAtCursor() : null;
            Editor.CommitText();
            if (!IsVisible) ShowEditor();
            string directory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var dialog = new SaveFileDialog { Title = "動画の保存先を選択", Filter = "MP4 動画|*.mp4",
                DefaultExt = ".mp4", AddExtension = true, FileName = $"WinSkitch-{DateTime.Now:yyyyMMdd-HHmmss}.mp4",
                InitialDirectory = Directory.Exists(directory) ? directory : PicturesDirectory() };
            _recordingSaveDialogOpen = true;
            try { if (dialog.ShowDialog(this) != true) return; }
            finally { _recordingSaveDialogOpen = false; }
            Hide();
            await Task.Delay(250, cancellation.Token);
            if (bounds is null)
            {
                var capture = ScreenCapture.CaptureDesktop();
                bounds = await CaptureOverlay.SelectAsync(capture, cancellation.Token);
            }
            if (bounds is null) return;
            await RecordingCountdownAsync(cancellation);
            cancellation.Token.ThrowIfCancellationRequested();
            var recorder = await VideoRecorder.StartAsync(dialog.FileName, bounds.Value);
            _recorder = recorder;
            if (cancellation.IsCancellationRequested)
            {
                await recorder.StopAsync();
                savedPath = dialog.FileName;
                return;
            }
            _recordingControls = new RecordingControlsWindow(() => recorder.Elapsed, () => _ = StopRecordingAsync());
            _recordingControls.Show();
            _recordingBusy = false;
            UpdateRecordingControls();
            await recorder.Completion;
            savedPath = dialog.FileName;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally
        {
            // Completion owns encoder/capture cleanup. Stop also covers UI setup failures.
            if (_recorder is { } recorder)
            {
                try { await recorder.StopAsync(); }
                catch (Exception error) { failure ??= error; }
            }
            _recordingControls?.CloseAfterRecording();
            _recordingControls = null;
            _recorder = null;
            _recordingTask = null;
            _recordingCancellation = null;
            _recordingBusy = false;
            cancellation.Dispose();
            UpdateRecordingControls();
            string historyWarning = savedPath is not null && failure is null ? RememberSavedFile(savedPath) : "";
            if (failure is not null)
            {
                // Keep a failed save visible even when shutdown triggered the stop.
                _exitRequested = false;
                ShowEditor();
                App.ReportError(failure);
                _host?.ShowNotification($"録画できませんでした: {failure.Message}");
            }
            else if (!_exitRequested)
            {
                if (wasVisible || savedPath is not null) ShowEditor();
                else Hide();
                if (savedPath is not null)
                {
                    ShowStatus($"動画を保存しました: {savedPath}{historyWarning}", 10000);
                    _host?.ShowNotification($"動画を保存しました: {savedPath}");
                }
            }
        }
    }

    public async Task StopRecordingAsync()
    {
        var recorder = _recorder;
        if (recorder is null)
        {
            _recordingCancellation?.Cancel();
            return;
        }
        if (_recordingBusy) return;
        _recordingBusy = true;
        _recordingControls?.BeginSaving();
        UpdateRecordingControls();
        try { await recorder.StopAsync(); }
        catch { /* RecordAsync reports completion failures once and restores the UI. */ }
    }

    private static async Task RecordingCountdownAsync(CancellationTokenSource cancellation)
    {
        var number = new TextBlock { FontSize = 42, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center };
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(number);
        panel.Children.Add(new TextBlock { Text = "録画開始 • Escで中止", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
        var countdown = new Window { Title = "WinSkitch — 録画の準備", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Width = 200, Height = 110,
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)), Foreground = Brushes.White,
            Content = panel, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        countdown.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { cancellation.Cancel(); e.Handled = true; }
        };
        try
        {
            countdown.Show();
            countdown.Activate();
            for (int n = 3; n > 0; n--)
            {
                number.Text = n.ToString();
                await Task.Delay(1000, cancellation.Token);
            }
        }
        finally { countdown.Close(); }
        await Task.Delay(200, cancellation.Token);
    }

    private async Task CountdownAsync()
    {
        var label = new TextBlock { Foreground = Brushes.White, FontSize = 48, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var countdown = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Width = 110, Height = 85, Opacity = 0.9,
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)), Content = label,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowActivated = false };
        try
        {
            countdown.Show();
            for (int n = 5; n > 0; n--) { label.Text = n.ToString(); await Task.Delay(1000); }
        }
        finally { countdown.Close(); }
        await Task.Delay(200);
    }

    public void ShowEditor()
    {
        if (_allowExit || _exitRequested) return;
        if (_recordingControls is { } controls)
        {
            controls.Show();
            if (controls.WindowState == WindowState.Minimized) controls.WindowState = WindowState.Normal;
            controls.Activate();
            return;
        }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    public void StartInTray()
    {
        // Creating the hidden HWND initializes tray and hotkeys without a visible flash.
        new WindowInteropHelper(this).EnsureHandle();
    }

    private void HideEditor()
    {
        Editor.CommitText();
        CloseSaveHistory();
        Hide();
        _host?.ShowHiddenHint();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowExit) { e.Cancel = true; HideEditor(); }
    }

    public void Exit()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        if (_recordingTask is { } recordingTask)
        {
            _recordingCancellation?.Cancel();
            DismissRecordingSaveDialog();
            _ = ExitAfterRecordingAsync(recordingTask);
            return;
        }
        ExitImmediately();
    }

    private async Task ExitAfterRecordingAsync(Task recordingTask)
    {
        await StopRecordingAsync();
        await recordingTask;
        if (_exitRequested) ExitImmediately();
    }

    private void DismissRecordingSaveDialog()
    {
        if (!_recordingSaveDialogOpen) return;
        IntPtr owner = new WindowInteropHelper(this).Handle;
        NativeMethods.EnumWindowsProc callback = (window, _) =>
        {
            // Only close the native dialog owned by this recording's editor window.
            if (NativeMethods.GetWindow(window, 4) == owner)
                NativeMethods.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            return true;
        };
        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
    }

    private void ExitImmediately()
    {
        _allowExit = true;
        _host?.Dispose();
        Close();
        Application.Current?.Shutdown();
    }
}
