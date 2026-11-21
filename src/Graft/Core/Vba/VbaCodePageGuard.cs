using System.Text;

namespace Graft.Core.Vba;

/// <summary>CP932 で表せない文字の位置。行・列は 1 始まりで、列は UTF-16 の位置ではなく文字（コードポイント）の数で数える。</summary>
/// <param name="Line">行（1 始まり。CRLF・LF・CR のいずれも行の区切り）。</param>
/// <param name="Column">列（1 始まり。サロゲートペアは 1 文字）。</param>
/// <param name="CodePoint">Unicode のコードポイント。</param>
/// <param name="Character">その文字（サロゲートペアなら 2 つの char の文字列）。</param>
public sealed record VbaUnencodableChar(int Line, int Column, int CodePoint, string Character)
{
    /// <summary>「U+2014（—）」形式の表示。制御文字は文字そのものを出さない。</summary>
    public string Display
    {
        get
        {
            var code = CodePoint <= 0xFFFF ? $"U+{CodePoint:X4}" : $"U+{CodePoint:X}";
            return CodePoint < 0x20 || CodePoint == 0x7F || (CodePoint >= 0x80 && CodePoint < 0xA0)
                ? code
                : $"{code}（{Character}）";
        }
    }
}

/// <summary>
/// 本文が CP932（Shift_JIS）で表せるかの検査（仕様書_VBA連携 6.4）。
/// <para>
/// 【なぜ必要か】 VBE の文字コードは UTF-8 ではなく OS の ANSI コードページ（日本語環境では CP932）の
/// 見込みで、表せない文字は書き込むと別の文字（<c>?</c> など）に化ける。ミラーは UTF-8 なので、
/// 既存の E219（そのファイルの文字コードで表せない文字）は働かない。AI は emダッシュ・波ダッシュ・
/// 絵文字を出しやすいので、解析時（適用の前）と Push の直前の2点で検査し、E811 にする。
/// </para>
/// <para>
/// 置換や近似（最近似文字への変換）を許さず例外を出すエンコーダで検査する。近似も元の文字が
/// 変わることに変わりはないため。何が表せないかは .NET の CP932 の実測をテストで固定している
/// （Python の cp932 とは結果が異なる場合があるため）。
/// </para>
/// </summary>
public static class VbaCodePageGuard
{
    /// <summary>CP932 のコードページ番号。</summary>
    public const int CodePage = 932;

    /// <summary>1 回の報告に含める文字の位置の最大数。巨大なファイルで詳細が膨れないように打ち切る。</summary>
    public const int DefaultMaxHits = 50;

    private static readonly Lazy<Encoding?> Strict = new(CreateStrictEncoding);

    private static Encoding? CreateStrictEncoding()
    {
        try
        {
            // 文字コードの提供元の登録は何度呼んでも害がない。単体で使われても（EncodingDetector が
            // 先に呼ばれていなくても）動くよう、ここでも登録する。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // CP932 を使えない環境では検査を諦める（誤検出で Push を止めるよりは、従来どおり通す）。
            return null;
        }
    }

    /// <summary>本文のすべてが CP932 で表せるか。</summary>
    public static bool CanEncode(string text)
    {
        var strict = Strict.Value;
        if (strict is null || string.IsNullOrEmpty(text)) return true;
        return TryEncode(strict, text);
    }

    /// <summary>
    /// CP932 で表せない文字を、出現順に位置つきで返す（最大 <paramref name="maxHits"/> 件。同じ文字が
    /// 何度も出る場合もすべて数える）。すべて表せれば空。
    /// </summary>
    public static IReadOnlyList<VbaUnencodableChar> Find(string text, int maxHits = DefaultMaxHits)
    {
        var strict = Strict.Value;
        if (strict is null || string.IsNullOrEmpty(text) || TryEncode(strict, text))
        {
            // 大半の本文は全部表せるので、まず全体を 1 回で試し、通れば文字ごとの走査はしない。
            return Array.Empty<VbaUnencodableChar>();
        }

        var hits = new List<VbaUnencodableChar>();
        var cache = new Dictionary<int, bool>();
        var line = 1;
        var column = 1;
        var index = 0;
        while (index < text.Length && hits.Count < maxHits)
        {
            var c = text[index];
            if (c == '\r')
            {
                // CRLF は 1 回の改行として数える。
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                line++;
                column = 1;
                index++;
                continue;
            }

            if (c == '\n')
            {
                line++;
                column = 1;
                index++;
                continue;
            }

            Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed);
            if (consumed <= 0) consumed = 1;
            var value = rune.Value;
            if (value >= 0x80)
            {
                if (!cache.TryGetValue(value, out var ok))
                {
                    ok = TryEncode(strict, text.Substring(index, consumed));
                    cache[value] = ok;
                }

                if (!ok) hits.Add(new VbaUnencodableChar(line, column, value, text.Substring(index, consumed)));
            }

            index += consumed;
            column++;
        }

        return hits;
    }

    /// <summary>
    /// 表せない文字があれば E811（エラー）を 1 件の問題として返す。詳細にはファイル名・行・列・文字を列挙する。
    /// なければ null。
    /// </summary>
    /// <param name="relativePath">ミラー内の相対パス（問題の対象ファイル）。</param>
    /// <param name="text">検査する本文。</param>
    public static GraftIssue? ToIssue(string relativePath, string text)
    {
        var hits = Find(text);
        if (hits.Count == 0) return null;

        var shown = hits.Take(10).Select(h => $"{h.Line}行{h.Column}列 {h.Display}");
        var more = hits.Count > 10 ? $" ほか{hits.Count - 10}件" : string.Empty;
        return GraftIssue.Of(
            ErrorCode.E811,
            detail: $"{relativePath}: {string.Join("、", shown)}{more}",
            line: hits[0].Line,
            path: relativePath);
    }

    private static bool TryEncode(Encoding strict, string text)
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
}
