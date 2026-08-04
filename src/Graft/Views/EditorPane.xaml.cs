using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Indentation;
using ICSharpCode.AvalonEdit.Rendering;
using Graft.Editor;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>Tabs.Countから、要素が0件かどうかに応じたVisibilityへ変換する（タブ見出しと空状態表示の切替に使う）。</summary>
public sealed class EmptyCollectionToVisibilityConverter : IValueConverter
{
    /// <summary>ConverterParameterに"Invert"を指定すると、0件のときVisibleを返す（空状態メッセージ用）。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is int count && count == 0;
        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        var visible = invert ? isEmpty : !isEmpty;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// エディタ領域（4章）。単一のAvalonEdit <see cref="Editor"/> を複数タブで使い回し、
/// アクティブタブが変わるたびに<c>Document</c>を差し替える方式を採る（10万行級のファイルでも
/// タブごとに重い可視化ツリーを複数保持しないための18章対応）。
/// </summary>
public partial class EditorPane : UserControl
{
    private readonly SyntaxHighlightBridge _bridge;
    private EditorPaneViewModel? _viewModel;

    public EditorPane()
    {
        InitializeComponent();

        Editor.Document = new TextDocument();
        Editor.IsEnabled = false;
        Editor.TextArea.IndentationStrategy = new DefaultIndentationStrategy();
        Editor.Options.EnableRectangularSelection = true; // 4.4: 矩形選択（Alt+ドラッグ）を明示的に有効化
        Editor.TextArea.SetResourceReference(TextArea.SelectionBrushProperty, "BgSelected");
        Editor.TextArea.TextView.SetResourceReference(TextView.CurrentLineBackgroundProperty, "BgSurface");
        Editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;

        _bridge = new SyntaxHighlightBridge(Editor);
        Editor.TextArea.TextView.LineTransformers.Add(_bridge);
        _bridge.Attach(Editor.Document, string.Empty, syntaxEnabled: false);

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = e.NewValue as EditorPaneViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        ApplyActiveTab(_viewModel?.ActiveTab);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorPaneViewModel.ActiveTab))
        {
            ApplyActiveTab(_viewModel?.ActiveTab);
        }
        else if (e.PropertyName == nameof(EditorPaneViewModel.ShowWhitespace))
        {
            ApplyWhitespaceOption();
        }
    }

    /// <summary>アクティブタブの切替。Documentの差し替え・言語別ハイライトの再接続・
    /// インデント設定の反映・カーソル位置の復元をまとめて行う。</summary>
    private void ApplyActiveTab(EditorTabViewModel? tab)
    {
        if (tab is null)
        {
            Editor.Document = new TextDocument();
            Editor.IsEnabled = false;
            _bridge.Attach(Editor.Document, string.Empty, syntaxEnabled: false);
            return;
        }

        Editor.IsEnabled = true;
        Editor.Document = tab.Session.Document;
        ApplyWhitespaceOption();
        ApplyIndentOptions(tab);

        var extension = System.IO.Path.GetExtension(tab.Session.FileName);
        _bridge.Attach(tab.Session.Document, extension, _viewModel?.SyntaxEnabled ?? true);

        MoveCaretTo(tab.CaretLine, tab.CaretColumn);
        Editor.Focus();
    }

    private void ApplyWhitespaceOption()
    {
        var show = _viewModel?.ShowWhitespace ?? false;
        Editor.Options.ShowSpaces = show;
        Editor.Options.ShowTabs = show;
        Editor.Options.HighlightCurrentLine = _viewModel?.HighlightCurrentLine ?? true;
    }

    private void ApplyIndentOptions(EditorTabViewModel tab)
    {
        Editor.Options.ConvertTabsToSpaces = !tab.IndentUseTabs;
        Editor.Options.IndentationSize = tab.IndentWidth;
    }

    /// <summary>4.5/3.2: タブごとに直近のカーソル位置を保持・復元する。範囲外は安全側へ丸める。</summary>
    private void MoveCaretTo(int line, int column)
    {
        var document = Editor.Document;
        if (document is null || document.LineCount == 0) return;

        var clampedLine = Math.Clamp(line, 1, document.LineCount);
        var lineObj = document.GetLineByNumber(clampedLine);
        var clampedColumn = Math.Clamp(column, 1, lineObj.Length + 1);

        Editor.TextArea.Caret.Line = clampedLine;
        Editor.TextArea.Caret.Column = clampedColumn;
        Editor.ScrollToLine(clampedLine);
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_viewModel?.ActiveTab is not { } tab) return;
        tab.CaretLine = Editor.TextArea.Caret.Line;
        tab.CaretColumn = Editor.TextArea.Caret.Column;
    }

    /// <summary>4.4: Ctrl+マウスホイールでフォントサイズを変更する。</summary>
    private void OnEditorPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || _viewModel is null) return;
        _viewModel.FontSize += e.Delta > 0 ? 1 : -1;
        e.Handled = true;
    }

    /// <summary>4.3: Ctrl+Tab（直近使用順のタブ切替）とCtrl+W（タブを閉じる）。</summary>
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;

        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_viewModel.PeekMruNeighbor() is { } next) _viewModel.ActiveTab = next;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_viewModel.ActiveTab is { } tab) await _viewModel.CloseTabAsync(tab).ConfigureAwait(true);
            e.Handled = true;
        }
    }

    /// <summary>4.3: 中クリックでタブを閉じ、タブ見出しのダブルクリックでプレビューを固定タブへ昇格する。</summary>
    private async void OnTabStripPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } container) return;
        if (container.DataContext is not EditorTabViewModel tab) return;

        if (e.ChangedButton == MouseButton.Middle)
        {
            await _viewModel.CloseTabAsync(tab).ConfigureAwait(true);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            tab.IsPreview = false;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null && node is not T)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        return node as T;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _bridge.Dispose();
    }
}
