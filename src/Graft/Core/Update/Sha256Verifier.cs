using System.Security.Cryptography;

namespace Graft.Core.Update;

/// <summary>
/// ダウンロードした配布物ZIPのSHA256検証。GitHubのリリースアセットが持つ<c>digest</c>フィールド
/// （"sha256:xxxx..." 形式）と突き合わせる。
/// </summary>
public static class Sha256Verifier
{
    private const string Sha256Prefix = "sha256:";

    /// <summary>
    /// ファイルのSHA256を計算し、小文字16進文字列で返す。
    /// </summary>
    public static async Task<string> ComputeHexAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// GitHubの<c>digest</c>フィールド（"sha256:xxxx..." 形式）からハッシュ部分（64桁の16進）だけを
    /// 取り出す。次のいずれかに該当する場合はnullを返す（＝検証できない扱い）:
    /// フィールド自体が無い／空、"sha256:"接頭辞でない（他のアルゴリズムのダイジェスト等）、
    /// 16進64桁として妥当でない。
    ///
    /// 【方針】GitHub API未対応の古いアセット・sha256以外のダイジェストしか無いアセット等、
    /// 検証できない状況では、呼び出し側（<see cref="UpdateInstallPipeline"/>）が
    /// インストールそのものを中止する（要件: 検証できないならインストールしない、が安全）。
    /// </summary>
    public static string? ExtractSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        if (!digest.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var hex = digest[Sha256Prefix.Length..].Trim();
        if (hex.Length != 64) return null;

        foreach (var c in hex)
        {
            var isHexDigit = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHexDigit) return null;
        }

        return hex.ToLowerInvariant();
    }

    /// <summary>
    /// 配布物に添える照合値ファイル（<c>&lt;書庫名&gt;.sha256</c>）の本文から、64桁の小文字16進を取り出す。
    ///
    /// 【書式】 <c>sha256sum</c>の出力と同じ「&lt;64桁の16進&gt;&lt;空白1個以上&gt;[*]&lt;ファイル名&gt;」の1行
    /// （末尾の改行・CRLFは許す。<c>*</c>はsha256sumのバイナリモード印）。リリースのワークフロー
    /// （<c>.github/workflows/release.yml</c>）と<c>tools/New-Release.ps1</c>が作るのはこの形。
    ///
    /// 【nullを返す（＝照合値として使わない）場合】 空・複数行・16進64桁でない・区切りが無い・
    /// ファイル名が<paramref name="expectedFileName"/>と一致しない・大きすぎる。ファイル名を見るのは、
    /// 別の書庫の値が紛れ込んだ（コピー違い・差し替え）ときに、無関係なハッシュで照合して
    /// 「不一致」で失敗させるより、使わずに従来の経路へ戻すほうが害が小さいため。
    /// </summary>
    public static string? ParseChecksumFile(string? text, string expectedFileName)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(expectedFileName)) return null;
        if (text.Length > 1024) return null;

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToArray();
        if (lines.Length != 1) return null;

        var line = lines[0];
        if (line.Length < 66) return null;

        var hex = line[..64];
        foreach (var c in hex)
        {
            var isHexDigit = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHexDigit) return null;
        }

        // 64桁の直後は空白（sha256sumは2個。1個も許す）。
        var rest = line[64..];
        if (rest[0] != ' ') return null;
        rest = rest.TrimStart(' ');
        if (rest.StartsWith('*')) rest = rest[1..];

        return string.Equals(rest, expectedFileName, StringComparison.Ordinal) ? hex.ToLowerInvariant() : null;
    }

    /// <summary>大文字小文字を区別せずに一致を判定する。</summary>
    public static bool Matches(string computedHex, string expectedHex)
        => string.Equals(computedHex, expectedHex, StringComparison.OrdinalIgnoreCase);
}
