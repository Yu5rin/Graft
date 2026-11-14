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
/// 適用が途中で止まった「未完了の適用」（manifest が in_progress のまま残ったもの）を、起動時のロールバック
/// （<see cref="RevisionRestorer.RestoreAsync"/>）で戻せなかった不具合の回帰テスト。
/// in_progress の manifest は entries が空（確定時にしか書かない）で、復元は entries を回すだけだったため、
/// 何も戻さずに成功を返し、そのうえで rolled_back の印が付いて、以後は検出もされなかった。
/// 適用が退避・新規作成を書き残す journal.jsonl（<see cref="BackupJournal"/>）から戻す。
/// </summary>
public class InterruptedApplyRestoreTests
{
    private static string Text(ApplyHarness h, string path) => Encoding.UTF8.GetString(h.ReadProjectBytes(path));

    private static async Task<BackupSession> BeginAsync(ApplyHarness h, int revision = 1)
    {
        var initial = new RevisionManifest
        {
            Revision = revision, ProjectId = h.ProjectId, AppliedAt = DateTimeOffset.Now, Status = RevisionStatus.InProgress,
        };
        var began = await h.Backup.BeginAsync(h.ProjectId, h.ProjectRoot, initial);
        began.IsSuccess.Should().BeTrue();
        return began.Value;
    }

    private static Task<GraftResult<System.Collections.Generic.IReadOnlyList<string>>> RestoreAsync(
        ApplyHarness h, RevisionSummary summary, bool force = true)
        => new RevisionRestorer(h.Paths).RestoreAsync(h.ProjectId, h.ProjectRoot, summary, force);

