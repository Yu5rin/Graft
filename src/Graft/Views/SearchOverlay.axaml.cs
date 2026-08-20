using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Graft.Editor;
using Graft.Themes;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// エディタ内検索・置換オーバーレイの見た目（4.4節）。<see cref="Attach"/>で対象の
/// <see cref="TextEditor"/>へ接続し、ヒットは<see cref="SearchHighlightRenderer"/>
/// （<see cref="IBackgroundRenderer"/>）で強調表示する。配置・Ctrl+F/Ctrl+Hからの
/// 呼び出しはEditorPane側から<see cref="OpenFind"/>/<see cref="OpenReplace"/>を
/// 呼ぶ形で行う。v2.0のWPF版からの移植（19章 L3）。
///
/// 「検索ハイライト機能：視認性を高める」（利用者要望、A＋B）の窓口も兼ねる。Aは本文内の
/// ヒット強調そのもの（<see cref="SearchHighlightRenderer"/>）、Bは縦スクロールバー上の
/// ヒット位置目印（<see cref="SearchMarkerBar"/>。<see cref="PushMarkerState"/>で押し込む。
/// 位置計算そのものは<see cref="SearchMarkerLayout"/>という純粋関数へ切り出してあり、
/// tests/Graft.Testsで検証している）。
/// </summary>
public partial class SearchOverlay : UserControl
{
    private SearchHighlightRenderer? _renderer;
    private TextEditor? _editor;
    private SearchOverlayViewModel? _viewModel;

    // B: 縦スクロールバー上のヒット位置目印（Themes/EditorScrollBar.axaml参照）。
    // ScrollBarのControlTemplate内に置かれたカスタムControlのため、SearchOverlay側からは
    // Editorの可視ツリーを辿って見つける必要がある（EnsureMarkerBar参照）。TextEditorの
    // ScrollViewer/ScrollBarはAvaloniaEdit自身のテンプレート適用（初回レイアウト後）で
    // 初めて実体化するため、Attach直後にはまだ見つからないことがある。MatchesChanged等の
    // 呼び出しのたびに未取得なら探し直す（自己修復。1回見つかれば以降はキャッシュを使う）。
    private SearchMarkerBar? _markerBar;

