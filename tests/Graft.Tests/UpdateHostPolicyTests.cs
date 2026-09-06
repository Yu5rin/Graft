using FluentAssertions;
using Graft.Core.Update;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="UpdateHostPolicy"/>: セキュリティ点検の指摘事項（「更新のダウンロード元ホストが
/// 未検証／SHA256が悪意あるcheckUrlに無力」）への対応を検証する。期待ハッシュと
/// ダウンロードURLはcheckUrlへの同じHTTP応答から来るため、checkUrl自体を書き換えられる
/// 攻撃者に対してはSHA256だけでは配布元の正当性を保証できない。ここではその代わりに
/// ダウンロードURLのホスト自体を検証するロジックを、GitHub Releases APIとの通信を一切
/// 行わずに確認する。
/// </summary>
public class UpdateHostPolicyTests
{
    [Fact(DisplayName = "既定のcheckUrlなら、実測したGitHubの配布ホスト（github.com）からのダウンロードを許可する")]
    public void 既定checkUrlでは実測済みホストを許可する()
    {
        UpdateHostPolicy.IsAllowedDownloadUrl(
                UpdateHostPolicy.DefaultCheckUrl,
                "https://github.com/Yu5rin/Graft/releases/download/v1.0.14/Graft-1.0.14-win-x64.zip")
            .Should().BeTrue();
    }

    [Fact(DisplayName = "既定のcheckUrlのまま、checkUrl自身のホスト（api.github.com）からのダウンロードは許可しない")]
    public void 既定checkUrlでもcheckUrl自身のホストは許可しない()
    {
        // checkUrlのホスト（api.github.com）はGitHub Releases APIのエンドポイントであり、
        // 実際の配布物（browser_download_url）が置かれるホストではない（実測済み）。
        // 「checkUrlのホストと一致すれば許可」という単純な実装だとこの既定運用そのものが
        // 常に拒否されてしまうため、既定値専用の許可ホスト集合を使う設計にしている。
        UpdateHostPolicy.IsAllowedDownloadUrl(
                UpdateHostPolicy.DefaultCheckUrl,
                "https://api.github.com/evil.zip")
            .Should().BeFalse();
    }

    [Fact(DisplayName = "既定のcheckUrlのまま、無関係なホストからのダウンロードは拒否する")]
    public void 既定checkUrlでは無関係なホストを拒否する()
    {
        // 悪意あるcheckUrlの典型的な攻撃形（settings.jsonのcheckUrlは既定のままに見せかけつつ、
        // 実際にはUpdateChecker呼び出し時に渡る値だけを差し替える等）を想定した回帰。
        UpdateHostPolicy.IsAllowedDownloadUrl(
                UpdateHostPolicy.DefaultCheckUrl,
                "https://evil.example.com/Graft-win-x64.zip")
            .Should().BeFalse();
    }

    [Fact(DisplayName = "checkUrlを既定から変更している場合、ダウンロードURLのホストがcheckUrl自身のホストと一致すれば許可する")]
    public void 非既定checkUrlではホストが一致すれば許可する()
    {
        UpdateHostPolicy.IsAllowedDownloadUrl(
                "https://git.example.co.jp/api/v4/projects/1/releases/latest",
                "https://git.example.co.jp/releases/download/v1.0.0/Graft-win-x64.zip")
            .Should().BeTrue();
    }

    [Fact(DisplayName = "checkUrlを既定から変更している場合、ダウンロードURLのホストが異なれば拒否する")]
    public void 非既定checkUrlではホストが異なれば拒否する()
    {
        // これがまさに指摘されていた攻撃筋: checkUrlを握った側は期待ハッシュと
        // ダウンロードURLの両方を自由に決められるため、SHA256は無力。ホスト自体を
        // 独立に検証することで、checkUrlのホストとは無関係な場所からのダウンロードを塞ぐ。
        UpdateHostPolicy.IsAllowedDownloadUrl(
                "https://git.example.co.jp/api/v4/projects/1/releases/latest",
                "https://attacker.example.net/Graft-win-x64.zip")
            .Should().BeFalse();
    }

    [Fact(DisplayName = "ダウンロードURLがhttps以外・不正なURLなら常に拒否する")]
    public void httpsでないダウンロードURLは拒否する()
    {
        UpdateHostPolicy.IsAllowedDownloadUrl(UpdateHostPolicy.DefaultCheckUrl, "http://github.com/x.zip")
            .Should().BeFalse();
        UpdateHostPolicy.IsAllowedDownloadUrl(UpdateHostPolicy.DefaultCheckUrl, "not-a-url")
            .Should().BeFalse();
    }

    [Fact(DisplayName = "checkUrl自体が不正なURLなら（既定でない場合）安全側で拒否する")]
    public void checkUrlが不正なら拒否する()
    {
        UpdateHostPolicy.IsAllowedDownloadUrl("not-a-url", "https://github.com/x.zip")
            .Should().BeFalse();
    }

    [Fact(DisplayName = "IsDefaultCheckUrlは前後の空白・大文字小文字の違いのみ許容する")]
    public void IsDefaultCheckUrlの判定()
    {
        UpdateHostPolicy.IsDefaultCheckUrl(UpdateHostPolicy.DefaultCheckUrl).Should().BeTrue();
        UpdateHostPolicy.IsDefaultCheckUrl("  " + UpdateHostPolicy.DefaultCheckUrl.ToUpperInvariant() + "  ").Should().BeTrue();
        UpdateHostPolicy.IsDefaultCheckUrl("https://api.github.com/repos/other/repo/releases/latest").Should().BeFalse();
        UpdateHostPolicy.IsDefaultCheckUrl(null).Should().BeFalse();
    }
}
