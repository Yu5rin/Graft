using System.Text;

namespace Graft.Core;

/// <summary>
/// ファイルの見た目（エンコーディング・改行・末尾改行）。6.4節の判定結果を保持する。
/// </summary>
public sealed record TextShape
{
    /// <summary>判定されたエンコーディング。</summary>
    public required Encoding Encoding { get; init; }

    /// <summary>BOMの有無。</summary>
    public bool HasBom { get; init; }

    /// <summary>優勢な改行コード。</summary>
    public string NewLine { get; init; } = "\r\n";

    /// <summary>改行コードが混在しているかどうか。</summary>
    public bool MixedNewLines { get; init; }

    /// <summary>末尾が改行で終わっているかどうか。</summary>
    public bool EndsWithNewLine { get; init; }
}

/// <summary>
/// バイト列からエンコーディングと改行の状態を判定する。判定順はBOM → UTF-8妥当性 → Shift_JIS（6.4節）。
/// </summary>
public static class EncodingDetector
{
    /// <summary>
    /// Shift_JIS（コードページ932）を利用するための CodePagesEncodingProvider 登録。
    /// 静的コンストラクタはCLRにより型初回利用時に一度だけ、かつスレッドセーフに実行される
    /// ことが保証されるため、多重登録を気にする必要がない。
    /// </summary>
    static EncodingDetector()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static TextShape Detect(byte[] bytes)
    {
        var (encoding, hasBom, contentStart) = DetectEncoding(bytes);
        var content = encoding.GetString(bytes, contentStart, bytes.Length - contentStart);
        var (newLine, mixed) = DetectNewLine(content);
        var endsWithNewLine = content.EndsWith('\n') || content.EndsWith('\r');

        return new TextShape
        {
            Encoding = encoding,
            HasBom = hasBom,
            NewLine = newLine,
            MixedNewLines = mixed,
            EndsWithNewLine = endsWithNewLine,
        };
    }

    private static (Encoding Encoding, bool HasBom, int ContentStart) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(false), true, 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (new UnicodeEncoding(false, false), true, 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (new UnicodeEncoding(true, false), true, 2);
        }

        if (IsValidUtf8(bytes))
        {
            return (new UTF8Encoding(false), false, 0);
        }

        var shiftJis = TryGetValidShiftJis(bytes);
        if (shiftJis is not null)
        {
            return (shiftJis, false, 0);
        }

        // 最終フォールバック: UTF-8としてもShift_JISとしても妥当でないバイト列。
        // 拡張子ホワイトリスト（PathGuard）によりテキスト系拡張子に限定されているため
        // 通常はここへ到達しないはずだが、EUC-JP等の未対応エンコーディングや
        // バイナリの取り違えを完全には排除できない。例外を投げて処理全体を止めるより、
        // 不正なバイト列をU+FFFDへ置換するUTF-8として読み込み、ASCII部分だけでも
        // 内容を確認できる状態を維持する方を優先する。
        return (new UTF8Encoding(false, false), false, 0);
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            var strict = new UTF8Encoding(false, true);
            strict.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// バイト列がShift_JIS（コードページ932）として妥当かどうかを検証し、妥当な場合のみ
    /// エンコーディングを返す。検証には例外送出フォールバック付きのインスタンスを使い、
    /// 実際の読み書きには通常の（置換フォールバック付きの）インスタンスを使う。
    /// </summary>
    private static Encoding? TryGetValidShiftJis(byte[] bytes)
    {
        try
        {
            var strict = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            strict.GetString(bytes);
            return Encoding.GetEncoding(932);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            // CodePagesEncodingProviderが何らかの理由で利用できない環境向けの保険
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static (string NewLine, bool Mixed) DetectNewLine(string content)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\r')
            {
                if (i + 1 < content.Length && content[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (content[i] == '\n')
            {
                lf++;
            }
        }

        var counts = new[]
        {
            (NewLine: "\r\n", Count: crlf),
            (NewLine: "\n", Count: lf),
            (NewLine: "\r", Count: cr),
        };

        var kindsUsed = counts.Count(c => c.Count > 0);
        var dominant = counts.OrderByDescending(c => c.Count).First();
        var newLine = dominant.Count == 0 ? "\r\n" : dominant.NewLine;

        return (newLine, kindsUsed > 1);
    }
}
