using System.Text.RegularExpressions;
using FluentAssertions;
using Graft.Core.Update;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="UpdatePlatformPolicy"/>（OSごとにどの添付を選び、自動の入れ替えまで提供するか）と、
/// 添付ファイル名の規則を固定する。
///
/// 【このテストが守っている2つの不具合（2026-09-25）】
/// <list type="number">
/// <item>Linuxでは自動更新が必ず失敗して巻き戻っていた。OSによる分岐が無く、Linuxでも
/// Windows版のzipを取りに行き、入れ替えで<c>Graft.exe</c>が見つからず失敗していた。
/// Linuxでは入れ替えを提供せず、リリースページの案内だけにした（理由は
/// <see cref="UpdatePlatformPolicy"/>のクラスコメント）。</item>
/// <item><c>.github/workflows/release.yml</c>がタグそのもの（"v"付き）を添付ファイル名に使い、
/// 自動更新がAPIに届かないときに組み立てるURL（"v"なし）と食い違っていた。さらに書庫に
/// 取扱説明書.md・はじめにお読みください.txtを入れておらず、中身の検査でも必ず中止されていた。
/// ワークフローを<c>tools/New-Release.ps1</c>と同じ形に直し、両方のファイルを読んで
/// 規則から外れていないことをここで確かめる。</item>
/// </list>
/// </summary>
public class UpdatePlatformPolicyTests
{
    [Theory(DisplayName = "OSの判定結果から区分を決める（WindowsとLinux以外は配布物の無いOS）")]
    [InlineData(true, false, UpdatePlatform.Windows)]
    [InlineData(false, true, UpdatePlatform.Linux)]
    [InlineData(false, false, UpdatePlatform.Unsupported)]
    public void OSの区分を決める(bool isWindows, bool isLinux, UpdatePlatform expected)
    {
        UpdatePlatformPolicy.Detect(isWindows, isLinux).Should().Be(expected);
    }

    [Fact(DisplayName = "自動の入れ替えを提供するのはWindowsだけ（Linuxで失敗して巻き戻る動作を見せない）")]
    public void 入れ替えを提供するのはWindowsだけ()
    {
        UpdatePlatformPolicy.CanSelfInstall(UpdatePlatform.Windows).Should().BeTrue();

        // 不具合の再発防止: 入れ替えの実処理（UpdateZipInspector・SelfUpdateInstaller・
        // UpdateFiles.RequiredFileNames）はWindows版のzip・Graft.exeに固定されている。
        // Linuxでtrueにすると、Windows版zipを落として Graft.exe の退避で必ず失敗する状態に戻る。
        UpdatePlatformPolicy.CanSelfInstall(UpdatePlatform.Linux).Should().BeFalse();
        UpdatePlatformPolicy.CanSelfInstall(UpdatePlatform.Unsupported).Should().BeFalse();
    }

    [Fact(DisplayName = "入れ替え対象の一覧はWindows版のファイル名であり、Linux版の実行ファイル名（Graft）を含まない")]
    public void 入れ替え対象はWindows版のファイル名()
    {
        // CanSelfInstall(Linux)をfalseにしている前提そのものを固定する。もしこの一覧が
        // Linux版にも対応したら、Linuxで入れ替えを提供するかを改めて判断すること。
        UpdateFiles.RequiredFileNames.Should().Contain("Graft.exe");
        UpdateFiles.RequiredFileNames.Should().NotContain("Graft");
    }

    [Theory(DisplayName = "添付ファイル名は Graft-<タグから先頭のvを除いた版><OSごとの末尾>")]
    [InlineData("v1.0.20", UpdatePlatform.Windows, "Graft-1.0.20-win-x64.zip")]
    [InlineData("v1.0.20", UpdatePlatform.Linux, "Graft-1.0.20-linux-x64.tar.gz")]
    [InlineData("V1.0.20", UpdatePlatform.Windows, "Graft-1.0.20-win-x64.zip")]
    [InlineData("1.0.20", UpdatePlatform.Windows, "Graft-1.0.20-win-x64.zip")]
    [InlineData("v1.0.20", UpdatePlatform.Unsupported, null)]
    public void 添付ファイル名の規則(string tag, UpdatePlatform platform, string? expected)
    {
        // 実物（GitHubのv1.0.2〜v1.0.20の添付。2026-09-25にAPIで確認）と同じ名前になること。
        UpdatePlatformPolicy.BuildAssetFileName(tag, platform).Should().Be(expected);
    }

    [Fact(DisplayName = "APIに届かないときに組み立てるWindows版の名前は、共通の規則と同じ")]
    public void Atom経路の名前は共通の規則と同じ()
    {
        UpdateAtomFeedLogic.BuildWindowsAssetFileName("v1.0.21")
            .Should().Be(UpdatePlatformPolicy.BuildAssetFileName("v1.0.21", UpdatePlatform.Windows));
    }

