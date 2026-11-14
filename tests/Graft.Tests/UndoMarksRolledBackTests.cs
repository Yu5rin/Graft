using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 取り消したリビジョンが success のまま残り、同じパッチを貼り直すと「適用済み」(E302)で適用できなかった不具合の
/// 回帰テスト。取り消しが成功したら manifest.json と history.jsonl の両方を rolled_back にする。
/// </summary>
public class UndoMarksRolledBackTests
{
    private const string Patch = "<<<< FILE: a.txt MODE=FULL\npatched\n>>>> END\n";

    private static string Text(ApplyHarness h, string path) => Encoding.UTF8.GetString(h.ReadProjectBytes(path));

    private static async Task<RevisionSummary> ApplyAsync(ApplyHarness h, int revision, string patch)
    {
        var ctx = h.MakeContext(revision);
        var dryRun = await h.DryRunAsync(patch, ctx);
        var applied = await h.ApplyAsync(dryRun, ctx);
        applied.IsSuccess.Should().BeTrue(string.Join(",", applied.Issues.Select(i => i.ToDisplayText())));
        return (await h.Revisions.ReadAsync(h.ProjectId, revision)).Value;
    }

    private static Task<GraftResult<System.Collections.Generic.IReadOnlyList<string>>> UndoAsync(
        ApplyHarness h, RevisionSummary summary, bool force = false)
        => new RevisionRestorer(h.Paths).RestoreAsync(h.ProjectId, h.ProjectRoot, summary, force);

