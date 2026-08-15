namespace Graft.Editor;

/// <summary>
/// 折りたたみ範囲の入れ子の深さ（レベル）を求める純粋関数。<see cref="FoldingSupport"/>の
/// 「レベル1〜5で折りたたむ」コマンドが使う。AvaloniaEditの<c>FoldingSection</c>に依存せず
/// オフセットの組だけを扱うため、<c>tests/Graft.Tests</c>から直接検証できる。
///
/// 前提: 入力は開始オフセット昇順（<see cref="AvaloniaEdit.Folding.FoldingManager.AllFoldings"/>の
/// 既定の並び）で、かつ互いに交差しない（真に入れ子になるか、重ならないかのどちらか）。
/// <see cref="Graft.Editor.FoldingSupport"/>が使う2つの折りたたみ戦略
/// （<c>BraceFoldingStrategy</c>・<c>IndentFoldingStrategy</c>）はどちらもこの前提を満たす
/// 範囲しか生成しない。
/// </summary>
public static class FoldingLevelCalculator
{
    /// <summary>
    /// 各範囲の入れ子レベル（最も外側が1）を、<paramref name="ranges"/>と同じ並び順で返す。
    /// スタックに「現在開いている祖先範囲の終了オフセット」を積み、自分より前に終わった
    /// 祖先を都度取り除きながら、その時点のスタックの深さ+1をレベルとする。
    /// </summary>
    public static int[] ComputeLevels(IReadOnlyList<(int Start, int End)> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        var levels = new int[ranges.Count];
        var openEndOffsets = new Stack<int>();
        for (var i = 0; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            while (openEndOffsets.Count > 0 && openEndOffsets.Peek() <= start)
            {
                openEndOffsets.Pop();
            }
            levels[i] = openEndOffsets.Count + 1;
            openEndOffsets.Push(end);
        }
        return levels;
    }
}
