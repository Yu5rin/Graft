using Avalonia.Input;
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
///
/// 【実機での指摘2: マーカーにマウスを合わせてもIビームのまま】
/// <see cref="Avalonia.Input.InputElement.CursorProperty"/>は<c>inherits: true</c>で登録された
/// 継承プロパティである。AvaloniaEditの<c>SelectionMouseHandler</c>はエディタ本文を1回でも
/// クリックすると<c>TextArea.Cursor = Cursor.Parse("IBeam")</c>を<c>TextArea</c>自身への
/// ローカル値として設定する。<c>TextArea</c>はこの折りたたみマージンの祖先であり、以後は
/// マージンも（ローカル値を持たない限り）このIビームを継承してしまう。
/// <c>FoldingMarginMarker.OnPointerMoved</c>自体は自分の<c>Cursor</c>をその場で
/// <c>Cursor.Default</c>（矢印）へ戻すコードを持つが、これは実測（tests/Graft.UiTests/
/// FoldingMarginTests.cs参照）で不十分だと分かった: <c>FoldingMargin.OnTextViewVisualLinesChanged</c>は
/// 可視行が変わるたび（折りたたみの開閉・スクロール・編集など）に<c>FoldingMarginMarker</c>を
/// 全部作り直す。マウスを動かさずに（例えばクリックの結果としてだけ）可視行が変わると、
/// 画面上の同じ位置に「Cursorのローカル値をまだ一度も設定していない新しいマーカー」が
/// 入れ替わり、次にマウスが実際に動くまでIビームのまま取り残される。
///
/// 【なぜマージン側で明示設定するのが確実か】
/// マージン自身（本クラスのインスタンス）は使い回されるため、コンストラクタで一度
/// <c>Cursor</c>をローカル値として矢印に設定しておけば、継承によって
/// 「その時点でまだCursorのローカル値を持たない子（作り直された直後のマーカーを含む）」
/// すべてに矢印が伝播する。マーカー側の<c>OnPointerMoved</c>頼みだとマウスの実際の移動
/// イベントを待つ必要があるが、マージン側の継承元を直しておけば、マーカーが作り直された
/// 瞬間から（マウスが動かなくても）矢印になる。ブラシと違いCursorはFoldingMargin側の
/// 添付プロパティではなく通常の継承プロパティなので、ここで設定してもマーカーの見た目
/// （枠線・背景）には一切影響しない。
/// </summary>
internal sealed class MarkerOnlyFoldingMargin : FoldingMargin
{
    public MarkerOnlyFoldingMargin()
    {
        // クラスコメント「実機での指摘2」参照。TextAreaから継承したIビームを、この
        // マージン（と、その子である＋/－マーカー）にだけ矢印へ上書きする。
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

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
