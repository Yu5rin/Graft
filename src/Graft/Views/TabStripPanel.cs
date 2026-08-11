using Avalonia;
using Avalonia.Controls;

namespace Graft.Views;

/// <summary>
/// エディタのタブ見出し（EditorPane.axaml の TabStrip、Themes/Editor.axaml の
/// EditorTabStripTheme が ItemsPanel として使う）専用のレイアウトパネル。
///
/// 「タブが増えたときに目的のタブへ到達できない」問題の依頼1・2に対応する:
///   1. まずタブ幅を縮めて収める（<see cref="ComputeWidths"/>）。
///   2. それでも収まらなければ、自前のOffsetで水平スクロールする
///      （スクロールボタン・マウスホイールはEditorPane.axaml.csから<see cref="Offset"/>と
///      <see cref="EnsureVisible"/>を操作する）。
///
/// 【なぜListBox標準のScrollViewerに乗らないか】
/// ScrollViewerは「中身が欲しいだけの幅を無制限に取れる」ことが前提（延びた分をスクロールで
/// 見せる）ため、子（このパネル）のMeasureOverrideには常に無限幅が渡ってしまい、
/// 「実際に使える幅に対して縮めるべきかどうか」を判定できない。そのため
/// EditorTabStripThemeではScrollViewer.HorizontalScrollBarVisibility="Disabled"にして
/// ScrollViewer自身の水平スクロール機能を切り、代わりにこのパネルが実際のビューポート幅
/// （MeasureOverrideのavailableSize.Width）を受け取ったうえで、縮小と水平オフセットの
/// 両方を自前で担当する。
///
/// 【幅の決め方（依頼1）】
/// 全タブの「本来の幅」（内容に応じた自然な幅）の合計が使える幅に収まるなら、そのまま
/// 自然な幅で並べる（タブが少ないときは今までどおりファイル名の長さに応じた幅になる）。
/// 収まらない場合はブラウザのタブと同じ「均等縮小」で縮める: まだ幅が確定していない
/// タブの間で残り幅を均等に割った額（target）以下の自然な幅を持つタブ（＝すでに短い
/// ファイル名のタブ）から順に、その自然な幅のまま確定させていく。確定するタブが
/// 無くなった時点で、残った（＝長いファイル名の）タブ全員に残り幅を均等に割った幅を割り当てる
/// （その額がMinTabWidthを下回るならMinTabWidthで床を打つ）。これにより「短いタブは
/// 縮めすぎず、長いタブから優先的に縮む」動きになる（CSS flexboxのshrinkや主要ブラウザの
/// タブ幅計算と同じ考え方）。全タブがMinTabWidthまで縮んでもなお収まらない場合だけ、
/// あふれた分を依頼2の水平スクロールに委ねる。
/// </summary>
public sealed class TabStripPanel : Panel
{
    /// <summary>
    /// タブの最小幅（依頼1）。実機（Xvfb）でタブを15個ほど開いた状態を実際に描画して決めた:
    /// これより狭いとタイトルの省略記号（…）表示と閉じるボタンが重なり始め、
    /// ファイル名の判別も閉じるボタンの押下もしづらくなる。この値ならファイル名の先頭数文字
    /// （例:「data-loa…」）と閉じるボタンの両方が実際に見分けられることを確認済み。
    /// </summary>
    public const double MinTabWidth = 96;

    /// <summary>スクロールボタン1回・マウスホイール1目盛りあたりの移動量（依頼2/4）。
    /// タブ約1枚分がおおよそ動く量として選んだ。</summary>
    public const double ScrollStep = 120;

    private readonly Dictionary<Control, Rect> _naturalRects = new();
    private double _offset;
    private double _contentWidth;
    private double _viewportWidth;

    public TabStripPanel()
    {
        ClipToBounds = true;
    }