    [Fact(DisplayName = "再現: 退避してファイルを書き換え、新規ファイルも作った途中で止まった適用を、起動時のロールバックで元に戻せる")]
    public async Task 途中で止まった適用を戻せる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "original\n");
        var session = await BeginAsync(h);
        (await session.StoreAsync("a.txt")).IsSuccess.Should().BeTrue();
        h.WriteProjectText("a.txt", "half-way\n");
        session.TrackCreated("sub/new.txt");
        h.WriteProjectText("sub/new.txt", "created\n"); // ここでアプリが落ちた想定: CompleteAsyncは呼ばれない

        var summary = (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value;
        summary.Manifest.Status.Should().Be(RevisionStatus.InProgress);
        summary.Manifest.Entries.Should().BeEmpty("in_progressのmanifestはentriesが空");
        (await h.Revisions.FindInterruptedAsync(h.ProjectId)).Value.Offer.Should().ContainSingle();

        var restored = await RestoreAsync(h, summary);

        restored.IsSuccess.Should().BeTrue(string.Join(",", restored.Issues.Select(i => i.ToDisplayText())));
        Text(h, "a.txt").Should().Be("original\n", "書き換わったファイルは退避から戻る");
        h.ProjectFileExists("sub/new.txt").Should().BeFalse("新規作成したファイルは削除される");
        (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        var interrupted = (await h.Revisions.FindInterruptedAsync(h.ProjectId)).Value;
        interrupted.Offer.Should().BeEmpty("戻せたので、以後は検出されない");
    }

    [Fact(DisplayName = "journalは退避・新規作成のたびにディスクへ追記される（落ちた時点までの記録が残る）")]
    public async Task 記録はその都度ディスクに残る()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "original\n");
        var session = await BeginAsync(h);

        await session.StoreAsync("a.txt");
        session.TrackCreated("new.txt");

        var ops = BackupJournal.TryRead(session.FolderPath);
        ops.Should().NotBeNull();
        ops!.Select(o => (o.Op, o.Path)).Should().Equal(
            (BackupJournal.Stored, "a.txt"), (BackupJournal.Created, "new.txt"));
    }

    [Fact(DisplayName = "互換: journalが無い古い形式のin_progressは、フォルダに退避されているファイルを書き戻し、新規作成は消せないと警告する")]
    public async Task journalが無い古い形式はフォルダの中身から戻す()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "original\n");
        var session = await BeginAsync(h);
        await session.StoreAsync("a.txt");
        h.WriteProjectText("a.txt", "half-way\n");
        File.Delete(BackupJournal.PathFor(session.FolderPath)); // journalを書かない古い版が作ったフォルダを再現

        var summary = (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value;
        var restored = await RestoreAsync(h, summary);

        restored.IsSuccess.Should().BeTrue(string.Join(",", restored.Issues.Select(i => i.ToDisplayText())));
        Text(h, "a.txt").Should().Be("original\n");
        restored.Issues.Should().Contain(i => i.Severity == Severity.Warning && i.ToDisplayText().Contains("新しく作られたファイル"));
        (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
    }

    [Fact(DisplayName = "戻せなかったとき（退避の実体が無い）は失敗を返し、rolled_backの印を付けず、次回の起動でも検出される")]
    public async Task 戻せなければ印を付けない()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "original\n");
        var session = await BeginAsync(h);
        await session.StoreAsync("a.txt");
        h.WriteProjectText("a.txt", "half-way\n");
        Directory.Delete(Path.Combine(session.FolderPath, BackupPathUtil.FilesSubfolderName), recursive: true);

        var summary = (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value;
        var restored = await RestoreAsync(h, summary);

        restored.IsSuccess.Should().BeFalse("退避が読めないので戻せていない");
        restored.Errors.Should().Contain(i => i.Code == ErrorCode.E405);
        (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value.Manifest.Status.Should().Be(RevisionStatus.InProgress);
        (await h.Revisions.FindInterruptedAsync(h.ProjectId)).Value.Offer.Should().ContainSingle();
        Text(h, "a.txt").Should().Be("half-way\n");
    }

    [Fact(DisplayName = "journalの最後の行が書きかけで壊れていても、読める行までは戻せる")]
    public async Task 書きかけの最終行は読み飛ばす()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "original\n");
        var session = await BeginAsync(h);
        await session.StoreAsync("a.txt");
        h.WriteProjectText("a.txt", "half-way\n");
        File.AppendAllText(BackupJournal.PathFor(session.FolderPath), "{\"op\":\"crea");

        var summary = (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value;
        var restored = await RestoreAsync(h, summary);

        restored.IsSuccess.Should().BeTrue();
        Text(h, "a.txt").Should().Be("original\n");
    }

    [Fact(DisplayName = "IOException・UnauthorizedAccessException以外の例外が適用の途中で出ても、ロールバックされて失敗として返る")]
    public async Task 想定外の例外でもロールバックされる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "A1\n");
        h.WriteProjectText("b.txt", "B1\n");
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync(
            "<<<< FILE: a.txt\n<<<<<<< SEARCH\nA1\n=======\nA2\n>>>>>>> REPLACE\n\n" +
            "<<<< FILE: b.txt\n<<<<<<< SEARCH\nB1\n=======\nB2\n>>>>>>> REPLACE\n", ctx);
        // 2つ目のファイルの書き込み直前に、IOExceptionでもUnauthorizedAccessExceptionでもない例外を起こす。
        h.Engine.DebugCorruptFinalTextForTests = text =>
            text.Contains("B2") ? throw new InvalidOperationException("想定外") : text;

        var apply = await h.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse();
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E402 && i.ToDisplayText().Contains("想定外"));
        Text(h, "a.txt").Should().Be("A1\n", "先に書いたa.txtも元に戻る");
        Text(h, "b.txt").Should().Be("B1\n");
        (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
    }

    [Fact(DisplayName = "適用の失敗後のロールバック自体が失敗したら、その旨(E403)も伝え、manifestはin_progressのまま残る")]
    public async Task ロールバックの失敗も伝える()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "A1\n");
        h.WriteProjectText("b.txt", "B1\n");
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync(
            "<<<< FILE: a.txt\n<<<<<<< SEARCH\nA1\n=======\nA2\n>>>>>>> REPLACE\n\n" +
            "<<<< FILE: b.txt\n<<<<<<< SEARCH\nB1\n=======\nB2\n>>>>>>> REPLACE\n", ctx);
        h.Engine.DebugCorruptFinalTextForTests = text =>
        {
            if (!text.Contains("B2")) return text;
            // 2つ目を書く直前に、1つ目の退避を壊す（ロールバックで戻せなくなる）。
            foreach (var backup in Directory.EnumerateFiles(h.Paths.GetProjectBackupDirectory(h.ProjectId), "a.txt", SearchOption.AllDirectories))
            {
                File.Delete(backup);
            }
            throw new InvalidOperationException("想定外");
        };

        var apply = await h.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse();
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E403, "戻せなかったことを伝える");
        apply.Errors.Should().Contain(i => i.Code == ErrorCode.E405, "戻せなかった理由も併記する");
        (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value.Manifest.Status.Should().Be(RevisionStatus.InProgress);
    }
}
