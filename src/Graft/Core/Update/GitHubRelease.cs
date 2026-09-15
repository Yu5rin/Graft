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
    /// true の場合、この情報はGitHub Releases APIの応答ではなく、Atomフィードで読み取れた
    /// タグから<see cref="UpdateAtomFeedLogic.TryBuildDownloadUrl"/>でダウンロードURLを
    /// 規則的に組み立てて合成したものであることを示す（実機不具合対応。詳しい経緯は
    /// <see cref="UpdateAtomFeedLogic.TryBuildDownloadUrl"/>のコメント参照）。
    ///
    /// 【この値の使いみち（置き場所をGitHubReleaseInfoにした理由）】
    /// この経路ではSHA256（<see cref="GitHubReleaseAsset.Digest"/>）が全アセットでnullになるが、
    /// それは「API未対応の古いアセット」等の異常ではなく、「APIに到達できず理由が分かって
    /// いる」という既知の制約である。この区別自体は個々のアセットではなくリリース全体（＝
    /// どの経路で情報を得たか）に属する性質のため、<see cref="GitHubReleaseAsset"/>ではなく
    /// ここに持たせている。
    /// <list type="bullet">
    /// <item>UI（<c>SettingsViewModel.Update.cs</c>のOfferUpdateAsync）: 更新確認ダイアログの
    /// 本文へ「SHA256の照合を省く」旨を追記する判断に使う。</item>
    /// <item>インストール処理（<c>SettingsViewModel.Update.cs</c>のRunUpdateAsync）:
    /// <see cref="UpdateInstallPipeline.RunAsync"/>へ渡す<c>allowMissingChecksum</c>引数を
    /// この値にする。既定（false）ではdigestが無ければ必ず<c>ChecksumUnavailable</c>で
    /// 中止する安全側の挙動を変えず、この経路（true）からのみハッシュ照合の省略を許す
    /// （<see cref="UpdateInstallPipeline"/>のRunAsyncコメント参照）。</item>
    /// </list>
    /// </summary>
    public bool AllowMissingChecksum { get; init; }

    /// <summary>
    /// 名前でアセットを探す（大文字小文字を区別しない）。Windows版配布物
    /// （<c>tools/New-Release.ps1</c>が作る "Graft-&lt;バージョン&gt;-win-x64.zip"）を
    /// 見つけるために使う。
    /// </summary>
    public GitHubReleaseAsset? FindAssetByNameSuffix(string suffix)
        => Assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
