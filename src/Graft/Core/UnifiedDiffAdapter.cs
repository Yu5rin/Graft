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
    private const string ImportSummary = "unified diff からの取り込み";
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
    /// メタは固定文言（"unified diff からの取り込み" / type=chore）を補い、
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
        var patch = new Patch { Meta = meta, Blocks = blocks, RawText = patchText, IsTruncated = false };
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

    private static (Hunk Hunk, int Next) ParseHunk(IReadOnlyList<string> lines, int i)
    {
        var headerLine = i + 1;
        i++; // "@@ ... @@" 見出し行を消費

        var body = new List<(char, string)>();
        while (i < lines.Count)
        {
            var line = lines[i];
            if (line.StartsWith("@@", StringComparison.Ordinal)) break;
            if (IsFileHeaderPair(lines, i)) break;

            // "\ No newline at end of file" は無視してよい（クラッシュしないことのみ保証）。
            if (line.StartsWith("\\ No newline", StringComparison.Ordinal)) { i++; continue; }

            if (line.Length == 0)
            {
                // 一部の生成AIは空の文脈行から先頭スペースを落とすため、寛容に文脈行として扱う。
                body.Add((' ', string.Empty));
                i++;
                continue;
            }

            var marker = line[0];
            if (marker is ' ' or '-' or '+')
            {
                body.Add((marker, line[1..]));
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
