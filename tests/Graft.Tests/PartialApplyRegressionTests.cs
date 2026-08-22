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
/// 再現の結論: 既定の適用モード（<see cref="Settings.ApplyMode"/>）が"allOrNothing"だったため、
/// パッチに含まれるブロックのうち1件でもSEARCH不一致（E101）等で適用できないものがあると、
/// 実際にチェックが付いている（適用可能な）ブロックが残っていても<see cref="ApplyEngine.ApplyAsync"/>
/// が全体を失敗として返し、利用者には「E101 SEARCH部が見つからない」等の内部エラーだけが表示され、
/// 履歴への記録も成功の通知も出なかった（ほとんどの利用者はこの設定を一度も開かないため、
/// 既定値そのものが実質的な不具合として体感されていた）。
/// 対応: <see cref="Settings.ApplyMode"/>の既定値を"partial"へ変更した（Settings.cs参照）。
/// これにより、以下のシナリオが既定設定のまま（設定を一切変更せずに）成立することを確認する。
/// 「全件適用（All or Nothing）」自体の挙動（選択状態を問わず1件でも失敗すれば全体を中止する）
/// 自体は変更していない。その回帰テスト（allOrNothingを明示的に選んだ場合は同じ状況でも
/// 中止され、設定が理由であることが分かるE304が出ること）はApplyEngineTests.csにある。
/// </summary>
public class PartialApplyRegressionTests
{
    private static string BuildSrPatch(string path, string search, string replace) =>
        $"<<<< FILE: {path}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n";

    [Fact(DisplayName = "既定設定（partial）で3ブロック中2つ成功・1つ失敗しても、成功した2件は適用され履歴が1件増える")]
    public async Task 既定設定で3ブロック中2成功1失敗が正常に適用される()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("a.txt", "aaa\n");
        harness.WriteProjectText("b.txt", "bbb\n");
        harness.WriteProjectText("c.txt", "存在しない検索対象は含まれていません\n");

        var patchText = BuildSrPatch("a.txt", "aaa", "aaa-changed")
            + "\n" + BuildSrPatch("b.txt", "bbb", "bbb-changed")
            + "\n" + BuildSrPatch("c.txt", "見つからない文字列", "置換後");

        // 設定は一切指定しない＝既定値（partial）のままであることが本質。
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patchText, ctx);
        dryRun.FailedCount.Should().Be(1, "c.txt側だけがSEARCH不一致で失敗するはず");
        dryRun.ApplicableCount.Should().Be(2, "a.txt・b.txt側は適用可能なはず");

        var apply = await harness.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeTrue(
            "既定設定のままでも、適用可能なブロックが残っている限りエラーにならず成功するはず。" +
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

    [Fact(DisplayName = "同じ状況でも「全件適用」を選んでいれば、何も書き込まれず中止し、履歴も増えない")]
    public async Task allOrNothingを選んでいれば同じ状況でも中止する()
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

        apply.IsSuccess.Should().BeFalse("「全件適用」を選んでいる限り、1件でも失敗すれば中止するのが仕様どおりの挙動");
        apply.Errors.First().Code.Should().Be(ErrorCode.E304,
            "設定（全件適用）が理由で中止したことが最初に伝わるべき");
        Encoding.UTF8.GetString(harness.ReadProjectBytes("a.txt")).Should().Be("aaa\n", "中止された場合は何も書き換わらないはず");
        Encoding.UTF8.GetString(harness.ReadProjectBytes("b.txt")).Should().Be("bbb\n");
        var history = await harness.Revisions.ListAsync(harness.ProjectId);
        history.Value.Should().BeEmpty("何も書き込まれていないので履歴は増えないはず");
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
        var ctx = harness.MakeContext(1);
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

    [Fact(DisplayName = "既定設定（partial）でも全ブロックが失敗すれば履歴は増えずエラーになる")]
    public async Task 既定設定でも全ブロック失敗ならエラーになる()
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

        apply.IsSuccess.Should().BeFalse("適用可能なブロックが1件も無い場合は、部分適用モードでもエラーになるはず");
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E101);
        var history = await harness.Revisions.ListAsync(harness.ProjectId);
        history.Value.Should().BeEmpty("何も書き換わっていないので履歴は増えないはず");
    }

    [Fact(DisplayName = "既定設定（partial）でもチェックを外して適用対象0件にすればエラーになり、空のリビジョンは記録されない")]
    public async Task 既定設定でもチェックを外して0件ならエラーになる()
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