    /// <summary>現在の水平スクロールオフセット（左端からのピクセル数）。範囲外は自動的に
    /// [0, MaxOffset]へ丸める。</summary>
    public double Offset
    {
        get => _offset;
        set
        {
            var clamped = Math.Clamp(value, 0, MaxOffset);
            if (Math.Abs(clamped - _offset) < 0.01) return;
            _offset = clamped;
            InvalidateArrange();
            ScrollStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>全タブを並べたときの合計幅（依頼1で縮小した後の幅の合計）。</summary>
    public double ContentWidth => _contentWidth;

    /// <summary>現在の表示可能幅（TabStripのビューポート幅）。</summary>
    public double ViewportWidth => _viewportWidth;

    /// <summary>Offsetが取りうる最大値。あふれが無ければ0。</summary>
    public double MaxOffset => Math.Max(0, _contentWidth - _viewportWidth);

    /// <summary>依頼2: 最小幅まで縮めてもなお全タブが収まりきらないかどうか。
    /// スクロールボタンの表示条件（EditorPane.axaml.cs）に使う。</summary>
    public bool HasOverflow => _contentWidth - _viewportWidth > 0.5;

    /// <summary>Offset・ContentWidth・ViewportWidthのいずれかが変わるたびに発火する。
    /// EditorPane.axaml.csがスクロールボタンのIsEnabled/IsVisibleを更新するために購読する。</summary>
    public event EventHandler? ScrollStateChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Children.Count;
        var naturalWidths = new double[count];

        // 1巡目: 各タブの「本来の幅」を無制限の幅で測る（縮めるかどうかの判定に使うだけ）。
        for (var i = 0; i < count; i++)
        {
            Children[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
            naturalWidths[i] = Children[i].DesiredSize.Width;
        }

        var naturalSum = 0.0;
        foreach (var w in naturalWidths) naturalSum += w;
        // ScrollViewerの水平スクロールを無効化しているため、availableSize.Widthは
        // 実際のビューポート幅（有限値）のはず。念のため無限大が来た場合
        // （例: このパネル単体をテストで直接measureする場合）は自然な合計幅を使う。
        var viewportWidth = double.IsInfinity(availableSize.Width) ? naturalSum : availableSize.Width;

        var widths = ComputeWidths(naturalWidths, viewportWidth, MinTabWidth);

        // 2巡目: 実際に割り当てる幅で測り直す。MeasureとArrangeで渡す幅が食い違うと
        // （1巡目のまま無制限幅の結果をArrangeだけ縮めると）、タイトルの省略記号
        // （TextTrimming="CharacterEllipsis"、EditorPane.axaml）が正しく計算されない
        // ことをXvfbでの実機確認で発見した（「…」が付かずに文字が唐突に切れていた）。
        // レイアウト契約としても、Measureで報告した幅と異なる幅でArrangeするのは本来
        // 避けるべきため、縮めた幅で1回測り直す。
        var maxHeight = 0.0;
        for (var i = 0; i < count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(widths[i], availableSize.Height));
            maxHeight = Math.Max(maxHeight, child.DesiredSize.Height);
        }

        _naturalRects.Clear();
        var x = 0.0;
        for (var i = 0; i < count; i++)
        {
            _naturalRects[Children[i]] = new Rect(x, 0, widths[i], maxHeight);
            x += widths[i];
        }

        _contentWidth = x;
        _viewportWidth = viewportWidth;
        if (_offset > MaxOffset) _offset = Math.Max(0, MaxOffset);

        return new Size(double.IsInfinity(availableSize.Width) ? x : availableSize.Width, maxHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewportWidth = finalSize.Width;
        if (_offset > MaxOffset) _offset = Math.Max(0, MaxOffset);

        foreach (var child in Children)
        {
            if (_naturalRects.TryGetValue(child, out var rect))
            {
                child.Arrange(new Rect(rect.X - _offset, rect.Y, rect.Width, Math.Max(rect.Height, finalSize.Height)));
            }
        }

        ScrollStateChanged?.Invoke(this, EventArgs.Empty);
        return finalSize;
    }

    /// <summary>
    /// 選択中のタブが常に見えるようにする（依頼の「忘れられがちな」注意点）。指定した子
    /// （タブのListBoxItem）が現在の表示範囲から左右いずれかにはみ出していれば、はみ出しが
    /// 無くなる必要最小限の量だけOffsetを動かす。既に全体が見えている場合は何もしない
    /// （戻り値false）。レイアウトがまだ一度も走っておらずこの子の位置が分からない場合も
    /// falseを返す（呼び出し側は次のレイアウト後に再試行する。EditorPane.axaml.cs参照）。
    /// </summary>
    public bool EnsureVisible(Control child)
    {
        if (!_naturalRects.TryGetValue(child, out var rect)) return false;

        double? newOffset = null;
        if (rect.X < _offset) newOffset = rect.X;
        else if (rect.Right > _offset + _viewportWidth) newOffset = rect.Right - _viewportWidth;

        if (newOffset is not { } value) return false;
        Offset = value;
        return true;
    }

    /// <summary>
    /// 均等縮小アルゴリズム（クラス冒頭のコメント参照）。EditorTabStripLayoutTestsから
    /// 直接検証できるようpublic staticにしている（EditorPane.ResolveDropIndexと同じ考え方）。
    /// </summary>
    public static double[] ComputeWidths(IReadOnlyList<double> naturalWidths, double availableWidth, double minWidth)
    {
        var n = naturalWidths.Count;
        var result = new double[n];
        if (n == 0) return result;

        var naturalSum = 0.0;
        foreach (var w in naturalWidths) naturalSum += w;
        if (naturalSum <= availableWidth)
        {
            for (var i = 0; i < n; i++) result[i] = naturalWidths[i];
            return result;
        }

        var fixedFlags = new bool[n];
        var remainingWidth = Math.Max(0, availableWidth);
        var remainingCount = n;

        while (remainingCount > 0)
        {
            var target = remainingWidth / remainingCount;
            var progressed = false;
            for (var i = 0; i < n; i++)
            {
                if (fixedFlags[i]) continue;
                if (naturalWidths[i] <= target)
                {
                    result[i] = naturalWidths[i];
                    fixedFlags[i] = true;
                    remainingWidth -= naturalWidths[i];
                    remainingCount--;
                    progressed = true;
                }
            }
            if (!progressed) break;
        }

        if (remainingCount > 0)
        {
            var target = Math.Max(minWidth, remainingWidth / remainingCount);
            for (var i = 0; i < n; i++)
            {
                if (!fixedFlags[i]) result[i] = target;
            }
        }

        return result;
    }
}
