namespace Graft.Core;

/// <summary>
/// パッチ本文（仕様書4章）を解析し <see cref="Patch"/> を組み立てる。
/// ブロック外のテキストやMarkdownコードフェンスは無視し、4.9のエスケープ規則と
/// 4.10の切断検出を踏まえて、可能な限り最後まで解析を進める。
/// </summary>
public sealed class PatchParser
{
    /// <summary>パッチ全文を解析する。</summary>
    public GraftResult<Patch> Parse(string patchText)
    {
        // Graft形式のマーカーが1つも無く、unified diff として解釈できる場合はアダプタへ委譲する。
        // Graft形式のマーカーが混在する場合は従来どおりこのメソッドで解析する（マーカー優先）。
        if (!PatchTextDetector.HasGraftMarker(patchText) && UnifiedDiffAdapter.IsUnifiedDiff(patchText))
            return UnifiedDiffAdapter.Parse(patchText);

        var scanner = PatchScanner.Create(patchText);
        var blocks = new List<PatchBlock>();
        var meta = new PatchMeta();
        var metaSeen = false;
        var truncated = false;

        try
        {
            while (scanner.HasNext)
            {
                var (lineNumber, text) = scanner.Peek();
                if (!text.StartsWith("<<<<", StringComparison.Ordinal))
                {
                    scanner.Next();
                    continue;
                }

                if (IsPatchMetaHeader(text))
                {
                    meta = ParsePatchMeta(scanner);
                    metaSeen = true;
                    continue;
                }

                blocks.Add(DispatchBlockHeader(scanner, text, lineNumber));
            }
        }
        catch (TruncatedSignal)
        {
            truncated = true;
        }
        catch (SyntaxFailure failure)
        {
            return GraftResult<Patch>.Fail(failure.Issue);
        }

        if (!truncated && blocks.Count == 0)
            return GraftResult<Patch>.Fail(ErrorCode.E001, line: 1);

        return BuildResult(patchText, meta, metaSeen, blocks, truncated);
    }

    private static GraftResult<Patch> BuildResult(
        string patchText, PatchMeta meta, bool metaSeen, List<PatchBlock> blocks, bool truncated)
    {
        var issues = new List<GraftIssue>();
        if (!metaSeen || string.IsNullOrEmpty(meta.Summary))
            issues.Add(GraftIssue.Of(ErrorCode.E004, severity: Severity.Warning));

        var tailLines = Array.Empty<string>() as IReadOnlyList<string>;
        if (truncated)
        {
            tailLines = PatchTextUtil.GetTailLines(patchText, 3);
            issues.Add(GraftIssue.Of(ErrorCode.E005, severity: Severity.Warning));
        }

        var patch = new Patch
        {
            Meta = meta,
            Blocks = blocks,
            RawText = patchText,
            IsTruncated = truncated,
            TailLines = tailLines,
        };
        return GraftResult<Patch>.Ok(patch, issues);
    }

    // ------------------------------------------------------------------
    // ヘッダ判定・振り分け
    // ------------------------------------------------------------------

    private static bool IsPatchMetaHeader(string text) => text.TrimEnd() == "<<<< PATCH";

    private static PatchBlock DispatchBlockHeader(PatchScanner scanner, string text, int lineNumber)
    {
        if (text.StartsWith("<<<< FILE:", StringComparison.Ordinal))
            return ParseFileBlock(scanner, text, lineNumber);
        if (text.StartsWith("<<<< DELETE:", StringComparison.Ordinal))
            return ParseDeleteBlock(scanner, text, lineNumber);
        if (text.StartsWith("<<<< RENAME:", StringComparison.Ordinal))
            return ParseRenameBlock(scanner, text, lineNumber);
        if (text.StartsWith("<<<< MKDIR:", StringComparison.Ordinal))
            return ParseMkdirBlock(scanner, text, lineNumber);
        if (text.StartsWith("<<<< APPEND:", StringComparison.Ordinal))
            return ParseAppendPrependBlock(scanner, text, lineNumber, isAppend: true);
        if (text.StartsWith("<<<< PREPEND:", StringComparison.Ordinal))
            return ParseAppendPrependBlock(scanner, text, lineNumber, isAppend: false);

        throw Fail(ErrorCode.E002, lineNumber, text.Trim());
    }

