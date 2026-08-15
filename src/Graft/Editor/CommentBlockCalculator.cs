namespace Graft.Editor;

/// <summary>
/// 「すべてのコメントブロックを折りたたむ」コマンドが使う、コメント専用行の連続区間探索。
/// 連続する2行以上のコメント専用行（各行の実内容がすべてコメントトークンの行。
/// <see cref="FoldingSupport.FoldAllComments"/>参照）を1つの折りたたみ候補とみなす。
/// 複数行コメント（<c>/* ... */</c>）・連続する単一行コメント（<c>//</c>の並び）の
/// どちらも「コメント専用行が連続する区間」という同じ形で表れるため、判定を共通化できる。
/// 1行だけのコメント行は折りたたむ内側行が無いため対象外（<see cref="IndentGuideCalculator.
/// ComputeInteriorRange"/>と同じく、範囲は最低2行必要）。
/// </summary>
public static class CommentBlockCalculator
{
    /// <summary>
    /// <paramref name="isCommentOnlyLine"/>（0始まりの配列、要素数=行数）から、
    /// 長さ2行以上の連続区間を(開始行, 終了行)の1始まり行番号で列挙する。
    /// </summary>
    public static IEnumerable<(int StartLine, int EndLine)> FindCommentBlocks(
        IReadOnlyList<bool> isCommentOnlyLine)
    {
        ArgumentNullException.ThrowIfNull(isCommentOnlyLine);

        var i = 0;
        while (i < isCommentOnlyLine.Count)
        {
            if (!isCommentOnlyLine[i]) { i++; continue; }

            var runStart = i;
            while (i < isCommentOnlyLine.Count && isCommentOnlyLine[i]) i++;
            var runLength = i - runStart;

            if (runLength >= 2)
            {
                yield return (runStart + 1, i); // 0始まり→1始まりへ変換。
            }
        }
    }
}
