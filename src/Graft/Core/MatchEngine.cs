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
            var scan = SimilarityScorer.Scan(fileLines, searchLines, _options.SimilarityThreshold);
            if (scan.Match is not null)
            {
                var best = scan.Match;
                var match = new LineMatch { StartLine = best.StartLine, LineCount = best.LineCount };
                var result = BuildResult(fileLines, pair, MatchStage.Similarity, match,
                    similarity: best.Similarity, needsConfirmation: true);
                return GraftResult<IReadOnlyList<MatchResult>>.Ok(new[] { result });
            }

            // 段階5は枝刈りで十分速くなったが（SimilarityScorerのクラスコメント参照）、
            // 「ほぼ同じ行が延々と続くファイル」のような病的な入力では、枝刈りが効かず
            // 際限なく時間を使い得る。そのためDPのセル数に予算を設けており、使い切ったら
            // 探索を打ち切る。利用者から見ると「一致しなかった」という結果は同じでも、
            // 「本当に似た箇所が無かった」のか「大きすぎて調べきれなかった」のかで
            // 次にやるべきこと（SEARCHを短くする・範囲指定に切り替える）が変わるため、
            // 理由を必ず添える。理由を書かずに黙って打ち切ると、利用者は
            // 「Graftが見落とした」としか受け取れない。
            if (scan.Aborted)
            {
                return GraftResult<IReadOnlyList<MatchResult>>.Fail(
                    ErrorCode.E101,
                    "ファイルが大きく似た行が多いため、類似度による照合（段階5）を途中で打ち切りました。"
                    + "SEARCH部を短くするか、範囲指定（アンカー）での指定をAIへ依頼してください",
                    pair.SourceLine);
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
                // 実機不具合対応: 対処文（ErrorCatalogのE102）で OCCURRENCE=1 を案内するため、
                // 「何番目まで指定できるのか」をここで具体的に添える。以前は件数しか出さず、
                // 利用者は 1〜N のどれを書けるのか画面から判断できなかった。
                return GraftResult<IReadOnlyList<MatchResult>>.Fail(
                    ErrorCode.E102,
                    $"{matches.Count}箇所でマッチしました（OCCURRENCE=1 〜 OCCURRENCE={matches.Count} を指定できます）",
                    pair.SourceLine);
            }

            var single = BuildResult(fileLines, pair, stage, matches[0], similarity: 1.0, needsConfirmation: false);
            return GraftResult<IReadOnlyList<MatchResult>>.Ok(new[] { single });
        }

        // ここへ来るのは OCCURRENCE を明示的に書いた場合だけ（OCCURRENCE=1 を含む）。
        // 実機不具合対応の要: 以前は Index==1 が「未指定」と区別できず、明示的に書いた
        // OCCURRENCE=1 が上の分岐へ吸い込まれて同じE102を返していた（OccurrenceSpec参照）。
        var index = occurrence.EffectiveIndex;
        if (index < 1 || index > matches.Count)
        {
            return GraftResult<IReadOnlyList<MatchResult>>.Fail(
                ErrorCode.E101, $"OCCURRENCE={index} は範囲外です（{matches.Count}箇所）", pair.SourceLine);
        }

        var chosen = BuildResult(fileLines, pair, stage, matches[index - 1],
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
