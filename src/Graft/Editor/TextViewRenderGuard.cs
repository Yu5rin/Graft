using AvaloniaEdit.Rendering;

namespace Graft.Editor;

/// <summary>
/// 自前の<see cref="IBackgroundRenderer"/>が、<c>DocumentLine.Offset</c>に触れる前に呼ぶ共通の防御。
///
/// 【なぜ共通化するか（実機ログ v1.0.23 → v1.0.28）】
/// 適用後の再読込は<c>Document.Text = text</c>で文書全体を1回のReplaceで置き換える。AvaloniaEditの
/// TextViewは、この置き換えの間（<c>Document.Changed</c>の処理中）から次のレイアウトパスが終わるまで、
/// <c>VisualLinesValid</c>が<c>true</c>のまま、<c>VisualLines</c>に削除済み（<c>IsDeleted</c>）の
/// DocumentLineを握る古いVisualLineを残す。この一瞬に描画が割り込むと、背景レンダラーが
/// <c>BackgroundGeometryBuilder.AddSegment</c>を通じて<c>DocumentLine.Offset</c>に触れ、
/// <see cref="InvalidOperationException"/>になる。1.0.24では選択範囲（SelectionLayer）だけを
/// 選択解除で避けたが、同じ層に描く自前レンダラー（括弧の強調：1.0.28で1日4回、検索結果の強調）
/// が同じ穴に落ちたため、症状ごとではなく「描く前に確かめる」形に一本化した。
///
/// 判定に使うのは<see cref="TextView.VisualLinesValid"/>と、可視行の
/// <c>FirstDocumentLine</c>/<c>LastDocumentLine</c>の<c>IsDeleted</c>（どちらも例外を投げない
/// 安全なプロパティ。AvaloniaEdit 11.1.0で確認）。見るのは可視行の数（せいぜい数十〜百行）だけで、
/// 全行の走査はしないため、描画のたびに呼んでも10万行のファイルで性能に影響しない。
/// 描かずに戻るのは1フレームだけ。置き換えは<c>InvalidateMeasure</c>を伴うので、
/// 次のレイアウト後の描画で正しい行から描き直される（描き損じは残らない）。
/// </summary>
internal static class TextViewRenderGuard
{
    /// <summary>
    /// 可視行がすべて生きた（文書から切り離されていない）行を指していて、いま背景を描いてよいなら
    /// true。falseのときは何も描かずに戻ること。
    /// </summary>
    public static bool CanDraw(TextView textView)
    {
        // VisualLinesはVisualLinesValidがfalseのとき例外を投げうるので、先に確かめる。
        if (!textView.VisualLinesValid) return false;
        foreach (var line in textView.VisualLines)
        {
            if (line.FirstDocumentLine.IsDeleted || line.LastDocumentLine.IsDeleted) return false;
        }
        return true;
    }
}
