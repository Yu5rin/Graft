using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Indentation;
using Graft.Core;
using Graft.Editor;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// エディタ領域（4章）。単一のAvaloniaEdit <see cref="Editor"/> を複数タブで使い回し、
/// アクティブタブが変わるたびに<c>Document</c>を差し替える方式を採る（10万行級のファイルでも
/// タブごとに重い可視化ツリーを複数保持しないための18章対応）。
/// v2.0のWPF版からの移植（19章 L3）。
/// </summary>
public partial class EditorPane : UserControl
{
    private readonly SyntaxHighlightBridge _bridge;
    private readonly BracketSupport _brackets;
    private readonly FoldingSupport _folding;
    private readonly CompletionProvider _completion;
    private readonly GitGutterProvider _gitGutter;
    private readonly AvaloniaDialogService _dialogs = new();
    private EditorPaneViewModel? _viewModel;

    // 現在Editorに読み込まれている（＝Documentを共有している）タブ。切替前にこのタブへ
    // スクロール位置等を退避してから、次のタブへ切り替える。
    private EditorTabViewModel? _loadedTab;

    public EditorPane()
    {
        InitializeComponent();

        Editor.Document = new TextDocument();
        Editor.IsEnabled = false;
        Editor.TextArea.IndentationStrategy = new DefaultIndentationStrategy();
        Editor.Options.EnableRectangularSelection = true; // 4.4: 矩形選択（Alt+ドラッグ）
        Editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;

        _bridge = new SyntaxHighlightBridge(Editor);
        Editor.TextArea.TextView.LineTransformers.Add(_bridge);
        _bridge.Attach(Editor.Document, string.Empty, syntaxEnabled: false);

        _brackets = new BracketSupport(Editor);
        _folding = new FoldingSupport(Editor);
        _completion = new CompletionProvider(Editor);

        // 4.7 Gitガター。行番号の左隣に置き、HEADとの差分を色帯で示す。
        _gitGutter = new GitGutterProvider(Editor, new Graft.Features.GitIntegration());
        Editor.TextArea.LeftMargins.Insert(0, _gitGutter);

        // AvaloniaにPreviewKeyDown/PreviewMouseWheelは無いため、トンネリング段階で購読する。
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        Editor.AddHandler(PointerWheelChangedEvent, OnEditorPointerWheelChanged, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(PointerPressedEvent, OnTabStripPointerPressed, RoutingStrategies.Tunnel);
        DiffHost.DoubleTapped += OnDiffDoubleTapped;

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.TabSaved -= OnTabSaved;
        }
        _viewModel = DataContext as EditorPaneViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.TabSaved += OnTabSaved;
        }

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
        else if (e.PropertyName == nameof(EditorPaneViewModel.WordWrap))
        {
            ApplyWordWrapOption();
        }
    }

    /// <summary>アクティブタブの切替。Documentの差し替え・言語別ハイライトの再接続・
    /// インデント設定の反映・スクロール位置/カーソル/選択範囲の退避・復元をまとめて行う。</summary>
    private void ApplyActiveTab(EditorTabViewModel? tab)
    {
        SaveViewStateInto(_loadedTab);
        _loadedTab = tab;

        if (tab is null) { ApplyEmptyTab(); return; }
        if (tab.Kind == EditorTabKind.Diff) { ApplyDiffTab(tab); return; }
        if (tab.Kind == EditorTabKind.HistoryDiff) { ApplyHistoryDiffTab(tab); return; }
        ApplyDocumentTab(tab);
    }

    // ApplyEmptyTab/ApplyDiffTab/ApplyHistoryDiffTabは EditorPane.Diff.axaml.cs（1ファイル400行上限のため分割）。

    private void ApplyDocumentTab(EditorTabViewModel tab)
    {
        Editor.IsVisible = true;
        DiffHost.IsVisible = false;
        DiffHost.DataContext = null;
        HistoryDiffHost.IsVisible = false;
        HistoryDiffHost.DataContext = null;

        Editor.IsEnabled = true;
        Editor.Document = tab.Session.Document;
        ApplyWhitespaceOption();
        ApplyWordWrapOption();
        ApplyIndentOptions(tab);

        // 課題3: 極端に長い行（1行20,000文字超）を含むファイルは、構文強調・折り返し・
        // 括弧の対応付けの計算コストがその1行の文字数に比例して増える（詳細は
        // DocumentSession.HasExtremelyLongLine・ApplyWordWrapOptionのコメント参照）。
        // 利用者の設定に関わらずこれらを自動的に無効化し、ステータスバーで通知する
        // （StatusBarView.axaml・EditorPaneViewModel.ActiveTabHasLongLineWarning）。
        var longLine = tab.Session.HasExtremelyLongLine;
        var extension = Path.GetExtension(tab.Session.FileName);
        _bridge.Attach(tab.Session.Document, extension, !longLine && (_viewModel?.SyntaxEnabled ?? true));
        _brackets.Attach(tab.Session.Document, extension, languageAware: !longLine);
        _brackets.SetAutoCloseEnabled(!longLine && (_viewModel?.AutoClosingBrackets ?? true));
        _folding.Attach(tab.Session.Document, extension);
        _folding.SetEnabled(!longLine && (_viewModel?.Folding ?? true));
        if (_viewModel is not null) Search.Attach(Editor, _viewModel.Ui);
        ApplyGitGutter(tab);

        RestoreViewStateFrom(tab);
        Editor.Focus();
    }

    /// <summary>4.7: 表示中のファイルをGitガターの対象に設定し、差分を取り直す。</summary>
    private void ApplyGitGutter(EditorTabViewModel tab)
    {
        _gitGutter.SetEnabled(_viewModel?.GitGutterEnabled ?? true);
        _gitGutter.SetTarget(_viewModel?.ProjectRoot, tab.Session.RelativePath);
        _ = _gitGutter.RefreshAsync();
    }

    /// <summary>4.7: 保存が更新契機。表示中のタブが保存されたときだけ取り直す。</summary>
    private void OnTabSaved(object? sender, EditorTabViewModel tab)
    {
        if (ReferenceEquals(tab, _loadedTab)) _ = _gitGutter.RefreshAsync();
    }

    /// <summary>非表示側へ回るタブのカーソル・選択範囲・スクロール位置を退避する。</summary>
    private void SaveViewStateInto(EditorTabViewModel? tab)
    {
        if (tab is null || !Editor.IsEnabled) return;

        tab.CaretLine = Editor.TextArea.Caret.Line;
        tab.CaretColumn = Editor.TextArea.Caret.Column;
        tab.SelectionStart = Editor.SelectionStart;
        tab.SelectionLength = Editor.SelectionLength;
        tab.ScrollOffsetX = Editor.HorizontalOffset;
        tab.ScrollOffsetY = Editor.VerticalOffset;
        tab.HasViewState = true;
    }

    /// <summary>
    /// タブ表示時にカーソル・選択範囲・スクロール位置を復元する。<see cref="MoveCaretTo"/>による
    /// おおまかな可視化（<c>ScrollToLine</c>）はカーソル位置を確実に画面内へ入れるために常に行う。
    /// 一方、正確なスクロールオフセットの復元は<see cref="EditorTabViewModel.HasViewState"/>が
    /// trueの場合（＝一度でもこのタブを離れ、退避済みの位置がある場合）のみ行う。
    /// Document切替直後はレイアウトが未確定のため、正確なオフセット設定はレイアウト確定後まで
    /// 遅延させる（AvaloniaのDispatcherPriority.Backgroundはレイアウトより後に走る）。
    /// </summary>
    private void RestoreViewStateFrom(EditorTabViewModel tab)
    {
        MoveCaretTo(tab.CaretLine, tab.CaretColumn);
        if (tab.SelectionLength > 0)
        {
            var maxOffset = Editor.Document.TextLength;
            var start = Math.Clamp(tab.SelectionStart, 0, maxOffset);
            var length = Math.Clamp(tab.SelectionLength, 0, maxOffset - start);
            if (length > 0) Editor.Select(start, length);
        }

        if (!tab.HasViewState) return;

        var x = tab.ScrollOffsetX;
        var y = tab.ScrollOffsetY;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_loadedTab, tab)) return; // 遅延実行中に別タブへ切り替わっていたら何もしない
            Editor.ScrollToVerticalOffset(y);
            Editor.ScrollToHorizontalOffset(x);
        }, DispatcherPriority.Background);
    }

    private void ApplyWhitespaceOption()
    {
        var show = _viewModel?.ShowWhitespace ?? false;
        Editor.Options.ShowSpaces = show;
        Editor.Options.ShowTabs = show;
        Editor.Options.HighlightCurrentLine = _viewModel?.HighlightCurrentLine ?? true;
    }

    /// <summary>
    /// 課題3: 折り返し表示の反映。極端に長い行を含むファイル
    /// （<see cref="DocumentSession.HasExtremelyLongLine"/>）では、利用者の設定に関わらず
    /// 折り返しを無効化する。実測（1行10万文字のファイル）では、折り返し有効時に
    /// AvaloniaEdit側の書式計算コストが無効時の10倍以上（数百ms→1.5秒前後）に
    /// 悪化することを確認しており、既定でオフの折り返しをこのファイルに限って
    /// 有効なままにしておくと利用者が気付かないまま極端に遅くなる。横スクロールで
    /// 内容自体は確認できるため、折り返しだけを諦める。
    /// XAML側ではバインドせずここで一括管理する（ShowWhitespace等と同じ方針）。
    /// </summary>
    private void ApplyWordWrapOption()
    {
        var longLine = _loadedTab is { Kind: EditorTabKind.Document } t && t.Session.HasExtremelyLongLine;
        Editor.WordWrap = !longLine && (_viewModel?.WordWrap ?? false);
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
    private void OnEditorPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control || _viewModel is null) return;
        _viewModel.FontSize += e.Delta.Y > 0 ? 1 : -1;
        e.Handled = true;
    }

    /// <summary>4.3/4.4のキー割り当てをまとめて処理する。</summary>
    private async void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;
        var mods = e.KeyModifiers;

        if (TryHandleTabNavigation(e, mods)) return;
        if (TryHandleSearchShortcuts(e, mods)) return;
        if (TryHandleLineEditShortcuts(e, mods)) return;

        await SafeHandler.RunAsync("エディタのキー操作", () => HandleAsyncShortcutsAsync(e, mods))
            .ConfigureAwait(true);
    }

    /// <summary>Ctrl+Tab: 直近使用順のタブ切替。</summary>
    private bool TryHandleTabNavigation(KeyEventArgs e, KeyModifiers mods)
    {
        if (mods != KeyModifiers.Control || e.Key != Key.Tab) return false;
        if (_viewModel!.PeekMruNeighbor() is { } next) _viewModel.ActiveTab = next;
        return e.Handled = true;
    }

    /// <summary>Ctrl+F/Ctrl+H: 検索・置換オーバーレイを開く。差分タブ表示中は対象外。</summary>
    private bool TryHandleSearchShortcuts(KeyEventArgs e, KeyModifiers mods)
    {
        if (mods != KeyModifiers.Control) return false;
        if (_viewModel?.ActiveTab is not { Kind: EditorTabKind.Document }) return false;
        if (e.Key == Key.F) { Search.OpenFind(); return e.Handled = true; }
        if (e.Key == Key.H) { Search.OpenReplace(); return e.Handled = true; }
        return false;
    }

    /// <summary>Ctrl+/、行複製・移動・削除、Ctrl+Spaceの単語補完。</summary>
    private bool TryHandleLineEditShortcuts(KeyEventArgs e, KeyModifiers mods)
    {
        if (_viewModel?.ActiveTab is not { Kind: EditorTabKind.Document } tab) return false;

        if (mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.K)
        {
            EditorCommands.DeleteLines(Editor);
            return e.Handled = true;
        }
        if (mods == (KeyModifiers.Shift | KeyModifiers.Alt) && e.Key == Key.Down)
        {
            EditorCommands.DuplicateLines(Editor);
            return e.Handled = true;
        }
        if (mods == KeyModifiers.Alt && e.Key == Key.Up)
        {
            EditorCommands.MoveLinesUp(Editor);
            return e.Handled = true;
        }
        if (mods == KeyModifiers.Alt && e.Key == Key.Down)
        {
            EditorCommands.MoveLinesDown(Editor);
            return e.Handled = true;
        }
        if (mods == KeyModifiers.Control && e.Key is Key.OemQuestion or Key.Divide)
        {
            var extension = Path.GetExtension(tab.Session.FileName);
            EditorCommands.ToggleLineComment(Editor, SyntaxLexer.RuleForExtension(extension));
            return e.Handled = true;
        }
        if (mods == KeyModifiers.Control && e.Key == Key.Space)
        {
            if (_viewModel.CompletionEnabled) _completion.RequestCompletion();
            return e.Handled = true;
        }
        return false;
    }

    /// <summary>Ctrl+W（タブを閉じる、保存確認あり）とCtrl+G（指定行へ移動）。</summary>
    private async Task HandleAsyncShortcutsAsync(KeyEventArgs e, KeyModifiers mods)
    {
        if (mods != KeyModifiers.Control) return;

        if (e.Key == Key.W)
        {
            if (_viewModel!.ActiveTab is { } tab) await _viewModel.CloseTabAsync(tab).ConfigureAwait(true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.G && _viewModel!.ActiveTab is { Kind: EditorTabKind.Document })
        {
            e.Handled = true;
            var input = await _dialogs.PromptAsync("指定行へ移動", "移動先の行番号を入力してください。").ConfigureAwait(true);
            if (int.TryParse(input, out var line)) EditorCommands.GoToLine(Editor, line);
        }
    }

    // OnDiffDoubleTapped/FindDiffRowは EditorPane.Diff.axaml.cs（1ファイル400行上限のため分割）。

    /// <summary>
    /// 不具合3対応: 差分タブに常時表示する「閉じる」ボタン（DiffCloseButton）。
    /// Ctrl+Wと同じCloseTabAsync経由で閉じる（差分タブは保存確認が無いため即座に閉じる）。
    /// </summary>
    private async void OnDiffCloseClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.ActiveTab is { Kind: EditorTabKind.Diff or EditorTabKind.HistoryDiff } tab)
        {
            await SafeHandler.RunAsync("差分表示を閉じる", () => _viewModel.CloseTabAsync(tab)).ConfigureAwait(true);
        }
    }

    /// <summary>4.3: 中クリックでタブを閉じ、タブ見出しのダブルクリックでプレビューを固定タブへ昇格する。</summary>
    private async void OnTabStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null) return;
        if (FindAncestor<ListBoxItem>(e.Source as Visual) is not { DataContext: EditorTabViewModel tab }) return;

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed)
        {
            e.Handled = true;
            await SafeHandler.RunAsync("タブを閉じる", () => _viewModel.CloseTabAsync(tab)).ConfigureAwait(true);
        }
        else if (point.Properties.IsLeftButtonPressed && e.ClickCount == 2)
        {
            tab.IsPreview = false;
        }
    }

    private static T? FindAncestor<T>(Visual? node) where T : Visual
    {
        while (node is not null and not T)
        {
            node = node.GetVisualParent();
        }

        return node as T;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        Editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.TabSaved -= OnTabSaved;
        }
        _gitGutter.Dispose();
        _bridge.Dispose();
        _brackets.Dispose();
        _folding.Dispose();
    }
}
