namespace Graft.Core.Vba;

/// <summary>書き込み後に読み直した本文の判定結果。</summary>
public enum VbaWriteVerdict
{
    /// <summary>正規化した本文が等しい。成功。</summary>
    Match,

    /// <summary>
    /// 行数が等しく、スペースとタブを除いて大文字小文字を無視すると等しい。VBE が自動で整形しただけなので成功。
    /// 整形差は課題にしない（呼び出し側が info にまとめる）。ミラーは書き換えない。
    /// </summary>
    FormattingDifference,

    /// <summary>上記以外（行数が違う、文字が違う、など）。失敗。控えを書き戻す。</summary>
    DifferentContent,
}

/// <summary>
/// 書き込み後の読み直し本文と期待値の比較（仕様書_VBA連携 5.5-4）。検証は COM を使うブリッジではなく
/// Core の役目にして、Linux の CI でしきい値を固定する。
/// <para>
/// 【しきい値が「行数の一致」＋「空白除去・大文字小文字無視」である理由】 VBE が書き込み時に自動で直すのは
/// キーワードの大文字小文字と、演算子まわりなどの空白である。それ以外（行が増減した、文字が違う）は
/// VBE の整形では説明できず、書き込みが壊れた可能性がある。整形差の基準を緩くしすぎると壊れた書き込みを
/// 成功と見なし、厳しくしすぎると整形のたびに失敗扱いになる。行数を先に見るのは、空白除去だけで
/// 比べると「行の途中の改行」のような構造の違いを見逃さないようにするため（改行は除かない）。
/// </para>
/// </summary>
public static class VbaWriteVerifier
{
    /// <summary>
    /// 期待した本文と読み直した本文を比べる。どちらも改行の違いと末尾の空行の数は無視する
    /// （<see cref="VbaTextNormalizer.Canonicalize"/>）。
    /// </summary>
    public static VbaWriteVerdict Verify(string expected, string readBack)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(readBack);

        if (string.Equals(
                VbaTextNormalizer.Canonicalize(expected), VbaTextNormalizer.Canonicalize(readBack), StringComparison.Ordinal))
        {
            return VbaWriteVerdict.Match;
        }

        if (VbaTextNormalizer.CountLines(expected) == VbaTextNormalizer.CountLines(readBack)
            && string.Equals(
                VbaTextNormalizer.ToLooseForm(expected), VbaTextNormalizer.ToLooseForm(readBack), StringComparison.Ordinal))
        {
            return VbaWriteVerdict.FormattingDifference;
        }

