using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinSkitch;

public sealed class SaveHistoryWindow : Window
{
    private readonly SavedFileHistory _history;
    private readonly Action<string>? _reportStatus;
    private bool _closed;
    private bool _refreshing;
    private static Brush MutedBrush => new SolidColorBrush(Color.FromRgb(175, 175, 175));
    private static Brush WarningBrush => new SolidColorBrush(Color.FromRgb(238, 185, 112));

    public ListBox HistoryList { get; } = new()
    {
        Background = new SolidColorBrush(Color.FromRgb(48, 48, 48)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(85, 85, 85)),
        BorderThickness = new Thickness(1),
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    public Button OpenFolderButton { get; } = new()
    {
        Content = "保存先を開く", Padding = new Thickness(16, 7, 16, 7),
        Background = new SolidColorBrush(Color.FromRgb(61, 111, 182)),
        Margin = new Thickness(0, 0, 8, 0)
    };
    public Button OpenFileButton { get; } = new()
    {
        Content = "ファイルを開く", Padding = new Thickness(14, 7, 14, 7)
    };
    public TextBlock StatusText { get; } = new()
    {
        Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10), MinHeight = 18
    };
    public TextBlock EmptyState { get; } = new()
    {
        Text = "保存履歴はまだありません\n画像・動画を保存すると、ここに表示されます。",
        Foreground = MutedBrush, TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(20), TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false
    };
    public SavedFileEntry? SelectedEntry => (HistoryList.SelectedItem as ListBoxItem)?.Tag as SavedFileEntry;

