using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 実機不具合対応（Windows実機・v1.0.6）: 「一部接ぎ木できる状態で、一部だけを適用すると、
/// 通常は履歴に関するお知らせが出るはずが、エラーが表示される」の回帰テスト。
///
/// 再現の結論: <see cref="Core.ApplyEngine.ApplyAsync"/>のallOrNothing判定
/// （<c>plan.Plans.Where(p => !p.CanApply)</c>）が、対象ブロックの選択状態（IsSelected）を
/// 見ずに「パッチ全体に1件でも適用できないブロックがあるか」だけで判定していた。失敗ブロックは
/// <see cref="Core.DryRunPlanner"/>が自動的にIsSelected=falseへ倒し、UI側でも
/// チェックを付け直せない（<see cref="ViewModels.BlockItemViewModel.CanToggle"/>参照）ため、
/// 利用者が「失敗ブロックを除いた残りだけを適用する」という通常の操作をしても、選ばれていない
/// 失敗ブロックのせいで適用処理全体がエラー扱いになり、履歴への記録も成功の通知も出なかった
/// （既定の適用モードが「全件適用（All or Nothing）」でも、AI回答の一部だけが実際のコードに
/// 当てはまらないのはごくありふれた状況であり、ほとんどの利用者がこの不具合に遭遇していた）。
/// 対応（<see cref="Core.ApplyEngine.ApplyAsync"/>）: 判定条件へ<c>p.IsSelected &amp;&amp;</c>
/// を加え、「選んだブロックが全部適用できるなら適用、選んだブロックの中に1つでもダメなものが
/// あれば中止する」という「全件適用（All or Nothing）」本来の意味に合わせた。既定の適用モード
/// （allOrNothing）自体は変更していない。allOrNothingの本来の安全機構（選択されている失敗
/// ブロックがあれば中止する）自体の回帰テストはApplyEngineTests.csにある。
/// </summary>
public class PartialApplyRegressionTests
{
    private static string BuildSrPatch(string path, string search, string replace) =>
        $"<<<< FILE: {path}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n";

    [Fact(DisplayName = "既定の適用モード（allOrNothing）でも3ブロック中2つを選択して適用・1つは選択せず失敗のままなら成功し、履歴が1件増える")]
    public async Task 既定のallOrNothingで2成功1失敗が正常に適用される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("a.txt", "aaa\n");
        harness.WriteProjectText("b.txt", "bbb\n");
        harness.WriteProjectText("c.txt", "存在しない検索対象は含まれていません\n");

        var patchText = BuildSrPatch("a.txt", "aaa", "aaa-changed")
            + "\n" + BuildSrPatch("b.txt", "bbb", "bbb-changed")
            + "\n" + BuildSrPatch("c.txt", "見つからない文字列", "置換後");

        // 明示的に既定値どおりのallOrNothingを指定する（コードの既定値が将来変わっても
        // このテストの意図＝「allOrNothingでも部分適用は正常系」が揺らがないようにするため）。
        var settings = new Settings { ApplyMode = "allOrNothing" };
        var ctx = harness.MakeContext(1, settings);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        dryRun.FailedCount.Should().Be(1, "c.txt側だけがSEARCH不一致で失敗するはず");
        dryRun.ApplicableCount.Should().Be(2, "a.txt・b.txt側は適用可能なはず");
        dryRun.Plans.Single(p => p.Path == "c.txt").IsSelected.Should().BeFalse("失敗ブロックは自動的に選択解除されるはず");

        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue(
            "allOrNothingのままでも、選んでいる（適用可能な）ブロックが残っている限りエラーにならず成功するはず。" +
            $"実際のissues: {string.Join(", ", apply.Issues.Select(i => i.ToDisplayText()))}");
        apply.Value.Entries.Should().HaveCount(2, "実際に書き換わったのはa.txt・b.txtの2件だけのはず");
        apply.Value.Entries.Select(e => e.Path).Should().BeEquivalentTo(new[] { "a.txt", "b.txt" },
            "失敗したc.txtは履歴のリビジョンに含まれないはず（実際に書き換えたファイルだけを含む）");

        Encoding.UTF8.GetString(harness.ReadProjectBytes("a.txt")).Should().Be("aaa-changed\n");
        Encoding.UTF8.GetString(harness.ReadProjectBytes("b.txt")).Should().Be("bbb-changed\n");
        Encoding.UTF8.GetString(harness.ReadProjectBytes("c.txt")).Should().Be("存在しない検索対象は含まれていません\n",
            "失敗したブロックの対象ファイルは書き換えられていないはず");

