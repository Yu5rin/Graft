using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Graft.Infra;

namespace Graft.Editor;

/// <summary>
/// 課題#72。元の<see cref="TextLine"/>を、X方向へ<c>indent</c>だけずらして見せるデコレータ。
/// 折り返しの2行目以降にだけ被せる（<see cref="WrapIndentTextFormatter"/>参照）。
///
/// <para>
/// 【なぜデコレータで書けるのか】 <see cref="TextLine"/>は全メンバーが
/// <c>public abstract</c>（Avalonia 11.2.3 <c>TextLine.cs</c>）であり、内部実装
/// （<c>TextLineImpl</c>、<c>internal</c>）に触れずとも外から派生クラスを書ける。
/// Avalonia側が<see cref="TextParagraphProperties.Indent"/>を読まない
/// （<see cref="WrapIndentSupport"/>のクラスコメント【原因②】）以上、
/// 「整形結果の座標系を後からずらす」このやり方が唯一の手段になる。
/// </para>
///
/// <para>
/// 【ずらすべき4種類】 AvaloniaEditが行の座標を扱う経路は次の4つで、すべてを一貫して
/// ずらさないと、見た目・キャレット・選択範囲・クリック位置がばらばらになる。
/// <list type="bullet">
///   <item><b>描画</b>: <see cref="Draw"/>の原点に<c>+indent</c></item>
///   <item><b>列→X座標</b>: <see cref="GetDistanceFromCharacterHit"/>に<c>+indent</c>
///   （キャレット位置・<c>VisualLine.GetTextLineVisualXPosition</c>がこれを使う）</item>
///   <item><b>X座標→列</b>: <see cref="GetCharacterHitFromDistance"/>に<c>-indent</c>
///   （マウスのヒットテスト。上と必ず逆向きに同量ずらす）</item>
///   <item><b>寸法</b>: <see cref="Start"/>・<see cref="Width"/>・
///   <see cref="WidthIncludingTrailingWhitespace"/>に<c>+indent</c>
///   （横スクロール範囲の算出<c>TextView.cs</c> 988行、仮想空間の判定
///   <c>VisualLine.cs</c> 505・571行がこれを使う）</item>
/// </list>
/// </para>
/// </summary>
internal sealed class WrapIndentTextLine : TextLine
{
    private readonly TextLine _inner;
    private readonly double _indent;

    /// <summary>
    /// 【なぜここでもリフレクションか】 選択範囲の背景矩形は
    /// <c>BackgroundGeometryBuilder</c>（AvaloniaEdit 11.1.0、229行）が
    /// <c>line.GetTextBounds(...)</c>の各要素の<c>b.Rectangle</c>から組み立てる。上書きモードの
    /// キャレット矩形（<c>Editing/Caret.cs</c> 420行）も同じ経路を通る。ところが
    /// <see cref="TextBounds"/>は<c>Rectangle</c>が<c>public get / internal set</c>、
    /// コンストラクタも<c>internal</c>（Avalonia 11.2.3 <c>TextBounds.cs</c>）であり、
    /// 外部からは<b>作り直すことも通常の代入もできない</b>。setterをリフレクションで
    /// 叩く以外に、選択範囲を字下げへ追従させる方法が無い。
    ///
    /// 取得できなかった場合は例外を投げず、矩形だけずれないまま動かす（描画・キャレット・
    /// ヒットテストは正しいままで、選択の背景色が字下げぶん左にずれて見えるだけ）。
    /// </summary>
    private static readonly MethodInfo? RectangleSetter =
        typeof(TextBounds).GetProperty(nameof(TextBounds.Rectangle))?.GetSetMethod(nonPublic: true);

    public WrapIndentTextLine(TextLine inner, double indent)
    {
        _inner = inner;
        _indent = indent;
    }

    /// <summary>この行に与えられている字下げ量（px）。テストからの検証用。</summary>
    public double Indent => _indent;

