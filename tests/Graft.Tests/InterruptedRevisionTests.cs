using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 「起動のたびに『未完了の適用を検出しました』が出る」不具合の回帰テスト。
/// 原因は2つ。(1) 失敗してロールバックした適用の manifest.json が in_progress のまま残ること、
/// (2) 起動時の検出が、後から成功した適用があるかを見ずに in_progress をすべて提案すること。
/// </summary>
public class InterruptedRevisionTests
{
    private const string FailingPatch = """
        <<<< FILE: keep.txt
        <<<<<<< SEARCH
        original
        =======
        changed
        >>>>>>> REPLACE

        <<<< FILE: blocked/file.txt MODE=FULL
        new content
        >>>> END
        """;

    private static async Task BeginAsync(ApplyHarness h, int revision, DateTimeOffset appliedAt, string? complete)
    {
        var initial = new RevisionManifest
        {
            Revision = revision, ProjectId = h.ProjectId, Summary = $"r{revision}",
            AppliedAt = appliedAt, Status = RevisionStatus.InProgress,
        };
        var began = await h.Backup.BeginAsync(h.ProjectId, h.ProjectRoot, initial);
        began.IsSuccess.Should().BeTrue();
        if (complete is not null)
        {
            (await began.Value.CompleteAsync(initial with { Status = complete })).IsSuccess.Should().BeTrue();
        }
    }

    [Fact(DisplayName = "適用が途中で失敗してロールバックできたら、manifestはrolled_backになり起動時の検出に出ない")]
    public async Task 失敗してロールバックできたらrolled_backになる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("keep.txt", "original\n");
        h.WriteProjectFileAsBlocker("blocked", "ファイル");
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync(FailingPatch, ctx);

        var apply = await h.ApplyAsync(dryRun, ctx);
        apply.IsSuccess.Should().BeFalse();

        var read = await h.Revisions.ReadAsync(h.ProjectId, 1);
        read.Value.Manifest.Status.Should().Be(RevisionStatus.RolledBack,
            "元に戻せた失敗は中断ではないので、in_progressのまま残してはならない");
        (await h.Revisions.FindInProgressAsync(h.ProjectId)).Value.Should().BeEmpty();
        var interrupted = (await h.Revisions.FindInterruptedAsync(h.ProjectId)).Value;
        interrupted.Offer.Should().BeEmpty();
        interrupted.Superseded.Should().BeEmpty();
    }

    [Fact(DisplayName = "ロールバック自体が失敗したら、manifestはin_progressのまま残り、起動時に提案される")]
    public async Task ロールバック失敗はin_progressのまま残る()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("keep.txt", "original\n");
        var initial = new RevisionManifest
        {
            Revision = 1, ProjectId = h.ProjectId, AppliedAt = DateTimeOffset.Now, Status = RevisionStatus.InProgress,
        };
        var began = await h.Backup.BeginAsync(h.ProjectId, h.ProjectRoot, initial);
        (await began.Value.StoreAsync("keep.txt")).IsSuccess.Should().BeTrue();
        // 退避ファイルを消して、書き戻しを失敗させる。
        Directory.Delete(Path.Combine(began.Value.FolderPath, "files"), recursive: true);

        var rollback = await began.Value.RollbackAsync();
        rollback.IsSuccess.Should().BeFalse();

        var read = await h.Revisions.ReadAsync(h.ProjectId, 1);
        read.Value.Manifest.Status.Should().Be(RevisionStatus.InProgress);
        var interrupted = (await h.Revisions.FindInterruptedAsync(h.ProjectId)).Value;
        interrupted.Offer.Select(r => r.Manifest.Revision).Should().Equal(1);
    }

    [Fact(DisplayName = "in_progressの後に成功したリビジョンがあれば、提案対象にならない（実機と同じr4・r5がin_progress、r6〜r8がsuccess）")]
    public async Task 後に成功があるin_progressは提案しない()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        await BeginAsync(h, 3, t0, RevisionStatus.Success);
        await BeginAsync(h, 4, t0.AddMinutes(1), null);
        await BeginAsync(h, 5, t0.AddMinutes(2), null);
        await BeginAsync(h, 6, t0.AddMinutes(3), RevisionStatus.Success);
        await BeginAsync(h, 7, t0.AddMinutes(4), RevisionStatus.Success);
        await BeginAsync(h, 8, t0.AddMinutes(5), RevisionStatus.Success);

        var interrupted = (await h.Revisions.FindInterruptedAsync(h.ProjectId)).Value;

        interrupted.Offer.Should().BeEmpty();
        interrupted.Superseded.Select(r => r.Manifest.Revision).Should().BeEquivalentTo(new[] { 4, 5 });
        // 履歴からは従来どおり見える。
        (await h.Revisions.FindInProgressAsync(h.ProjectId)).Value.Should().HaveCount(2);
    }

    [Fact(DisplayName = "最新がin_progressなら従来どおり提案される（古いin_progressは除く）")]
    public async Task 最新のin_progressは提案される()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        await BeginAsync(h, 1, t0, RevisionStatus.Success);
        await BeginAsync(h, 2, t0.AddMinutes(1), null);
        await BeginAsync(h, 3, t0.AddMinutes(2), RevisionStatus.Success);
        await BeginAsync(h, 4, t0.AddMinutes(3), null);

        var interrupted = (await h.Revisions.FindInterruptedAsync(h.ProjectId)).Value;

        interrupted.Offer.Select(r => r.Manifest.Revision).Should().Equal(4);
        interrupted.Superseded.Select(r => r.Manifest.Revision).Should().Equal(2);
    }

    [Fact(DisplayName = "番号は小さくても適用日時が新しい成功があれば、後があるものとして提案しない")]
    public void 日時が新しい成功があれば提案しない()
    {
        RevisionSummary Make(int rev, string status, DateTimeOffset at) => new()
        {
            Manifest = new RevisionManifest { Revision = rev, ProjectId = "p", Status = status, AppliedAt = at },
            FolderPath = "x", IsRestorable = true,
        };
        var t0 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

        var result = RevisionStore.SplitInProgress(new[]
        {
            Make(9, RevisionStatus.InProgress, t0),
            Make(2, RevisionStatus.Success, t0.AddHours(1)),
        });

        result.Offer.Should().BeEmpty();
        result.Superseded.Should().HaveCount(1);
    }
}