        return VbaWriteVerdict.DifferentContent;
    }

    /// <summary>
    /// 判定を E809 の問題にする。別内容はエラー、一致と整形差は null。
    /// <para>
    /// 【整形差を課題（警告）にしない理由】 VBE が空白・大文字小文字を直すのは、書き込みのたびに起きる通常の動きで、
    /// 利用者が何かをする必要はない。以前はモジュールごとに E809 の警告を出していたため、15 モジュールを反映すると
    /// 警告が 15 件並び、本当の失敗（別内容・書き戻し失敗）と見分けにくかった。整形差は課題にせず、呼び出し側
    /// （<c>VbaSyncService</c>）が 1 回の反映につき 1 行の info にまとめる。
    /// </para>
    /// </summary>
    /// <param name="componentName">対象のコンポーネント名（詳細に出す）。</param>
    /// <param name="diff">別内容のときの差分の要約。詳細へ添える（行数・最初に違う行・末尾の増減）。</param>
    public static GraftIssue? ToIssue(VbaWriteVerdict verdict, string componentName, VbaWriteDiff? diff = null) => verdict switch
    {
        VbaWriteVerdict.Match => null,
        VbaWriteVerdict.FormattingDifference => null,
        _ => GraftIssue.Of(
            ErrorCode.E809,
            detail: $"{componentName}: 書き込んだ内容と、読み直した内容が一致しません。" + (diff is null ? string.Empty : diff.ToSummary()),
            severity: Severity.Error),
    };

    /// <summary>
    /// 読み直した本文が「書いた本文と行単位で完全に一致する先頭部分＋末尾の余分な行」の形かを調べる。
    /// <para>
    /// 【なぜ要るか】 実機で、行継続「 _」の次の行が <c>()</c> で始まる Declare を含むモジュールに
    /// <c>AddFromString</c> すると、書くたびに末尾へ「()」だけの行が 1 行増えた。書き戻し（元の本文の
    /// <c>AddFromString</c>）でも同じことが起き、書き戻しの照合まで失敗してモジュールを E809 のまま残していた。
    /// 原因は VBE の <c>AddFromString</c> の挙動と 2026-11-22 の実機（Microsoft 365・Windows 10.0.26200）で確かめ、
    /// 書き込みは <c>InsertLines</c> に変えて避けた（<c>ICodeModuleAccess.InsertLines</c> 参照）。この判定は、同じ種類の
    /// 不具合が別の形で出たときのための保険。
    /// 余分な行が末尾に増えただけなら、その行を消せば書いた本文になる。
    /// </para>
    /// <para>
    /// 【先頭部分が完全に一致するときに限る理由】 先頭の行を 1 文字でも変えているなら、VBE が途中を変えたのであって
    /// 「末尾にゴミが付いた」では説明できない。その状態で末尾を消しても正しい本文にはならず、利用者の本文を
    /// 余計に削るおそれがある。そこで空白・大文字小文字の差も許さず（<see cref="VbaTextNormalizer.Canonicalize"/> の
    /// 正規形で厳密に）比べ、少しでも違えば null（取り除かない。安全側）にする。書いた本文が空のときも null
    /// （「先頭部分」が無く、全行を消すことになってしまう）。
    /// </para>
    /// </summary>
    /// <returns>余分な行の形でなければ null。</returns>
    public static VbaTrailingExtra? FindTrailingExtra(string expected, string readBack)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(readBack);

        var wanted = SplitLines(expected);
        var actual = SplitLines(readBack);
        if (wanted.Count == 0 || actual.Count <= wanted.Count) return null;

        for (var i = 0; i < wanted.Count; i++)
        {
            if (!string.Equals(wanted[i], actual[i], StringComparison.Ordinal)) return null;
        }

        return new VbaTrailingExtra(wanted.Count, actual.Skip(wanted.Count).ToList());
    }

    /// <summary>
    /// 別内容と判定した 2 つの本文の差分の要約を作る（E809 の詳細とログに載せる）。
    /// <para>
    /// 【最初に違う行を「ゆるい形」で探す理由】 VBE の整形（空白・大文字小文字）は別内容の判定には含まれない。
    /// 厳密に比べると、先頭近くの整形の差が「最初に違う行」として出て、本当の違い（末尾に増えた行など）を隠す。
    /// そこで行ごとに <see cref="VbaTextNormalizer.ToLooseForm"/> で比べ、整形では説明できない最初の行を挙げる。
    /// </para>
    /// </summary>
    public static VbaWriteDiff Describe(string expected, string readBack)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(readBack);

        var wanted = SplitLines(expected);
        var actual = SplitLines(readBack);
        var common = Math.Min(wanted.Count, actual.Count);

        int? first = null;
        string? wantedLine = null;
        string? actualLine = null;
        for (var i = 0; i < Math.Max(wanted.Count, actual.Count); i++)
        {
            var hasWanted = i < wanted.Count;
            var hasActual = i < actual.Count;
            if (hasWanted && hasActual
                && string.Equals(VbaTextNormalizer.ToLooseForm(wanted[i]), VbaTextNormalizer.ToLooseForm(actual[i]), StringComparison.Ordinal))
            {
                continue;
            }

            first = i + 1;
            wantedLine = hasWanted ? wanted[i] : null;
            actualLine = hasActual ? actual[i] : null;
            break;
        }

        return new VbaWriteDiff(
            wanted.Count, actual.Count, first, wantedLine, actualLine,
            actual.Skip(common).ToList(), wanted.Skip(common).ToList());
    }

    private static List<string> SplitLines(string text)
    {
        var canonical = VbaTextNormalizer.Canonicalize(text);
        return canonical.Length == 0 ? new List<string>() : canonical.Split("\r\n").ToList();
    }
}

