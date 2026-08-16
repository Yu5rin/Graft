using AvaloniaEdit.Rendering;

namespace Graft.Editor;

/// <summary>
/// 実機での指摘（Windows）: 折りたたみマーカーへカーソルを合わせたとき、対応する
/// インデントガイド（縦線）の強調がちらつく不具合の対処。
///
/// 【真因】 <c>TextView.InvalidateLayer(KnownLayer)</c>の実装は<c>InvalidateMeasure()</c>の
/// 1行だけ（AvaloniaEdit 11.1.0を<c>ilspycmd</c>で逆コンパイルして確認済み。引数の
/// <c>KnownLayer</c>は一切参照されない）。つまり「そのレイヤーだけ再描画」ではなく
/// <c>TextView</c>全体のレイアウトを測り直す。<c>TextView.MeasureOverride</c>は可視行
/// （<c>VisualLines</c>）を作り直し、これを購読している<c>AvaloniaEdit.Folding.
/// FoldingMargin.OnTextViewVisualLinesChanged</c>が＋/－マーカー（<c>FoldingMarginMarker</c>）を
/// 全部破棄して作り直す。ポインタの真下にあったマーカーが破棄されるとAvaloniaのポインタオーバー
/// 判定が変わり、<see cref="FoldingSupport"/>が購読している<c>FoldingMargin.PointerExited</c>が
/// 発火して<c>SetHoveredFolding(null)</c>になる。強調が解除されると<see cref="Editor.
/// IndentGuideRenderer.OnHoveredFoldingChanged"/>が再び発火して<c>InvalidateLayer</c>→測り直し→
/// マーカー再生成……と循環し、これがちらつきとして見える。
///
/// 【対処方針】 <c>IBackgroundRenderer</c>は<c>Layer</c>の値に応じて実際の描画先が異なる。
///   ・<c>KnownLayer.Background</c>: <c>TextView.Render</c>自身が無条件に
///     <c>RenderBackground(dc, KnownLayer.Background)</c>を呼んで直接描く
///     （<see cref="TextView.Render"/>のソース参照）。よって<c>textView.InvalidateVisual()</c>
///     だけで、測り直し無しに再描画できる。
///   ・それ以外（<c>Selection</c>・<c>Caret</c>等）: <c>TextView.InsertLayer</c>で
///     <c>TextView.Layers</c>へ追加された専用の<c>Control</c>（AvaloniaEdit内部の
///     <c>Layer</c>/<c>SelectionLayer</c>等。いずれも<c>internal</c>でGraft側から型名を
///     名指しできない）が、自分の<c>Render</c>の中で<c>TextView.RenderBackground(dc, layer)</c>を
///     呼んで描く、<c>TextView</c>本体のRenderとは別の描画パス。Avaloniaのコンポジションでは
///     この専用Controlは<c>TextView</c>とは独立したビジュアルノードのため、<c>TextView</c>を
///     <c>InvalidateVisual()</c>しても、この専用Control自身は再描画されない。
///
/// 【どのControlがどのKnownLayerに対応するか外部から判定できない】 <c>LayerPosition</c>・
/// <c>Layer</c>・<c>SelectionLayer</c>はいずれも<c>internal</c>のため、<c>TextView.Layers</c>
/// （これ自体は<c>public</c>）の各要素がどの<c>KnownLayer</c>に対応するかをGraft側から
/// 判定するAPIが無い。幸い<c>InvalidateVisual()</c>自体はレイアウトの測り直しを伴わない軽い
/// 操作（描画コマンドの再生成を次のコンポジションパスへ予約するだけ）のため、
/// <c>TextView.Layers</c>の全要素へ無差別に呼んでも実害は無視できる（高々数個のControlへの
/// 再描画予約が増えるだけで、<c>FoldingMarginMarker</c>の再生成は一切発生しない）。
/// </summary>
internal static class TextViewRedraw
{
    /// <summary>
    /// <c>TextView.InvalidateLayer(KnownLayer)</c>の代わりに呼ぶ。<c>TextView</c>自身と
    /// <c>TextView.Layers</c>配下の全レイヤーControlを再描画するだけで、
    /// <c>InvalidateMeasure</c>（＝可視行の作り直し・折りたたみマーカーの再生成）は発生させない。
    /// </summary>
    public static void WithoutRemeasure(TextView textView)
    {
        textView.InvalidateVisual();
        foreach (var layer in textView.Layers) layer.InvalidateVisual();
    }
}