    // --- 単純な委譲（座標に関係しないもの） ---------------------------------------------
    public override IReadOnlyList<TextRun> TextRuns => _inner.TextRuns;
    public override int FirstTextSourceIndex => _inner.FirstTextSourceIndex;
    public override int Length => _inner.Length;
    public override TextLineBreak? TextLineBreak => _inner.TextLineBreak;
    public override double Baseline => _inner.Baseline;
    public override double Extent => _inner.Extent;
    public override bool HasCollapsed => _inner.HasCollapsed;
    public override bool HasOverflowed => _inner.HasOverflowed;
    public override double Height => _inner.Height;
    public override int NewLineLength => _inner.NewLineLength;
    public override double OverhangAfter => _inner.OverhangAfter;
    public override double OverhangLeading => _inner.OverhangLeading;
    public override double OverhangTrailing => _inner.OverhangTrailing;
    public override int TrailingWhitespaceLength => _inner.TrailingWhitespaceLength;

    // --- 字下げを反映するもの -----------------------------------------------------------
    public override double Start => _inner.Start + _indent;
    public override double Width => _inner.Width + _indent;
    public override double WidthIncludingTrailingWhitespace => _inner.WidthIncludingTrailingWhitespace + _indent;

    public override void Draw(DrawingContext drawingContext, Point lineOrigin)
        => _inner.Draw(drawingContext, new Point(lineOrigin.X + _indent, lineOrigin.Y));

    public override double GetDistanceFromCharacterHit(CharacterHit characterHit)
        => _inner.GetDistanceFromCharacterHit(characterHit) + _indent;

    public override CharacterHit GetCharacterHitFromDistance(double distance)
        => _inner.GetCharacterHitFromDistance(distance - _indent);

    /// <summary>
    /// 折りたたみ等で行が省略される場合も、包んだままでないと字下げが失われるため
    /// 包み直す（AvaloniaEditはWordWrap時にこの経路をほぼ使わないが、素通しにすると
    /// 将来使われ始めたときに静かに壊れるため）。
    /// </summary>
    public override TextLine Collapse(params TextCollapsingProperties?[] collapsingPropertiesList)
        => new WrapIndentTextLine(_inner.Collapse(collapsingPropertiesList), _indent);

    public override void Justify(JustificationProperties justificationProperties)
        => _inner.Justify(justificationProperties);

    // キャレット移動（次/前/BackSpace）は列（CharacterHit）の世界だけで完結し、X座標を
    // 含まないためそのまま委譲してよい。
    public override CharacterHit GetNextCaretCharacterHit(CharacterHit characterHit)
        => _inner.GetNextCaretCharacterHit(characterHit);

    public override CharacterHit GetPreviousCaretCharacterHit(CharacterHit characterHit)
        => _inner.GetPreviousCaretCharacterHit(characterHit);

    public override CharacterHit GetBackspaceCaretCharacterHit(CharacterHit characterHit)
        => _inner.GetBackspaceCaretCharacterHit(characterHit);

    /// <summary>
    /// 選択範囲・上書きキャレットの矩形を字下げへ追従させる。
    ///
    /// <para>
    /// 返ってきた<see cref="TextBounds"/>を<b>その場で書き換えてよい</b>のは、
    /// <c>TextLineImpl.GetTextBounds</c>（Avalonia 11.2.3、603行）が呼び出しのたびに
    /// <c>new List&lt;TextBounds&gt;()</c>へ<c>new TextBounds(...)</c>を詰めて返す実装で、
    /// キャッシュを一切共有しないため（共有していれば呼ぶたびに字下げが累積してしまう）。
    /// </para>
    /// </summary>
    public override IReadOnlyList<TextBounds> GetTextBounds(int firstTextSourceCharacterIndex, int textLength)
    {
        var bounds = _inner.GetTextBounds(firstTextSourceCharacterIndex, textLength);
        if (RectangleSetter is null || bounds.Count == 0) return bounds;

        try
        {
            foreach (var b in bounds)
            {
                var r = b.Rectangle;
                RectangleSetter.Invoke(b, [new Rect(r.X + _indent, r.Y, r.Width, r.Height)]);
            }
        }
        catch (Exception ex) when (ex is TargetInvocationException or MethodAccessException
            or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // 【縮退】 選択範囲の矩形がずれるだけの話で、ここで落ちる価値は無い
            // （このメソッドは選択やキャレット移動のたびに呼ばれる高頻度経路でもある）。
            // 発生回数は終了時のshutdownログへ集計される（SuppressedExceptionTracker参照）。
            SuppressedExceptionTracker.Shared.Record("wrap-indent-text-bounds", ex);
        }

        return bounds;
    }

    public override void Dispose() => _inner.Dispose();
}