    [Fact(DisplayName = "再現: 適用→取り消し→同じパッチをもう一度解析しても、E302にならず適用し直せる")]
    public async Task 取り消したパッチを貼り直せる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "original\n");
        var summary = await ApplyAsync(h, 1, Patch);

        (await UndoAsync(h, summary)).IsSuccess.Should().BeTrue();
        Text(h, "a.txt").Should().Be("original\n");

        var ctx = h.MakeContext(2);
        var dryRun = await h.DryRunAsync(Patch, ctx);
        dryRun.AlreadyAppliedRevision.Should().BeNull("取り消し済みのr1と同じパッチは、適用済みではない");
        var again = await h.ApplyAsync(dryRun, ctx);
        again.IsSuccess.Should().BeTrue(string.Join(",", again.Issues.Select(i => i.ToDisplayText())));
        Text(h, "a.txt").Should().Be("patched\n");
    }

    [Fact(DisplayName = "取り消すと、manifest.jsonとhistory.jsonlの両方がrolled_backになる")]
    public async Task manifestとhistoryの両方に反映される()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "original\n");
        var summary = await ApplyAsync(h, 1, Patch);

        await UndoAsync(h, summary);

        (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        var index = await new RevisionIndex(h.Paths).ReadAllAsync(h.ProjectId);
        index.Value.Single(e => e.Revision == 1).Status.Should().Be(RevisionStatus.RolledBack);

        // バックアップフォルダを外部から消しても、履歴の記録上は取り消し済みのままに見える（success へ戻って見えない）。
        Directory.Delete(summary.FolderPath, recursive: true);
        var afterLoss = (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value;
        afterLoss.IsRestorable.Should().BeFalse();
        afterLoss.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
    }

    [Fact(DisplayName = "取り消し済みのリビジョンを（古い一覧のまま）もう一度取り消そうとすると、E219で断り、ファイルは変えない")]
    public async Task 取り消しを重ねるとE219()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "original\n");
        var stale = await ApplyAsync(h, 1, Patch); // 取り消す前に取った一覧の行（statusはsuccessのまま）

        (await UndoAsync(h, stale)).IsSuccess.Should().BeTrue();
        h.WriteProjectText("a.txt", "取り消したあとの別の変更\n");

        var second = await UndoAsync(h, stale, force: true);

        second.IsSuccess.Should().BeFalse();
        second.Errors.Should().Contain(i => i.Code == ErrorCode.E219);
        Text(h, "a.txt").Should().Be("取り消したあとの別の変更\n", "古いバックアップで上書きして失ってはならない");
    }

    [Fact(DisplayName = "適用後フックの失敗で巻き戻したとき（復元経路は同じ）も、rolled_backになる")]
    public async Task フック失敗の巻き戻しも同じ経路()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "original\n");
        await ApplyAsync(h, 1, Patch);

        // MainViewModel.RollbackAfterHookFailureAsyncと同じ手順: 最新を読み直して force: true で復元する。
        var summary = (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value;
        (await UndoAsync(h, summary, force: true)).IsSuccess.Should().BeTrue();

        (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        (await h.Revisions.FindByPatchHashAsync(h.ProjectId, RevisionStore.ComputePatchHash(Patch))).Value
            .Should().BeNull("取り消し済みのリビジョンは二重適用の判定に使われない");
    }

    [Fact(DisplayName = "ここまで戻すの対象から、取り消し済みのリビジョンは外れる（重ねて取り消して上書きしない）")]
    public async Task ここまで戻すの対象から取り消し済みは外れる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "v0\n");
        await ApplyAsync(h, 1, "<<<< FILE: a.txt MODE=FULL\nv1\n>>>> END\n");
        await ApplyAsync(h, 2, "<<<< FILE: a.txt MODE=FULL\nv2\n>>>> END\n");
        var r3 = await ApplyAsync(h, 3, "<<<< FILE: a.txt MODE=FULL\nv3\n>>>> END\n");
        (await UndoAsync(h, r3)).IsSuccess.Should().BeTrue(); // v2に戻る

        var list = (await h.Revisions.ListAsync(h.ProjectId)).Value;
        var preview = RevisionRestorer.BuildRestoreThroughPreview(list, targetRevision: 1);

        preview.RevisionsToUndo.Select(r => r.Manifest.Revision).Should().Equal(2);
    }

    [Fact(DisplayName = "ここまで戻すは、取り消したリビジョンをrolled_backにして新しいリビジョンに記録し、それを取り消すと元に戻る")]
    public async Task ここまで戻すの取り消しと戻しすぎの取り消し()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "v0\n");
        await ApplyAsync(h, 1, "<<<< FILE: a.txt MODE=FULL\nv1\n>>>> END\n");
        await ApplyAsync(h, 2, "<<<< FILE: a.txt MODE=FULL\nv2\n>>>> END\n");
        await ApplyAsync(h, 3, "<<<< FILE: a.txt MODE=FULL\nv3\n>>>> END\n");
        var restorer = new RevisionRestorer(h.Paths);
        var list = (await h.Revisions.ListAsync(h.ProjectId)).Value;
        var preview = RevisionRestorer.BuildRestoreThroughPreview(list, targetRevision: 1);

        var through = await restorer.RestoreThroughAsync(h.ProjectId, h.ProjectRoot, 1, preview.RevisionsToUndo, 4, force: false);

        through.IsSuccess.Should().BeTrue(string.Join(",", through.Issues.Select(i => i.ToDisplayText())));
        Text(h, "a.txt").Should().Be("v1\n");
        through.Value.RolledBackRevisions.Should().Equal(3, 2);
        (await h.Revisions.ReadAsync(h.ProjectId, 2)).Value.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        (await h.Revisions.ReadAsync(h.ProjectId, 3)).Value.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value.Manifest.Status.Should().Be(RevisionStatus.Success);

        // v1の状態でr2のパッチをもう一度解析しても、適用済みとは扱われない。
        var dryRun = await h.DryRunAsync("<<<< FILE: a.txt MODE=FULL\nv2\n>>>> END\n", h.MakeContext(5));
        dryRun.AlreadyAppliedRevision.Should().BeNull();

        // 戻しすぎた: r4を取り消すと、v3の状態に戻り、r2・r3は有効に戻る。
        var r4 = (await h.Revisions.ReadAsync(h.ProjectId, 4)).Value;
        r4.Manifest.RolledBackRevisions.Should().Equal(3, 2);
        (await UndoAsync(h, r4)).IsSuccess.Should().BeTrue();
        Text(h, "a.txt").Should().Be("v3\n");
        (await h.Revisions.ReadAsync(h.ProjectId, 4)).Value.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        (await h.Revisions.ReadAsync(h.ProjectId, 3)).Value.Manifest.Status.Should().Be(RevisionStatus.Success);
        (await h.Revisions.ReadAsync(h.ProjectId, 2)).Value.Manifest.Status.Should().Be(RevisionStatus.Success);
    }

    [Fact(DisplayName = "互換: rolledBackRevisionsを持たない古いmanifestも読める（出力もされない）")]
    public async Task 古いmanifestとの互換()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "original\n");
        var summary = await ApplyAsync(h, 1, Patch);

        var json = await File.ReadAllTextAsync(Path.Combine(summary.FolderPath, "manifest.json"));
        json.Should().NotContain("rolledBackRevisions", "通常のリビジョンのmanifestの形は変わらない");
        summary.Manifest.RolledBackRevisions.Should().BeNull();
    }
}
