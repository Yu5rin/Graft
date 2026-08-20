using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Graft.Themes;

namespace Graft.Editor;

/// <summary>
/// 検索ヒットの位置を縦スクロールバー上に小さな目印として描くカスタムControl
/// （利用者要望「検索ハイライト機能」B）。<see cref="Themes.EditorScrollBar"/>が本文エディタ
/// （<c>ae:TextEditor</c>）の縦ScrollBarにだけ適用するControlThemeのテンプレート内に置かれ、
/// <see cref="Views.SearchOverlay"/>が<see cref="UpdateState"/>で最新のヒット行番号・総行数・
/// 現在ヒット・開閉状態を押し込む（プッシュ型。ScrollBarのテンプレート内からは
/// SearchOverlayViewModelへ直接バインドできないため、コード側から明示的に橋渡しする）。
///
/// 【他画面のスクロールバーを巻き添えにしない】
/// このControl自体はThemes/EditorScrollBar.axamlの<c>ae|TextEditor ScrollBar[Orientation=
/// Vertical]</c>という絞り込んだセレクタのControlTemplate内にしか登場しないため、
/// 設定画面・プロジェクトペイン・履歴・Markdownプレビュー等、本文エディタ以外のScrollBarには
/// そもそもインスタンスが生成されない（Themes/Controls.Layout.axamlの{x:Type ScrollBar}
/// ControlThemeは書き換えていない。詳細はEditorScrollBar.axaml冒頭コメント参照）。
///
/// 【位置計算はAvalonia非依存の<see cref="SearchMarkerLayout"/>へ委譲】
/// 行番号→ピクセル位置の変換自体は<see cref="SearchMarkerLayout.Compute"/>という純粋関数
/// （tests/Graft.Testsで境界値を検証済み）が担い、このクラスは「その結果をどのブラシで
/// 塗るか」というAvalonia依存の描画だけを担当する。
///
/// 【ブラシのキャッシュ（A-3と同じ設計）】
/// <see cref="SearchMatchColor"/>系トークンは<see cref="Views.SearchOverlay"/>内の
/// <see cref="Views.SearchOverlay.SearchHighlightRenderer"/>相当の色だが、こちらは独立した
/// Controlのため、テーマ切り替えの検知も自前で<see cref="ThemeManager.ThemeChanged"/>を
/// 購読して行う（親であるSearchOverlay側からは働きかけない）。Loaded/Unloadedで購読の
/// 着脱を行う（<see cref="Views.CodeLineControl"/>と同じ作法）。
/// </summary>
public sealed class SearchMarkerBar : Control
{
    /// <summary>目印の左右の余白（px）。ScrollBarの全幅いっぱいに描くと角が丸まった
    /// スクロールバー自体の縁と重なって見づらいため、わずかに内側へ寄せる。</summary>
    private const double HorizontalMargin = 3.0;

    private IReadOnlyList<int> _matchLineNumbers = Array.Empty<int>();
    private int _totalLines;
    private int _currentIndex = -1;
    private bool _isActive;

    private IBrush? _matchBrush;
    private IBrush? _currentBrush;
    private bool _brushesResolved;

    public SearchMarkerBar()
    {
        // Track/Thumbより前面に描かれても（EditorScrollBar.axamlのTemplate内でTrackの後に
        // 配置する）ドラッグ・クリックの妨げにならないよう、ヒットテスト対象から外す
        // （Avaloniaのヒットテストは前面から背面へ走査し、IsHitTestVisible=falseのControlは
        // 自身をスキップして背面のControlへ処理を譲る。WPFのIsHitTestVisibleと同じ挙動）。
        IsHitTestVisible = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 最新の検索状態を反映する。<see cref="Views.SearchOverlay"/>がヒット変化・開閉変化の
    /// たびに呼ぶ。行番号は1起点（<c>TextDocument.GetLineByOffset(...).LineNumber</c>）で、
    /// <paramref name="currentIndex"/>と同じ添字で対応する。
    /// </summary>
    public void UpdateState(IReadOnlyList<int> matchLineNumbers, int totalLines, int currentIndex, bool isActive)
    {
        _matchLineNumbers = matchLineNumbers;
        _totalLines = totalLines;
        _currentIndex = currentIndex;
        _isActive = isActive;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // 「検索オーバーレイが閉じているときは何も描かない」（依頼Bの要件）。
        if (!_isActive || _matchLineNumbers.Count == 0) return;

        EnsureBrushes();
        if (_matchBrush is null || _currentBrush is null) return;

        var rects = SearchMarkerLayout.Compute(_matchLineNumbers, _totalLines, Bounds.Height, _currentIndex);
        if (rects.Count == 0) return;

        var width = Math.Max(0.0, Bounds.Width - HorizontalMargin * 2);
        if (width <= 0) return;

        foreach (var rect in rects)
        {
            var brush = rect.IsCurrent ? _currentBrush : _matchBrush;
            context.FillRectangle(brush, new Rect(HorizontalMargin, rect.Y, width, rect.Height));
        }
    }

    private void EnsureBrushes()
    {
        if (_brushesResolved) return;
        _brushesResolved = true;

        // SearchOverlay.SearchHighlightRendererと同じ色トークンを流用する（依頼B「目印の色も
        // 9テーマ分のリソースとして持たせる（SearchMatchColor系を流用してよい）」）。
        _matchBrush = ResolveBrush("SearchMatchColor");
        _currentBrush = ResolveBrush("SearchCurrentMatchColor");
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ThemeManager.ThemeChanged += OnThemeChanged;

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ThemeManager.ThemeChanged -= OnThemeChanged;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _brushesResolved = false;
        _matchBrush = null;
        _currentBrush = null;
        InvalidateVisual();
    }

    private static IBrush? ResolveBrush(string colorKey)
    {
        if (Application.Current is not { } app
            || !app.TryFindResource(colorKey, null, out var value)
            || value is not Color color)
        {
            return null;
        }

        return new SolidColorBrush(color);
    }
}
