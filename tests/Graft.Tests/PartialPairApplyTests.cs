using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// バグA（同一FILEセクション内の1ペア失敗が、成功しているペアまで連座させる）の
/// エンドツーエンド回帰テスト。ApplyEngine（DryRunPlanner・BlockResolver・書き込み）を
/// 通しで検証する（単体テストは <see cref="BlockResolverTests"/> を参照）。
/// </summary>
public class PartialPairApplyTests
{
    [Fact(DisplayName = "バグA回帰: 同一FILEセクション内で1ペアだけ失敗しても、もう一方のペアは本適用まで成功する")]
    public async Task 同一FILEセクション内の1ペア失敗は他のペアを連座させない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("multi.txt", "line1\nline2\nline3\n");

        var patchText = """
            <<<< FILE: multi.txt
            <<<<<<< SEARCH
            存在しない検索対象
            =======
            置換後
            >>>>>>> REPLACE
            <<<<<<< SEARCH
            line2
            =======
            line2changed
            >>>>>>> REPLACE
            """;
        var settings = new Graft.Infra.Settings { ApplyMode = "partial" };
        var ctx = harness.MakeContext(1, settings);
        var dryRun = await harness.DryRunAsync(patchText, ctx);

        var filePlans = dryRun.Plans.Where(p => p.Path == "multi.txt").ToList();
        filePlans.Should().HaveCount(2, "1件のFILEセクションでも、失敗ペアと成功ペアはそれぞれ独立したユニットになるはず");

        var failedPlan = filePlans.Single(p => !p.CanApply);
        failedPlan.Issues.Should().Contain(i => i.Code == ErrorCode.E101);
        failedPlan.BeforeText.Should().Be("line1\nline2\nline3",
            "バグB回帰: 失敗ユニットにもインライン編集の再判定に使う基準テキストが設定されるはず");

        var okPlan = filePlans.Single(p => p.CanApply);
        okPlan.IsSelected.Should().BeTrue();

        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue("部分適用モードでは成功ペアだけが適用され全体は成功するはず");
        var content = Encoding.UTF8.GetString(harness.ReadProjectBytes("multi.txt"));
        content.Should().Be("line1\nline2changed\nline3\n",
            "失敗ペアに引きずられず、成功ペアだけが正しく書き込まれるはず");
    }
}