    public SaveHistoryWindow(SavedFileHistory history, Action<string>? reportStatus = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history = history;
        _reportStatus = reportStatus;
        Title = "WinSkitch — 保存履歴";
        Width = 740; Height = 520; MinWidth = 480; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(38, 38, 38));
        Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230));
        FontFamily = new FontFamily("Yu Gothic UI");
        FontSize = 13;
        ShowInTaskbar = false;
        BuildLayout();
        HistoryList.SelectionChanged += (_, _) => { if (!_refreshing) UpdateActions(); };
        HistoryList.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || !OpenFolderButton.IsEnabled
                || !ClickedHistoryItem(e.OriginalSource as DependencyObject)) return;
            OpenSelectedFolder();
            e.Handled = true;
        };
        OpenFolderButton.Click += (_, _) => OpenSelectedFolder();
        OpenFileButton.Click += (_, _) => OpenSelectedFile();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        Activated += (_, _) => Refresh();
        Closed += (_, _) => { _closed = true; _history.Changed -= OnHistoryChanged; };
        _history.Changed += OnHistoryChanged;
        Refresh();
    }

    private void BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(18) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = "保存履歴", FontSize = 21, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "最近保存した画像・動画（最大20件）。ダブルクリックで保存先を開けます。",
            Foreground = MutedBrush, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(StatusText);
        var buttons = new DockPanel();
        var close = new Button { Content = "閉じる", Padding = new Thickness(14, 7, 14, 7), IsCancel = true };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(close);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(OpenFolderButton);
        actions.Children.Add(OpenFileButton);
        buttons.Children.Add(actions);
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new Grid();
        ScrollViewer.SetHorizontalScrollBarVisibility(HistoryList, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(HistoryList, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(HistoryList, "保存した画像と動画の履歴");
        AutomationProperties.SetName(OpenFolderButton, "選択したファイルの保存先を開く");
        AutomationProperties.SetName(OpenFileButton, "選択した保存ファイルを開く");
        HistoryList.ItemContainerStyle = CreateItemStyle();
        body.Children.Add(HistoryList);
        body.Children.Add(EmptyState);
        root.Children.Add(body);
        Content = root;
    }

    private static Style CreateItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 10, 12, 10)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(230, 230, 230))));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.AppendChild(presenter);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border }));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(64, 64, 64))));
        style.Triggers.Add(hover);
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(52, 82, 120))));
        style.Triggers.Add(selected);
        return style;
    }

    public void Refresh() => RefreshEntries(selectNewest: false);

    private void RefreshEntries(bool selectNewest)
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => RefreshEntries(selectNewest))); return; }
        string? selectedPath = selectNewest ? null : SelectedEntry?.FilePath;
        _refreshing = true;
        try
        {
            HistoryList.Items.Clear();
            ListBoxItem? selectedItem = null;
            foreach (SavedFileEntry entry in _history.Entries)
            {
                var item = new ListBoxItem { Tag = entry, Content = CreateEntry(entry), ToolTip = entry.FilePath };
                HistoryList.Items.Add(item);
                if (string.Equals(entry.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase)) selectedItem = item;
            }
            if (selectedItem is not null) HistoryList.SelectedItem = selectedItem;
            else if (HistoryList.Items.Count > 0) HistoryList.SelectedIndex = 0;
            EmptyState.Visibility = HistoryList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _refreshing = false; }
        UpdateActions();
    }

    private static FrameworkElement CreateEntry(SavedFileEntry entry)
    {
        bool video = string.Equals(Path.GetExtension(entry.FilePath), ".mp4", StringComparison.OrdinalIgnoreCase);
        var panel = new StackPanel();
        var title = new DockPanel { LastChildFill = true };
        var badge = new Border
        {
            Background = new SolidColorBrush(video ? Color.FromRgb(123, 57, 57) : Color.FromRgb(50, 86, 125)),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = video ? "動画" : "画像", FontSize = 11 }
        };
        DockPanel.SetDock(badge, Dock.Left);
        title.Children.Add(badge);
        var savedAt = new TextBlock
        {
            Text = entry.SavedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm"), FontSize = 11,
            Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0)
        };
        DockPanel.SetDock(savedAt, Dock.Right);
        title.Children.Add(savedAt);
        title.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(entry.FilePath), FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(title);
        panel.Children.Add(new TextBlock
        {
            Text = entry.FilePath, Foreground = MutedBrush, FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!SavedFileActions.CanOpenFile(entry.FilePath))
            panel.Children.Add(new TextBlock
            {
                Text = "ファイルが見つかりません（移動または削除されています）",
                Foreground = WarningBrush, FontSize = 11, Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        return panel;
    }

    private void UpdateActions()
    {
        SavedFileEntry? entry = SelectedEntry;
        OpenFolderButton.IsEnabled = entry is not null && SavedFileActions.CanOpenFolder(entry.FilePath);
        OpenFileButton.IsEnabled = entry is not null && SavedFileActions.CanOpenFile(entry.FilePath);
        if (entry is null) SetStatus("保存すると、履歴から保存先を開けます。", false);
        else if (!OpenFolderButton.IsEnabled) SetStatus("保存先のフォルダーが見つかりません。移動または削除されています。", true);
        else if (!OpenFileButton.IsEnabled) SetStatus("ファイルは見つかりませんが、保存先のフォルダーは開けます。", false);
        else SetStatus("選択した画像・動画の保存先を開けます。", false);
        OpenFolderButton.ToolTip = OpenFolderButton.IsEnabled ? "エクスプローラーで保存したファイルの場所を開きます" : StatusText.Text;
        OpenFileButton.ToolTip = OpenFileButton.IsEnabled ? "既定のアプリで画像・動画を開きます" : StatusText.Text;
    }

    private void OpenSelectedFolder()
    {
        if (SelectedEntry is not { } entry) return;
        OpenSafely(() => SavedFileActions.OpenFolder(entry.FilePath), "保存先を開きました。", "保存先を開けませんでした");
    }

    private void OpenSelectedFile()
    {
        if (SelectedEntry is not { } entry) return;
        OpenSafely(() => SavedFileActions.OpenFile(entry.FilePath), "ファイルを開きました。", "ファイルを開けませんでした");
    }

    private void OpenSafely(Action open, string success, string failure)
    {
        try { open(); SetStatus(success, false); _reportStatus?.Invoke(success); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or InvalidOperationException)
        {
            Refresh();
            string message = $"{failure}: {error.Message}";
            SetStatus(message, true);
            _reportStatus?.Invoke(message);
        }
    }

    private void SetStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = error ? WarningBrush : MutedBrush;
    }

    private void OnHistoryChanged()
    {
        if (_closed) return;
        RefreshEntries(selectNewest: true);
    }

    private static bool ClickedHistoryItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListBoxItem) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
