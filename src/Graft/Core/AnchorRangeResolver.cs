namespace Graft.Core;

/// <summary>
/// 仕様書4.4「アンカー省略記法（SEARCH-RANGE）」の範囲解決を担う。
/// SearchText を "..." のみの行で開始アンカー・終了アンカーに分割し、開始アンカーの最初の
/// 出現位置から、それ以降で最初に現れる終了アンカーまで（両端を含む）を範囲として返す。
/// アンカー自体の照合には <see cref="TextNormalizer.FindStagedMatches"/>（段階1〜4）を用いる。
/// </summary>
public static class AnchorRangeResolver
{
    /// <summary>解決された置換範囲。</summary>
    public sealed record AnchorRangeMatch
    {
        /// <summary>範囲の開始行（0始まり）。</summary>
        public required int StartLine { get; init; }

        /// <summary>範囲の行数。</summary>
        public required int LineCount { get; init; }

        /// <summary>開始・終了アンカーの照合段階のうち、より緩い（不確実な）方。</summary>
        public required MatchStage Stage { get; init; }
    }

    /// <summary>SEARCH-RANGE のアンカーからファイル中の置換範囲を求める。</summary>
    public static GraftResult<AnchorRangeMatch> Resolve(
        IReadOnlyList<string> fileLines, string searchText, int rangeWarningLines)
    {
        var anchors = SplitAnchors(searchText);
        if (anchors is null)
        {
            return GraftResult<AnchorRangeMatch>.Fail(ErrorCode.E002,
                "SEARCH-RANGEは '...' のみの行をちょうど1つ含む必要があります");
        }

        var (startAnchor, endAnchor) = anchors.Value;

        var startFound = TextNormalizer.FindStagedMatches(fileLines, startAnchor);
        if (startFound.Matches.Count == 0)
        {
            return GraftResult<AnchorRangeMatch>.Fail(ErrorCode.E101, "開始アンカーが見つかりません");
        }

        var startLine = startFound.Matches[0].StartLine;
        var tail = fileLines.Skip(startLine).ToList();
        var endFound = TextNormalizer.FindStagedMatches(tail, endAnchor);
        if (endFound.Matches.Count == 0)
        {
            return GraftResult<AnchorRangeMatch>.Fail(ErrorCode.E103);
        }

        var endMatch = endFound.Matches[0];
        var lineCount = endMatch.StartLine + endMatch.LineCount;
        var stage = (MatchStage)Math.Max((int)startFound.Stage, (int)endFound.Stage);

        var result = new AnchorRangeMatch { StartLine = startLine, LineCount = lineCount, Stage = stage };
        return GraftResult<AnchorRangeMatch>.Ok(result, BuildRangeWarning(lineCount, rangeWarningLines));
    }

    private static IReadOnlyList<GraftIssue> BuildRangeWarning(int lineCount, int rangeWarningLines)
    {
        if (lineCount <= rangeWarningLines) return Array.Empty<GraftIssue>();

        var detail = $"アンカー範囲が{lineCount}行あり、警告閾値（{rangeWarningLines}行）を超えています";
        return new[] { GraftIssue.Of(ErrorCode.E103, detail, severity: Severity.Warning) };
    }

    private static (IReadOnlyList<string> Start, IReadOnlyList<string> End)? SplitAnchors(string searchText)
    {
        var lines = TextNormalizer.SplitLines(searchText);
        var separators = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() == "...") separators.Add(i);
        }

        if (separators.Count != 1) return null;

        var sep = separators[0];
        var start = lines.Take(sep).ToList();
        var end = lines.Skip(sep + 1).ToList();
        if (start.Count == 0 || end.Count == 0) return null;

        return (start, end);
    }
}