/// <summary>
/// 読み直した本文の「書いた本文と一致する先頭部分＋末尾の余分な行」（<see cref="VbaWriteVerifier.FindTrailingExtra"/>）。
/// </summary>
/// <param name="KeptLineCount">先頭の一致している行数。余分な行はその次（<c>KeptLineCount + 1</c> 行目）から。</param>
/// <param name="ExtraLines">余分な行の内容（取り除く行）。</param>
public sealed record VbaTrailingExtra(int KeptLineCount, IReadOnlyList<string> ExtraLines)
{
    /// <summary>取り除いた行を、警告に載せる短い文にする（1 行ずつ「…」で括り、長い行と多い行は省く）。</summary>
    public string Describe() => VbaWriteDiff.QuoteLines(ExtraLines);
}

/// <summary>別内容と判定した 2 つの本文の差分の要約（<see cref="VbaWriteVerifier.Describe"/>）。</summary>
/// <param name="ExpectedLines">書いた本文の行数。</param>
/// <param name="ActualLines">読み直した本文の行数。</param>
/// <param name="FirstDifferentLine">整形では説明できない最初の違う行の番号（1 始まり）。見つからなければ null。</param>
/// <param name="ExpectedLine">その行の、書いた本文での内容。その行が無ければ null。</param>
/// <param name="ActualLine">その行の、読み直した本文での内容。その行が無ければ null。</param>
/// <param name="ExtraTail">読み直した側にだけある末尾の行（末尾に増えた行）。</param>
/// <param name="MissingTail">書いた側にだけある末尾の行（末尾で消えた行）。</param>
public sealed record VbaWriteDiff(
    int ExpectedLines, int ActualLines, int? FirstDifferentLine, string? ExpectedLine, string? ActualLine,
    IReadOnlyList<string> ExtraTail, IReadOnlyList<string> MissingTail)
{
    /// <summary>1 行を要約に載せるときの最大の文字数。これを超える分は「…」で省く。</summary>
    public const int MaxLineChars = 60;

    /// <summary>要約に載せる行の最大の数。これを超える分は「ほか N 行」にする。</summary>
    public const int MaxLines = 5;

    /// <summary>E809 の詳細とログに載せる 1 文。</summary>
    public string ToSummary()
    {
        var parts = new List<string> { $"行数: 書いた {ExpectedLines} 行／読み直した {ActualLines} 行。" };
        if (FirstDifferentLine is { } line)
        {
            parts.Add(
                $"最初に違う行: {line} 行目（書いた: {QuoteOrNone(ExpectedLine)}／読み直した: {QuoteOrNone(ActualLine)}）。");
        }

        if (ExtraTail.Count > 0) parts.Add($"末尾に増えた行（{ExtraTail.Count} 行）: {QuoteLines(ExtraTail)}。");
        if (MissingTail.Count > 0) parts.Add($"末尾で消えた行（{MissingTail.Count} 行）: {QuoteLines(MissingTail)}。");
        return string.Join(string.Empty, parts);
    }

    private static string QuoteOrNone(string? line) => line is null ? "（行なし）" : $"「{Shorten(line)}」";

    /// <summary>行を「…」で括って並べる。長い行は <see cref="MaxLineChars"/> で省き、多いときは「ほか N 行」にする。</summary>
    internal static string QuoteLines(IReadOnlyList<string> lines)
    {
        var shown = string.Join("、", lines.Take(MaxLines).Select(l => $"「{Shorten(l)}」"));
        return lines.Count > MaxLines ? $"{shown} ほか {lines.Count - MaxLines} 行" : shown;
    }

    private static string Shorten(string line)
    {
        if (line.Length <= MaxLineChars) return line;
        var cut = MaxLineChars;
        // サロゲートペアの途中で切ると文字化けするので、手前で切る。
        if (char.IsHighSurrogate(line[cut - 1])) cut--;
        return line[..cut] + "…";
    }
}
