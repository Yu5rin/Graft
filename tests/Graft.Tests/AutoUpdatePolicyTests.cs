using FluentAssertions;
using Graft.Core.Update;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="AutoUpdatePolicy"/>（確認なしの自動更新に進めてよいか）の判断を固定する。
/// 判断は純粋な関数なので、通信もファイルも使わない。「進めてよい」条件が1つでも欠けたら
/// 必ず確認ダイアログへ戻ること、とくにSHA256を照合できない場合に照合を省いたまま
/// 進まないこと（確認なしの入れ替えで整合性の照合を黙って省かない、という要件）を守る。
/// </summary>
public class AutoUpdatePolicyTests
{
    private const string CurrentVersion = "1.0.24.0";
    private static readonly string DefaultUrl = UpdateHostPolicy.DefaultCheckUrl;
    private static readonly string GoodDigest = "sha256:" + new string('a', 64);

    private static GitHubReleaseInfo Release(
        string tag = "v1.0.25", string? digest = null, bool allowMissingChecksum = false,
        bool prerelease = false, bool withWindowsAsset = true)
    {
        var assets = new List<GitHubReleaseAsset>
        {
            new() { Name = $"Graft-{tag.TrimStart('v')}-linux-x64.tar.gz", BrowserDownloadUrl = "https://github.com/Yu5rin/Graft/releases/download/x/linux", Digest = GoodDigest },
        };
        if (withWindowsAsset)
        {
            assets.Add(new GitHubReleaseAsset
            {
                Name = $"Graft-{tag.TrimStart('v')}-win-x64.zip",
                BrowserDownloadUrl = "https://github.com/Yu5rin/Graft/releases/download/x/win",
                Digest = digest ?? GoodDigest,
            });
        }

        return new GitHubReleaseInfo
        {
            TagName = tag,
            HtmlUrl = "https://github.com/Yu5rin/Graft/releases/tag/" + tag,
            Prerelease = prerelease,
            AllowMissingChecksum = allowMissingChecksum,
            Assets = assets,
        };
    }

    [Fact(DisplayName = "全条件を満たす（Windows・新しい版・既定の確認先・SHA256あり）なら確認なしで進める")]
    public void 全条件を満たせば進める()
    {
        var decision = AutoUpdatePolicy.Evaluate(Release(), CurrentVersion, UpdatePlatform.Windows, DefaultUrl);

        decision.CanProceed.Should().BeTrue();
        decision.Block.Should().Be(AutoInstallBlock.None);
        decision.Reason.Should().BeNull();
    }

    [Fact(DisplayName = "回数上限でURLを規則から組み立てた経路（AllowMissingChecksum）は、照合を省いて進めずダイアログへ戻す")]
    public void AllowMissingChecksumならダイアログへ戻す()
    {
        // この経路は配布物のdigestが全てnull。確認なしでは、照合を省くことへの同意を得られない。
        var release = Release(digest: "", allowMissingChecksum: true);

        var decision = AutoUpdatePolicy.Evaluate(release, CurrentVersion, UpdatePlatform.Windows, DefaultUrl);

        decision.Block.Should().Be(AutoInstallBlock.ChecksumUnavailable);
        decision.Reason.Should().Contain("SHA256");
    }

    [Fact(DisplayName = "AllowMissingChecksumは、配布物にdigestが付いていても確認なしでは進めない")]
    public void AllowMissingChecksumはdigestがあっても戻す()
    {
        // フラグの意味は「この経路では照合を省く」。digestの有無で判断を緩めない。
        var decision = AutoUpdatePolicy.Evaluate(
            Release(allowMissingChecksum: true), CurrentVersion, UpdatePlatform.Windows, DefaultUrl);

        decision.Block.Should().Be(AutoInstallBlock.ChecksumUnavailable);
    }

    [Theory(DisplayName = "APIが応答したのにdigestが無い・sha256として読めないときも、ダウンロードする前にダイアログへ戻す")]
    [InlineData("")]
    [InlineData("md5:abcdef")]
    [InlineData("sha256:not-hex")]
    [InlineData("sha256:abcd")]
    public void digestを読めなければダイアログへ戻す(string digest)
    {
        var decision = AutoUpdatePolicy.Evaluate(
            Release(digest: digest), CurrentVersion, UpdatePlatform.Windows, DefaultUrl);

        decision.Block.Should().Be(AutoInstallBlock.ChecksumUnavailable);
    }

