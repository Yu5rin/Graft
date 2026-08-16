using Avalonia.Media;
using AvaloniaEdit.Folding;

namespace Graft.Editor;

/// <summary>
/// 折りたたみマージン（コードエディタ左端の＋/－が並ぶ帯）から、マーカーから下へ伸びる縦線と
/// 折りたたみ範囲の終端で右へ伸びる短い横線（合わせてL字に見える線）だけを消し、＋/－の
/// マーカー自体は残す<see cref="FoldingMargin"/>の派生クラス。実機での指摘（Windows）に基づく
/// 対処で、設定項目は追加せず常にL字線なしにする。
///
/// 【なぜブラシを透明にする方法ではダメか】
/// L字線は基底クラス<see cref="FoldingMargin"/>の<c>Render</c>が
/// <c>FoldingMarkerBrush</c>/<c>SelectedFoldingMarkerBrush</c>添付プロパティから作った
/// <c>Pen</c>で描いている。ところが＋/－のマーカー枠を描く<c>FoldingMarginMarker.Render</c>も
/// 同じ2つのブラシ（<c>FoldingMargin</c>の添付プロパティなので参照先は同一インスタンス）を
/// 使って枠線を描く。つまり線とマーカーは描画に使うブラシを共有しているため、ブラシを透明に
/// 差し替えるとL字線と一緒に＋/－の枠線まで消えてしまい、「線だけ消してマーカーは残す」
/// という要求を満たせない。
///
/// 【なぜこれでマーカーだけ残るか】
/// ＋/－マーカーは<c>FoldingMargin.OnTextViewVisualLinesChanged</c>（本クラスでは上書きしない）が
/// 生成する別コントロール<c>FoldingMarginMarker</c>であり、<c>VisualChildren</c>へ子として
/// 追加されて自分自身の<c>Render</c>で独立に描画される。一方、L字線は<c>FoldingMargin.Render</c>
/// 自身が（内部で<c>CalculateFoldLinesForFoldingsActiveAtStart</c>→
/// <c>CalculateFoldLinesForMarkers</c>→<c>DrawFoldLines</c>を順に呼び出し）マージンの
/// <c>drawingContext</c>へ直接描いている、マーカーの描画とは完全に別経路の処理である。
/// <c>FoldingMargin.Render</c>は<c>public override</c>でsealedではないため、本クラスで
/// <see cref="Render"/>だけを「何もしない」に再オーバーライドすれば、マージン自身が描く線は
/// 一切発生しなくなる一方、子コントロールであるマーカーの描画・レイアウト
/// （<c>MeasureOverride</c>/<c>ArrangeOverride</c>/<c>OnTextViewVisualLinesChanged</c>）は
/// 一切変更していないため、＋/－マーカーはそのまま表示され続ける。
/// </summary>
internal sealed class MarkerOnlyFoldingMargin : FoldingMargin
{
    /// <summary>
    /// 基底クラスの実装を意図的に呼ばない（呼ぶとL字線が復活する）。何もしないことで
    /// マージン自身が描く線を消す。＋/－マーカーは子コントロールとして別途描画されるため、
    /// ここで何もしなくても消えない（クラスコメント参照）。
    /// </summary>
    public override void Render(DrawingContext drawingContext)
    {
        // 意図的に何もしない。
    }
}
