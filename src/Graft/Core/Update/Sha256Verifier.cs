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

    /// <summary>大文字小文字を区別せずに一致を判定する。</summary>
    public static bool Matches(string computedHex, string expectedHex)
        => string.Equals(computedHex, expectedHex, StringComparison.OrdinalIgnoreCase);
}
