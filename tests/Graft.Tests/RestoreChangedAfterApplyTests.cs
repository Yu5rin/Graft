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
/// 取り消しが、適用後に利用者が変えたファイルを警告なしで消す・上書きしていた不具合の回帰テスト。
/// 以前の事前確認は HashAfter を持つ entry（modify・create）しか見ず、RENAME と DELETE は素通りだった。
/// </summary>
public class RestoreChangedAfterApplyTests
{
    private static string Text(ApplyHarness h, string path) => Encoding.UTF8.GetString(h.ReadProjectBytes(path));

    private static async Task<RevisionSummary> ApplyAsync(ApplyHarness h, int revision, string patch)
    {
        var ctx = h.MakeContext(revision);
        var dryRun = await h.DryRunAsync(patch, ctx);
        var applied = await h.ApplyAsync(dryRun, ctx);
        applied.IsSuccess.Should().BeTrue(string.Join(",", applied.Issues.Select(i => i.ToDisplayText())));
        return (await h.Revisions.ReadAsync(h.ProjectId, revision)).Value;
    }

    private static Task<GraftResult<System.Collections.Generic.IReadOnlyList<string>>> RestoreAsync(
        ApplyHarness h, RevisionSummary summary, bool force)
        => new RevisionRestorer(h.Paths).RestoreAsync(h.ProjectId, h.ProjectRoot, summary, force);

    [Fact(DisplayName = "再現1: RENAMEだけのリビジョンのあと移動先を手で編集して取り消すと、E301の警告で止まり、移動先は消えない")]
    public async Task 改名先を編集したあとの取り消しは警告で止まる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        var summary = await ApplyAsync(h, 1, "<<<< RENAME: a.txt -> b.txt\n");
        summary.Manifest.Entries.Single().HashAfter.Should().NotBeNullOrEmpty("適用時に移動先の内容のハッシュを記録する");

        h.WriteProjectText("b.txt", "手で編集した内容\n");
        var withoutForce = await RestoreAsync(h, summary, force: false);

        withoutForce.IsSuccess.Should().BeFalse();
        withoutForce.Issues.Should().Contain(i => i.Code == ErrorCode.E301 && i.Path == "b.txt");
        Text(h, "b.txt").Should().Be("手で編集した内容\n", "警告で止まった時点では何も変えない");
        h.ProjectFileExists("a.txt").Should().BeFalse();