        var history = await harness.Revisions.ListAsync(harness.ProjectId);
        history.Value.Should().HaveCount(1, "履歴が1件増えるはず");
        history.Value[0].Manifest.Status.Should().Be(RevisionStatus.Success);
    }

    [Fact(DisplayName = "上記の部分適用を元に戻すと、実際に適用した2ファイルだけが元に戻り、失敗したブロックの対象ファイルには触れない")]
    public async Task 部分適用の元に戻すは適用した2ファイルだけを戻す()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("a.txt", "aaa\n");
        harness.WriteProjectText("b.txt", "bbb\n");
        harness.WriteProjectText("c.txt", "存在しない検索対象は含まれていません\n");

        var patchText = BuildSrPatch("a.txt", "aaa", "aaa-changed")
            + "\n" + BuildSrPatch("b.txt", "bbb", "bbb-changed")
            + "\n" + BuildSrPatch("c.txt", "見つからない文字列", "置換後");
        var settings = new Settings { ApplyMode = "allOrNothing" };
        var ctx = harness.MakeContext(1, settings);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        var apply = await harness.ApplyAsync(dryRun, ctx);
        apply.IsSuccess.Should().BeTrue();

        var history = await harness.Revisions.ListAsync(harness.ProjectId);
        var summary = history.Value.Single(r => r.Manifest.Revision == 1);

        var restorer = new RevisionRestorer(harness.Paths);
        var restored = await restorer.RestoreAsync(harness.ProjectId, harness.ProjectRoot, summary, force: false);

        restored.IsSuccess.Should().BeTrue(string.Join(", ", restored.Issues.Select(i => i.ToDisplayText())));
        restored.Value.Should().BeEquivalentTo(new[] { "a.txt", "b.txt" },
            "元に戻す対象は実際に適用した2ファイルだけのはず");
        Encoding.UTF8.GetString(harness.ReadProjectBytes("a.txt")).Should().Be("aaa\n");
        Encoding.UTF8.GetString(harness.ReadProjectBytes("b.txt")).Should().Be("bbb\n");
        Encoding.UTF8.GetString(harness.ReadProjectBytes("c.txt")).Should().Be("存在しない検索対象は含まれていません\n",
            "元々適用されていないc.txtは元に戻す操作でも触れられないはず");
    }

    [Fact(DisplayName = "全ブロックが失敗すれば（allOrNothing・部分適用のどちらでも）履歴は増えずエラーになる")]
    public async Task 全ブロック失敗ならどちらのモードでもエラーになる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("bad1.txt", "存在しない検索対象1\n");
        harness.WriteProjectText("bad2.txt", "存在しない検索対象2\n");

        var patchText = BuildSrPatch("bad1.txt", "見つからない1", "置換後1")
            + "\n" + BuildSrPatch("bad2.txt", "見つからない2", "置換後2");
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        dryRun.ApplicableCount.Should().Be(0, "両方ともSEARCH不一致で失敗するはず");

        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse("適用可能なブロックが1件も無い場合はエラーになるはず");
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E101);
        var history = await harness.Revisions.ListAsync(harness.ProjectId);
        history.Value.Should().BeEmpty("何も書き換わっていないので履歴は増えないはず");
    }

    [Fact(DisplayName = "チェックを外して適用対象0件にすればエラーになり、空のリビジョンは記録されない")]
    public async Task チェックを外して0件ならエラーになる()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("a.txt", "aaa\n");

        var patchText = BuildSrPatch("a.txt", "aaa", "aaa-changed");
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        dryRun.ApplicableCount.Should().Be(1);

        // 利用者が手動でチェックを外した状態を模す（MainViewModel.ApplyCoreAsyncが
        // Blocks各行のIsSelectedをPlanへ反映してからApplyEngine.ApplyAsyncへ渡す処理を再現）。
        var deselected = dryRun with { Plans = dryRun.Plans.Select(p => p with { IsSelected = false }).ToList() };

        var apply = await harness.ApplyAsync(deselected, ctx);

        apply.IsSuccess.Should().BeFalse("チェック済みの適用対象が1件も無いのに成功扱いにしてはならない");
        Encoding.UTF8.GetString(harness.ReadProjectBytes("a.txt")).Should().Be("aaa\n", "何も書き換わっていないはず");
        var history = await harness.Revisions.ListAsync(harness.ProjectId);
        history.Value.Should().BeEmpty("空のリビジョンを記録してはならない");
    }
}
