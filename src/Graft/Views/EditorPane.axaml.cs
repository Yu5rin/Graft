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

    // 機能改善（タブのドラッグ並べ替え）: ドラッグ中の状態。ポインタが押された時点では
    // まだドラッグと確定させず（単なるクリック＝タブ切替を邪魔しないため）、しきい値
    // （DragThresholdPixels）を超えて動いて初めてドラッグ表示に切り替える。
    private const double DragThresholdPixels = 4;
    private EditorTabViewModel? _dragTab;
    private Point _dragStartPoint;
    private bool _isDragging;
    private int _dragTargetIndex = -1;
    private ListBoxItem? _dragIndicatorItem;

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
        // 機能改善（タブのドラッグ並べ替え）: 押下後の移動・離しをトンネル段階で拾う
        // （OnEditorPointerWheelChanged等、既存のCtrl+マウスホイールと同じ理由）。
        TabStrip.AddHandler(PointerMovedEvent, OnTabStripPointerMoved, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(PointerReleasedEvent, OnTabStripPointerReleased, RoutingStrategies.Tunnel);
        TabStrip.PointerCaptureLost += (_, _) => ResetDragState();
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
        if (_loadedTab is not null) _loadedTab.PropertyChanged -= OnLoadedTabPropertyChanged;
        _loadedTab = tab;
        if (tab is not null) tab.PropertyChanged += OnLoadedTabPropertyChanged;

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

        // 課題3（再設計）: 極端に長い行（1行20,000文字超）を含んでいても、無効化するのは
        // その行だけに留める（ファイル全体は対象外）。構文強調は行単位のキャップ
        // （SyntaxHighlightBridge.ColorizeLine）、括弧の対応付けは言語認識の行単位キャップ
        // （BracketSupport.IsInsideStringOrComment）がそれぞれ内部で処理するため、ここでは
        // 常に利用者の設定どおりに有効化する。折りたたみは実測でコストが無視できるほど
        // 小さいため、そもそも長い行による特別扱いをしない（FoldingSupportのコメント参照）。
        // ステータスバーへの通知（何が制限され何が有効かという正確な文言）は
        // StatusBarView.axaml・EditorPaneViewModel.ActiveTabHasLongLineWarning側で行う。
        var extension = Path.GetExtension(tab.Session.FileName);
        _bridge.Attach(tab.Session.Document, extension, _viewModel?.SyntaxEnabled ?? true);
        _brackets.Attach(tab.Session.Document, extension);
        _brackets.SetAutoCloseEnabled(_viewModel?.AutoClosingBrackets ?? true);
        _folding.Attach(tab.Session.Document, extension);
        _folding.SetEnabled(_viewModel?.Folding ?? true);
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
    /// 課題3（再設計）: 折り返し表示の反映。以前は極端に長い行を含むファイルでは利用者の
    /// 設定に関わらず折り返しを強制無効化していたが、「エディタとして致命的」という
    /// 指摘（利用者の設定を勝手に無視すること自体が問題）を受けて廃止した。
    ///
    /// 経緯: 実測（1行10万文字のファイル）では、折り返し有効時にAvaloniaEdit側の
    /// 書式計算コストが無効時の10倍以上（数百ms→1.5秒前後）に悪化することを確認して
    /// いる。これは実在するコストであり無視はできないが、「遅くなる可能性があるコストを
    /// 利用者に無断で払わせない／勝手に機能を奪わない」のバランスを取り、既定は利用者の
    /// 設定にそのまま従わせたうえで、重くなりうることが分かっているこのファイルに限り
    /// 「このファイルでは折り返しを無効にする」ボタン（通知バー、EditorPane.axaml）で
    /// 利用者自身がコストを選べる逃げ道を用意する方針にした
    /// （<see cref="EditorTabViewModel.WordWrapDisabledForTab"/>・
    /// <see cref="EditorTabViewModel.DisableWordWrapForTabCommand"/>・
    /// <see cref="OnLoadedTabPropertyChanged"/>）。
    /// XAML側ではバインドせずここで一括管理する（ShowWhitespace等と同じ方針）。
    /// </summary>
    private void ApplyWordWrapOption()
    {
        var disabledForTab = _loadedTab is { Kind: EditorTabKind.Document } t && t.WordWrapDisabledForTab;
        Editor.WordWrap = !disabledForTab && (_viewModel?.WordWrap ?? false);
    }

    /// <summary>
    /// 課題3（再設計）: 現在読み込み中のタブ（<see cref="_loadedTab"/>）のプロパティ変更を監視し、
    /// <see cref="EditorTabViewModel.WordWrapDisabledForTab"/>が変わったら折り返し表示へ
    /// 即座に反映する。通知バー（EditorPane.axaml）の「このファイルでは折り返しを無効にする」
    /// ボタンは<see cref="EditorTabViewModel.DisableWordWrapForTabCommand"/>へCommandバインド
    /// しているだけ（MVVM、コードビハインドのクリックハンドラは持たない）なので、実際に
    /// AvaloniaEditのWordWrapへ反映する経路をここに一本化する。
    /// </summary>
    private void OnLoadedTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorTabViewModel.WordWrapDisabledForTab)) ApplyWordWrapOption();
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
        _viewModel.AdjustFontSize(e.Delta.Y > 0 ? 1 : -1);
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

    /// <summary>
    /// 4.3: 中クリックでタブを閉じ、タブ見出しのダブルクリックでプレビューを固定タブへ昇格する。
    /// 機能改善（タブのドラッグ並べ替え）: 左ボタン単発の押下はまだ並べ替えと確定させず、
    /// ドラッグ候補として開始位置だけ記録する（実際にドラッグと認めるのは
    /// <see cref="OnTabStripPointerMoved"/>がしきい値超えの移動を検知してから。閉じるボタン上の
    /// 押下は対象外とし、ボタン自体のクリックを妨げない）。
    /// </summary>
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
        else if (point.Properties.IsLeftButtonPressed && e.ClickCount == 1
                 && tab.Kind == EditorTabKind.Document
                 && FindAncestor<Button>(e.Source as Visual) is null)
        {
            _dragTab = tab;
            _dragStartPoint = e.GetCurrentPoint(TabStrip).Position;
            _isDragging = false;
        }
    }

    /// <summary>
    /// 機能改善（タブのドラッグ並べ替え）: しきい値を超えて動いた時点でドラッグへ移行し、
    /// 挿入位置のインジケータ（ドロップ先タブの左端／末尾なら最後のタブの右端の縦線）を更新する。
    /// ドラッグ中はListBoxの通常のポインタ処理（選択の再評価等）と衝突しないようe.Handledを立てる。
    /// </summary>
    private void OnTabStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTab is null || _viewModel is null) return;

        var point = e.GetCurrentPoint(TabStrip);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ResetDragState();
            return;
        }

        if (!_isDragging)
        {
            var dx = point.Position.X - _dragStartPoint.X;
            var dy = point.Position.Y - _dragStartPoint.Y;
            if (dx * dx + dy * dy < DragThresholdPixels * DragThresholdPixels) return;
            _isDragging = true;
        }

        e.Handled = true;
        UpdateDragIndicator(point.Position.X);
    }

    /// <summary>ドラッグ中に離されたら、記録済みの挿入先へ実際に並べ替える。</summary>
    private void OnTabStripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragTab is null) return;

        if (_isDragging && _viewModel is not null && _dragTargetIndex >= 0)
        {
            _viewModel.ReorderTab(_dragTab, _dragTargetIndex);
            e.Handled = true;
        }

        ResetDragState();
    }

    /// <summary>
    /// ポインタのX座標（TabStrip基準）から挿入先インデックス（ドキュメントタブのみを数えた
    /// 0起点、ドラッグ開始前の並び順での位置）を決め、対応するタブ項目へ視覚的な
    /// インジケータ（Classes.dragInsertBefore/After、Editor.axamlのStyle参照）を付ける。
    /// </summary>
    private void UpdateDragIndicator(double pointerX)
    {
        var containers = TabStrip.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(c => c.DataContext is EditorTabViewModel { Kind: EditorTabKind.Document })
            .OrderBy(c => c.TranslatePoint(new Point(0, 0), TabStrip)?.X ?? 0)
            .ToList();

        ClearDragIndicator();
        if (containers.Count == 0) { _dragTargetIndex = -1; return; }

        var centers = containers
            .Select(c => (c.TranslatePoint(new Point(0, 0), TabStrip)?.X ?? 0) + c.Bounds.Width / 2)
            .ToList();
        var index = ResolveDropIndex(centers, pointerX);
        _dragTargetIndex = index;

        if (index < containers.Count)
        {
            containers[index].Classes.Add("dragInsertBefore");
            _dragIndicatorItem = containers[index];
        }
        else
        {
            containers[^1].Classes.Add("dragInsertAfter");
            _dragIndicatorItem = containers[^1];
        }
    }

    /// <summary>
    /// 各タブ中心のX座標一覧とポインタのX座標から、挿入先インデックスを求める
    /// （一般的なタブUIの規則: 中心より左側にかかったらそのタブの前へ挿入）。
    /// 実ドラッグ座標に依存する部分をここへ切り出し、UITestsから直接検証できるようにする
    /// （ProjectPane.ResolveDropTargetと同じ考え方）。
    /// </summary>
    public static int ResolveDropIndex(IReadOnlyList<double> centersX, double pointerX)
    {
        for (var i = 0; i < centersX.Count; i++)
        {
            if (pointerX < centersX[i]) return i;
        }
        return centersX.Count;
    }

    private void ClearDragIndicator()
    {
        if (_dragIndicatorItem is null) return;
        _dragIndicatorItem.Classes.Remove("dragInsertBefore");
        _dragIndicatorItem.Classes.Remove("dragInsertAfter");
        _dragIndicatorItem = null;
    }

    private void ResetDragState()
    {
        ClearDragIndicator();
        _dragTab = null;
        _isDragging = false;
        _dragTargetIndex = -1;
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
        if (_loadedTab is not null) _loadedTab.PropertyChanged -= OnLoadedTabPropertyChanged;
        _gitGutter.Dispose();
        _bridge.Dispose();
        _brackets.Dispose();
        _folding.Dispose();
    }
}
