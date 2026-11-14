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
/// 適用が失敗してロールバックしても、RENAME の移動先と MKDIR・親フォルダ作成のフォルダが残っていた不具合の
/// 回帰テスト。改名は移動元へ戻し、この適用で新しく作ったフォルダは、空のときだけ消す。
/// 失敗は、最後に書く新規ファイルの書き込み直前に例外を起こして再現する
/// （RENAME・MKDIR は FULL/SR より先に実行されるため、それらは済んでいる）。
/// </summary>
public class RollbackRenameAndMkdirTests
{
    private static string Text(ApplyHarness h, string path) => Encoding.UTF8.GetString(h.ReadProjectBytes(path));

    private static bool DirExists(ApplyHarness h, string relative)
        => Directory.Exists(Path.Combine(h.ProjectRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static async Task<GraftResult<RevisionManifest>> ApplyFailingAsync(ApplyHarness h, string patch)
    {
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync(patch, ctx);
        dryRun.Plans.Should().OnlyContain(p => p.CanApply, "前提: ドライランは通る");
        h.Engine.DebugCorruptFinalTextForTests = text =>
            text.Contains("FAIL_HERE") ? throw new InvalidOperationException("失敗の再現") : text;
        var apply = await h.ApplyAsync(dryRun, ctx);
        apply.IsSuccess.Should().BeFalse();
        return apply;
    }

    [Fact(DisplayName = "失敗してロールバックすると、RENAMEの移動先は消え、移動元が元の内容で戻る")]
    public async Task 失敗したら改名が元に戻る()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");

        await ApplyFailingAsync(h,
            "<<<< RENAME: a.txt -> b.txt\n\n<<<< FILE: z.txt MODE=FULL\nFAIL_HERE\n>>>> END\n");

        h.ProjectFileExists("b.txt").Should().BeFalse("改名先が残ってはならない");
        Text(h, "a.txt").Should().Be("AAA\n");
        h.ProjectFileExists("z.txt").Should().BeFalse();
        (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
    }

    [Fact(DisplayName = "RENAMEの移動先を同じパッチで編集してから失敗しても、移動元が元の内容で戻り移動先は消える")]
    public async Task 改名先を編集したあとの失敗でも戻る()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");

        await ApplyFailingAsync(h,
            "<<<< RENAME: a.txt -> b.txt\n\n<<<< FILE: b.txt\n<<<<<<< SEARCH\nAAA\n=======\nBBB\n>>>>>>> REPLACE\n\n" +
            "<<<< FILE: z.txt MODE=FULL\nFAIL_HERE\n>>>> END\n");

        h.ProjectFileExists("b.txt").Should().BeFalse();
        Text(h, "a.txt").Should().Be("AAA\n", "編集前の内容で戻る");
    }

    [Fact(DisplayName = "別のフォルダへの改名が失敗でロールバックされたら、改名のために作ったフォルダも消える")]
    public async Task 改名のために作ったフォルダも消える()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");

        await ApplyFailingAsync(h,
            "<<<< RENAME: a.txt -> moved/deep/b.txt\n\n<<<< FILE: z.txt MODE=FULL\nFAIL_HERE\n>>>> END\n");

        DirExists(h, "moved").Should().BeFalse("改名先のために新しく作ったフォルダは、空なので消える");
        Text(h, "a.txt").Should().Be("AAA\n");
    }

    [Fact(DisplayName = "MKDIRで作ったフォルダは、失敗でロールバックされたら消える。もともとあったフォルダは残る")]
    public async Task 作ったフォルダだけが消える()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        Directory.CreateDirectory(Path.Combine(h.ProjectRoot, "existing"));

        await ApplyFailingAsync(h,
            "<<<< MKDIR: existing/newdir\n\n<<<< MKDIR: fresh/inner\n\n<<<< FILE: z.txt MODE=FULL\nFAIL_HERE\n>>>> END\n");