        var withForce = await RestoreAsync(h, summary, force: true);
        withForce.IsSuccess.Should().BeTrue();
        Text(h, "a.txt").Should().Be("AAA\n");
        h.ProjectFileExists("b.txt").Should().BeFalse();
    }

    [Fact(DisplayName = "RENAMEのあと何も触っていなければ、警告なしで取り消せる")]
    public async Task 改名のあと触らなければ警告なし()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        var summary = await ApplyAsync(h, 1, "<<<< RENAME: a.txt -> b.txt\n");

        var restored = await RestoreAsync(h, summary, force: false);

        restored.IsSuccess.Should().BeTrue();
        restored.Issues.Should().BeEmpty();
        Text(h, "a.txt").Should().Be("AAA\n");
    }

    [Fact(DisplayName = "RENAMEのあと、改名前のパスに別のファイルが作られていたら、書き戻しで上書きになるのでE301で止まる")]
    public async Task 改名前のパスが作り直されていたら警告()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        var summary = await ApplyAsync(h, 1, "<<<< RENAME: a.txt -> b.txt\n");

        h.WriteProjectText("a.txt", "新しく作ったa\n");
        var restored = await RestoreAsync(h, summary, force: false);

        restored.IsSuccess.Should().BeFalse();
        restored.Issues.Should().Contain(i => i.Code == ErrorCode.E301 && i.Path == "a.txt");
        Text(h, "a.txt").Should().Be("新しく作ったa\n");
    }

    [Fact(DisplayName = "RENAMEと移動先の編集を同じパッチで行ったリビジョンは、何も触っていなければ警告なしで取り消せる（二重に警告しない）")]
    public async Task 改名と編集のあと触らなければ警告なし()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        var summary = await ApplyAsync(h, 1,
            "<<<< RENAME: a.txt -> b.txt\n\n<<<< FILE: b.txt\n<<<<<<< SEARCH\nAAA\n=======\nBBB\n>>>>>>> REPLACE\n");

        var restored = await RestoreAsync(h, summary, force: false);

        restored.IsSuccess.Should().BeTrue(string.Join(",", restored.Issues.Select(i => i.ToDisplayText())));
        restored.Issues.Should().BeEmpty();
    }

    [Fact(DisplayName = "RENAMEと移動先の編集のあとに、さらに手で編集して取り消すと、警告は1件にまとまる")]
    public async Task 改名と編集のあとの手編集の警告は1件()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        var summary = await ApplyAsync(h, 1,
            "<<<< RENAME: a.txt -> b.txt\n\n<<<< FILE: b.txt\n<<<<<<< SEARCH\nAAA\n=======\nBBB\n>>>>>>> REPLACE\n");

        h.WriteProjectText("b.txt", "CCC\n");
        var restored = await RestoreAsync(h, summary, force: false);

        restored.IsSuccess.Should().BeFalse();
        restored.Issues.Count(i => i.Code == ErrorCode.E301 && i.Path == "b.txt").Should().Be(1);
    }

    [Fact(DisplayName = "再現2: DELETEのリビジョンのあと同じパスを別の内容で作り直して取り消すと、E301の警告で止まり、作り直した内容は消えない")]
    public async Task 削除のあと作り直した内容は警告で守られる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("old.txt", "古い内容\n");
        var summary = await ApplyAsync(h, 1, "<<<< DELETE: old.txt\n");

        h.WriteProjectText("old.txt", "作り直した別の内容\n");
        var withoutForce = await RestoreAsync(h, summary, force: false);

        withoutForce.IsSuccess.Should().BeFalse();
        withoutForce.Issues.Should().Contain(i => i.Code == ErrorCode.E301 && i.Path == "old.txt");
        Text(h, "old.txt").Should().Be("作り直した別の内容\n");

        var withForce = await RestoreAsync(h, summary, force: true);
        withForce.IsSuccess.Should().BeTrue();
        Text(h, "old.txt").Should().Be("古い内容\n");
    }

    [Fact(DisplayName = "DELETEのあと同じパスに削除前と同じ内容のファイルがあるなら、失われるものが無いので警告しない")]
    public async Task 削除前と同じ内容なら警告しない()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("old.txt", "同じ内容\n");
        var summary = await ApplyAsync(h, 1, "<<<< DELETE: old.txt\n");

        h.WriteProjectText("old.txt", "同じ内容\n");
        var restored = await RestoreAsync(h, summary, force: false);

        restored.IsSuccess.Should().BeTrue();
        restored.Issues.Should().BeEmpty();
    }

    [Fact(DisplayName = "DELETEのあと何も作られていなければ、警告なしで取り消せる")]
    public async Task 削除のあと何もなければ警告なし()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("old.txt", "古い内容\n");
        var summary = await ApplyAsync(h, 1, "<<<< DELETE: old.txt\n");

        var restored = await RestoreAsync(h, summary, force: false);

        restored.IsSuccess.Should().BeTrue();
        restored.Issues.Should().BeEmpty();
        Text(h, "old.txt").Should().Be("古い内容\n");
    }

    [Fact(DisplayName = "互換: HashAfterを持たない古い形式のRENAMEでも、移動先がバックアップと違えば警告し、同じなら警告しない")]
    public async Task 古い形式のrenameでも分かる範囲で警告する()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");

        // 修正前のApplyEngineが書いたmanifestの再現: renameのentryにHashAfterが無い。
        var initial = new RevisionManifest
        {
            Revision = 1, ProjectId = h.ProjectId, AppliedAt = DateTimeOffset.Now, Status = RevisionStatus.InProgress,
        };
        var began = await h.Backup.BeginAsync(h.ProjectId, h.ProjectRoot, initial);
        (await began.Value.StoreAsync("a.txt")).IsSuccess.Should().BeTrue();
        File.Move(Path.Combine(h.ProjectRoot, "a.txt"), Path.Combine(h.ProjectRoot, "b.txt"));
        var entries = new[] { new RevisionEntry { Path = "b.txt", Operation = EntryOperation.Rename, RenamedFrom = "a.txt" } };
        await began.Value.CompleteAsync(initial with { Status = RevisionStatus.Success, Entries = entries });
        var summary = (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value;

        h.WriteProjectText("b.txt", "手で編集\n");
        var edited = await RestoreAsync(h, summary, force: false);
        edited.IsSuccess.Should().BeFalse();
        edited.Issues.Should().Contain(i => i.Code == ErrorCode.E301 && i.Path == "b.txt");

        h.WriteProjectText("b.txt", "AAA\n");
        var untouched = await RestoreAsync(h, summary, force: false);
        untouched.IsSuccess.Should().BeTrue(string.Join(",", untouched.Issues.Select(i => i.ToDisplayText())));
        Text(h, "a.txt").Should().Be("AAA\n");
    }
}
