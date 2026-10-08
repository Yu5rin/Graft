using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// E302（このパッチはrNで適用済み）を、ドライランの結果が構造化して返すこと
/// （<see cref="DryRunResult.AlreadyAppliedRevision"/>）の検証。
/// 結果レベルのissuesにE302が載るだけでは、MainViewModelが成功時にそれを読まないため、
/// 利用者は要約入力と適用確認の窓を通り抜けた後にしか適用済みと分からなかった。
/// プレビューの時点で画面に出せるよう、適用済みのリビジョン番号を結果に持たせる。
/// 既存のE302（issues側）とE305（プランのIssues）の挙動は変えない。
/// </summary>
public class AlreadyAppliedDetectionTests
{
    private static string FullPatch(string summary, string path, string content) =>
        $"<<<< PATCH\nsummary: {summary}\n>>>>\n\n<<<< FILE: {path} MODE=FULL\n{content}\n>>>> END\n";

    private static async Task ApplyAsRevisionAsync(ApplyHarness harness, string patchText, int revision)
    {
        var ctx = harness.MakeContext(revision);
        var plan = await harness.DryRunAsync(patchText, ctx);
        var apply = await harness.ApplyAsync(plan, ctx);
        apply.IsSuccess.Should().BeTrue($"前提: r{revision}の適用が成功している必要がある");
    }

    private static async Task<GraftResult<DryRunResult>> PlanAsync(
        ApplyHarness harness, string patchText, int revision, bool forceReapply = false)
    {
        var planner = new DryRunPlanner(harness.Matcher, harness.Revisions);
        return await planner.PlanAsync(
            ApplyHarness.Parse(patchText), harness.MakeContext(revision, forceReapply: forceReapply), default);
    }

    [Fact(DisplayName = "適用済みのパッチをドライランすると、適用済みのリビジョン番号が結果に載り、E302もこれまでどおりissuesに載る")]
    public async Task 適用済みのパッチは適用済みのリビジョンが結果に載る()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var patch = FullPatch("最初", "a.txt", "first");
        await ApplyAsRevisionAsync(harness, patch, 1);

        var result = await PlanAsync(harness, patch, 2);

        result.IsSuccess.Should().BeTrue("ドライラン自体は成功のまま（ブロックを失敗扱いにしない）");
        result.Value.AlreadyAppliedRevision.Should().Be(1);
        var e302 = result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E302).Subject;
        e302.Severity.Should().Be(Severity.Error, "既存の挙動（適用時の再判定と同じ）を変えない");
        result.Value.Plans.Should().OnlyContain(p => p.CanApply,
            "全ブロックを失敗扱いにすると「修正を依頼」が押せてしまうため、ブロックは適用可能のまま");
    }

    [Fact(DisplayName = "未適用のパッチでは適用済みのリビジョンは載らない")]
    public async Task 未適用のパッチでは載らない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        await ApplyAsRevisionAsync(harness, FullPatch("最初", "a.txt", "first"), 1);

        var result = await PlanAsync(harness, FullPatch("別の内容", "a.txt", "second"), 2);

        result.Value.AlreadyAppliedRevision.Should().BeNull();
        result.Issues.Should().NotContain(i => i.Code == ErrorCode.E302);
    }

    [Fact(DisplayName = "強制再適用が許されているときは警告のE302だけで、適用済みのリビジョンは載らない（適用は止まらない）")]
    public async Task 強制再適用では適用を止めないので載らない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var patch = FullPatch("最初", "a.txt", "first");
        await ApplyAsRevisionAsync(harness, patch, 1);

        var result = await PlanAsync(harness, patch, 2, forceReapply: true);

        result.Value.AlreadyAppliedRevision.Should().BeNull("止まらないのに「適用できない」と見せてはならない");
        result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E302 && i.Severity == Severity.Warning);
    }

    [Fact(DisplayName = "適用済みのリビジョンが取り消された（rolled_back）後は、同じパッチでも適用済みとは見なさない")]
    public async Task 取り消されたリビジョンは適用済みとは見なさない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        var patch = FullPatch("最初", "a.txt", "first");
        await ApplyAsRevisionAsync(harness, patch, 1);

        // 履歴上のr1を取り消し済み（rolled_back）にする。RevisionStoreのListAsyncは
        // Status=successだけを「適用済み」と数える（DryRunPlanner.CheckDuplicateAsync参照）。
        var listed = await harness.Revisions.ListAsync(harness.ProjectId);
        var r1 = listed.Value.Single(s => s.Manifest.Revision == 1);
        var path = System.IO.Path.Combine(r1.FolderPath, "manifest.json");
        var json = System.IO.File.ReadAllText(path);
        System.IO.File.WriteAllText(path, json.Replace("\"status\": \"success\"", "\"status\": \"rolled_back\"")
            .Replace("\"status\":\"success\"", "\"status\":\"rolled_back\""));

        var result = await PlanAsync(harness, patch, 2);

        result.Value.AlreadyAppliedRevision.Should().BeNull();
        result.Issues.Should().NotContain(i => i.Code == ErrorCode.E302);
    }
}
