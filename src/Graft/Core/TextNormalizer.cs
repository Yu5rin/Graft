namespace Graft.Core;

/// <summary>
/// マッチング段階1〜4（完全一致〜空行無視）で見つかった1件の一致範囲。
/// 行インデックスは呼び出し側が渡した行配列（多くはファイル全体）の0始まりの相対位置。
/// </summary>
public sealed record LineMatch
{
    /// <summary>一致開始行（0始まり）。</summary>
    public required int StartLine { get; init; }

    /// <summary>一致した行数。</summary>
    public required int LineCount { get; init; }
}

/// <summary>
/// 改行コードの違いを吸収した行分割と、マッチング段階1〜4の判定を提供するユーティリティ。
/// 段階5（類似度）は <see cref="SimilarityScorer"/> が担当する。
/// </summary>
public static class TextNormalizer
{
    /// <summary>CRLF・LF・CRのいずれも行区切りとして扱い、行の配列に分割する。</summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\r' || c == '\n')
            {
                lines.Add(text.Substring(start, i - start));
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                i++;
                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start < text.Length) lines.Add(text.Substring(start));
        return lines;
    }

    /// <summary>
    /// 指定文字数を超える行が含まれるかどうかを判定する（改行コードの種類は問わない）。
    /// 課題3: 構文強調・折り返しの計算コストは行数ではなくその行の文字数に比例して増える
    /// （仕様書18章の「10万行」という性能目標は行数を前提にしており、1行が極端に長い
    /// ケースは想定していない）。呼び出し元（<see cref="Graft.Editor.DocumentSession"/>）は
    /// この判定を使って構文強調・折り返し・括弧対応付けを自動的に無効化する。
    /// しきい値を超えた時点で走査を打ち切るため、該当しない大半のファイルでも
    /// 全文を1回なめる以上のコストはかからない。
    /// </summary>
    public static bool HasLineLongerThan(string text, int threshold)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (threshold < 0) return text.Length > 0;

        var runLength = 0;
        foreach (var c in text)
        {
            if (c is '\r' or '\n')
            {
                runLength = 0;
                continue;
            }

            runLength++;
            if (runLength > threshold) return true;
        }

        return false;
    }

    /// <summary>行末の空白（スペース・タブ）を取り除く。</summary>
    public static string TrimTrailingWhitespace(string line) => line.TrimEnd(' ', '\t');

    /// <summary>行頭の空白文字列（インデント）を返す。</summary>
    public static string LeadingWhitespace(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return line.Substring(0, i);
    }

    /// <summary>空行（空文字列または空白のみ）かどうか。</summary>
    public static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    /// <summary>行群全体で優勢なインデント文字（スペースかタブ）を判定する。優劣がなければスペース。</summary>
    public static char DominantIndentChar(IReadOnlyList<string> fileLines)
    {
        var spaceCount = 0;
        var tabCount = 0;
        foreach (var line in fileLines)
        {
            foreach (var ch in LeadingWhitespace(line))
            {
                if (ch == ' ') spaceCount++;
                else if (ch == '\t') tabCount++;
            }
        }

        return tabCount > spaceCount ? '\t' : ' ';
    }

    /// <summary>
    /// 段階1（完全一致）〜段階4（空行無視）の順にフォールバックし、最初に1件以上見つかった段階の
    /// 結果を返す。全段階で見つからない場合は <see cref="MatchStage.Failed"/> と空リストを返す。
    /// </summary>
    public static (MatchStage Stage, IReadOnlyList<LineMatch> Matches) FindStagedMatches(
        IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines)
    {
        if (searchLines.Count == 0) return (MatchStage.Failed, Array.Empty<LineMatch>());

        var exact = FindExact(fileLines, searchLines);
        if (exact.Count > 0) return (MatchStage.Exact, exact);

        var trailing = FindTrailingWhitespaceIgnored(fileLines, searchLines);
        if (trailing.Count > 0) return (MatchStage.TrailingWhitespace, trailing);

        var relative = FindRelativeIndent(fileLines, searchLines);
        if (relative.Count > 0) return (MatchStage.RelativeIndent, relative);

        var blankIgnored = FindIgnoringBlankLines(fileLines, searchLines);
        if (blankIgnored.Count > 0) return (MatchStage.IgnoreBlankLines, blankIgnored);

        return (MatchStage.Failed, Array.Empty<LineMatch>());
    }

    /// <summary>
    /// 段階3のとき、REPLACE部の各行に「該当開始行のインデント − SEARCH部先頭行のインデント」を
    /// 加算する（仕様書5.2）。差が負なら該当分を取り除く。空行は補正しない。
    /// </summary>
    public static string ApplyIndentCorrection(string replaceText, int deltaChars, char dominantChar)
    {
        if (replaceText.Length == 0) return replaceText;
        var lines = SplitLines(replaceText);
        var adjusted = lines.Select(line => ApplyIndentToLine(line, deltaChars, dominantChar));
        return string.Join("\n", adjusted);
    }

    private static string ApplyIndentToLine(string line, int deltaChars, char dominantChar)
    {
        if (IsBlank(line)) return line;
        var ws = LeadingWhitespace(line);
        var rest = line.Substring(ws.Length);
        var newLen = Math.Max(0, ws.Length + deltaChars);
        return new string(dominantChar, newLen) + rest;
    }

    private static List<LineMatch> FindExact(IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines)
    {
        var results = new List<LineMatch>();
        var n = searchLines.Count;
        for (var i = 0; i + n <= fileLines.Count; i++)
        {
            var matched = true;
            for (var j = 0; j < n; j++)
            {
                if (fileLines[i + j] != searchLines[j]) { matched = false; break; }
            }

            if (matched) results.Add(new LineMatch { StartLine = i, LineCount = n });
        }

        return results;
    }

    private static List<LineMatch> FindTrailingWhitespaceIgnored(
        IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines)
    {
        var results = new List<LineMatch>();
        var n = searchLines.Count;
        for (var i = 0; i + n <= fileLines.Count; i++)
        {
            var matched = true;
            for (var j = 0; j < n; j++)
            {
                if (TrimTrailingWhitespace(fileLines[i + j]) != TrimTrailingWhitespace(searchLines[j]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched) results.Add(new LineMatch { StartLine = i, LineCount = n });
        }

        return results;
    }

    private static List<LineMatch> FindRelativeIndent(
        IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines)
    {
        var results = new List<LineMatch>();
        var n = searchLines.Count;
        var searchBase = LeadingWhitespace(searchLines[0]).Length;
        for (var i = 0; i + n <= fileLines.Count; i++)
        {
            var fileBase = LeadingWhitespace(fileLines[i]).Length;
            if (MatchesRelativeIndent(fileLines, searchLines, i, n, searchBase, fileBase))
            {
                results.Add(new LineMatch { StartLine = i, LineCount = n });
            }
        }

        return results;
    }

    private static bool MatchesRelativeIndent(IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines,
        int start, int n, int searchBase, int fileBase)
    {
        for (var j = 0; j < n; j++)
        {
            var s = searchLines[j];
            var f = fileLines[start + j];
            if (s.Trim(' ', '\t') != f.Trim(' ', '\t')) return false;

            var relS = LeadingWhitespace(s).Length - searchBase;
            var relF = LeadingWhitespace(f).Length - fileBase;
            if (relS != relF) return false;
        }

        return true;
    }

    private static List<LineMatch> FindIgnoringBlankLines(
        IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines)
    {
        var searchContent = searchLines.Where(l => !IsBlank(l)).Select(TrimTrailingWhitespace).ToList();
        if (searchContent.Count == 0) return new List<LineMatch>();

        var fileContent = new List<(int Index, string Value)>();
        for (var i = 0; i < fileLines.Count; i++)
        {
            if (!IsBlank(fileLines[i])) fileContent.Add((i, TrimTrailingWhitespace(fileLines[i])));
        }

        var results = new List<LineMatch>();
        var n = searchContent.Count;
        for (var k = 0; k + n <= fileContent.Count; k++)
        {
            var matched = true;
            for (var j = 0; j < n; j++)
            {
                if (fileContent[k + j].Value != searchContent[j]) { matched = false; break; }
            }

            if (!matched) continue;
            var startLine = fileContent[k].Index;
            var endLine = fileContent[k + n - 1].Index;
            results.Add(new LineMatch { StartLine = startLine, LineCount = endLine - startLine + 1 });
        }

        return results;
    }
}