    [Fact(DisplayName = "WindowsではWindows版の添付を、LinuxではLinux版の添付を選ぶ（実物の名前）")]
    public void OSごとに添付を選ぶ()
    {
        var release = Release("Graft-1.0.20-linux-x64.tar.gz", "Graft-1.0.20-win-x64.zip");

        UpdatePlatformPolicy.SelectAsset(release, UpdatePlatform.Windows)!.Name.Should().Be("Graft-1.0.20-win-x64.zip");
        UpdatePlatformPolicy.SelectAsset(release, UpdatePlatform.Linux)!.Name.Should().Be("Graft-1.0.20-linux-x64.tar.gz");
        UpdatePlatformPolicy.SelectAsset(release, UpdatePlatform.Unsupported).Should().BeNull();
    }

    [Fact(DisplayName = "過去にワークフローが作った v 付きの名前（Graft-v1.0.1-win-x64.zip）も、APIの経路では選べる")]
    public void v付きの名前も選べる()
    {
        // v1.0.0・v1.0.1の実物の添付名。APIで添付の一覧が取れる経路では末尾だけで選ぶので、
        // 名前の"v"の有無で更新が止まらない。
        var release = Release("Graft-v1.0.1-linux-x64.tar.gz", "Graft-v1.0.1-win-x64.zip");

        UpdatePlatformPolicy.SelectAsset(release, UpdatePlatform.Windows)!.Name.Should().Be("Graft-v1.0.1-win-x64.zip");
        UpdatePlatformPolicy.SelectAsset(release, UpdatePlatform.Linux)!.Name.Should().Be("Graft-v1.0.1-linux-x64.tar.gz");
    }

    [Fact(DisplayName = "Linux版の添付しか無ければ、WindowsではLinux版を取り違えずnullを返す")]
    public void 取り違えない()
    {
        var release = Release("Graft-1.0.20-linux-x64.tar.gz");

        UpdatePlatformPolicy.SelectAsset(release, UpdatePlatform.Windows).Should().BeNull();
    }

    [Fact(DisplayName = "tools/New-Release.ps1 は、自動更新の規則どおり v なしの版で添付を作る")]
    public void 手動のリリーススクリプトは規則どおり()
    {
        var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "tools", "New-Release.ps1"));

        script.Should().Contain("\"Graft-$resolvedVersion-win-x64.zip\"");
        script.Should().Contain("\"Graft-$resolvedVersion-linux-x64.tar.gz\"");
    }

    [Fact(DisplayName = "release.yml は、タグから v を除いた版で添付を作る（タグそのものを名前に使わない）")]
    public void ワークフローは規則どおり()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "release.yml"));

        // 不具合の再発防止: 以前は Graft-${{ github.ref_name }}-win-x64.zip（v付き）だった。
        workflow.Should().NotMatchRegex(@"Graft-\$\{\{\s*github\.ref_name\s*\}\}");
        workflow.Should().Contain("VERSION=\"${GITHUB_REF_NAME#v}\"");
        workflow.Should().Contain("\"Graft-${VERSION}-win-x64.zip\"");
        workflow.Should().Contain("\"Graft-${VERSION}-linux-x64.tar.gz\"");
        workflow.Should().Contain("Graft-${{ steps.ver.outputs.version }}-win-x64.zip");
        workflow.Should().Contain("Graft-${{ steps.ver.outputs.version }}-linux-x64.tar.gz");
    }

    [Fact(DisplayName = "release.yml が配る前に確かめる zip の中身は、自動更新の入れ替え対象と一致する")]
    public void ワークフローの中身の一覧は入れ替え対象と一致する()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "release.yml"));

        // 「配布物の中身を確かめる」段の expected の一覧（printf の引数）を取り出す。
        var block = Regex.Match(workflow, @"expected=""\$\(printf '%s\\n' \\(?<list>.*?)\| LC_ALL=C sort\)""", RegexOptions.Singleline);
        block.Success.Should().BeTrue("release.yml に zip の中身を確かめる段が無い");
        var names = Regex.Matches(block.Groups["list"].Value, @"'([^']+)'").Select(m => m.Groups[1].Value);

        // 不具合の再発防止: 以前のワークフローの zip には 取扱説明書.md・はじめにお読みください.txt が
        // 無く、UpdateZipInspector.Validate が「必要なファイルが不足」で必ず中止していた。
        names.Should().BeEquivalentTo(UpdateFiles.RequiredFileNames);
    }

    private static GitHubReleaseInfo Release(params string[] assetNames) => new()
    {
        TagName = "v1.0.20",
        Assets = assetNames.Select(n => new GitHubReleaseAsset { Name = n }).ToArray(),
    };

    /// <summary>テスト実行ディレクトリからGraft.slnを上へ辿って、リポジトリのルートを返す。</summary>
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Graft.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Graft.slnが見つからず、リポジトリルートを特定できませんでした。");
    }
}
