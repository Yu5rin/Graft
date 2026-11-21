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
    /// ゆるい形の算法の版。<see cref="ToLooseForm"/> の規則を変えたときに上げる。同期状態の
    /// <see cref="VbaModuleState.LooseFormVersion"/> に記録し、古い算法で作った <c>excelLooseHash</c> を
    /// 新しい算法の値と取り違えないために使う（移行の考え方は <see cref="VbaSyncPlanner"/> の PlanOne を参照）。
    /// 1 = 文字列・コメントの中も含めて空白を除き大文字化していた旧算法（版の記録がない古い同期状態もこれ）、
    /// 2 = 文字列リテラルとコメントの外だけを緩め、行頭の字下げを残す現在の算法。
    /// </summary>
    public const int LooseFormVersion = 2;

    /// <summary>
    /// 「ゆるい形」。<see cref="Canonicalize"/> に加えて、<b>文字列リテラルとコメントの外側だけ</b>で、
    /// スペースとタブを除き、大文字に揃える。VBE が書き込み時に直す空白（<c>x=1</c> → <c>x = 1</c>）と
    /// キーワード・識別子の大文字小文字は、利用者の変更ではないので、これらの差を無視して「実質同じか」を
    /// 見るために使う（6.5・5.5）。
    /// <para>
    /// 【文字列・コメントの中は緩めない理由】 以前は本文全体の空白を除いて大文字化していたので、
    /// <c>MsgBox "hi"</c> を <c>MsgBox "H I"</c> に直したり、コメントの文言を直したりした Excel 側の編集が
    /// 「整形差だけ」と見なされ、取り込まれず、次の Push で黙って消えていた。VBE が自動で直すのはコードの部分だけで、
    /// 文字列とコメントの中身は触らないので、そこは厳密に比べてよい（不具合 2）。
    /// </para>
    /// <para>
    /// 【行頭の字下げは残す】 字下げの整理も利用者の編集である。VBE は書き込み時に既存行の字下げを直さない
    /// ので、字下げを無視する必要がない。行末の空白（コメントの後ろを含む）だけは、VBE が詰めても利用者の
    /// 編集とは言えないので無視する。
    /// </para>
    /// <para>
    /// 文字列は <c>"</c> の開閉で追う（<c>""</c> は閉じてすぐ開く扱いで足りる）。コメントは <c>'</c>、
    /// または文の先頭の <c>Rem</c>（後ろが空白）から行末まで。VBA の文字列とコメントは行をまたがないので、
    /// 行ごとに状態を持てばよい。改行は残すので、行数が違えばゆるい形でも別物になる。
    /// </para>
    /// </summary>
    public static string ToLooseForm(string text)
    {
        var crlf = TrimTrailingLineBreaks(ToCrLf(text));
        var builder = new StringBuilder(crlf.Length);
        var lines = crlf.Split("\r\n");
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) builder.Append("\r\n");
            AppendLooseLine(builder, lines[i]);
        }

        return builder.ToString();
    }

    private static void AppendLooseLine(StringBuilder builder, string line)
    {
        // 行頭の字下げはそのまま残す（空白だけの行は、行末の空白として下で除かれて空になる）。
        var indentEnd = 0;
        while (indentEnd < line.Length && line[indentEnd] is ' ' or '\t') indentEnd++;
        var lineStart = builder.Length;
        builder.Append(line, 0, indentEnd);

        var inString = false;
        var atStatementStart = true;
        for (var i = indentEnd; i < line.Length; i++)
        {
            var c = line[i];
            if (inString)
            {
                builder.Append(c);
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                atStatementStart = false;
                builder.Append(c);
                continue;
            }

            if (c == '\'' || (atStatementStart && IsRemKeyword(line, i)))
            {
                // コメント。Rem の綴り（大文字小文字）は VBE が直すことがあるので大文字にそろえ、本文は厳密に残す。
                if (c != '\'')
                {
                    builder.Append("REM");
                    i += 3;
                }

                builder.Append(line, i, line.Length - i);
                break;
            }

            if (c is ' ' or '\t') continue;

            // ':' の後ろは新しい文の先頭（'Else: Rem …' のような形）。
            atStatementStart = c == ':';
            builder.Append(char.ToUpperInvariant(c));
        }

        // 行末の空白（コメントの後ろを含む）は VBE が詰めることがあるので無視する。
        var end = builder.Length;
        while (end > lineStart && builder[end - 1] is ' ' or '\t') end--;
        builder.Length = end;
    }

    /// <summary>位置 <paramref name="index"/> から <c>Rem</c>（大文字小文字不問）で始まり、後ろが空白か。</summary>
    private static bool IsRemKeyword(string line, int index)
    {
        if (index + 4 > line.Length) return false;
        return string.Compare(line, index, "Rem", 0, 3, StringComparison.OrdinalIgnoreCase) == 0
               && line[index + 3] is ' ' or '\t';
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
