using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Graft.Core;
using Graft.Themes;

namespace Graft.Views;

/// <summary>
/// 1行分のコードをシンタックス色付きで描画する軽量コントロール（仕様書8.6）。
///
/// 実装方式: <see cref="TextBlock"/> を継承し、<see cref="Inlines"/> を <see cref="Run"/> の
/// 並びとして都度組み立てる方式を採用した（DrawingVisual/FormattedTextによる直描画ではなく）。
/// 理由: 8.6終盤の重ね合わせ規則（診断済みブラシの都度解決・15%彩度低下・コメントのみ
/// イタリック）や8.13の空白可視化など派生規則が多く、Runごとに独立した見た目を持たせる方が
/// 保守しやすい。表示行数自体は呼び出し側（DiffView）のVirtualizingStackPanelで可視範囲へ
/// 絞り込まれるため、1行あたりのInlines構築コストは実用上問題にならない。
/// テーマ切り替え追従は <see cref="ThemeManager.ThemeChanged"/> を購読して明示的に再構築する
/// ことで実現する（彩度変換後の色は固定Brushとして都度生成するため、DynamicResourceの
/// 自動追従だけでは対応できないため）。
/// </summary>
public sealed class CodeLineControl : TextBlock
{
    public static readonly DependencyProperty LineTextProperty = DependencyProperty.Register(
        nameof(LineText), typeof(string), typeof(CodeLineControl),
        new FrameworkPropertyMetadata(string.Empty, OnVisualPropertyChanged));

    public static readonly DependencyProperty TokensProperty = DependencyProperty.Register(
        nameof(Tokens), typeof(IReadOnlyList<SyntaxToken>), typeof(CodeLineControl),
        new FrameworkPropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty InlineHighlightsProperty = DependencyProperty.Register(
        nameof(InlineHighlights), typeof(IReadOnlyList<InlineSpan>), typeof(CodeLineControl),
        new FrameworkPropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty ShowWhitespaceProperty = DependencyProperty.Register(
        nameof(ShowWhitespace), typeof(bool), typeof(CodeLineControl),
        new FrameworkPropertyMetadata(false, OnVisualPropertyChanged));

    public static readonly DependencyProperty IsDiffRowProperty = DependencyProperty.Register(
        nameof(IsDiffRow), typeof(bool), typeof(CodeLineControl),
        new FrameworkPropertyMetadata(false, OnVisualPropertyChanged));

    public static readonly DependencyProperty DiffKindProperty = DependencyProperty.Register(
        nameof(DiffKind), typeof(DiffLineKind), typeof(CodeLineControl),
        new FrameworkPropertyMetadata(DiffLineKind.Unchanged, OnVisualPropertyChanged));

    public CodeLineControl()
    {
        TextWrapping = TextWrapping.NoWrap;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>行の内容。</summary>
    public string LineText { get => (string)GetValue(LineTextProperty); set => SetValue(LineTextProperty, value); }

    /// <summary>シンタックストークン（8.6）。未対応言語・無効時は null もしくは空。</summary>
    public IReadOnlyList<SyntaxToken>? Tokens
    {
        get => (IReadOnlyList<SyntaxToken>?)GetValue(TokensProperty);
        set => SetValue(TokensProperty, value);
    }

    /// <summary>文字単位の差分ハイライト範囲（8.3）。背景を一段強める対象。</summary>
    public IReadOnlyList<InlineSpan>? InlineHighlights
    {
        get => (IReadOnlyList<InlineSpan>?)GetValue(InlineHighlightsProperty);
        set => SetValue(InlineHighlightsProperty, value);
    }

    /// <summary>タブ・行末空白を可視化するかどうか（8.13）。</summary>
    public bool ShowWhitespace { get => (bool)GetValue(ShowWhitespaceProperty); set => SetValue(ShowWhitespaceProperty, value); }

    /// <summary>diff行上での表示かどうか。true の場合シンタックス色の彩度を15%落とす（8.6）。</summary>
    public bool IsDiffRow { get => (bool)GetValue(IsDiffRowProperty); set => SetValue(IsDiffRowProperty, value); }

    /// <summary>diff行種別。文字単位ハイライトの背景色（追加/削除どちらを基準に強めるか）の判定に使う。</summary>
    public DiffLineKind DiffKind { get => (DiffLineKind)GetValue(DiffKindProperty); set => SetValue(DiffKindProperty, value); }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CodeLineControl)d).Rebuild();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.ThemeChanged += OnThemeChanged;
        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ThemeManager.ThemeChanged -= OnThemeChanged;

    private void OnThemeChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();
        var text = LineText;
        if (string.IsNullOrEmpty(text)) return;

        var trailingStart = TrailingWhitespaceStart(text);
        foreach (var seg in TokenSegmentBuilder.Build(text, Tokens, InlineHighlights))
        {
            Inlines.Add(BuildRun(text, seg, trailingStart));
        }
    }

    private static int TrailingWhitespaceStart(string text)
    {
        var i = text.Length;
        while (i > 0 && (text[i - 1] == ' ' || text[i - 1] == '\t')) i--;
        return i;
    }

    private Run BuildRun(string fullText, TokenSegment seg, int trailingStart)
    {
        var raw = fullText.Substring(seg.Start, seg.Length);
        var display = ShowWhitespace ? WhitespaceVisualizer.Visualize(raw, seg.Start, trailingStart) : raw;
        var run = new Run(display);

        run.Foreground = ResolveForeground(seg.Kind);
        if (seg.Kind == TokenKind.Comment) run.FontStyle = FontStyles.Italic;
        if (seg.IsHighlighted) run.Background = ResolveHighlightBackground();
        return run;
    }

