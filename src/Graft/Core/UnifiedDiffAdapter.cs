using System.Linq;

namespace Graft.Core;

/// <summary>
/// unified diff 形式（<c>--- a/X</c> / <c>+++ b/X</c> / <c>@@ ... @@</c>）のパッチ本文を、
/// 既存の SEARCH/REPLACE ペア・FULL形式ブロックへ変換するアダプタ（仕様書5章の拡張）。
///
/// 変換後は <see cref="PatchParser"/> が作る <see cref="Patch"/> と全く同じ形で
/// マッチング（<see cref="MatchEngine"/>）・プレビュー・適用・リビジョンのパイプラインへ流せる。
/// 行番号のずれには関知しない（unified diff のハンク番号は使わず、文脈行だけを頼りに
/// MatchEngine 側の段階的マッチングで位置を特定する）。
/// </summary>
public static class UnifiedDiffAdapter
{
    private const string ImportSummary = "unified diff の適用";
    private const string ImportType = "chore";
    private const string DevNull = "/dev/null";

    // ------------------------------------------------------------------
    // 判定
    // ------------------------------------------------------------------

    /// <summary>
    /// テキストが unified diff として解釈できるかどうかを判定する。
    /// "--- " "+++ " のヘッダ対の後に "@@" ハンクが1つ以上続く箇所があれば true とする。
    /// ```diff 等のコードフェンス行は判定前に取り除く。
    /// </summary>
    public static bool IsUnifiedDiff(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var lines = StripFences(PatchTextUtil.SplitRawLines(text));

        for (var i = 0; i + 1 < lines.Count; i++)
        {
            if (!IsFileHeaderPair(lines, i)) continue;
            for (var j = i + 2; j < lines.Count; j++)
            {
                if (IsFileHeaderPair(lines, j)) break;
                if (lines[j].StartsWith("@@", StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------
    // 解析
    // ------------------------------------------------------------------

    /// <summary>
    /// unified diff 本文を解析し <see cref="Patch"/> を組み立てる。summary が無いため
    /// メタは固定文言（"unified diff の適用" / type=chore）を補い、
    /// requireSummary 設定の必須チェックに引っかからないようにする。
    /// </summary>
    public static GraftResult<Patch> Parse(string patchText)
    {
        var lines = StripFences(PatchTextUtil.SplitRawLines(patchText));
        var blocks = new List<PatchBlock>();
        var issues = new List<GraftIssue>();

        var i = 0;
        while (i < lines.Count)
        {
            if (!IsFileHeaderPair(lines, i))
            {
                i++;
                continue;
            }

            var (block, issue, next) = ParseFileSection(lines, i);
            if (block is not null) blocks.Add(block);
            if (issue is not null) issues.Add(issue);
            i = next;
        }

        if (blocks.Count == 0)
        {
            var failIssues = issues.Count > 0 ? issues : new List<GraftIssue> { GraftIssue.Of(ErrorCode.E001, line: 1) };
            return GraftResult<Patch>.Fail(failIssues);
        }

        var meta = new PatchMeta { Summary = ImportSummary, Type = ImportType };
        // 修正依頼文が「受け取ったのと同じ形式で」と頼めるよう、形式をブロックとパッチの両方に刻む。
        var patch = new Patch
        {
            Meta = meta,
            Blocks = blocks.Select(b => b with { SourceFormat = PatchFormat.UnifiedDiff }).ToList(),
            RawText = patchText,
            Format = PatchFormat.UnifiedDiff,
            IsTruncated = false,
        };
        return GraftResult<Patch>.Ok(patch, issues);
    }

    private static bool IsFileHeaderPair(IReadOnlyList<string> lines, int i)
        => i + 1 < lines.Count
           && lines[i].StartsWith("--- ", StringComparison.Ordinal)
           && lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal);

    /// <summary>1ファイルセクション（ヘッダ2行＋続くハンク群）を1ブロックへ変換する。</summary>
    private static (PatchBlock? Block, GraftIssue? Issue, int Next) ParseFileSection(
        IReadOnlyList<string> lines, int i)
    {
        var headerLine = i + 1;
        var minusPath = ExtractHeaderPath(lines[i], "--- ");
        var plusPath = ExtractHeaderPath(lines[i + 1], "+++ ");
        i += 2;

        var hunks = new List<Hunk>();
        while (i < lines.Count && lines[i].StartsWith("@@", StringComparison.Ordinal))
        {
            var (hunk, next) = ParseHunk(lines, i);
            hunks.Add(hunk);
            i = next;
        }

        if (plusPath == DevNull)
        {
            if (!TryNormalize(minusPath, headerLine, out var deletedPath, out var deleteIssue))
                return (null, deleteIssue, i);
            return (new DeleteBlock { Path = deletedPath, HeaderLine = headerLine }, null, i);
        }

        if (!TryNormalize(plusPath, headerLine, out var targetPath, out var pathIssue))
            return (null, pathIssue, i);

        var isNewFile = minusPath == DevNull;
        var (block, issue) = isNewFile
            ? BuildFullContentBlock(targetPath, headerLine, hunks)
            : BuildSearchReplaceBlock(targetPath, headerLine, hunks);
        return (block, issue, i);
    }

    private static (PatchBlock? Block, GraftIssue? Issue) BuildFullContentBlock(
        string path, int headerLine, List<Hunk> hunks)
    {
        var content = string.Join('\n', hunks.SelectMany(h => h.ReplaceLines()));
        var block = new FullContentBlock { Path = path, HeaderLine = headerLine, Content = content };
        return (block, null);
    }

    private static (PatchBlock? Block, GraftIssue? Issue) BuildSearchReplaceBlock(
        string path, int headerLine, List<Hunk> hunks)
    {
        var pairs = new List<SearchReplacePair>();
        GraftIssue? skipIssue = null;

        foreach (var hunk in hunks)
        {
            var search = string.Join('\n', hunk.SearchLines());
            var replace = string.Join('\n', hunk.ReplaceLines());
            if (search.Length == 0)
            {
                skipIssue = GraftIssue.Of(ErrorCode.E003,
                    detail: "文脈行の無いハンクは適用位置を特定できないため取り込みから除外しました",
                    line: hunk.HeaderLine, path: path, severity: Severity.Warning);
                continue;
            }
            pairs.Add(new SearchReplacePair { SearchText = search, ReplaceText = replace, SourceLine = hunk.HeaderLine });
        }

        if (pairs.Count == 0)
        {
            var issue = skipIssue is null
                ? GraftIssue.Of(ErrorCode.E003, line: headerLine, path: path)
                : skipIssue with { Severity = Severity.Error };
            return (null, issue);
        }

        var block = new SearchReplaceBlock { Path = path, HeaderLine = headerLine, Pairs = pairs };
        return (block, skipIssue);
    }

    // ------------------------------------------------------------------
    // ハンク
    // ------------------------------------------------------------------

    /// <summary>1ハンクの内容。各行は先頭1文字（種別）を剥がした状態で保持する。</summary>
    private sealed record Hunk(int HeaderLine, IReadOnlyList<(char Kind, string Text)> Lines)
    {
        /// <summary>SEARCH = 文脈行(' ') + 削除行('-')。</summary>
        public IEnumerable<string> SearchLines() => Lines.Where(l => l.Kind != '+').Select(l => l.Text);

        /// <summary>REPLACE = 文脈行(' ') + 追加行('+')。</summary>
        public IEnumerable<string> ReplaceLines() => Lines.Where(l => l.Kind != '-').Select(l => l.Text);
    }

    /// <summary>ハンクの見出し <c>@@ -a,b +c,d @@</c> の行数部分（b と d。省略時は 1）。</summary>
    private static readonly System.Text.RegularExpressions.Regex HunkHeaderCounts = new(
        @"^@@ -\d+(?:,(?<old>\d+))? \+\d+(?:,(?<new>\d+))? @@",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// 見出しが宣言した、ハンクに含まれる元側・新側の行数。読み取れない見出し
    /// （数字が無い、形が違う、桁あふれ）は null を返し、従来どおり行数を使わない判定にする。
    /// </summary>
    private static (int Old, int New)? ParseDeclaredCounts(string headerLine)
    {
        var m = HunkHeaderCounts.Match(headerLine);
        if (!m.Success) return null;

        var oldText = m.Groups["old"].Success ? m.Groups["old"].Value : "1";
        var newText = m.Groups["new"].Success ? m.Groups["new"].Value : "1";
        if (!int.TryParse(oldText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var oldCount)
            || !int.TryParse(newText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var newCount))
        {
            return null;
        }

        return (oldCount, newCount);
    }

    private static (Hunk Hunk, int Next) ParseHunk(IReadOnlyList<string> lines, int i)
    {
        var headerLine = i + 1;
        // 見出しが宣言した行数。ハンクの中にいる間（宣言した行をまだ読み切っていない間）は、
        // 「--- 」「+++ 」で始まる行をファイルのヘッダとは見なさない。
        //
        // 【なぜ必要か】 SQL や Lua のコメントは「-- 」で始まる。そのコメント行を削除すると
        // 差分では「-」を頭に付けた「--- コメント」になり、直後にコメントを追加する行が
        // 「++ 」で始まると「+++ ...」になる。この2行は見た目がファイルのヘッダ
        // （--- a/x と +++ b/x）と同じなので、以前はハンクの途中で打ち切られ、以降の行が
        // 取り込まれなかった（実測: 削除行「-- old」＋追加行「++ new」のハンクが、
        // ファイルのヘッダと誤読された）。
        // 判定は「この削除行1つと追加行1つを入れる余地が、宣言した行数にまだ残っているか」。
        // 余地があればハンクの本文、使い切っていればファイルのヘッダとして扱う。
        // 行数を宣言していない・読み取れない見出し（AIが数字を書かない等）では、従来どおり行数を使わない。
        var declared = ParseDeclaredCounts(lines[i]);
        var remainingOld = declared?.Old ?? 0;
        var remainingNew = declared?.New ?? 0;
        i++; // "@@ ... @@" 見出し行を消費

        var body = new List<(char, string)>();
        while (i < lines.Count)
        {
            var line = lines[i];
            if (line.StartsWith("@@", StringComparison.Ordinal)) break;
            var insideDeclaredHunk = declared is not null && remainingOld >= 1 && remainingNew >= 1;
            if (!insideDeclaredHunk && IsFileHeaderPair(lines, i)) break;

            // "\ No newline at end of file" は無視してよい（クラッシュしないことのみ保証）。
            if (line.StartsWith("\\ No newline", StringComparison.Ordinal)) { i++; continue; }

            if (line.Length == 0)
            {
                // 一部の生成AIは空の文脈行から先頭スペースを落とすため、寛容に文脈行として扱う。
                body.Add((' ', string.Empty));
                remainingOld--;
                remainingNew--;
                i++;
                continue;
            }

            var marker = line[0];
            if (marker is ' ' or '-' or '+')
            {
                body.Add((marker, line[1..]));
                if (marker != '+') remainingOld--;
                if (marker != '-') remainingNew--;
                i++;
                continue;
            }

            break; // 未知の行はハンクの終わりとみなす
        }

        return (new Hunk(headerLine, body), i);
    }

    // ------------------------------------------------------------------
    // 共通ヘルパ
    // ------------------------------------------------------------------

    /// <summary>
    /// Markdownコードフェンス行（```diff 等）を取り除く。あわせて、末尾改行に由来する
    /// 最後の空行1つも取り除く（<see cref="PatchTextUtil.GetTailLines"/> と同じ考え方）。
    /// これを行わないと最後のハンクの本文へ余分な空の文脈行が混入してしまう。
    /// </summary>
    private static List<string> StripFences(string[] rawLines)
    {
        var result = new List<string>(rawLines.Length);
        foreach (var line in rawLines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal)) continue;
            result.Add(line);
        }
        if (result.Count > 0 && result[^1].Length == 0) result.RemoveAt(result.Count - 1);
        return result;
    }

    /// <summary>ヘッダ行から対象パスを取り出す。"a/" "b/" 前置とタブ区切りのタイムスタンプを除く。</summary>
    private static string ExtractHeaderPath(string headerLine, string prefix)
    {
        var rest = headerLine[prefix.Length..];
        var tabIdx = rest.IndexOf('\t');
        if (tabIdx >= 0) rest = rest[..tabIdx];
        rest = rest.TrimEnd();

        if (rest == DevNull) return DevNull;
        if (rest.Length > 2 && (rest.StartsWith("a/", StringComparison.Ordinal) || rest.StartsWith("b/", StringComparison.Ordinal)))
            rest = rest[2..];
        return rest;
    }

    /// <summary>仕様書4.7のパス表記ルールで正規化する。不正な形の場合は失敗として issue を返す。</summary>
    private static bool TryNormalize(string rawPath, int headerLine, out string normalized, out GraftIssue? issue)
    {
        if (string.IsNullOrEmpty(rawPath))
        {
            normalized = string.Empty;
            issue = GraftIssue.Of(ErrorCode.E002, detail: "パスが指定されていません", line: headerLine);
            return false;
        }
        if (!PatchTextUtil.TryNormalizePath(rawPath, out normalized))
        {
            issue = GraftIssue.Of(ErrorCode.E201, line: headerLine, path: rawPath);
            return false;
        }
        issue = null;
        return true;
    }
}
