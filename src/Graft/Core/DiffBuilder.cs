using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Graft.Core;

/// <summary>
/// DiffPlexを用いた差分生成。仕様書8.13の折りたたみ（既定 <see cref="Build"/>）と
/// 全展開（<see cref="BuildFull"/>）、変更行内の文字単位ハイライトを担当する。
/// </summary>
public static class DiffBuilder
{
    /// <summary>
    /// 機能改善（単語レベルの差分強調）: 行内の文字単位ハイライト計算（<see cref="PairInlineSpans"/>）を
    /// 行う上限の行長。DiffPlexの<c>CreateCharacterDiffs</c>はMyers法ベースで、2行の内容が
    /// 大きく異なるほど（一致する共通部分が少ないほど）計算量が行の文字数に対して悪化する
    /// （このリポジトリには「極端に長い行」で構文強調・折り返し・括弧対応付けが指数的に遅くなる
    /// 既知の性能問題があり、LongLineTests・TextNormalizerLongLineTestsで回帰を防いでいる。
    /// 文字単位diffも同種の入力に弱いアルゴリズムのため、同じ考え方で上限を設ける）。
    /// 通常のコード行（数十〜数百文字）はこの上限を大きく下回るため実害は無く、1行2000文字を
    /// 超えるような行（ミニファイ済みJS・長い1行のJSON等）だけ行単位の色分けのみに留め、
    /// 単語レベル強調の計算そのものを打ち切る。
    /// </summary>
    private const int MaxInlineDiffLineLength = 2000;

    /// <summary>差分を生成する。変更のない範囲は前後 contextLines 行を残して折りたたむ。</summary>
    public static DiffModel Build(string path, string? before, string? after, int contextLines)
        => BuildCore(path, before, after, contextLines);

    /// <summary>8.13 全展開。折りたたみを行わない。</summary>
    public static DiffModel BuildFull(string path, string? before, string? after)
        => BuildCore(path, before, after, contextLines: null);

    private static DiffModel BuildCore(string path, string? before, string? after, int? contextLines)
    {
        var rawLines = BuildRawLines(before, after);
        var visible = contextLines is int n ? FoldContext(rawLines, n) : rawLines;
        var hunks = GroupIntoHunks(visible);
        var added = rawLines.Count(l => l.Kind == DiffLineKind.Added);
        var removed = rawLines.Count(l => l.Kind == DiffLineKind.Removed);
        return new DiffModel { Path = path, Hunks = hunks, Added = added, Removed = removed };
    }

    // ------------------------------------------------------------------
    // 行差分の生成（新規作成・削除・通常の変更）
    // ------------------------------------------------------------------

    private static List<DiffLine> BuildRawLines(string? before, string? after)
    {
        var beforeLines = before is null ? null : TextNormalizer.SplitLines(before);
        var afterLines = after is null ? null : TextNormalizer.SplitLines(after);

        if (beforeLines is null && afterLines is null) return new List<DiffLine>();
        if (beforeLines is null) return afterLines!.Select((l, i) => MakeAdded(l, i + 1)).ToList();
        if (afterLines is null) return beforeLines.Select((l, i) => MakeRemoved(l, i + 1)).ToList();

        return BuildLineDiff(beforeLines, afterLines);
    }

    private static DiffLine MakeAdded(string text, int newLine)
        => new() { Kind = DiffLineKind.Added, NewLine = newLine, Text = text };

    private static DiffLine MakeRemoved(string text, int oldLine)
        => new() { Kind = DiffLineKind.Removed, OldLine = oldLine, Text = text };

    /// <summary>
    /// DiffPlex の InlineDiffBuilder で行単位の差分を求める。改行文字は "\n" に統一して渡し
    /// （元の改行コードは行内容そのものには含まれないため差分結果に影響しない）、行番号は
    /// 変更前・変更後それぞれの走行カウンタで独自に採番する（DiffPiece.Position は変更後側の
    /// 番号しか持たないため使わない）。
    /// </summary>
    private static List<DiffLine> BuildLineDiff(IReadOnlyList<string> beforeLines, IReadOnlyList<string> afterLines)
    {
        var beforeText = string.Join("\n", beforeLines);
        var afterText = string.Join("\n", afterLines);
        var pieces = InlineDiffBuilder.Diff(beforeText, afterText).Lines;

        var lines = new List<DiffLine>(pieces.Count);
        var oldNo = 0;
        var newNo = 0;
        foreach (var piece in pieces)
        {
            var text = piece.Text ?? string.Empty;
            switch (piece.Type)
            {
                case ChangeType.Unchanged:
                    oldNo++; newNo++;
                    lines.Add(new DiffLine { Kind = DiffLineKind.Unchanged, OldLine = oldNo, NewLine = newNo, Text = text });
                    break;
                case ChangeType.Deleted:
                    oldNo++;
                    lines.Add(new DiffLine { Kind = DiffLineKind.Removed, OldLine = oldNo, Text = text });
                    break;
                case ChangeType.Inserted:
                    newNo++;
                    lines.Add(new DiffLine { Kind = DiffLineKind.Added, NewLine = newNo, Text = text });
                    break;
            }
        }

        ApplyInlineSpans(lines);
        return lines;
    }

