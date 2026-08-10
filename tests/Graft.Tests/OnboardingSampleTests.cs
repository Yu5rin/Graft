using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 細かいユーザビリティ改善6: 初回起動ガイドの「サンプルで試す」が生成する
/// <see cref="OnboardingSample"/>の内容を検証する。要件どおり「パッチが確実に適用できる」ことを、
/// 実際に<see cref="PatchParser"/>・<see cref="ApplyEngine"/>を通して確認する（気持ちだけの
/// SEARCH/REPLACE一致ではなく、本物の適用パイプラインで検証する）。
/// </summary>
public class OnboardingSampleTests
{
    [Fact(DisplayName = "一時フォルダ配下にサンプルプロジェクトを生成する")]
    public void 一時フォルダにサンプルを生成する()
    {
        var sample = OnboardingSample.Create();
        try
        {
            // 要件: 生成先は一時フォルダ（利用者のドキュメント等を汚さない）。
            Path.GetFullPath(sample.ProjectRoot).Should().StartWith(Path.GetFullPath(Path.GetTempPath()));
            Directory.Exists(sample.ProjectRoot).Should().BeTrue();

            var filePath = Path.Combine(sample.ProjectRoot, OnboardingSample.SampleFileName);
            File.Exists(filePath).Should().BeTrue();

            var content = File.ReadAllText(filePath);
            // 日本語コメント入りであること（要件）。
            content.Should().Contain("#");
            content.Should().MatchRegex(@"[ぁ-んァ-ン一-龥]");
        }
        finally
        {
            OnboardingSample.Cleanup(sample.ProjectRoot);
        }
    }

    [Fact(DisplayName = "生成したパッチは実際のApplyEngineで確実に適用できる")]
    public async Task サンプルパッチが確実に適用できる()
    {
        var sample = OnboardingSample.Create();
        try
        {
            using var ws = new TempWorkspace();
            var appPaths = new AppPaths(ws.CreateDirectory("app"));
            var backup = new BackupManager(appPaths);
            var revisions = new RevisionStore(appPaths);
            var matcher = new MatchEngine();
            var engine = new ApplyEngine(backup, revisions, matcher);

            var patch = new PatchParser().Parse(sample.PatchText).Value;
            var guard = new PathGuard(sample.ProjectRoot, PathGuardOptions.Default);
            var ctx = new ApplyContext
            {
                ProjectId = "p_onboarding_sample",
                ProjectRoot = sample.ProjectRoot,
                Revision = 1,
                Settings = new Settings(),
                Guard = guard,
            };

            var dryRun = (await engine.DryRunAsync(patch, ctx)).Value;
            dryRun.Plans.Should().ContainSingle();
            dryRun.Plans[0].CanApply.Should().BeTrue("サンプルのSEARCH/REPLACEは生成したファイルと完全一致し、確認なしで適用できる必要がある");
            dryRun.Plans[0].NeedsConfirmation.Should().BeFalse("サンプル体験は迷わず適用できるものにするため、あいまい一致による要確認は避ける");

            var applyResult = await engine.ApplyAsync(dryRun, ctx);
            applyResult.IsSuccess.Should().BeTrue();

            var filePath = Path.Combine(sample.ProjectRoot, OnboardingSample.SampleFileName);
            var updated = File.ReadAllText(filePath);
            updated.Should().Contain("こんにちは、{name}さん！Graftへようこそ。");
            updated.Should().NotContain("TODO: ここでnameを使ったあいさつ文を組み立てて return してください");
        }
        finally
        {
            OnboardingSample.Cleanup(sample.ProjectRoot);
        }
    }

    [Fact(DisplayName = "Cleanupで一時フォルダを削除できる")]
    public void Cleanupで一時フォルダを削除できる()
    {
        var sample = OnboardingSample.Create();
        Directory.Exists(sample.ProjectRoot).Should().BeTrue();

        OnboardingSample.Cleanup(sample.ProjectRoot);

        Directory.Exists(sample.ProjectRoot).Should().BeFalse();
    }
}
