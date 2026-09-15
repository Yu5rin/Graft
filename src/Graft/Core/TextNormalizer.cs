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
    /// 加算する（仕様書5.2）。
    ///
    /// 実機不具合対応: この補正はもともと「AIがブロック全体を別の基準インデント（例: 桁0）へ
    /// 書き直した」場合の救済であり、この前提が成り立たない状況で機械的に文字を書き換えると、
    /// 正しく書かれたREPLACE本文を壊してしまう（実機で「4/8スペースのファイルにSEARCH先頭行だけ
    /// 5スペースのAI出力が来た結果、正しい4/8スペースのREPLACEが3/7スペースへ潰されて書き込まれた」
    /// ケースを実測で確認済み）。そのため、前提が崩れていると判断できる次の3条件のいずれかに
    /// 当てはまる場合は、一切書き換えずREPLACE本文をそのまま返す（ゲート）。
    ///
    /// 【ゲート1】 <paramref name="deltaChars"/> == 0 — 1文字も動かす理由が無い。
    /// 旧実装は delta==0 でも <c>new string(dominantChar, ws.Length)</c> でインデントを作り直して
    /// いたため、動かす必要が無いのに文字種だけ入れ替わっていた（実測:
    /// タブ4個の行に delta=0, dominantChar=' ' を渡すとスペース4個に化ける。逆にスペース4個の行に
    /// dominantChar='\t' を渡すとタブ4個になり、見た目のインデント量が数倍に化ける）。
    ///
    /// 【ゲート2】 SEARCH本文とREPLACE本文とで基準インデント（先頭に現れる非空行の行頭空白量）が
    /// 異なる — 補正の前提（ブロック全体が同じ基準でずれている）が成り立つなら、AIはSEARCHと
    /// REPLACEを同じ基準で書いているはずである。基準が食い違うなら、AIはREPLACEのインデントを
    /// SEARCHとは独立に意図して決めている（今回の実機報告そのもの）ので、それを補正でずらすのは
    /// 証拠に反する推測になる。
    /// 基準は「先頭の非空行」の行頭空白量とする。先頭行が空行（AIが区切りとして空行から書き始めた
    /// 場合など）だと、その行自体にはインデントとしての意味が無く手がかりにならないため、
    /// 最初に現れる非空行まで読み進めて基準にする。SEARCH・REPLACEのいずれかが全行空行
    /// （＝REPLACE本文が空文字列の場合を含む）で比較のしようが無いときは、このゲートでは
    /// 何も判断できないため素通りさせる（従来どおり。補正対象となる行自体が無いので実害も無い）。
    ///
    /// 【ゲート3】 <paramref name="deltaChars"/> &lt; 0 で、REPLACE本文の非空行の中に
    /// 行頭空白が |deltaChars| 文字未満の行がある — 「ブロック全体を一律 |deltaChars| 文字
    /// 削って良い」という前提が、その行では成り立たない（削り切れない）ことを意味する。
    /// 旧実装は <c>Math.Max(0, ...)</c> で黙って桁0へ潰しており、削り切れなかったという事実
    /// そのものが失われたまま書き込まれていた。
    ///
    /// 戻り値は (適用後テキスト, 実際に使った補正量)。ゲートで見送った場合は必ず
    /// (<paramref name="replaceText"/>, 0) を返す（呼び出し元 <see cref="MatchEngine"/> は
    /// この0を「補正なし」としてUIにそのまま表示する）。
    /// </summary>
    public static (string Applied, int CorrectionChars) ApplyIndentCorrection(
        IReadOnlyList<string> searchLines, string replaceText, int deltaChars, char dominantChar)
    {
        if (replaceText.Length == 0) return (replaceText, 0);
        if (deltaChars == 0) return (replaceText, 0); // ゲート1

        var replaceLines = SplitLines(replaceText);

        var searchBase = FirstNonBlankIndentLength(searchLines);
        var replaceBase = FirstNonBlankIndentLength(replaceLines);
        if (searchBase is not null && replaceBase is not null && searchBase.Value != replaceBase.Value)
        {
            return (replaceText, 0); // ゲート2
        }

        if (deltaChars < 0)
        {
            var cannotShrink = replaceLines.Any(line => !IsBlank(line) && LeadingWhitespace(line).Length < -deltaChars);
            if (cannotShrink) return (replaceText, 0); // ゲート3
        }

        var adjusted = replaceLines.Select(line => ApplyIndentToLine(line, deltaChars, dominantChar));
        return (string.Join("\n", adjusted), deltaChars);
    }

    /// <summary>行群の中で最初に現れる非空行の行頭空白の長さ。全行が空行（0行を含む）ならnull。</summary>
    private static int? FirstNonBlankIndentLength(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (!IsBlank(line)) return LeadingWhitespace(line).Length;
        }

        return null;
    }

    private static string ApplyIndentToLine(string line, int deltaChars, char dominantChar)
    {
        if (IsBlank(line)) return line;
        var ws = LeadingWhitespace(line);
        var rest = line.Substring(ws.Length);
        // ゲート3により、ここへ到達する時点で非空行の newLen が負になることは無い
        // （deltaChars<0 のケースは全非空行で ws.Length >= -deltaChars を確認済み）。
        var newLen = ws.Length + deltaChars;
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