    // ------------------------------------------------------------------
    // 4.2 PATCH メタ
    // ------------------------------------------------------------------

    private static PatchMeta ParsePatchMeta(PatchScanner scanner)
    {
        var (metaLine, metaText) = scanner.Next(); // "<<<< PATCH" 行を消費
        var result = scanner.CollectBody(t => t.TrimEnd() == ">>>>");
        if (result.Outcome == BodyOutcome.Truncated) throw new TruncatedSignal();
        if (result.Outcome == BodyOutcome.Broken) throw BrokenBodyFailure(result, metaLine, metaText, ">>>>");

        string? summary = null;
        string? type = null;
        var baseHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in result.Lines)
        {
            var line = raw.Trim();
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim().ToLowerInvariant();
            var value = line[(idx + 1)..].Trim();
            switch (key)
            {
                case "summary": summary = value.Length == 0 ? null : value; break;
                case "type": type = value.Length == 0 ? null : value; break;
                case "base": baseHashes = PatchTextUtil.ParseBaseHashes(value); break;
            }
        }
        return new PatchMeta { Summary = summary, Type = type, BaseHashes = baseHashes };
    }

    // ------------------------------------------------------------------
    // 4.5 FULL形式 / 4.3 SR形式（<<<< FILE: ...）
    // ------------------------------------------------------------------

    private static PatchBlock ParseFileBlock(PatchScanner scanner, string headerText, int lineNumber)
    {
        var afterPrefix = headerText["<<<< FILE:".Length..];
        var (rawPath, isFull, fence, occurrence, description) = ParseHeaderTokens(afterPrefix, hasPath: true);
        var path = NormalizePathOrThrow(rawPath, lineNumber);
        scanner.Next(); // FILE ヘッダ行を消費

        return isFull
            ? ParseFullContentBody(scanner, path, lineNumber, headerText, fence, occurrence, description)
            : ParseSearchReplaceBody(scanner, path, lineNumber, occurrence);
    }

    private static FullContentBlock ParseFullContentBody(
        PatchScanner scanner, string path, int lineNumber, string headerText, string? fence, OccurrenceSpec? occurrence, string? description)
    {
        var result = scanner.CollectBody(BlockEndTerminator(fence));
        if (result.Outcome == BodyOutcome.Truncated) throw new TruncatedSignal();
        if (result.Outcome == BodyOutcome.Broken) throw BrokenBodyFailure(result, lineNumber, headerText, EndMarkerText(fence));

        return new FullContentBlock
        {
            Path = path,
            HeaderLine = lineNumber,
            Content = string.Join('\n', result.Lines),
            Fence = fence,
            Description = description,
            Occurrence = occurrence ?? OccurrenceSpec.Single,
        };
    }

    /// <summary>FULL形式・APPEND/PREPENDの終了マーカーの表示文字列（FENCE指定を反映）。</summary>
    private static string EndMarkerText(string? fence) => fence is null ? ">>>> END" : $">>>> END:{fence}";

    private static Func<string, bool> BlockEndTerminator(string? fence)
    {
        var expected = EndMarkerText(fence);
        return t => t.TrimEnd() == expected;
    }

    // ------------------------------------------------------------------
    // 4.3 / 4.4 SEARCH / REPLACE ペアの連結
    // ------------------------------------------------------------------

    private static SearchReplaceBlock ParseSearchReplaceBody(
        PatchScanner scanner, string path, int lineNumber, OccurrenceSpec? headerOccurrence)
    {
        var pairs = new List<SearchReplacePair>();
        var occurrence = headerOccurrence;

        while (scanner.HasNext)
        {
            var (searchLine, text) = scanner.Peek();
            if (!TryParseSearchMarker(text, out var isRange, out var pairOccurrence, out var description))
            {
                if (text.StartsWith("<<<<", StringComparison.Ordinal)) break;
                scanner.Next();
                continue;
            }

            scanner.Next();
            if (pairOccurrence is not null) occurrence = pairOccurrence;
            pairs.Add(ParseSearchReplacePair(scanner, searchLine, text, isRange, description));
        }

        if (pairs.Count == 0)
        {
            if (!scanner.HasNext) throw new TruncatedSignal();
            throw Fail(ErrorCode.E002, lineNumber, "FILEヘッダの後にSEARCHペアがありません");
        }

        return new SearchReplaceBlock
        {
            Path = path,
            HeaderLine = lineNumber,
            Pairs = pairs,
            Occurrence = occurrence ?? OccurrenceSpec.Single,
        };
    }

    private static SearchReplacePair ParseSearchReplacePair(
        PatchScanner scanner, int searchLine, string searchMarkerText, bool isRange, string? description)
    {
        var searchResult = scanner.CollectBody(t => t.TrimEnd() == "=======");
        if (searchResult.Outcome == BodyOutcome.Truncated) throw new TruncatedSignal();
        if (searchResult.Outcome == BodyOutcome.Broken)
            throw BrokenBodyFailure(searchResult, searchLine, searchMarkerText, "=======");
        if (searchResult.Lines.Count == 0 || searchResult.Lines.All(l => l.Length == 0))
            throw Fail(ErrorCode.E003, searchLine);

        // "=======" が見つかった行がREPLACE本文の開始行になる（Completedのため必ず値を持つ）。
        var replaceStartLine = searchResult.TerminatorLine!.Value;
        var replaceResult = scanner.CollectBody(t => t.TrimEnd() == ">>>>>>> REPLACE");
        if (replaceResult.Outcome == BodyOutcome.Truncated) throw new TruncatedSignal();
        if (replaceResult.Outcome == BodyOutcome.Broken)
            throw BrokenBodyFailure(replaceResult, replaceStartLine, "=======", ">>>>>>> REPLACE");

        return new SearchReplacePair
        {
            SearchText = string.Join('\n', searchResult.Lines),
            ReplaceText = string.Join('\n', replaceResult.Lines),
            Description = description,
            IsRange = isRange,
            SourceLine = searchLine,
        };
    }

    private static bool TryParseSearchMarker(
        string text, out bool isRange, out OccurrenceSpec? occurrence, out string? description)
    {
        isRange = false;
        occurrence = null;
        description = null;

        string rest;
        if (text.StartsWith("<<<<<<< SEARCH-RANGE", StringComparison.Ordinal))
        {
            isRange = true;
            rest = text["<<<<<<< SEARCH-RANGE".Length..];
        }
        else if (text.StartsWith("<<<<<<< SEARCH", StringComparison.Ordinal))
        {
            rest = text["<<<<<<< SEARCH".Length..];
        }
        else
        {
            return false;
        }

        var (_, _, _, occ, desc) = ParseHeaderTokens(rest, hasPath: false);
        occurrence = occ;
        description = desc;
        return true;
    }

    // ------------------------------------------------------------------
    // 4.6 ファイル操作ブロック（DELETE / RENAME / MKDIR / APPEND / PREPEND）
    // ------------------------------------------------------------------

    private static DeleteBlock ParseDeleteBlock(PatchScanner scanner, string headerText, int lineNumber)
    {
        var raw = headerText["<<<< DELETE:".Length..].Trim();
        var path = NormalizePathOrThrow(raw, lineNumber);
        scanner.Next();
        return new DeleteBlock { Path = path, HeaderLine = lineNumber };
    }

    private static MkdirBlock ParseMkdirBlock(PatchScanner scanner, string headerText, int lineNumber)
    {
        var raw = headerText["<<<< MKDIR:".Length..].Trim();
        var path = NormalizePathOrThrow(raw, lineNumber);
        scanner.Next();
        return new MkdirBlock { Path = path, HeaderLine = lineNumber };
    }

    private static RenameBlock ParseRenameBlock(PatchScanner scanner, string headerText, int lineNumber)
    {
        var raw = headerText["<<<< RENAME:".Length..];
        var arrowIdx = raw.IndexOf("->", StringComparison.Ordinal);
        if (arrowIdx < 0)
            throw Fail(ErrorCode.E002, lineNumber, "RENAMEの書式が不正です（\"旧パス -> 新パス\" の形式で指定してください）");

        var fromPath = NormalizePathOrThrow(raw[..arrowIdx].Trim(), lineNumber);
        var toPath = NormalizePathOrThrow(raw[(arrowIdx + 2)..].Trim(), lineNumber);
        scanner.Next();
        return new RenameBlock { Path = fromPath, ToPath = toPath, HeaderLine = lineNumber };
    }

    private static PatchBlock ParseAppendPrependBlock(
        PatchScanner scanner, string headerText, int lineNumber, bool isAppend)
    {
        var prefixLength = (isAppend ? "<<<< APPEND:" : "<<<< PREPEND:").Length;
        var afterPrefix = headerText[prefixLength..];
        var (rawPath, _, fence, occurrence, description) = ParseHeaderTokens(afterPrefix, hasPath: true);
        var path = NormalizePathOrThrow(rawPath, lineNumber);
        scanner.Next();

        var result = scanner.CollectBody(BlockEndTerminator(fence));
        if (result.Outcome == BodyOutcome.Truncated) throw new TruncatedSignal();
        if (result.Outcome == BodyOutcome.Broken)
            throw BrokenBodyFailure(result, lineNumber, headerText, EndMarkerText(fence));

        var content = string.Join('\n', result.Lines);
        var occ = occurrence ?? OccurrenceSpec.Single;
        return isAppend
            ? new AppendBlock { Path = path, HeaderLine = lineNumber, Content = content, Fence = fence, Description = description, Occurrence = occ }
            : new PrependBlock { Path = path, HeaderLine = lineNumber, Content = content, Fence = fence, Description = description, Occurrence = occ };
    }

    // ------------------------------------------------------------------
    // 共通ヘルパ
    // ------------------------------------------------------------------

    /// <summary>
    /// ヘッダ行の残り部分から、先頭パス（任意）・属性（MODE=FULL / FENCE= / OCCURRENCE=）・
    /// "#" 以降の説明文を取り出す。<paramref name="hasPath"/> が false の場合は
    /// 先頭トークンをパスとして消費せず、すべて属性として解釈する（SEARCH マーカー行用）。
    /// </summary>
    private static (string Path, bool IsFull, string? Fence, OccurrenceSpec? Occurrence, string? Description)
        ParseHeaderTokens(string afterPrefix, bool hasPath)
    {
        var hashIdx = afterPrefix.IndexOf('#');
        var mainPart = hashIdx >= 0 ? afterPrefix[..hashIdx] : afterPrefix;
        var description = hashIdx >= 0 ? afterPrefix[(hashIdx + 1)..].Trim() : null;
        if (string.IsNullOrEmpty(description)) description = null;

        var tokens = mainPart.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var path = string.Empty;
        var attrTokens = tokens.AsEnumerable();
        if (hasPath)
        {
            path = tokens.Length > 0 ? tokens[0] : string.Empty;
            attrTokens = tokens.Skip(1);
        }

        var isFull = false;
        string? fence = null;
        OccurrenceSpec? occurrence = null;
        foreach (var token in attrTokens)
        {
            if (token == "MODE=FULL") isFull = true;
            else if (token.StartsWith("FENCE=", StringComparison.Ordinal)) fence = token["FENCE=".Length..];
            else if (token.StartsWith("OCCURRENCE=", StringComparison.Ordinal))
                occurrence = PatchTextUtil.ParseOccurrence(token["OCCURRENCE=".Length..]);
        }
        return (path, isFull, fence, occurrence, description);
    }

    /// <summary>4.7 のパス表記ルールに従い正規化する。不正な形の場合は E201 で打ち切る。</summary>
    private static string NormalizePathOrThrow(string rawPath, int lineNumber)
    {
        if (rawPath.Length == 0) throw Fail(ErrorCode.E002, lineNumber, "パスが指定されていません");
        if (!PatchTextUtil.TryNormalizePath(rawPath, out var normalized))
            throw Fail(ErrorCode.E201, lineNumber, path: rawPath);
        return normalized;
    }

    private static SyntaxFailure Fail(ErrorCode code, int? line, string? detail = null, string? path = null)
        => new(GraftIssue.Of(code, detail, line, path));

    /// <summary>
    /// 本文収集（CollectBody）がBrokenで終わった場合の失敗を組み立てる。実際の事故対応:
    /// 「1行目の PATCH メタが >>>> で閉じられないまま、5行目で次の PATCH メタが始まった」
    /// というパッチが、原因の特定できないE006（エスケープ規則の不整合）としてしか
    /// 報告されず、利用者が本当の原因（1つ目のブロックの閉じ忘れ）に辿り着けなかった問題を直す。
    ///
    /// 【E006と使い分ける理由】現れた行が「新しいブロックの開始行として解釈できる形」
    /// （"&lt;&lt;&lt;&lt;"で始まる行）かどうかで出し分ける。Graft形式のブロックヘッダ
    /// （&lt;&lt;&lt;&lt; PATCH・&lt;&lt;&lt;&lt; FILE:・&lt;&lt;&lt;&lt;&lt;&lt;&lt; SEARCH 等）は
    /// すべて"&lt;&lt;&lt;&lt;"始まりであり、逆にAIが生成する通常の本文（コード・文章）が
    /// 偶然この形になることは実務上ほぼ無い。そのためこの形の行は「新しいブロックが
    /// 始まった」と断定してよく、専用のE008で開始行・出現行の両方を伝える。
    /// 一方 ">>>>" や "=======" だけの行は、あらゆるブロックヘッダの先頭にはなり得ない
    /// （閉じマーカー・区切り線としてのみ使われる）ため「次のブロックが始まった」とは言えず、
    /// 「本文として書きたかった記号のエスケープ忘れ」である可能性の方が高い。この場合は
    /// 従来どおりE006とし、文言にエスケープの案内を残す（区別できない・すべきでない
    /// と判断したわけではなく、記号の種類から機械的に判定できると判断した）。
    /// </summary>
    private static SyntaxFailure BrokenBodyFailure(
        BodyResult result, int startLine, string startMarkerText, string expectedTerminatorText)
    {
        var brokenLine = result.BrokenLine!.Value;
        var brokenText = result.BrokenText ?? string.Empty;
        var openMarker = startMarkerText.Trim();

        if (brokenText.StartsWith("<<<<", StringComparison.Ordinal))
        {
            var detail =
                $"{startLine}行目の \"{openMarker}\" ブロックが \"{expectedTerminatorText}\" で閉じられていないまま、" +
                $"{brokenLine}行目で次のブロック \"{brokenText}\" が始まっています。" +
                $"{startLine}行目のブロックを \"{expectedTerminatorText}\" で閉じるか、{brokenLine}行目をこのブロックの" +
                "本文として使いたい場合は行頭に \\ を付けてエスケープしてください。";
            return Fail(ErrorCode.E008, brokenLine, detail);
        }

        var mismatchDetail =
            $"{startLine}行目の \"{openMarker}\" ブロックの本文を集めている途中、{brokenLine}行目に \"{brokenText}\" という" +
            $"記号だけの行が現れ、期待する終了マーカー \"{expectedTerminatorText}\" と一致しませんでした。" +
            "本文としてこの記号を使いたい場合は、行頭に \\ を付けてエスケープしてください。";
        return Fail(ErrorCode.E006, brokenLine, mismatchDetail);
    }

    /// <summary>4.10 の切断検出を表す内部制御用シグナル。パーサ内部でのみ捕捉する。</summary>
    private sealed class TruncatedSignal : Exception;

    /// <summary>致命的な構文エラーを表す内部制御用シグナル。パーサ内部でのみ捕捉する。</summary>
    private sealed class SyntaxFailure(GraftIssue issue) : Exception
    {
        public GraftIssue Issue { get; } = issue;
    }
}