        DirExists(h, "existing").Should().BeTrue("もともとあったフォルダは消さない");
        DirExists(h, "existing/newdir").Should().BeFalse();
        DirExists(h, "fresh/inner").Should().BeFalse();
        DirExists(h, "fresh").Should().BeFalse("途中の親フォルダも、この適用で作ったものなので消える");
    }

    [Fact(DisplayName = "新規ファイルのために作った親フォルダも、失敗でロールバックされたら（空なので）消える")]
    public async Task 新規ファイルの親フォルダも消える()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);

        await ApplyFailingAsync(h, "<<<< FILE: newpkg/sub/z.txt MODE=FULL\nFAIL_HERE\n>>>> END\n");

        h.ProjectFileExists("newpkg/sub/z.txt").Should().BeFalse();
        DirExists(h, "newpkg").Should().BeFalse();
    }

    [Fact(DisplayName = "作ったフォルダに他のものが入っていたら、ロールバックでも消さない（空のときだけ消す）")]
    public async Task 空でないフォルダは消さない()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        var initial = new RevisionManifest
        {
            Revision = 1, ProjectId = h.ProjectId, AppliedAt = DateTimeOffset.Now, Status = RevisionStatus.InProgress,
        };
        var session = (await h.Backup.BeginAsync(h.ProjectId, h.ProjectRoot, initial)).Value;
        session.CreateDirectoryTracked(Path.Combine(h.ProjectRoot, "made"));
        h.WriteProjectText("made/user-file.txt", "後から入れた\n");

        var rollback = await session.RollbackAsync();

        rollback.IsSuccess.Should().BeTrue();
        DirExists(h, "made").Should().BeTrue();
        h.ProjectFileExists("made/user-file.txt").Should().BeTrue();
    }

    [Fact(DisplayName = "起動時のロールバック: 改名まで済んで止まった適用も、journalから移動元へ戻せる")]
    public async Task 途中で止まった改名も起動時に戻せる()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        var initial = new RevisionManifest
        {
            Revision = 1, ProjectId = h.ProjectId, AppliedAt = DateTimeOffset.Now, Status = RevisionStatus.InProgress,
        };
        var session = (await h.Backup.BeginAsync(h.ProjectId, h.ProjectRoot, initial)).Value;
        (await session.StoreAsync("a.txt")).IsSuccess.Should().BeTrue();
        session.CreateDirectoryTracked(Path.Combine(h.ProjectRoot, "moved"));
        File.Move(Path.Combine(h.ProjectRoot, "a.txt"), Path.Combine(h.ProjectRoot, "moved", "b.txt"));
        session.TrackRenamed("a.txt", "moved/b.txt");
        h.WriteProjectText("moved/b.txt", "編集まで済んだ\n"); // ここでアプリが落ちた想定

        var summary = (await h.Revisions.ReadAsync(h.ProjectId, 1)).Value;
        var restored = await new RevisionRestorer(h.Paths).RestoreAsync(h.ProjectId, h.ProjectRoot, summary, force: true);

        restored.IsSuccess.Should().BeTrue(string.Join(",", restored.Issues.Select(i => i.ToDisplayText())));
        Text(h, "a.txt").Should().Be("AAA\n");
        DirExists(h, "moved").Should().BeFalse();
    }

    [Fact(DisplayName = "失敗した移動はjournalに記録されず、移動先に元からあったファイルをロールバックが消すことはない")]
    public async Task 失敗した移動は記録されない()
    {
        using var ws = new TempWorkspace();
        var h = new ApplyHarness(ws);
        h.WriteProjectText("a.txt", "AAA\n");
        h.WriteProjectText("keep.txt", "利用者の既存ファイル\n");
        var ctx = h.MakeContext(1);
        var dryRun = await h.DryRunAsync("<<<< RENAME: a.txt -> b.txt\n", ctx);
        // ドライランの後に、移動先へ利用者のファイルができた（本適用の判定で止まり、ロールバックされる）。
        h.WriteProjectText("b.txt", "利用者が後から作った\n");

        var apply = await h.ApplyAsync(dryRun, ctx);

        apply.IsSuccess.Should().BeFalse();
        Text(h, "b.txt").Should().Be("利用者が後から作った\n", "ロールバックが既存の移動先を消してはならない");
        Text(h, "a.txt").Should().Be("AAA\n");
        Text(h, "keep.txt").Should().Be("利用者の既存ファイル\n");
    }
}
