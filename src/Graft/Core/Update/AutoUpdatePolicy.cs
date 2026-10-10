namespace Graft.Core.Update;

/// <summary>
/// 「確認なしで自動更新する」設定（<see cref="Infra.UpdateSettings.AutoInstall"/>）がオンのとき、
/// 見つかった版を<b>確認ダイアログなしで</b>入れ替えてよいかの判断が返す理由。
/// <see cref="None"/>以外は、従来どおり確認ダイアログへ戻す理由を表す。
/// </summary>
public enum AutoInstallBlock
{
    /// <summary>確認なしで進めてよい。</summary>
    None,

    /// <summary>このOSでは自分で入れ替えられない（<see cref="UpdatePlatformPolicy.CanSelfInstall"/>）。</summary>
    UnsupportedPlatform,

    /// <summary>そのOS向けの配布物（添付ファイル）が見つからない。</summary>
    NoAsset,

    /// <summary>SHA256を照合できない（回数上限で規則からURLを組み立てた経路、またはdigestが無い・読めない）。</summary>
    ChecksumUnavailable,

    /// <summary>更新確認先が既定（公式のGitHub Releases）から変更されている。</summary>
    NonDefaultCheckUrl,

    /// <summary>プレリリースである。</summary>
    Prerelease,

    /// <summary>現在の版より新しくない、またはタグを版として読めない（ダウングレードや同一版の防止）。</summary>
    NotNewer,
}

/// <summary>判断の結果。<see cref="Block"/>が<see cref="AutoInstallBlock.None"/>なら確認なしで進めてよい。</summary>
/// <param name="Block">ダイアログへ戻す理由（進めてよいときは<see cref="AutoInstallBlock.None"/>）。</param>
/// <param name="Reason">ログ（update）に残す日本語の理由。進めてよいときはnull。</param>
public sealed record AutoInstallDecision(AutoInstallBlock Block, string? Reason)
{
    public bool CanProceed => Block == AutoInstallBlock.None;

    public static AutoInstallDecision Proceed { get; } = new(AutoInstallBlock.None, null);
}

/// <summary>
/// 確認なしの自動更新に進めてよいかの判断だけを集めたもの（純粋な関数。通信・ファイル・時刻に
/// 触れないため<c>AutoUpdatePolicyTests</c>で固定している。<see cref="UpdatePlatformPolicy"/>と同じ流儀）。
///
/// 【基本の考え方】 確認ダイアログは、利用者が「この版を、この条件で入れてよい」と判断する場だった。
/// 確認を省く以上、ダイアログが見せていた注意（整合性の照合を省くこと・更新確認先が変更されて
/// いること）が必要になる場面は、省いてはいけない。迷う場合は、黙って進めるのではなく
/// 従来のダイアログへ戻す（失敗しても何も起きない側に倒す）。
///
/// 【ここで判断しないもの】 実行ファイルのフォルダへ書き込めるか、が必要な判断は環境（ファイル
/// システム）に依存するため、画面側（<c>SettingsViewModel</c>の入れ替え計画の組み立て）で行う。
/// その結果もこのクラスの結果と同様、ダイアログへ戻す側に倒れる。
/// </summary>
public static class AutoUpdatePolicy
{
    /// <param name="release">更新確認で見つかったリリース。</param>
    /// <param name="currentVersion">実行中の版（例: "1.0.24.0"）。</param>
    /// <param name="platform">実行中のOS。</param>
    /// <param name="checkUrl">更新確認に使ったURL（設定の<c>update.checkUrl</c>）。</param>
    public static AutoInstallDecision Evaluate(
        GitHubReleaseInfo release, string currentVersion, UpdatePlatform platform, string checkUrl)
    {
        ArgumentNullException.ThrowIfNull(release);

        // 自分で入れ替えられないOS。既存の判断（UpdatePlatformPolicy）にそのまま従い、
        // ここで別の基準を持たない。
        if (!UpdatePlatformPolicy.CanSelfInstall(platform))
        {
            return Block(AutoInstallBlock.UnsupportedPlatform,
                $"このOS（{platform}）では自動の入れ替えに対応していないため");
        }

        // 【プレリリース】 更新確認はreleases/latest（安定版だけを返す）を使うため、通常ここへは
        // 来ない（UpdateChecker.CheckNowAsyncのコメント参照）。ただし将来の仕様変更やAtomの
        // 非対称で紛れ込んだときに、確認なしで試験版へ入れ替えてしまわないための保険。
        if (release.Prerelease)
        {
            return Block(AutoInstallBlock.Prerelease, "プレリリースのため");
        }

        // 【ダウングレードの防止】 UpdateCheckerは「新しい」と判定した場合しか返さないが、
        // 確認なしで古い版や同じ版へ入れ替えるのは取り返しがつきにくいので、ここでも数値で
        // 見直す（文字列比較は"1.0.10"と"1.0.9"で誤るため、UpdateVersionを使う）。
        // タグ・現在の版のどちらかを読めない場合も、新しいと言い切れないので進めない。
        if (!UpdateVersion.TryParse(release.TagName, out var latest)
            || !UpdateVersion.TryParse(currentVersion, out var current)
            || latest.CompareTo(current) <= 0)
        {
            return Block(AutoInstallBlock.NotNewer,
                $"現在の版（{currentVersion}）より新しい版と判断できないため（{release.TagName}）");
        }

        // 【更新確認先】 既定から変更されていると、確認ダイアログは「更新の取得先が既定から変更
        // されています」と警告していた（settings.jsonを書き換えられた場合に気付く手がかり）。
        // 確認なしではその警告を見せる場が無くなるので、変更されている間は自動では進めない。
        if (!UpdateHostPolicy.IsDefaultCheckUrl(checkUrl))
        {
            return Block(AutoInstallBlock.NonDefaultCheckUrl, "更新の確認先が既定から変更されているため");
        }

        var asset = UpdatePlatformPolicy.SelectAsset(release, platform);
        if (asset is null)
        {
            return Block(AutoInstallBlock.NoAsset, "このOS向けの配布物がリリースに見つからないため");
        }

        // 【SHA256を照合できない場合】 確認ダイアログは「整合性の照合を省いて更新します」と
        // 開示したうえで、利用者が同意した場合だけ照合なしで進めていた。確認なしでは同意を
        // 得られないので、照合を省いた入れ替えは行わない。
        // 1) AllowMissingChecksum: GitHub APIの回数上限で、Atomから規則的にURLを組み立てた経路。
        // 2) APIは応答したがdigestが無い・sha256として読めない: UpdateInstallPipelineが
        //    ChecksumUnavailableで中止する経路。ダウンロードして失敗させるより、先に分かる
        //    ここで戻す方が、無駄な通信と「失敗」の通知を避けられる。
        if (release.AllowMissingChecksum || Sha256Verifier.ExtractSha256(asset.Digest) is null)
        {
            return Block(AutoInstallBlock.ChecksumUnavailable, "SHA256を照合できないため");
        }

        return AutoInstallDecision.Proceed;
    }

    private static AutoInstallDecision Block(AutoInstallBlock block, string reason) => new(block, reason);
}
