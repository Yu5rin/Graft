using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using LogicalDirection = AvaloniaEdit.Document.LogicalDirection;

namespace Graft.Editor;

/// <summary>
/// カラープレビュー機能（検討書「コード中のカラープレビュー」）: <c>#RRGGBB</c>・<c>rgb()</c>・
/// <c>hsl()</c>の直前に小さな色見本（スウォッチ）を差し込み、クリックでカラーピッカーを開けるように
/// する。
///
/// 【差し込み方 ― IBackgroundRendererではなくVisualLineElementGenerator】
/// 検討書は「<see cref="IndentGuideRenderer"/>（<see cref="IBackgroundRenderer"/>）や
/// <see cref="MarkdownInlineColorizer"/>（<see cref="AvaloniaEdit.Rendering.
/// DocumentColorizingTransformer"/>）の作法に倣うのが素直」としているが、実際に両方読んだ結果、
/// どちらも「既存の文字の見た目を変える」仕組みであり、「文字の前に新しい図形を割り込ませて
/// クリックも受け取る」用途には向かないと判断した（IBackgroundRendererは背景に描くだけでヒット
/// テストを持たず、DocumentColorizingTransformerは既存文字の装飾しかできない）。
/// 代わりに、AvaloniaEdit組み込みのリンク機能（<see cref="LinkElementGenerator"/>・
/// <see cref="VisualLineLinkText"/>、URLをクリック可能にする仕組み）と同じ
/// <see cref="VisualLineElementGenerator"/>の作法を採用した。この仕組みは「文書0文字ぶんの
/// 見た目だけの要素」を任意の位置へ挿入でき（<see cref="ColorSwatchElement"/>の
/// <c>documentLength: 0</c>）、かつ<see cref="VisualLineElement.OnPointerPressed"/>で
/// クリックを受け取れる（<see cref="VisualLineLinkText.OnPointerPressed"/>と同じ仕組み）ため、
/// 「文字は一切変更せず、直前にクリック可能な図形を差し込む」という要件にちょうど合う。
///
/// 【可視範囲だけを処理する（性能要件）】
/// <see cref="VisualLineElementGenerator"/>はAvaloniaEdit自身が可視行の構築時にしか呼ばない
/// （<c>VisualLine.PerformVisualElementConstruction</c>、<see cref="LinkElementGenerator"/>と
/// 同じ）ため、この仕組みを使うだけで可視範囲限定は自動的に満たされる。行ごとの正規表現走査
/// （<see cref="ColorLiteralParser.FindAll"/>）は1行の構築パスの中で最大1回になるよう
/// <see cref="_lineMatchCache"/>でキャッシュする（<see cref="StartGeneration"/>で毎回破棄する
/// ため古い内容が残らない）。極端に長い行は<see cref="MarkdownInlineColorizer"/>・
/// <see cref="SyntaxHighlightBridge"/>と同じ<see cref="DocumentSession.LongLineThreshold"/>で
/// 打ち切る。10万行のファイルでの性能は<c>tests/Graft.UiTests/ColorPreviewPerformanceTests.cs</c>
/// で検証している。
/// </summary>
public sealed class ColorPreviewElementGenerator : VisualLineElementGenerator
{
    private bool _enabled = true;
    private readonly Dictionary<int, IReadOnlyList<ColorLiteralMatch>> _lineMatchCache = new();

    /// <summary>スウォッチがクリックされたときに発火する。<see cref="EditorPane"/>側でカラーピッカーを開く。</summary>
    public event EventHandler<ColorSwatchClickedEventArgs>? SwatchClicked;

    /// <summary>検討書の設定<c>colorPreviewInCode</c>（既定true）。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
    }

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _lineMatchCache.Clear();
    }

    public override void FinishGeneration()
    {
        base.FinishGeneration();
        _lineMatchCache.Clear();
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (!_enabled) return -1;
        foreach (var m in GetMatchesForOffset(startOffset))
        {
            if (m.Start >= startOffset) return m.Start;
        }
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (!_enabled) return null;
        foreach (var m in GetMatchesForOffset(offset))
        {
            // TextViewをここで渡しておく(CurrentContext経由でしか取れない)。クリック時の
            // 画面座標計算をe.Source頼みにしない理由はColorSwatchElement.OnPointerPressed参照。
            if (m.Start == offset) return new ColorSwatchElement(this, m, CurrentContext.TextView);
        }
        return null;
    }

    internal void RaiseSwatchClicked(ColorLiteralMatch match, PixelPoint screenPoint)
        => SwatchClicked?.Invoke(this, new ColorSwatchClickedEventArgs(match, screenPoint));

    private IReadOnlyList<ColorLiteralMatch> GetMatchesForOffset(int offset)
    {
        var document = CurrentContext.Document;
        var line = document.GetLineByOffset(offset);
        if (_lineMatchCache.TryGetValue(line.Offset, out var cached)) return cached;

        IReadOnlyList<ColorLiteralMatch> result;
        if (line.Length == 0 || line.Length > DocumentSession.LongLineThreshold)
        {
            result = Array.Empty<ColorLiteralMatch>();
        }
        else
        {
            var text = document.GetText(line.Offset, line.Length);
            var local = ColorLiteralParser.FindAll(text);
            result = local.Count == 0
                ? Array.Empty<ColorLiteralMatch>()
                : local.Select(m => m with { Start = m.Start + line.Offset }).ToList();
        }
        _lineMatchCache[line.Offset] = result;
        return result;
    }
}

/// <summary><see cref="ColorPreviewElementGenerator.SwatchClicked"/>の引数。</summary>
public sealed class ColorSwatchClickedEventArgs(ColorLiteralMatch match, PixelPoint screenPoint) : EventArgs
{
    public ColorLiteralMatch Match { get; } = match;

