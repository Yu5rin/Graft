namespace Graft.Core;

/// <summary>
/// マッチ動作の設定（settings.json の matching セクションに対応、仕様書14章）。
/// </summary>
public sealed record MatchOptions
{
    /// <summary>段階5（類似度）で一致と見なす閾値（0〜1）。</summary>
    public double SimilarityThreshold { get; init; } = 0.85;

    /// <summary>段階5（類似度マッチ）を試みるかどうか。</summary>
    public bool AllowSimilarityMatch { get; init; } = true;

    /// <summary>アンカー範囲がこの行数を超えたら警告する（仕様書4.4）。</summary>
    public int RangeWarningLines { get; init; } = 300;

    /// <summary>既定値。</summary>
    public static MatchOptions Default { get; } = new();
}

/// <summary>1ペアのマッチ結果。</summary>
public sealed record MatchResult
{
    /// <summary>どの段階でマッチしたか。</summary>
    public required MatchStage Stage { get; init; }

    /// <summary>置換対象の開始行（0始まり）。</summary>
    public required int StartLine { get; init; }

    /// <summary>置換対象の行数。</summary>
    public required int LineCount { get; init; }

    /// <summary>インデント補正済みの置換テキスト。</summary>
    public required string AppliedReplacement { get; init; }

    /// <summary>類似度（段階5のみ意味を持つ。他の段階は1.0）。</summary>
    public double Similarity { get; init; } = 1.0;

    /// <summary>段階5のとき true。プレビューで強調し個別承認を求める。</summary>
    public bool NeedsConfirmation { get; init; }
}

/// <summary>
/// 仕様書5章のマッチングエンジン。段階1〜6のフォールバック、複数候補の扱い（5.1）、
/// 段階3の適用規則（5.2）、アンカー省略記法（4.4）を担当する。
/// </summary>
public sealed class MatchEngine
{
    private readonly MatchOptions _options;

    public MatchEngine(MatchOptions? options = null)
    {
        _options = options ?? MatchOptions.Default;
    }

    /// <summary>originalText: 対象ファイルの全文。pair: 検索置換ペア。occurrence: 出現指定。</summary>
    public GraftResult<IReadOnlyList<MatchResult>> Match(
        string originalText, SearchReplacePair pair, OccurrenceSpec occurrence)
    {
        var fileLines = TextNormalizer.SplitLines(originalText);

        return pair.IsRange
            ? MatchRange(fileLines, pair)
            : MatchPlain(fileLines, pair, occurrence);
    }

    private GraftResult<IReadOnlyList<MatchResult>> MatchRange(IReadOnlyList<string> fileLines, SearchReplacePair pair)
    {
        var resolved = AnchorRangeResolver.Resolve(fileLines, pair.SearchText, _options.RangeWarningLines);
        if (!resolved.IsSuccess) return GraftResult<IReadOnlyList<MatchResult>>.Fail(resolved.Issues);

        var range = resolved.Value;
        var match = new LineMatch { StartLine = range.StartLine, LineCount = range.LineCount };
        var result = BuildResult(fileLines, pair, range.Stage, match, similarity: 1.0, needsConfirmation: false);
        return GraftResult<IReadOnlyList<MatchResult>>.Ok(new[] { result }, resolved.Issues);
    }

    private GraftResult<IReadOnlyList<MatchResult>> MatchPlain(
        IReadOnlyList<string> fileLines, SearchReplacePair pair, OccurrenceSpec occurrence)
    {
        var searchLines = TextNormalizer.SplitLines(pair.SearchText);
        if (searchLines.Count == 0)
        {
            return GraftResult<IReadOnlyList<MatchResult>>.Fail(ErrorCode.E101, "SEARCH部が空です", pair.SourceLine);
        }

        var (stage, matches) = TextNormalizer.FindStagedMatches(fileLines, searchLines);
        if (matches.Count > 0)
        {
            return SelectByOccurrence(fileLines, pair, occurrence, stage, matches);
        }

        if (_options.AllowSimilarityMatch)
        {
            var best = SimilarityScorer.FindBestMatch(fileLines, searchLines, _options.SimilarityThreshold);
            if (best is not null)
            {
                var match = new LineMatch { StartLine = best.StartLine, LineCount = best.LineCount };
                var result = BuildResult(fileLines, pair, MatchStage.Similarity, match,
                    similarity: best.Similarity, needsConfirmation: true);
                return GraftResult<IReadOnlyList<MatchResult>>.Ok(new[] { result });
            }
        }

        return GraftResult<IReadOnlyList<MatchResult>>.Fail(ErrorCode.E101, line: pair.SourceLine);
    }

    private GraftResult<IReadOnlyList<MatchResult>> SelectByOccurrence(IReadOnlyList<string> fileLines,
        SearchReplacePair pair, OccurrenceSpec occurrence, MatchStage stage, IReadOnlyList<LineMatch> matches)
    {
        if (occurrence.All)
        {
            var all = matches
                .OrderByDescending(m => m.StartLine)
                .Select(m => BuildResult(fileLines, pair, stage, m, similarity: 1.0, needsConfirmation: false))
                .ToArray();
            return GraftResult<IReadOnlyList<MatchResult>>.Ok(all);
        }

        if (occurrence.IsDefault)
        {
            if (matches.Count > 1)
            {
                return GraftResult<IReadOnlyList<MatchResult>>.Fail(
                    ErrorCode.E102, $"{matches.Count}箇所でマッチしました", pair.SourceLine);
            }

            var single = BuildResult(fileLines, pair, stage, matches[0], similarity: 1.0, needsConfirmation: false);
            return GraftResult<IReadOnlyList<MatchResult>>.Ok(new[] { single });
        }

        if (occurrence.Index < 1 || occurrence.Index > matches.Count)
        {
            return GraftResult<IReadOnlyList<MatchResult>>.Fail(
                ErrorCode.E101, $"OCCURRENCE={occurrence.Index} は範囲外です（{matches.Count}箇所）", pair.SourceLine);
        }

        var chosen = BuildResult(fileLines, pair, stage, matches[occurrence.Index - 1],
            similarity: 1.0, needsConfirmation: false);
        return GraftResult<IReadOnlyList<MatchResult>>.Ok(new[] { chosen });
    }

    private static MatchResult BuildResult(IReadOnlyList<string> fileLines, SearchReplacePair pair,
        MatchStage stage, LineMatch match, double similarity, bool needsConfirmation)
    {
        var applied = stage == MatchStage.RelativeIndent
            ? ApplyIndentCorrection(fileLines, pair, match.StartLine)
            : pair.ReplaceText;

        return new MatchResult
        {
            Stage = stage,
            StartLine = match.StartLine,
            LineCount = match.LineCount,
            AppliedReplacement = applied,
            Similarity = similarity,
            NeedsConfirmation = needsConfirmation,
        };
    }

    private static string ApplyIndentCorrection(IReadOnlyList<string> fileLines, SearchReplacePair pair, int startLine)
    {
        var searchLines = TextNormalizer.SplitLines(pair.SearchText);
        var searchFirstLine = searchLines.Count > 0 ? searchLines[0] : string.Empty;
        var delta = TextNormalizer.LeadingWhitespace(fileLines[startLine]).Length
                    - TextNormalizer.LeadingWhitespace(searchFirstLine).Length;
        var dominantChar = TextNormalizer.DominantIndentChar(fileLines);
        return TextNormalizer.ApplyIndentCorrection(pair.ReplaceText, delta, dominantChar);
    }
}
