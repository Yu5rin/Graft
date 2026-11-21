namespace Graft.Core.Vba;

/// <summary>書き込み後に読み直した本文の判定結果。</summary>
public enum VbaWriteVerdict
{
    /// <summary>正規化した本文が等しい。成功。</summary>
    Match,

    /// <summary>
    /// 行数が等しく、スペースとタブを除いて大文字小文字を無視すると等しい。VBE が自動で整形しただけなので成功。
    /// E809 を警告で出す。ミラーは書き換えない。
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
    /// 判定を E809 の問題にする。整形差は警告、別内容はエラー、一致は null。
    /// </summary>
    /// <param name="componentName">対象のコンポーネント名（詳細に出す）。</param>
    public static GraftIssue? ToIssue(VbaWriteVerdict verdict, string componentName) => verdict switch
    {
        VbaWriteVerdict.Match => null,
        VbaWriteVerdict.FormattingDifference => GraftIssue.Of(
            ErrorCode.E809,
            detail: $"{componentName}: VBE が整形したため、Excel 側の本文は少し異なります（空白や大文字小文字の違いだけです）。",
            severity: Severity.Warning),
        _ => GraftIssue.Of(
            ErrorCode.E809,
            detail: $"{componentName}: 書き込んだ内容と、読み直した内容が一致しません。",
            severity: Severity.Error),
    };
}