    /// <summary>クリックされた位置の画面座標。カラーピッカーをその近くに開く位置決めに使う。</summary>
    public PixelPoint ScreenPoint { get; } = screenPoint;
}

/// <summary>
/// スウォッチ1個ぶんの見た目（<c>documentLength: 0</c>＝文字は一切消費しない、リテラルの直前に
/// 挿入されるだけの図形）。半透明の色は市松模様の上に重ねて透明度が分かるようにする
/// （検討書のPane仕様の踏襲）。
/// </summary>
internal sealed class ColorSwatchElement : VisualLineElement
{
    private readonly ColorPreviewElementGenerator _owner;
    private readonly ColorLiteralMatch _match;
    private readonly TextView _textView;

    public ColorSwatchElement(ColorPreviewElementGenerator owner, ColorLiteralMatch match, TextView textView)
        : base(visualLength: 1, documentLength: 0)
    {
        _owner = owner;
        _match = match;
        _textView = textView;
    }

    /// <summary>このスウォッチが表す色リテラル（テスト用。tests/Graft.UiTests参照）。</summary>
    internal ColorLiteralMatch Match => _match;

    /// <summary>このスウォッチの色（テスト用のショートカット）。</summary>
    internal RgbaColor Color => _match.Color;

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        var emSize = TextRunProperties.FontRenderingEmSize;
        return new ColorSwatchTextRun(_match.Color, emSize, TextRunProperties);
    }

    /// <summary>キャレットの停止点にしない（0文字ぶんの見た目だけの要素のため、矢印キーで
    /// 「動いていないのに一段止まる」ような違和感を避ける。<see cref="VisualLineLinkText"/>と違い
    /// リンクはテキストそのものなので既定の挙動のままでよいが、本要素は挿入物のため明示的に無効化する）。</summary>
    public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) => -1;

    protected override void OnQueryCursor(PointerEventArgs e)
    {
        if (e.Source is InputElement inputElement) inputElement.Cursor = new Cursor(StandardCursorType.Hand);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.Handled) return;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

        // スウォッチ自身の矩形（VisualLineの内部状態）は取得できないため、クリック位置そのものを
        // 画面座標へ変換してカラーピッカーの位置決めに使う（ColorPickerPopup側でクランプする）。
        // e.Source（イベントの発生元）はAvaloniaEditが独自にVisualLineElementへディスパッチする
        // 際に必ずしも期待どおりのVisualになるとは限らない（実機のXvfb起動で、パネルが常に
        // 画面左上へ出てしまう不具合として発覚した）ため、CurrentContext経由で確実に受け取った
        // TextView自身（ConstructElementが渡す、実際のコントロール）を基準に変換する。
        if (TopLevel.GetTopLevel(_textView) is { } topLevel)
        {
            var screenPoint = topLevel.PointToScreen(e.GetPosition(topLevel));
            _owner.RaiseSwatchClicked(_match, screenPoint);
        }
        e.Handled = true;
    }
}

/// <summary>スウォッチの実描画。角丸の正方形＋枠線。半透明色は市松模様の上に重ねる。</summary>
internal sealed class ColorSwatchTextRun : DrawableTextRun
{
    // Pane「カラープレビュー仕様.md」§3.1: 0.85emの正方形、文字の直前に0.25emの余白。
    private const double SwatchEmRatio = 0.85;
    private const double MarginEmRatio = 0.35; // 余白は文字の直前に加え、スウォッチ自身の右側にも少し空ける。

    private readonly Color _color;
    private readonly double _emSize;
    private readonly TextRunProperties _properties;

    public ColorSwatchTextRun(RgbaColor color, double emSize, TextRunProperties properties)
    {
        _color = Color.FromArgb(color.A, color.R, color.G, color.B);
        _emSize = emSize;
        _properties = properties;
    }

    public override ReadOnlyMemory<char> Text => ReadOnlyMemory<char>.Empty;
    public override TextRunProperties Properties => _properties;
    public override double Baseline => Measure(_emSize).Height * 0.85;
    public override Size Size => Measure(_emSize);

    internal static Size Measure(double emSize)
    {
        var swatch = emSize * SwatchEmRatio;
        var margin = emSize * MarginEmRatio;
        return new Size(swatch + margin, Math.Max(swatch, emSize));
    }

    public override void Draw(DrawingContext context, Point origin)
    {
        var swatch = _emSize * SwatchEmRatio;
        var size = Measure(_emSize);
        var rect = new Rect(origin.X, origin.Y + (size.Height - swatch) / 2, swatch, swatch);
        var geometry = new RoundedRect(rect, swatch * 0.2);

        if (_color.A < 255)
        {
            DrawCheckerboard(context, rect, swatch);
        }

        context.DrawRectangle(new SolidColorBrush(_color), null, geometry);

        var borderBrush = ResolveBrush("BorderSubtle");
        if (borderBrush is not null)
        {
            context.DrawRectangle(null, new Pen(borderBrush, 1), geometry);
        }
    }

    private static void DrawCheckerboard(DrawingContext context, Rect rect, double size)
    {
        // 4分割の市松模様。IBrush2枚だけで済む簡易版（半透明であることが伝わればよく、
        // マス目の細かさまでは要求されていないため、性能・実装量とのバランスでこの粒度にした）。
        var half = size / 2;
        var light = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        var dark = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
        context.DrawRectangle(light, null, rect);
        context.DrawRectangle(dark, null, new Rect(rect.X, rect.Y, half, half));
        context.DrawRectangle(dark, null, new Rect(rect.X + half, rect.Y + half, half, half));
    }

    private static IBrush? ResolveBrush(string key)
        => Avalonia.Application.Current is { } app && app.TryFindResource(key, null, out var value) && value is IBrush brush
            ? brush
            : null;
}
