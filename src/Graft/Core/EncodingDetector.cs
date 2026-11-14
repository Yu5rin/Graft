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

    /// <summary>
    /// 文字コードを判定できず（UTF-8・Shift_JIS・UTF-16のいずれでも元のバイト列を正しく復元
    /// できず）、不正なバイトをU+FFFDへ置換して読んだことを表す。
    /// <para>
    /// 【なぜ必要か】 EUC-JPやWindows-1252のファイルは、置換つきのUTF-8として読むと日本語などが
    /// すべてU+FFFDになる。その文字列をそのまま書き戻すと、変更していない行まで含めて内容が
    /// 取り返しのつかない形で壊れる（実測: EUC-JPの2行のファイルの2行目だけを変える適用が、
    /// 警告なしで成功し、1行目の日本語が全部U+FFFDになった。エディタでの保存も同じ）。
    /// 読むだけの機能（コンテキスト収集・検索・表示）はASCII部分だけでも使えるので読み込み自体は
    /// 成功させ、書き込みの側（<see cref="FileTextIO.WriteAsync"/>・適用のドライラン・エディタ）が
    /// この印を見て止める。
    /// </para>
    /// </summary>
    public bool UnsupportedEncoding { get; init; }

    /// <summary>
    /// 改行コードを実際のファイルから判定できたかどうか。改行が1つも無いファイルでは
    /// false（<see cref="NewLine"/>は仮の値）で、追記時の改行コードは呼び出し側が
    /// <see cref="NewLineResolver"/>で決める。手で組み立てる形（新規ファイル等）は既定のtrue。
    /// </summary>
    public bool NewLineKnown { get; init; } = true;
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
        var (encoding, hasBom, contentStart, unsupported) = DetectEncoding(bytes);
        var content = encoding.GetString(bytes, contentStart, bytes.Length - contentStart);
        var (newLine, mixed, newLineKnown) = DetectNewLine(content);
        var endsWithNewLine = content.EndsWith('\n') || content.EndsWith('\r');

        return new TextShape
        {
            Encoding = encoding,
            HasBom = hasBom,
            NewLine = newLine,
            MixedNewLines = mixed,
            EndsWithNewLine = endsWithNewLine,
            UnsupportedEncoding = unsupported,
            NewLineKnown = newLineKnown,
        };
    }

    private static (Encoding Encoding, bool HasBom, int ContentStart, bool Unsupported) DetectEncoding(byte[] bytes)
    {
        // BOM付きでも、中身が妥当とは限らない。BOMだけを見て通すと、BOM付きUTF-8の途中に
        // 不正なバイトがあるファイルを置換つきで読んだまま書き戻して壊す。BOMの後ろまで
        // 厳密に検証し、外れたら「判定できない」として扱う（UTF-16も同じ）。
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return IsValidUnicode(new UTF8Encoding(false, true), bytes, 3)
                ? (new UTF8Encoding(false), true, 3, false)
                : (new UTF8Encoding(false), true, 3, true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (new UnicodeEncoding(false, false), true, 2,
                !IsValidUnicode(new UnicodeEncoding(false, false, true), bytes, 2));
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (new UnicodeEncoding(true, false), true, 2,
                !IsValidUnicode(new UnicodeEncoding(true, false, true), bytes, 2));
        }

        if (IsValidUtf8(bytes))
        {
            return (new UTF8Encoding(false), false, 0, false);
        }

        var shiftJis = TryGetValidShiftJis(bytes);
        if (shiftJis is not null)
        {
            return (shiftJis, false, 0, false);
        }

        // 最終フォールバック: UTF-8としてもShift_JISとしても妥当でないバイト列。
        // EUC-JP・Windows-1252・ISO-8859系などの未対応エンコーディング、またはバイナリの取り違え。
        // 例外を投げて処理全体を止めると、読むだけの機能（コンテキスト収集・検索・表示）まで
        // 使えなくなるため、不正なバイトをU+FFFDへ置換するUTF-8として読み込み、ASCII部分だけでも
        // 内容を確認できる状態を維持する。ただし、この読み方で得た文字列は元のバイト列を復元できない
        // ので、書き込みに使ってはならない。そのため UnsupportedEncoding の印を付け、書き込みの側が
        // 止める（以前は印が無く、EUC-JPのファイルの別の行を変えるだけで日本語がすべてU+FFFDに
        // なって「成功」していた）。
        //
        // 【EUC-JP等を判定して対応しない理由】 EUC-JPの漢字・かなは、2バイトとも0xA1〜0xFEで、
        // Shift_JISとして読んでも「半角カナ＋別の文字」の並びとして妥当になることが多い
        // （例: 「あ」= A4 A2 は、Shift_JISでは半角カナ2文字）。つまりShift_JISの検証を通る
        // EUC-JPのファイルが現実に存在し、バイト列だけでは確実に区別できない。逆にEUC-JPを
        // Shift_JISより先に判定すると、本物のShift_JISを誤判定して壊す。誤判定で壊すよりは止める
        // ほうが安全なので、確実に判定できるUTF-8・Shift_JIS・BOM付きUTF-16だけを対応範囲とする。
        return (new UTF8Encoding(false, false), false, 0, true);
    }

    private static bool IsValidUnicode(Encoding strict, byte[] bytes, int start)
    {
        try
        {
            strict.GetCharCount(bytes, start, bytes.Length - start);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
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

    private static (string NewLine, bool Mixed, bool Known) DetectNewLine(string content)
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

        return (newLine, kindsUsed > 1, dominant.Count > 0);
    }
}
