using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Indentation;
using Graft.Core;
using Graft.Editor;
using Graft.Infra;
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
    // 検討書「インデントガイド（縦線）」。_foldingが持つFoldingManagerを読み取り専用で参照する
    // （FoldingSupportクラスコメント参照）ため、_foldingより後に構築する。
    private readonly IndentGuideRenderer _indentGuide;
    private readonly CompletionProvider _completion;
    // 課題#72（折り返し行のインデント継承）。AvaloniaEdit・Avalonia双方の制約から、
    // TextViewが握るTextFormatterを包む形で実現している（WrapIndentSupportのクラスコメント参照）。
    // Documentの差し替えで外れてしまうため、自身でTextView.DocumentChangedを購読して
    // 入れ直す（ApplyDocumentTab／ApplyEmptyTabの側に呼び出しは要らない）。
    private readonly WrapIndentSupport _wrapIndent;
    private readonly GitGutterProvider _gitGutter;
    // Markdownプレビュー機能（案B）: 編集モードでのMarkdown控えめ装飾。詳細はMarkdownInlineColorizer参照。
    private readonly MarkdownInlineColorizer _markdownColorizer = new();
    // 検討書「コード中のカラープレビュー」。統合はEditorPane.ColorPreview.cs参照。
    private readonly ColorPreviewElementGenerator _colorPreview = new();
    private readonly AvaloniaDialogService _dialogs = new();
    private EditorPaneViewModel? _viewModel;

    /// <summary>
    /// 課題1の<see cref="ShellWindow"/>.Loggerと同じ流儀（生成後にStartupCoordinator経由で
    /// 設定するnullableプロパティ）。<see cref="IndentGuideRenderer"/>の防御的catch
    /// （<see cref="IndentGuideRenderer.Logger"/>参照）がログを書けるよう、設定と同時に
    /// そのまま橋渡しするだけの薄いプロパティ。未設定でも通常の描画・編集は行える。
    /// </summary>
    public Logger? Logger
    {
        get => _indentGuide.Logger;
        set
        {
            _indentGuide.Logger = value;
            // 課題#72: リフレクション（private API）に依存する機能のため、取得に失敗して
            // 静かに無効化された事実をログへ残せるようにする（WrapIndentSupport.Logger参照）。
            _wrapIndent.Logger = value;
        }
    }

    // 現在Editorに読み込まれている（＝Documentを共有している）タブ。切替前にこのタブへ
    // スクロール位置等を退避してから、次のタブへ切り替える。
    private EditorTabViewModel? _loadedTab;

    // Markdownプレビュー機能: 現在プレビューが監視しているDocument（.mdタブが読み込まれている
    // 間のみ非null）。パッチ適用後の再読込等、編集操作を経ずにDocumentの内容が変わった場合でも
    // プレビュー表示を追従させるために購読する（利用者指示の追加要件「パッチ適用後のタブ再読込
    // でもプレビューのままであること」）。タブ切替のたびにApplyActiveTabで張り直す。
    private TextDocument? _markdownWatchedDocument;

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
        // 細かいユーザビリティ改善1: 選択文字数のステータスバー表示用。TextArea.SelectionChangedは
        // ドラッグ選択中も連続発火するが、Editor.SelectionLengthはAvalonEdit内部で維持済みの
        // O(1)プロパティを読むだけ（文書を走査しない）なので、既存のCaret.PositionChanged
        // （同様に非デバウンスで購読済み）と同じ考え方でそのまま購読してよいと判断した。
        Editor.TextArea.SelectionChanged += OnSelectionChanged;

        _bridge = new SyntaxHighlightBridge(Editor);
        Editor.TextArea.TextView.LineTransformers.Add(_bridge);
        _bridge.Attach(Editor.Document, string.Empty, syntaxEnabled: false);
        // Markdownプレビュー機能（案B）。_bridgeの後ろに積む＝色付けの後から書体・背景を
        // 上書きする順で適用される（見出しの太字等がシンタックスハイライトの色を消さない）。
        Editor.TextArea.TextView.LineTransformers.Add(_markdownColorizer);

        // 検討書「コード中のカラープレビュー」。VisualLineElementGeneratorとして登録する理由は
        // ColorPreviewElementGeneratorのクラスコメント参照。
        Editor.TextArea.TextView.ElementGenerators.Add(_colorPreview);
        _colorPreview.SwatchClicked += OnColorSwatchClicked;

        _brackets = new BracketSupport(Editor);
        _folding = new FoldingSupport(Editor);
        _indentGuide = new IndentGuideRenderer(Editor, _folding);
        _completion = new CompletionProvider(Editor);
        // 課題#72。LineTransformersへ番人を差し込むため、_bridge・_markdownColorizerの登録より
        // 後（順序は結果に影響しない。WrapIndentVisualLineTrackerのクラスコメント参照）に構築する。
        _wrapIndent = new WrapIndentSupport(Editor);

        // 4.7 Gitガター。行番号の左隣に置き、HEADとの差分を色帯で示す。GitGutterProviderの
        // カーソル（矢印固定）はコンストラクタ内で設定済み（実機での指摘2、クラスコメント参照）。
        _gitGutter = new GitGutterProvider(Editor, new Graft.Features.GitIntegration());
        Editor.TextArea.LeftMargins.Insert(0, _gitGutter);

        // 実機での指摘2（Windows）: 折りたたみマージン・Gitガターと同じ理由で、AvaloniaEdit標準の
        // LineNumberMarginもTextAreaからIビームを継承したままになる（MarkerOnlyFoldingMarginの
        // クラスコメント参照）。GitGutterProviderと違い、LineNumberMarginは
        // ShowLineNumbers="{Binding ShowLineNumbers}"（利用者が設定でいつでも切り替えられる）が
        // 変化するたびAvaloniaEdit内部（TextEditor.OnShowLineNumbersChanged、非公開・非virtual）が
        // 新しいインスタンスを作り直すため、生成直後を名指しで一度だけ直すことができない。
        // LeftMargins自体はCollectionChangedを発行する（AbstractMargin.RemoveFromTextView/
        // AddToTextViewの配線に使われているのと同じ仕組み）ので、ここへ実際に追加された
        // LineNumberMarginを見つけるたび矢印を設定する形で、再生成にも追従できるようにする。
        Editor.TextArea.LeftMargins.CollectionChanged += OnLeftMarginsChanged;
        ApplyLineNumberMarginCursor(); // 既にShowLineNumbers=trueで挿入済みの場合の初回分。

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
        // Markdownプレビュー機能: プレビュー本文のダブルクリックで編集モードへ切り替え、
        // ダブルクリックした段落に対応する行へカーソルを置く（利用者指示の追加要件3）。
        MarkdownPreviewHost.BlockDoubleClicked += OnMarkdownBlockDoubleClicked;
        // Markdownプレビュー機能: テーマ切替時の再描画（実機検証で発覚した不具合の対応。
        // OnThemeChangedForMarkdownPreviewのコメント参照）。
        Graft.Themes.ThemeManager.ThemeChanged += OnThemeChangedForMarkdownPreview;

        // 機能改善（タブが増えたときに到達できない問題）: スクロールボタン・タブ一覧
        // ドロップダウン・ホイールスクロールの初期化（EditorPane.TabStrip.cs）。
        InitializeTabStrip();

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.TabSaved -= OnTabSaved;
            _viewModel.FoldCommandRequested -= OnFoldCommandRequested;
        }
        _viewModel = DataContext as EditorPaneViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.TabSaved += OnTabSaved;
            _viewModel.FoldCommandRequested += OnFoldCommandRequested;
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
        else if (e.PropertyName == nameof(EditorPaneViewModel.IndentGuideMode))
        {
            // 検討書の必須要件: 3モードの切り替えはタブを切り替えなくても即時反映する
            // （EditorPaneViewModel.IndentGuideModeのXMLコメント参照）。
            ApplyIndentGuideModeOption();
        }
    }

    /// <summary>アクティブタブの切替。Documentの差し替え・言語別ハイライトの再接続・
    /// インデント設定の反映・スクロール位置/カーソル/選択範囲の退避・復元をまとめて行う。</summary>
    private void ApplyActiveTab(EditorTabViewModel? tab)
    {
        SaveViewStateInto(_loadedTab);
        if (_loadedTab is not null) _loadedTab.PropertyChanged -= OnLoadedTabPropertyChanged;
        DetachMarkdownDocumentWatch();
        _loadedTab = tab;
        if (tab is not null) tab.PropertyChanged += OnLoadedTabPropertyChanged;

        // 機能改善: 選択中のタブが常に見えるようにする。Ctrl+Tab・クイックオープン・タブ一覧
        // ドロップダウン・マウスクリックなど、ActiveTabが変わる経路はすべてここへ集約される
        // （EditorPaneViewModel.ActiveTabのsetter→OnViewModelPropertyChanged→ApplyActiveTab）ため、
        // ここ1箇所からの呼び出しで網羅できる（EditorPane.TabStrip.cs）。
        ScheduleEnsureTabVisible(tab);

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
        // 課題#82: Editor.Documentを差し替える前に、古い文書に紐づいたFoldingManagerを
        // 先回りしてuninstallする（FoldingSupport.PrepareForDocumentSwapのクラスコメント
        // 【課題#82】節参照）。TextEditor.DocumentChanged（次のAttach呼び出しの前提となる
        // 同期イベント）は代入の中で一番最後に発火するため、それより手前でのAvaloniaEdit内部の
        // 再入（Caretリセットに伴うPositionChanged経由。実機ではWindowsのIME・フォーカス変更の
        // メッセージポンプ入れ子呼び出しが典型例）に対しては無力だった。先に外しておけば
        // その再入区間のどの瞬間でもFoldingManagerが存在しないため、Invalid documentの
        // 温床そのものが無くなる。
        _folding.PrepareForDocumentSwap();
        // 課題#72: この代入の「中で最後に」発火するTextView.DocumentChangedを
        // WrapIndentSupport自身が購読しており、素のTextFormatterで上書きされた直後に
        // 自動で入れ直す。上のPrepareForDocumentSwap（代入の"前"）とは働く時点が
        // 異なるため、順序が衝突することはない。
        Editor.Document = tab.Session.Document;
        ApplyWhitespaceOption();
        ApplyWordWrapOption();
        ApplyIndentGuideModeOption();
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
        _markdownColorizer.SetEnabled(tab.IsMarkdownFile);
        ApplyColorPreviewOption();
        if (_viewModel is not null) Search.Attach(Editor, _viewModel.Ui);
        ApplyGitGutter(tab);

        if (tab.IsMarkdownFile)
        {
            _markdownWatchedDocument = tab.Session.Document;
            _markdownWatchedDocument.Changed += OnDocumentChangedForMarkdownPreview;
        }

        RestoreViewStateFrom(tab);
        ApplyMarkdownPreviewMode();
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
    ///
    /// 【不具合修正1: ファイルを開いた直後に1行目が半行分切れる（Windows実機報告）】
    /// 「コードを開いた時に1行目が表示しきれていない（カーソルは1:1なのに、表示だけ半行ぶん
    /// 下へスクロールした状態になる）」という報告があった。ヘッドレステスト（Xvfb不要、
    /// Avalonia.Headless+Skiaで実フォント計測込みで再現できた。EditorOpenScrollPositionTests
    /// 参照）で調査したところ、原因は本メソッド側のロジックではなく、AvaloniaのScrollViewer/
    /// ScrollContentPresenter側にある一過性の競合だと判明した:
    ///
    ///   1. 本メソッドの<c>MoveCaretTo</c>（同期呼び出し）は<c>Editor.ScrollToLine(1)</c>で
    ///      正しくVerticalOffset=0を設定する（この時点では問題ない）。
    ///   2. しかしこのEditorPane方式は「単一のEditor/ScrollViewerを全タブで使い回し、
    ///      Documentだけ差し替える」（クラス冒頭のコメント参照）ため、直後に走る
    ///      レイアウトパス（<c>TextView.MeasureOverride</c>）が新しい文書の実際の行数から
    ///      Extent（コンテンツ全体の高さ）を再計算する。ExtentがOffsetより先に
    ///      ScrollContentPresenter→ScrollViewerへ伝播する実装（Avalonia側
    ///      <c>ScrollContentPresenter.OnPropertyChanged</c>のExtent分岐）になっており、
    ///      その伝播の最中に走る<c>CoerceValue(OffsetProperty)</c>が「まだ0へ更新される前の
    ///      （直前のタブの）Offsetの生値」を新しいExtentに対して再クランプしてしまう。
    ///      結果としてOffset.Yが行の高さの半分程度（実測: 既定フォントで8.775px、
    ///      行高17.55pxのちょうど半分）だけ動いてしまい、1行目の上半分が画面外へ出る。
    ///   3. これはAvalonia本体のScrollViewer実装の内部的な伝播順序に起因する一過性の
    ///      ズレであり、レイアウトが完全に落ち着いた後（<c>DispatcherPriority.Background</c>、
    ///      レイアウト/描画より後に走る）に同じ位置へ改めてスクロールし直せば消える
    ///      （実測で確認済み）。
    ///
    /// このため、スクロール位置の再適用は<see cref="EditorTabViewModel.HasViewState"/>の
    /// 真偽に関わらず必ずレイアウト確定後まで遅延させて行う。<c>HasViewState</c>がtrue
    /// （＝一度でもこのタブを離れ、退避済みの位置がある場合）なら退避した正確なオフセットへ、
    /// falseの場合（新規に開いたタブ）は<c>MoveCaretTo</c>をもう一度呼んでキャレット行へ
    /// 改めてスクロールし直すことで、上記のズレを打ち消す。
    ///
    /// 【不具合修正2（副次的に見つかった既存不具合）: タブを離れて戻ったときの正確なスクロール
    /// 位置復元が、実は無条件に無効だった】
    /// 上記調査の過程で、<c>AvaloniaEdit.TextEditor.ScrollToVerticalOffset</c>/
    /// <c>ScrollToHorizontalOffset</c>（本プロジェクトが参照するAvaloniaEdit 11.1.0）が
    /// <c>ApplyTemplate()</c>を呼ぶだけで実際には何もしない未実装のメソッドであることが
    /// 判明した（ILSpyでの逆コンパイル・ヘッドレステストでの実測の両方で確認済み）。
    /// つまり本メソッドが以前これらを呼んでいた「HasViewStateがtrueのときの正確な位置復元」は、
    /// タブ切替直後にたまたま<c>MoveCaretTo</c>のキャレット行スクロールで近い位置に来ていた
    /// 場合を除き、実質的に機能していなかった（回帰テストが存在しなかったため気付かれずに
    /// 残っていたと見られる）。
    /// 代わりに、AvaloniaEdit内部の<c>TextView</c>が実装する<c>Avalonia.Controls.Primitives.
    /// IScrollable</c>（<c>TextEditor.ScrollTo</c>が最終的に<c>ScrollViewer.Offset</c>へ代入する
    /// 経路と同じ実体へ到達する）を、公開されている<c>Editor.TextArea</c>経由で直接操作することで
    /// 実際にオフセットを反映させる（ヘッドレステストで、この経路のみが確実に効くことを
    /// 実測確認済み）。
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

        var hasViewState = tab.HasViewState;
        var x = tab.ScrollOffsetX;
        var y = tab.ScrollOffsetY;
        // Markdownプレビュー機能との競合対策（防御的措置）: この時点でのプレビュー/編集モードを
        // 覚えておく。タブ切替直後（本メソッドの呼び出し元はApplyDocumentTab）はまだ「タブを
        // 離れる前のモード」のままだが、この遅延補正が発火するまでの間に、利用者がプレビュー
        // 本文のダブルクリック・切替ボタン・Escでプレビュー⇔編集を切り替える可能性がある
        // （EditorPane.MarkdownPreview.csのApplyMarkdownPreviewMode参照）。その切替は
        // MoveCaretToで正しい新しいスクロール位置へ同期的に合わせ直すが、その直後にこの遅延
        // 補正がhasViewStateの古いx/y（タブを離れる前・別モードだった時点のオフセット）で
        // 上書きしてしまうと、切替直後に合わせたはずの位置が古い位置へ巻き戻ってしまう可能性が
        // ある。モードが変わっていたら「タブは変わっていないが状況が変わった」とみなし、
        // ApplyMarkdownPreviewMode側が既に合わせた位置を信頼してこの遅延補正は何もしない。
        // 【検証メモ】ヘッドレステストでこの順序（プレビュー→編集切替の直後に遅延補正が発火）を
        // 意図的に再現しようとしたところ、切替のヒットテストに必要なレイアウト確定
        // （CaptureRenderedFrame）自体がBackground優先度のジョブも合わせて処理してしまうため、
        // 「エディタが見えている状態で古い位置に上書きされる」順序をテスト内では作れなかった
        // （EditorOpenScrollPositionTests.タブ再訪後のダブルクリックで正しい行の編集モードへ
        // 切り替わる のコメント参照）。実機の連続レンダリングでも同様の理由でこの順序にはならない
        // と考えられるが、コードを読んだだけでは競合し得る形になっていたため、安全側として
        // このガードは残す。
        var showMarkdownPreviewAtSchedule = tab.ShowMarkdownPreview;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_loadedTab, tab)) return; // 遅延実行中に別タブへ切り替わっていたら何もしない
            if (tab.ShowMarkdownPreview != showMarkdownPreviewAtSchedule) return; // 同上: プレビュー⇔編集切替と競合させない
            if (hasViewState)
            {
                // 不具合修正2: Editor.ScrollToVerticalOffset/ScrollToHorizontalOffsetは
                // AvaloniaEdit 11.1.0では何もしないため使わず、IScrollable.Offsetを
                // TextArea経由で直接設定する。
                var scrollable = (IScrollable)Editor.TextArea;
                scrollable.Offset = new Vector(x, y);
            }
            else
            {
                // 新規に開いたタブ: レイアウト確定に伴うAvalonia側のExtent再計算とOffset
                // クランプの競合（不具合修正1）でずれたスクロール位置を、改めてスクロールし
                // 直すことで打ち消す。ここで打ち消したいのはスクロール位置だけであり、
                // キャレット位置・選択範囲は絶対に触れてはならない。この遅延コールバックは
                // Background優先度のため、発火するまでの間に利用者（またはテストコード）が
                // 既に選択操作等を行っている可能性があり、MoveCaretTo（Caret.Line/Columnを
                // 直接代入する＝選択範囲を消してしまう）をここで呼ぶと、開いた直後に素早く
                // 選択して右クリックメニューを使うような操作の選択範囲を消してしまう回帰と
                // なる（EditorSelectionPromptTestsで実際に検出した）。そのためCaretには触れず、
                // その時点のキャレット行（Editor.TextArea.Caret.Line。利用者が既に動かして
                // いればその行）へ表示だけを合わせ直すScrollToLineに留める。
                Editor.ScrollToLine(Editor.TextArea.Caret.Line);
            }
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
    /// 検討書「インデントガイド（縦線）」。設定画面での変更は
    /// <see cref="EditorPaneViewModel.IndentGuideMode"/>のPropertyChanged経由で即座にここへ
    /// 届く（ShowWhitespace等と同じ方針だが、こちらはタブ切替を待たず反映する必要があるため
    /// 専用の通知を使う。OnViewModelPropertyChanged参照）。
    /// </summary>
    private void ApplyIndentGuideModeOption()
        => _indentGuide.SetMode(IndentGuideModeParser.Parse(_viewModel?.IndentGuideMode));

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
        else if (e.PropertyName == nameof(EditorTabViewModel.ShowMarkdownPreview)) ApplyMarkdownPreviewMode();
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

    /// <summary>細かいユーザビリティ改善1: 選択範囲が変わるたびにステータスバー表示用の文字数を更新する。</summary>
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_viewModel?.ActiveTab is not { } tab) return;
        tab.SelectionStart = Editor.SelectionStart;
        tab.SelectionLength = Editor.SelectionLength;
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
        if (TryHandleMarkdownPreviewEscape(e, mods)) return;
        if (TryHandleMarkdownEditing(e, mods)) return;
        if (TryHandleSearchShortcuts(e, mods)) return;
        if (TryHandleLineEditShortcuts(e, mods)) return;
        if (TryHandleFoldShortcuts(e, mods)) return;

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

    /// <summary>
    /// 検討書「Markdownの編集支援」: リスト/引用のEnter継続・脱出、表のTab/Shift+Tab移動。
    /// <c>.md</c>タブの**編集モード**（Markdownプレビュー表示中でない）のときだけ働く
    /// （<see cref="MarkdownEditingSupport"/>クラスコメント参照）。プレビュー表示中は
    /// <c>Editor.IsVisible = false</c>でキー入力がそもそも届かないが、ここでも明示的に
    /// ガードして二重に保証する。<c>.md</c>以外のファイルではTab/Enterの意味を一切変えない
    /// （<see cref="EditorTabViewModel.IsMarkdownFile"/>がfalseの時点で即falseを返す）。
    /// </summary>
    private bool TryHandleMarkdownEditing(KeyEventArgs e, KeyModifiers mods)
    {
        if (_viewModel?.ActiveTab is not { Kind: EditorTabKind.Document, IsMarkdownFile: true, ShowMarkdownPreview: false }) return false;
        if (_completion.IsOpen) return false; // 補完候補選択中のTab/Enterを奪わない。

        if (mods == KeyModifiers.None && e.Key == Key.Enter)
        {
            return e.Handled = MarkdownEditingSupport.HandleEnter(Editor);
        }
        if (mods == KeyModifiers.None && e.Key == Key.Tab)
        {
            return e.Handled = MarkdownEditingSupport.HandleTab(Editor, shift: false);
        }
        if (mods == KeyModifiers.Shift && e.Key == Key.Tab)
        {
            return e.Handled = MarkdownEditingSupport.HandleTab(Editor, shift: true);
        }
        return false;
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

    /// <summary>
    /// 検討書「折りたたみの機能追加」(b) 折りたたみコマンドのショートカット。
    /// Ctrl+Shift+1〜5（レベル1〜5）・Ctrl+Shift+/（すべてのコメントブロック）・
    /// Ctrl+Shift+[（再帰的）。既存のCtrl+Shift+K（現在行削除、上のTryHandleLineEditShortcuts）
    /// とは異なるキーのため衝突しない。実行は<see cref="EditorPaneViewModel"/>側のICommand
    /// （コマンドパレットと共通の経路。EditorPaneViewModel.Folding.cs参照）へそのまま委譲する。
    /// </summary>
    private bool TryHandleFoldShortcuts(KeyEventArgs e, KeyModifiers mods)
    {
        if (_viewModel?.ActiveTab is not { Kind: EditorTabKind.Document }) return false;
        if (mods != (KeyModifiers.Control | KeyModifiers.Shift)) return false;

        switch (e.Key)
        {
            case Key.D1: _viewModel.FoldLevel1Command.Execute(null); return e.Handled = true;
            case Key.D2: _viewModel.FoldLevel2Command.Execute(null); return e.Handled = true;
            case Key.D3: _viewModel.FoldLevel3Command.Execute(null); return e.Handled = true;
            case Key.D4: _viewModel.FoldLevel4Command.Execute(null); return e.Handled = true;
            case Key.D5: _viewModel.FoldLevel5Command.Execute(null); return e.Handled = true;
            case Key.OemQuestion or Key.Divide: _viewModel.FoldAllCommentsCommand.Execute(null); return e.Handled = true;
            case Key.OemOpenBrackets: _viewModel.FoldRecursiveCommand.Execute(null); return e.Handled = true;
            default: return false;
        }
    }

    /// <summary>
    /// <see cref="EditorPaneViewModel.FoldCommandRequested"/>の受け手。コマンドパレット・
    /// 上のショートカットのどちらから来ても、ここから<see cref="FoldingSupport"/>を1回だけ呼ぶ
    /// （EditorPaneViewModel.Folding.csのクラスコメント参照）。
    /// </summary>
    private void OnFoldCommandRequested(object? sender, FoldCommandKind kind)
    {
        if (_viewModel?.ActiveTab is not { Kind: EditorTabKind.Document }) return;

        switch (kind)
        {
            case FoldCommandKind.Level1: _folding.FoldToLevel(1); break;
            case FoldCommandKind.Level2: _folding.FoldToLevel(2); break;
            case FoldCommandKind.Level3: _folding.FoldToLevel(3); break;
            case FoldCommandKind.Level4: _folding.FoldToLevel(4); break;
            case FoldCommandKind.Level5: _folding.FoldToLevel(5); break;
            case FoldCommandKind.AllComments: _folding.FoldAllComments(); break;
            case FoldCommandKind.Recursive: _folding.FoldRecursiveAt(Editor.CaretOffset); break;
        }
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

    /// <summary>
    /// 実機での指摘2（Windows）: <c>TextArea.LeftMargins</c>へ新しく追加された
    /// <see cref="LineNumberMargin"/>を見つけて矢印カーソルを設定する。<c>ShowLineNumbers</c>の
    /// 切り替えでAvaloniaEdit内部（非公開の<c>TextEditor.OnShowLineNumbersChanged</c>）が
    /// <see cref="LineNumberMargin"/>を作り直すたびに、この購読（コンストラクタ参照）経由で
    /// 呼ばれる。理由の詳細は<see cref="Graft.Editor.MarkerOnlyFoldingMargin"/>のクラスコメント参照。
    /// </summary>
    private void OnLeftMarginsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null) return;
        foreach (var item in e.NewItems)
        {
            if (item is LineNumberMargin margin) margin.Cursor = new Cursor(StandardCursorType.Arrow);
        }
    }

    /// <summary>コンストラクタ時点で既に<c>ShowLineNumbers=true</c>で挿入済みの分の初回反映。
    /// 以降の作り直しは<see cref="OnLeftMarginsChanged"/>が拾う。</summary>
    private void ApplyLineNumberMarginCursor()
    {
        foreach (var margin in Editor.TextArea.LeftMargins.OfType<LineNumberMargin>())
        {
            margin.Cursor = new Cursor(StandardCursorType.Arrow);
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        Editor.TextArea.LeftMargins.CollectionChanged -= OnLeftMarginsChanged;
        UninitializeTabStrip();
        Editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        Editor.TextArea.SelectionChanged -= OnSelectionChanged;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.TabSaved -= OnTabSaved;
            _viewModel.FoldCommandRequested -= OnFoldCommandRequested;
        }
        if (_loadedTab is not null) _loadedTab.PropertyChanged -= OnLoadedTabPropertyChanged;
        DetachMarkdownDocumentWatch();
        MarkdownPreviewHost.BlockDoubleClicked -= OnMarkdownBlockDoubleClicked;
        Graft.Themes.ThemeManager.ThemeChanged -= OnThemeChangedForMarkdownPreview;
        _gitGutter.Dispose();
        _bridge.Dispose();
        _brackets.Dispose();
        // 検討書「インデントガイド（縦線）」: _foldingのFoldingManager/HoveredFoldingChangedを
        // 参照しているため、_folding.Dispose()より前に外す。
        _indentGuide.Dispose();
        _folding.Dispose();
    }
}