    private Brush ResolveForeground(TokenKind kind)
    {
        var color = TryFindResource(ResourceKeyFor(kind)) is Color c ? c : Colors.Transparent;
        if (IsDiffRow) color = ColorMath.Desaturate(color, 0.15);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private Brush ResolveHighlightBackground()
    {
        var removed = DiffKind == DiffLineKind.Removed;
        var bgKey = removed ? "DiffDelBgColor" : "DiffAddBgColor";
        var barKey = removed ? "DiffDelBarColor" : "DiffAddBarColor";
        var bg = TryFindResource(bgKey) is Color b ? b : Colors.Transparent;
        var bar = TryFindResource(barKey) is Color r ? r : bg;

        var brush = new SolidColorBrush(ColorMath.Blend(bg, bar, 0.35));
        brush.Freeze();
        return brush;
    }

    private static string ResourceKeyFor(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => "SyntaxKeywordColor",
        TokenKind.String => "SyntaxStringColor",
        TokenKind.Number => "SyntaxNumberColor",
        TokenKind.Comment => "SyntaxCommentColor",
        TokenKind.Function => "SyntaxFunctionColor",
        TokenKind.Type => "SyntaxTypeColor",
        TokenKind.Operator => "SyntaxOperatorColor",
        _ => "TextPrimaryColor",
    };
}

/// <summary>行内の1区間（トークン種別・ハイライト有無が一定の範囲）。</summary>
internal readonly record struct TokenSegment(int Start, int Length, TokenKind Kind, bool IsHighlighted);

/// <summary>
/// シンタックストークンと文字単位ハイライト範囲の境界をすべて集め、行を重複のない区間へ分割する。
/// </summary>
internal static class TokenSegmentBuilder
{
    public static List<TokenSegment> Build(
        string text, IReadOnlyList<SyntaxToken>? tokens, IReadOnlyList<InlineSpan>? spans)
    {
        var boundaries = new SortedSet<int> { 0, text.Length };
        AddBounds(boundaries, tokens?.Select(t => (t.Start, t.Length)), text.Length);
        AddBounds(boundaries, spans?.Select(s => (s.Start, s.Length)), text.Length);

        var points = boundaries.ToArray();
        var result = new List<TokenSegment>(points.Length);
        for (var i = 0; i < points.Length - 1; i++)
        {
            var start = points[i];
            var end = points[i + 1];
            if (end <= start) continue;

            result.Add(new TokenSegment(start, end - start, FindKind(tokens, start), FindHighlighted(spans, start)));
        }
        return result;
    }

    private static void AddBounds(SortedSet<int> boundaries, IEnumerable<(int Start, int Length)>? ranges, int textLength)
    {
        if (ranges is null) return;
        foreach (var (start, length) in ranges)
        {
            boundaries.Add(Math.Clamp(start, 0, textLength));
            boundaries.Add(Math.Clamp(start + length, 0, textLength));
        }
    }

    private static TokenKind FindKind(IReadOnlyList<SyntaxToken>? tokens, int at)
    {
        if (tokens is null) return TokenKind.Plain;
        foreach (var t in tokens)
        {
            if (at >= t.Start && at < t.Start + t.Length) return t.Kind;
        }
        return TokenKind.Plain;
    }

    private static bool FindHighlighted(IReadOnlyList<InlineSpan>? spans, int at)
    {
        if (spans is null) return false;
        foreach (var s in spans)
        {
            if (at >= s.Start && at < s.Start + s.Length) return true;
        }
        return false;
    }
}

/// <summary>
/// 空白文字の可視化（8.13）。タブは常に「→」へ、行末の連続する空白（スペース）は「・」へ
/// 置き換える。文字数は変更しない（1文字を1文字へ置き換えるのみ）ため、呼び出し側が計算した
/// 区間の長さとの整合は崩れない。
/// </summary>
internal static class WhitespaceVisualizer
{
    public static string Visualize(string segment, int absoluteStart, int trailingStart)
    {
        var sb = new StringBuilder(segment.Length);
        for (var i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];
            var isTrailingSpace = ch == ' ' && absoluteStart + i >= trailingStart;
            sb.Append(ch switch
            {
                '\t' => '→',
                ' ' when isTrailingSpace => '・',
                _ => ch,
            });
        }
        return sb.ToString();
    }
}

/// <summary>色の彩度調整・混合。8.6の重ね合わせ規則（diff行上での彩度15%低下、文字単位
/// ハイライトの背景を一段強める）を計算するための最小限のHSL変換。</summary>
internal static class ColorMath
{
    public static Color Desaturate(Color c, double amount)
    {
        RgbToHsl(c, out var h, out var s, out var l);
        s = Math.Clamp(s * (1 - amount), 0, 1);
        return HslToRgb(h, s, l, c.A);
    }

    public static Color Blend(Color a, Color b, double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        return Color.FromArgb(a.A, Mix(a.R, b.R, ratio), Mix(a.G, b.G, ratio), Mix(a.B, b.B, ratio));
    }

    private static byte Mix(byte x, byte y, double ratio) => (byte)Math.Round(x + (y - x) * ratio);

    private static void RgbToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;

        if (max == min)
        {
            h = 0; s = 0; return;
        }

        var d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h /= 6;
    }

    private static Color HslToRgb(double h, double s, double l, byte alpha)
    {
        if (s == 0)
        {
            var gray = (byte)Math.Round(l * 255);
            return Color.FromArgb(alpha, gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        var r = (byte)Math.Round(HueToRgb(p, q, h + 1.0 / 3) * 255);
        var g = (byte)Math.Round(HueToRgb(p, q, h) * 255);
        var b = (byte)Math.Round(HueToRgb(p, q, h - 1.0 / 3) * 255);
        return Color.FromArgb(alpha, r, g, b);
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
