using System.Security.Cryptography;
using System.Text;

namespace Graft.Core.Vba;

/// <summary>
/// モジュール本文の正規化とハッシュ計算（仕様書_VBA連携 6.3）。ミラー側と Excel 側の本文を
/// 「同じか」で比べる、同期の判定（<see cref="VbaSyncPlanner"/>）と書き込み後の検証
/// （<see cref="VbaWriteVerifier"/>）のすべてがここを通る。
/// <para>
/// 【ハッシュが末尾の CR/LF をすべて除く理由】 VBE は末尾の空行を保持したりしなかったりする
/// （<c>CodeModule.Lines</c> の読み戻しで増減しうる）。最終行の改行の有無や末尾の空行の数を
/// 「変更」と見なすと、書いていない差で衝突や再 Pull が起きる。一方、行頭・行末の空白と、
/// 空白だけの行は<b>除かない</b>（行末の空白は行継続の直前などで意味を持ちうるため）。
/// </para>
/// <para>
/// 【ミラーのファイル自体の末尾改行は変えない】 ここで正規化するのは比較用の形だけ。ファイルに書く
/// 内容は触らない（既存のファイルは末尾改行の有無を保ち、Pull が新規に作るファイルは末尾に CRLF を
/// 1 つ付ける。ハッシュは末尾の改行を除くので、どちらでも結果は同じ）。
/// </para>
/// </summary>
public static class VbaTextNormalizer
{
    /// <summary>
    /// 改行を CRLF に統一する（LF・CR 単独・CRLF が混在していてもよい）。末尾の改行は残す。
    /// Push で VBE に渡す本文は、この形にそろえる。
    /// </summary>
    public static string ToCrLf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return text;

        var builder = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                builder.Append("\r\n");
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (c == '\n')
            {
                builder.Append("\r\n");
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 比較用の正規形。改行を CRLF に統一し、末尾の CR/LF をすべて除く（行頭・行末の空白、
    /// 空白だけの行は残す）。
    /// </summary>
    public static string Canonicalize(string text) => TrimTrailingLineBreaks(ToCrLf(text));

    /// <summary>
    /// 「ゆるい形」。<see cref="Canonicalize"/> に加えて、スペースとタブをすべて除き、大文字に揃える。
    /// VBE が書き込み時に直す空白（<c>x=1</c> → <c>x = 1</c>）とキーワードの大文字小文字は、
    /// 利用者の変更ではないので、これらの差を無視して「実質同じか」を見るために使う（6.5・5.5）。
    /// 改行は残すので、行数が違えばゆるい形でも別物になる。
    /// </summary>
    public static string ToLooseForm(string text)
    {
        var crlf = ToCrLf(text);
        var builder = new StringBuilder(crlf.Length);
        foreach (var c in crlf)
        {
            if (c is ' ' or '\t') continue;
            builder.Append(char.ToUpperInvariant(c));
        }

        return TrimTrailingLineBreaks(builder.ToString());
    }

    /// <summary>正規形（<see cref="Canonicalize"/>）の SHA-256 を小文字 16 進で返す。</summary>
    public static string Hash(string text) => Sha256Hex(Canonicalize(text));

    /// <summary>
    /// ゆるい形（<see cref="ToLooseForm"/>）の SHA-256 を小文字 16 進で返す。同期状態の
    /// <c>excelLooseHash</c> に使う。
    /// </summary>
    public static string LooseHash(string text) => Sha256Hex(ToLooseForm(text));

    /// <summary>
    /// 本文の行数（正規形で数える）。空の本文は 0 行。書き込み後の検証（<see cref="VbaWriteVerifier"/>）が
    /// 「行数が等しい」を見るのに使う。末尾の空行は数えない（VBE の保持の仕方に左右されないため）。
    /// </summary>
    public static int CountLines(string text)
    {
        var canonical = Canonicalize(text);
        if (canonical.Length == 0) return 0;

        var count = 1;
        for (var i = 0; i + 1 < canonical.Length; i++)
        {
            if (canonical[i] == '\r' && canonical[i + 1] == '\n')
            {
                count++;
                i++;
            }
        }

        return count;
    }

    private static string TrimTrailingLineBreaks(string text) => text.TrimEnd('\r', '\n');

    private static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
