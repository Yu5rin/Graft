using System.Text;

namespace Graft.Core;

/// <summary>
/// 書き込む文字列が、対象ファイルの文字コードで元の文字のまま表せるかを調べる。
/// <para>
/// 【なぜ必要か】 Shift_JIS（コードページ932）のファイルに、絵文字・€・✓・é などShift_JISに無い
/// 文字を含む内容を書くと、<c>Encoding.GetBytes</c>は例外を出さずに <c>?</c> へ置き換えて
/// 書き込む（既存の挙動。EncodingRoundTripTests・WrittenContentVerificationTestsが
/// 「既知の挙動」として固定している）。書き込み後の検証もその置換を正当な差異として扱うため、
/// 利用者には何も知らされないまま文字が <c>?</c> に化ける。置換の挙動そのものは変えず
/// （変えると既存の適用が失敗するようになる）、書く前にここで検出して、適用のドライランでは
/// 警告（要確認）、エディタの保存では確認として利用者に知らせる。
/// </para>
/// <para>
/// UTF-8・UTF-16はすべての文字を表せるので検査しない。検査には、置換や近似（é→e のような
/// 最近似文字への変換）を許さず例外を出すエンコーダを使う。近似も元の文字が変わることに
/// 変わりはないため、置換と同じく「表せない」とみなす。
/// </para>
/// </summary>
public static class EncodingSafety
{
    /// <summary>報告に含める文字の最大数。メッセージが長くなりすぎないようにする。</summary>
    private const int MaxReported = 10;

    /// <summary>UTF-8・UTF-16・UTF-32など、すべての文字を表せるエンコーディングか。</summary>
    public static bool IsUnicode(Encoding encoding) => encoding.CodePage is 65001 or 1200 or 1201 or 12000 or 12001;

    /// <summary>
    /// <paramref name="text"/>のうち<paramref name="encoding"/>で表せない文字（サロゲートペアは
    /// 1文字として）を、出現順に重複なしで最大10個返す。すべて表せるなら空。
    /// </summary>
    public static IReadOnlyList<string> FindUnrepresentable(string text, Encoding encoding)
    {
        if (string.IsNullOrEmpty(text) || IsUnicode(encoding)) return Array.Empty<string>();

        Encoding strict;
        try
        {
            strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // 検査用のエンコーダを作れない環境では、検査を諦めて従来の挙動に任せる。
            return Array.Empty<string>();
        }

        // 大半の書き込みは全文が表せるので、まず全体を1回で試し、通れば文字ごとの走査はしない。
        if (CanEncode(strict, text)) return Array.Empty<string>();

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value < 0x80) continue;
            var s = rune.ToString();
            if (!seen.Add(s)) continue;
            if (CanEncode(strict, s)) continue;
            found.Add(s);
            if (found.Count >= MaxReported) break;
        }

        return found;
    }

    private static bool CanEncode(Encoding strict, string text)
    {
        try
        {
            strict.GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>警告の文面に使う、表せない文字の一覧（例: 「🎉」「€」）。</summary>
    public static string Describe(IReadOnlyList<string> characters)
        => string.Join("", characters.Select(c => $"「{c}」"));
}