    // ------------------------------------------------------------------
    // 変更行内の文字単位ハイライト（8.3・8.13）
    // ------------------------------------------------------------------

    /// <summary>
    /// 連続する削除行の並びと、それに続く連続する追加行の並びを同数ぶんだけ突き合わせ、
    /// 文字単位の差分をハイライトへ変換する。行数が一致しない余剰分は対応付けない。
    /// </summary>
    private static void ApplyInlineSpans(List<DiffLine> lines)
    {
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Kind != DiffLineKind.Removed) { i++; continue; }

            var removedStart = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Removed) i++;
            var removedCount = i - removedStart;

            var addedStart = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Added) i++;
            var addedCount = i - addedStart;

            var pairCount = Math.Min(removedCount, addedCount);
            for (var k = 0; k < pairCount; k++) PairInlineSpans(lines, removedStart + k, addedStart + k);
        }
    }

    private static void PairInlineSpans(List<DiffLine> lines, int removedIndex, int addedIndex)
    {
        var oldText = lines[removedIndex].Text;
        var newText = lines[addedIndex].Text;

        // 性能対策: どちらかの行が極端に長い場合は文字単位diffの計算そのものを打ち切り、
        // 行単位の色分け（既存のGrid.diffCell.added/removed背景）だけに留める
        // （MaxInlineDiffLineLengthのコメント参照）。InlineSpansを空のままにするだけで、
        // 行の追加・削除種別（Kind）や表示自体には一切影響しない。
        if (oldText.Length > MaxInlineDiffLineLength || newText.Length > MaxInlineDiffLineLength) return;

        var charDiff = Differ.Instance.CreateCharacterDiffs(oldText, newText, false);

        var oldSpans = new List<InlineSpan>();
        var newSpans = new List<InlineSpan>();
        foreach (var block in charDiff.DiffBlocks)
        {
            if (block.DeleteCountA > 0) oldSpans.Add(new InlineSpan(block.DeleteStartA, block.DeleteCountA));
            if (block.InsertCountB > 0) newSpans.Add(new InlineSpan(block.InsertStartB, block.InsertCountB));
        }

        lines[removedIndex] = lines[removedIndex] with { InlineSpans = oldSpans };
        lines[addedIndex] = lines[addedIndex] with { InlineSpans = newSpans };
    }

    // ------------------------------------------------------------------
    // 折りたたみ（8.13）
    // ------------------------------------------------------------------

    /// <summary>変更行の前後 contextLines 行だけを残し、残りを Omitted の擬似行へ折りたたむ。</summary>
    private static List<DiffLine> FoldContext(List<DiffLine> lines, int contextLines)
    {
        if (lines.Count == 0) return lines;
        var keep = ComputeKeepMask(lines, Math.Max(0, contextLines));

        var result = new List<DiffLine>();
        var i = 0;
        while (i < lines.Count)
        {
            if (keep[i]) { result.Add(lines[i]); i++; continue; }

            var start = i;
            while (i < lines.Count && !keep[i]) i++;
            var count = i - start;
            result.Add(new DiffLine { Kind = DiffLineKind.Omitted, Text = $"…（{count}行省略）", OmittedCount = count });
        }
        return result;
    }

    private static bool[] ComputeKeepMask(List<DiffLine> lines, int contextLines)
    {
        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffLineKind.Unchanged) continue;
            var from = Math.Max(0, i - contextLines);
            var to = Math.Min(lines.Count - 1, i + contextLines);
            for (var j = from; j <= to; j++) keep[j] = true;
        }
        return keep;
    }

    // ------------------------------------------------------------------
    // ハンクへの分割
    // ------------------------------------------------------------------

    /// <summary>Omitted行は単独のハンクへ分離し、それ以外の連続行はまとめて1ハンクにする。</summary>
    private static List<DiffHunk> GroupIntoHunks(List<DiffLine> lines)
    {
        var hunks = new List<DiffHunk>();
        var current = new List<DiffLine>();
        foreach (var line in lines)
        {
            if (line.Kind == DiffLineKind.Omitted)
            {
                FlushHunk(hunks, current);
                hunks.Add(new DiffHunk { Lines = new[] { line } });
                continue;
            }
            current.Add(line);
        }
        FlushHunk(hunks, current);
        return hunks;
    }

    private static void FlushHunk(List<DiffHunk> hunks, List<DiffLine> current)
    {
        if (current.Count == 0) return;
        hunks.Add(new DiffHunk { Lines = current.ToArray() });
        current.Clear();
    }
}
