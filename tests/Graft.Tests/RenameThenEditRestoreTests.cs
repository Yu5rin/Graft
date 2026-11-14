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
/// RENAME と、その移動先への編集を1つのパッチで行ったリビジョンが「復元不可」になっていた不具合の回帰テスト。
/// 移動先 b.txt は適用前には存在せず、退避されたのは移動元 a.txt だけなのに、b.txt の modify が
/// 存在しない b.txt のバックアップを探していた（RevisionEntryLinks参照）。
/// </summary>
public class RenameThenEditRestoreTests
{
    private const string RenameAndEditPatch =
        "<<<< RENAME: a.txt -> b.txt\n\n" +
        "<<<< FILE: b.txt\n<<<<<<< SEARCH\nAAA\n=======\nBBB\n>>>>>>> REPLACE\n";

    private static string Text(ApplyHarness h, string path) => Encoding.UTF8.GetString(h.ReadProjectBytes(path));

    private static async Task<RevisionManifest> ApplyAsync(ApplyHarness h, int revision, string patch)
    {
        var ctx = h.MakeContext(revision);
        var dryRun = await h.DryRunAsync(patch, ctx);
        var applied = await h.ApplyAsync(dryRun, ctx);
        applied.IsSuccess.Should().BeTrue(string.Join(",", applied.Issues.Select(i => i.ToDisplayText())));
        return applied.Value;
    }

    [Fact(DisplayName = "RENAMEと移動先への編集を1パッチで適用しても、リビジョンは復元可能で、改名先のentryは移動元を指す")]
    public async Task 改名と移動先の編集でも復元可能()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");

        var manifest = await ApplyAsync(h, 1, RenameAndEditPatch);

        Text(h, "b.txt").Should().Be("BBB\n");
        manifest.Entries.Should().Contain(e => e.Operation == EntryOperation.Modify && e.Path == "b.txt" && e.RenamedFrom == "a.txt");
        var read = await h.Revisions.ReadAsync(h.ProjectId, 1);
        read.Value.IsRestorable.Should().BeTrue("a.txtのバックアップがあれば、改名先の変更前の内容は復元できる");
    }

    [Fact(DisplayName = "取り消すと、a.txtが元の内容で戻りb.txtが消える")]
    public async Task 取り消すと元の名前と内容に戻る()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        await ApplyAsync(h, 1, RenameAndEditPatch);
        var summary = (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value;

        var restored = await new RevisionRestorer(h.Paths).RestoreAsync(h.ProjectId, h.ProjectRoot, summary, force: false);

        restored.IsSuccess.Should().BeTrue(string.Join(",", restored.Issues.Select(i => i.ToDisplayText())));
        Text(h, "a.txt").Should().Be("AAA\n");
        h.ProjectFileExists("b.txt").Should().BeFalse();
    }

    [Fact(DisplayName = "このリビジョンを挟む「ここまで戻す」もできる")]
    public async Task ここまで戻すもできる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        h.WriteProjectText("other.txt", "one\n");
        await ApplyAsync(h, 1, "<<<< FILE: other.txt\n<<<<<<< SEARCH\none\n=======\ntwo\n>>>>>>> REPLACE\n");
        await ApplyAsync(h, 2, RenameAndEditPatch);
        await ApplyAsync(h, 3, "<<<< FILE: other.txt\n<<<<<<< SEARCH\ntwo\n=======\nthree\n>>>>>>> REPLACE\n");

        var list = (await h.Revisions.ListAsync(h.ProjectId)).Value;
        var preview = RevisionRestorer.BuildRestoreThroughPreview(list, targetRevision: 1);
        preview.CanExecute.Should().BeTrue("r2は復元可能になっているので、挟んでいても戻せる");

        var result = await new RevisionRestorer(h.Paths).RestoreThroughAsync(
            h.ProjectId, h.ProjectRoot, 1, preview.RevisionsToUndo, newRevisionNumber: 4, force: false);

        result.IsSuccess.Should().BeTrue(string.Join(",", result.Issues.Select(i => i.ToDisplayText())));
        Text(h, "a.txt").Should().Be("AAA\n");
        h.ProjectFileExists("b.txt").Should().BeFalse();
        Text(h, "other.txt").Should().Be("two\n");
    }

    [Fact(DisplayName = "互換: 修正前に記録された、改名先のentryが移動元を持たないmanifestでも、復元可能と判定され取り消せる")]
    public async Task 古い形式のmanifestでも復元できる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");

        // 修正前のApplyEngineが書いたmanifestを手で再現する: a.txtだけ退避し、entriesはrenameとmodify（RenamedFromなし）。
        var initial = new RevisionManifest
        {
            Revision = 1, ProjectId = h.ProjectId, AppliedAt = DateTimeOffset.Now, Status = RevisionStatus.InProgress,
        };
        var began = await h.Backup.BeginAsync(h.ProjectId, h.ProjectRoot, initial);
        (await began.Value.StoreAsync("a.txt")).IsSuccess.Should().BeTrue();
        File.Move(Path.Combine(h.ProjectRoot, "a.txt"), Path.Combine(h.ProjectRoot, "b.txt"));
        h.WriteProjectText("b.txt", "BBB\n");
        var entries = new[]
        {
            new RevisionEntry { Path = "b.txt", Operation = EntryOperation.Rename, RenamedFrom = "a.txt" },
            new RevisionEntry
            {
                Path = "b.txt", Operation = EntryOperation.Modify, HashBefore = FileTextIO.ComputeHash("AAA\n"),
                HashAfter = FileTextIO.ComputeHash("BBB\n"),
            },
        };
        await began.Value.CompleteAsync(initial with { Status = RevisionStatus.Success, Entries = entries });

        var summary = (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value;
        summary.IsRestorable.Should().BeTrue("古い形式でも、同じ移動先を持つrenameをたどって移動元のバックアップを見つける");

        var restored = await new RevisionRestorer(h.Paths).RestoreAsync(h.ProjectId, h.ProjectRoot, summary, force: false);
        restored.IsSuccess.Should().BeTrue(string.Join(",", restored.Issues.Select(i => i.ToDisplayText())));
        Text(h, "a.txt").Should().Be("AAA\n");
        h.ProjectFileExists("b.txt").Should().BeFalse();
    }
}
