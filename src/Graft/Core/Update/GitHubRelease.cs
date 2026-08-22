namespace Graft.Core.Update;

/// <summary>
/// GitHub Releases APIの1アセット（添付ファイル）から、更新確認に必要な項目だけを取り出したもの。
/// </summary>
public sealed record GitHubReleaseAsset
{
    /// <summary>ファイル名（例: "Graft-1.0.8-win-x64.zip"）。</summary>
    public string Name { get; init; } = "";

    /// <summary>ダウンロードURL。</summary>
    public string BrowserDownloadUrl { get; init; } = "";

    /// <summary>バイト数。</summary>
    public long Size { get; init; }

    /// <summary>
    /// GitHubが算出したダイジェスト（"sha256:xxxx..." 形式）。古いリリースや算出前のアセットでは
    /// 存在しないことがあるためnullable。<see cref="Sha256Verifier.ExtractSha256"/>で解釈する。
    /// </summary>
    public string? Digest { get; init; }
}

/// <summary>
/// GitHub Releases APIの<c>GET /repos/{owner}/{repo}/releases/latest</c>から、
/// 更新確認に必要な項目だけを取り出したもの。
/// </summary>
public sealed record GitHubReleaseInfo
{
    /// <summary>タグ名（例: "v1.0.8"）。</summary>
    public string TagName { get; init; } = "";

    /// <summary>リリースページのURL（手動更新の案内で使う）。</summary>
    public string HtmlUrl { get; init; } = "";

    /// <summary>
    /// プレリリースかどうか。<c>releases/latest</c>エンドポイントはプレリリースを返さない仕様のため
    /// 通常は常にfalseのはずだが、念のため保持しておく（将来別のエンドポイントへ切り替える場合や、
    /// 仕様変更があった場合の保険）。
    /// </summary>
    public bool Prerelease { get; init; }

    /// <summary>添付ファイル一覧。</summary>
    public IReadOnlyList<GitHubReleaseAsset> Assets { get; init; } = Array.Empty<GitHubReleaseAsset>();

    /// <summary>
    /// 名前でアセットを探す（大文字小文字を区別しない）。Windows版配布物
    /// （<c>tools/New-Release.ps1</c>が作る "Graft-&lt;バージョン&gt;-win-x64.zip"）を
    /// 見つけるために使う。
    /// </summary>
    public GitHubReleaseAsset? FindAssetByNameSuffix(string suffix)
        => Assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