    public SearchOverlay()
    {
        InitializeComponent();

        // AvaloniaにはPreviewKeyDownが無く、トンネリング段階の購読はAddHandlerで行う。
        AddHandler(KeyDownEvent, OnTunnelKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // A-3: ハイライトの塗り・枠線ブラシはテーマが実際に切り替わった瞬間にだけ引き直す
        // （SearchHighlightRendererクラスコメント参照）。SearchOverlay自体はEditorPane.axaml内に
        // x:Name="Search"として置かれ、アプリ生存期間ずっと存在し続ける単一インスタンスのため、
        // 購読解除はしない（Themes/Platform/TitleBarThemeSync.cs等、同種の永続購読と同じ扱い）。
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    /// <summary>
    /// 検索の状態。<see cref="Attach"/> より前は未初期化のため参照できない。
    /// XAMLから引数なしで生成されるコントロールのため、UI機能（デバウンス用タイマー）は
    /// <see cref="Attach"/> で受け取る。
    /// </summary>
    public SearchOverlayViewModel ViewModel
        => _viewModel ?? throw new InvalidOperationException("Attachより前に参照されました。");

    /// <summary>
    /// 検索・置換オーバーレイが開いているかどうか。<see cref="ViewModel"/>と異なり、
    /// <see cref="Attach"/>より前でも例外を投げずfalseを返す（Markdownプレビュー機能:
    /// <see cref="ShellWindow"/>がEscapeの割り当て判断＝キューの破棄より検索を優先するかを
    /// 判定するために使う。ShellWindow.Keyboard.cs参照）。
    /// </summary>
    public bool IsOpen => _viewModel?.IsOpen ?? false;

    /// <summary>対象のエディタへ接続する。タブ切替のたびに呼び直す。</summary>
    public void Attach(TextEditor editor, Graft.Platform.IUiServices ui)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(ui);

        if (_viewModel is null)
        {
            _viewModel = new SearchOverlayViewModel(ui);
            DataContext = _viewModel;
            _renderer = new SearchHighlightRenderer(_viewModel);
            _viewModel.MatchesChanged += OnMatchesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (!ReferenceEquals(_editor, editor))
        {
            if (_editor is not null && _renderer is not null)
            {
                _editor.TextArea.TextView.BackgroundRenderers.Remove(_renderer);
            }
            _editor = editor;
            if (_renderer is not null) _editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);

            // B: マーカーバーは初回レイアウト後でないと可視ツリーに現れないため、Attach直後の
            // 同期的な探索では見つからないことが多い。Dispatcher.UIThread.Postで1フレーム
            // 遅らせてから最初の探索を試みる（見つからなくても実害はなく、後続のMatchesChanged/
            // IsOpen変化のたびにEnsureMarkerBarが再度探すため、最終的には解決する）。
            _markerBar = null;
            Dispatcher.UIThread.Post(PushMarkerState);
        }
        ViewModel.Attach(new Graft.Editor.AvaloniaEditTextAccess(editor));
    }

    /// <summary>Ctrl+F。選択中の文字列があれば検索欄へ引き継ぐ。</summary>
    public void OpenFind() => ViewModel.OpenFind(CurrentSelectionOrNull());

    /// <summary>Ctrl+H。</summary>
    public void OpenReplace() => ViewModel.OpenReplace(CurrentSelectionOrNull());

    private string? CurrentSelectionOrNull()
        => _editor is { SelectionLength: > 0 } ? _editor.SelectedText : null;

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _viewModel is null) return;
        ViewModel.Close();
        _editor?.Focus();
        e.Handled = true;
    }

    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (e.KeyModifiers == KeyModifiers.Shift) ViewModel.CommitAndFindPrevious();
        else ViewModel.CommitAndFindNext();
        e.Handled = true;
    }

    private void OnReplaceBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ViewModel.ReplaceCommand.Execute(null);
        e.Handled = true;
    }

    // 実機での指摘（Windows、折りたたみマーカーのホバー強調のちらつき）の調査で判明した
    // 落とし穴（Graft.Editor.TextViewRedrawのクラスコメント参照）: TextView.InvalidateLayerは
    // 実質TextView.InvalidateMeasure()であり、呼ぶたびに可視行が作り直され、
    // FoldingMarginが折りたたみマーカーを全部再生成してしまう（マウスがマーカー上に
    // あるとホバー強調がちらつく）。検索ヒットの強調表示もレイアウトの変化を伴わない
    // （ハイライト矩形の位置・色が変わるだけ）ため、測り直し無しに再描画する。
    private void OnMatchesChanged(object? sender, EventArgs e)
    {
        if (_editor is not null) TextViewRedraw.WithoutRemeasure(_editor.TextArea.TextView);
        PushMarkerState();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchOverlayViewModel.IsOpen))
        {
            // 閉じた瞬間も含めて目印の表示・非表示を切り替える（「検索オーバーレイが
            // 閉じているときは何も描かない」という依頼Bの要件）。開いた瞬間はCurrentIndex等が
            // まだ確定していないこともあるが、直後のRecomputeNow→MatchesChangedで
            // 改めて更新されるため実害はない。
            PushMarkerState();
        }

        if (e.PropertyName != nameof(SearchOverlayViewModel.IsOpen) || !ViewModel.IsOpen) return;
        Dispatcher.UIThread.Post(() =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        });
    }

    /// <summary>
    /// テーマが実際に切り替わった瞬間だけ、ハイライト用ブラシのキャッシュを破棄して
    /// 再描画する（A-3。Drawは可視行ごとに毎フレーム呼ばれるため、そこでTryFindResourceを
    /// 都度実行するとコストが増える。IndentGuideRenderer.OnThemeChangedと同じ設計）。
    /// マーカーバー側は自前でThemeManager.ThemeChangedを購読しキャッシュを持つため
    /// （Editor/SearchMarkerBar.cs参照）、ここから明示的に働きかける必要は無い。
    /// </summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _renderer?.InvalidateThemeCache();
        if (_editor is not null) TextViewRedraw.WithoutRemeasure(_editor.TextArea.TextView);
    }

    /// <summary>
    /// マーカーバー（Editor/SearchMarkerBar.cs）へ最新のヒット行番号・総行数・現在ヒット・
    /// 開閉状態を渡す。ヒットのオフセット→行番号への変換はAvaloniaEditの
    /// <c>TextDocument.GetLineByOffset</c>に依存するため（純ロジック側は行番号だけを扱う。
    /// SearchMarkerLayoutクラスコメント参照）、この変換はUI層であるここで行う。
    /// </summary>
    private void PushMarkerState()
    {
        if (_editor is null) return;
        if (_markerBar is null) _markerBar = _editor.GetVisualDescendants().OfType<SearchMarkerBar>().FirstOrDefault();
        if (_markerBar is null) return;

        var document = _editor.Document;
        if (document is null || _viewModel is null)
        {
            _markerBar.UpdateState(Array.Empty<int>(), 0, -1, false);
            return;
        }

        var matches = _viewModel.Matches;
        var lineNumbers = new int[matches.Count];
        for (var i = 0; i < matches.Count; i++)
        {
            // 検索中の文書差し替え等でオフセットが一瞬文書長を超える窓があり得るため、
            // GetLineByOffsetへ渡す前に安全側へクランプする（SearchMarkerLayout側は
            // 行番号の範囲外クランプまでは行うが、GetLineByOffset自体の例外は防げないため）。
            var offset = Math.Clamp(matches[i].Index, 0, document.TextLength);
            lineNumbers[i] = document.GetLineByOffset(offset).LineNumber;
        }

        _markerBar.UpdateState(lineNumbers, document.LineCount, _viewModel.CurrentIndex, _viewModel.IsOpen);
    }

    /// <summary>
    /// ヒット箇所を強調表示する背景レンダラ（A: 検索ハイライトの作り直し）。全ヒットは
    /// <c>SearchMatch</c>系、現在ヒットは<c>SearchCurrentMatch</c>系のトークン（9テーマ全てに
    /// 新設。Themes/*.axaml参照）を塗り＋枠線で使う。塗りは不透明色（9テーマとも本文色との
    /// WCAGコントラスト比4.5:1以上を実測済み。値の一覧はThemes/Dark.axamlの検索ハイライト
    /// 節コメント参照）で、半透明には頼らない。枠線は現在ヒットを太く（1.5px、通常は1.0px）
    /// することで、色が判別できなくても現在位置が分かるようにする（9.4「色だけに依存しない」
    /// 方針。件数表示（StatusText）と実選択範囲であることも併せて補っている）。
    ///
    /// 【ブラシ・ペンのキャッシュ（A-3）】
    /// <see cref="Draw"/>は可視行が変わるたびに（スクロール・ウィンドウリサイズ・入力のたび
    /// 等）繰り返し呼ばれる。<c>Application.Current.TryFindResource</c>は辞書探索を伴うため、
    /// 呼び出しのたびに4色ぶん解決するのは無駄なコストになる。テーマが実際に切り替わるまで
    /// 解決結果を保持し、<see cref="InvalidateThemeCache"/>（<see cref="SearchOverlay"/>が
    /// <c>ThemeManager.ThemeChanged</c>を購読して呼ぶ）が呼ばれたときにだけ次のDrawで
    /// 引き直す（IndentGuideRenderer・SyntaxHighlightBridge等、このコードベースの
    /// 既存IBackgroundRendererと同じ設計方針）。
    ///
    /// AvaloniaのBrush/PenにはFreeze()に相当するAPIが無いため呼び出さない（不変オブジェクトを
    /// 作って使い回すだけで、明示的な凍結操作自体が存在しない）。
    /// </summary>
    private sealed class SearchHighlightRenderer : IBackgroundRenderer
    {
        // 現在ヒットの枠線を通常より太くする（1.5px）ことで、遠目や色弱等で色の違いが
        // 判別しづらくても「太い枠＝現在位置」だと分かるようにする（9.4方針、クラスコメント参照）。
        private const double NormalBorderThickness = 1.0;
        private const double CurrentBorderThickness = 1.5;

        private readonly SearchOverlayViewModel _viewModel;

        private IBrush? _matchFill;
        private IPen? _matchBorder;
        private IBrush? _currentFill;
        private IPen? _currentBorder;
        private bool _brushesResolved;

        public SearchHighlightRenderer(SearchOverlayViewModel viewModel) => _viewModel = viewModel;

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!_viewModel.IsOpen || _viewModel.Matches.Count == 0) return;
            textView.EnsureVisualLines();

            EnsureBrushes();
            // 9テーマ全てに新設4色があることはThemeTests側（tests/Graft.UiTests）で機械的に
            // 検証している。ここでnullということはキーそのものが欠けている異常事態であり、
            // 誤った色へフォールバックするより「何も描かない」安全側を選ぶ
            // （IndentGuideRenderer.Drawと同じ流儀）。
            if (_matchFill is null || _currentFill is null) return;

            for (var i = 0; i < _viewModel.Matches.Count; i++)
            {
                var isCurrent = i == _viewModel.CurrentIndex;
                var fill = isCurrent ? _currentFill : _matchFill;
                var border = isCurrent ? _currentBorder : _matchBorder;
                var match = _viewModel.Matches[i];
                DrawMatch(textView, drawingContext, fill, border, match.Index, match.Length);
            }
        }

        /// <summary>テーマが切り替わった直後にSearchOverlayから呼ばれる。次回のDrawで
        /// 新しいテーマの色を引き直す（クラスコメントのA-3参照）。</summary>
        public void InvalidateThemeCache()
        {
            _brushesResolved = false;
            _matchFill = null;
            _matchBorder = null;
            _currentFill = null;
            _currentBorder = null;
        }

        private void EnsureBrushes()
        {
            if (_brushesResolved) return;
            _brushesResolved = true;

            var matchFill = ResolveBrush("SearchMatchColor");
            var matchBorderBrush = ResolveBrush("SearchMatchBorderColor");
            var currentFill = ResolveBrush("SearchCurrentMatchColor");
            var currentBorderBrush = ResolveBrush("SearchCurrentMatchBorderColor");

            if (matchFill is null || currentFill is null)
            {
                // 塗り色が1つでも欠けていたら、枠線だけ描いても意味が無いため両方諦める。
                _matchFill = null;
                _currentFill = null;
                return;
            }

            _matchFill = matchFill;
            _currentFill = currentFill;
            // 枠線色（Border系）は塗りほど必須ではない（無くても塗りだけで視認できる）ため、
            // 欠けていてもDraw自体は続行し、その場合は枠線なし（brush=nullのPen）として扱う。
            _matchBorder = matchBorderBrush is not null ? new Pen(matchBorderBrush, NormalBorderThickness) : null;
            _currentBorder = currentBorderBrush is not null ? new Pen(currentBorderBrush, CurrentBorderThickness) : null;
        }

        private static void DrawMatch(TextView textView, DrawingContext dc, IBrush fill, IPen? border, int offset, int length)
        {
            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            builder.AddSegment(textView, new TextSegment { StartOffset = offset, EndOffset = offset + length });
            var geometry = builder.CreateGeometry();
            if (geometry is not null) dc.DrawGeometry(fill, border, geometry);
        }

        private static IBrush? ResolveBrush(string colorKey)
        {
            if (Application.Current is not { } app
                || !app.TryFindResource(colorKey, null, out var value)
                || value is not Color color)
            {
                return null;
            }

            // 塗り・枠線とも不透明色をそのまま使う（半透明に頼らない。クラスコメント参照）。
            return new SolidColorBrush(color);
        }
    }
}