    [Theory(DisplayName = "自分で入れ替えられないOS（Linux・その他）は、確認なしでは進めない")]
    [InlineData(UpdatePlatform.Linux)]
    [InlineData(UpdatePlatform.Unsupported)]
    public void 入れ替えに対応しないOSではダイアログへ戻す(UpdatePlatform platform)
    {
        var decision = AutoUpdatePolicy.Evaluate(Release(), CurrentVersion, platform, DefaultUrl);

        decision.Block.Should().Be(AutoInstallBlock.UnsupportedPlatform);
    }

    [Fact(DisplayName = "プレリリースは確認なしでは入れ替えない")]
    public void プレリリースはダイアログへ戻す()
    {
        var decision = AutoUpdatePolicy.Evaluate(
            Release(prerelease: true), CurrentVersion, UpdatePlatform.Windows, DefaultUrl);

        decision.Block.Should().Be(AutoInstallBlock.Prerelease);
    }

    [Theory(DisplayName = "同じ版・古い版・版として読めないタグへは、確認なしでは入れ替えない（ダウングレードの防止）")]
    [InlineData("v1.0.24")]
    [InlineData("v1.0.23")]
    [InlineData("v0.9.99")]
    [InlineData("v1.0.25-beta")]
    [InlineData("latest")]
    public void 新しくない版はダイアログへ戻す(string tag)
    {
        var decision = AutoUpdatePolicy.Evaluate(
            Release(tag: tag), CurrentVersion, UpdatePlatform.Windows, DefaultUrl);

        decision.Block.Should().Be(AutoInstallBlock.NotNewer);
    }

    [Fact(DisplayName = "現在の版を読めないときは、新しいと言い切れないので進めない")]
    public void 現在の版を読めなければ戻す()
    {
        var decision = AutoUpdatePolicy.Evaluate(Release(), "不明", UpdatePlatform.Windows, DefaultUrl);

        decision.Block.Should().Be(AutoInstallBlock.NotNewer);
    }

    [Fact(DisplayName = "版の比較は数値で行う（1.0.10は1.0.9より新しい）")]
    public void 版は数値で比べる()
    {
        var decision = AutoUpdatePolicy.Evaluate(
            Release(tag: "v1.0.10"), "1.0.9.0", UpdatePlatform.Windows, DefaultUrl);

        decision.CanProceed.Should().BeTrue("文字列比較だと1.0.10が古いと誤判定される");
    }

    [Fact(DisplayName = "更新の確認先が既定から変更されているときは、確認ダイアログの警告を見せるために進めない")]
    public void 確認先が変更されていればダイアログへ戻す()
    {
        var decision = AutoUpdatePolicy.Evaluate(
            Release(), CurrentVersion, UpdatePlatform.Windows, "https://example.com/releases/latest");

        decision.Block.Should().Be(AutoInstallBlock.NonDefaultCheckUrl);
    }

    [Fact(DisplayName = "このOS向けの配布物がリリースに無ければ進めない")]
    public void 配布物が無ければダイアログへ戻す()
    {
        var decision = AutoUpdatePolicy.Evaluate(
            Release(withWindowsAsset: false), CurrentVersion, UpdatePlatform.Windows, DefaultUrl);

        decision.Block.Should().Be(AutoInstallBlock.NoAsset);
    }

    [Fact(DisplayName = "進めない理由は、すべてログに残せる日本語の文になっている")]
    public void 進めない理由には文言がある()
    {
        var blocked = new[]
        {
            AutoUpdatePolicy.Evaluate(Release(allowMissingChecksum: true), CurrentVersion, UpdatePlatform.Windows, DefaultUrl),
            AutoUpdatePolicy.Evaluate(Release(), CurrentVersion, UpdatePlatform.Linux, DefaultUrl),
            AutoUpdatePolicy.Evaluate(Release(prerelease: true), CurrentVersion, UpdatePlatform.Windows, DefaultUrl),
            AutoUpdatePolicy.Evaluate(Release(tag: "v1.0.24"), CurrentVersion, UpdatePlatform.Windows, DefaultUrl),
            AutoUpdatePolicy.Evaluate(Release(), CurrentVersion, UpdatePlatform.Windows, "https://example.com/"),
            AutoUpdatePolicy.Evaluate(Release(withWindowsAsset: false), CurrentVersion, UpdatePlatform.Windows, DefaultUrl),
        };

        blocked.Should().OnlyContain(d => !d.CanProceed && !string.IsNullOrWhiteSpace(d.Reason));
        blocked.Select(d => d.Block).Should().OnlyHaveUniqueItems();
    }
}
