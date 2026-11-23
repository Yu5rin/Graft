using System.IO;
using System.Text.Json;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 仕様書 v1.3（リボン書き換えの点検の不具合の修正）の純ロジック・小さな部品のテスト。サービス全体を通すテストは
/// <c>VbaRibbonRewriteServiceTests.V13.cs</c>。
/// </summary>
public class VbaRibbonV13LogicTests
{
    private const string Book = @"D:\b\計画.xlsm";

    private static VbaRibbonRewriteJob RestoreJob(string? backupSha = "pre", string? sourceSha = "src", bool closed = false) => new()
    {
        Kind = VbaRibbonJobKind.RestoreWorkbook,
        ProjectId = "p",
        WorkbookPath = Book,
        BackupPath = @"D:\b\計画-20261123-100000-ribbon.xlsm",
        BackupSha256 = backupSha,
        RestoreSourceFileName = "計画-20261122-090000-ribbon.xlsm",
        RestoreSourceSha256 = sourceSha,
        ClosedWorkbook = closed,
        StartedAt = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero),
    };

    [Fact(DisplayName = "不具合 3: 丸ごと戻す操作の判定: 選んだ控えのハッシュなら「置き換え済み」、置き換える前の控えなら「元のまま」、どちらでもなければ「分からない」")]
    public void 丸ごと戻す操作の判定()
    {
        VbaRibbonRecoveryLogic.Judge(RestoreJob(), "src", null).Should().Be(VbaRibbonInterruptedState.Replaced);
        VbaRibbonRecoveryLogic.Judge(RestoreJob(), "SRC", null).Should().Be(VbaRibbonInterruptedState.Replaced, "16 進の大文字小文字は区別しない");
        VbaRibbonRecoveryLogic.Judge(RestoreJob(), "pre", null).Should().Be(VbaRibbonInterruptedState.Original);
        VbaRibbonRecoveryLogic.Judge(RestoreJob(), "other", null).Should().Be(VbaRibbonInterruptedState.Unknown);
        VbaRibbonRecoveryLogic.Judge(RestoreJob(), null, null).Should().Be(VbaRibbonInterruptedState.Missing);
        VbaRibbonRecoveryLogic.Judge(RestoreJob(backupSha: null), "other", null).Should().Be(VbaRibbonInterruptedState.Original, "控えを書き終える前ならブックには触れていない");
        VbaRibbonRecoveryLogic.Judge(RestoreJob(sourceSha: null), "src", null).Should().Be(VbaRibbonInterruptedState.Unknown, "選んだ控えのハッシュが無ければ置き換え済みとは言えない");
    }

    [Fact(DisplayName = "不具合 3: 丸ごと戻す操作の知らせの文は、書き換えの文ではなく、置き換えの文（選んだ控え・基準を捨てたこと・置き換える前の控え）")]
    public void 丸ごと戻す操作の知らせ()
    {
        var replaced = VbaRibbonRecoveryLogic.Message(RestoreJob(), VbaRibbonInterruptedState.Replaced);

        replaced.Should().Contain("前回の控えでブックを置き換える操作").And.Contain("途中で終わりました（Graft が終了したため）")
            .And.Contain("「計画.xlsm」は控え「計画-20261122-090000-ribbon.xlsm」の内容に置き換え済みです")
            .And.Contain("リボンの同期の記録は捨てた").And.Contain(@"置き換える前の控え: D:\b\計画-20261123-100000-ribbon.xlsm")
            .And.NotContain("リボンの書き換え");
        VbaRibbonRecoveryLogic.Message(RestoreJob(), VbaRibbonInterruptedState.Original).Should().Contain("置き換える前のままです");
        VbaRibbonRecoveryLogic.Message(RestoreJob(), VbaRibbonInterruptedState.Unknown).Should().Contain("置き換える前とも").And.Contain("内容とも違います");
    }

    [Fact(DisplayName = "不具合 4: 実行中の失敗用の文（duringRun）は状態だけ。「Graft が終了したため」「Excel では閉じたままです」を含めない")]
    public void 実行中の失敗用の文()
    {
        var job = RestoreJob(closed: true);

        var after = VbaRibbonRecoveryLogic.Message(job, VbaRibbonInterruptedState.Replaced);
        var during = VbaRibbonRecoveryLogic.Message(job, VbaRibbonInterruptedState.Replaced, duringRun: true);

        after.Should().Contain("Graft が終了したため").And.Contain("Excel では閉じたままです");
        during.Should().NotContain("Graft が終了したため").And.NotContain("閉じたまま").And.NotContain("Excel でブックを開いてください");
        during.Should().Contain("置き換え済み");

        var rewrite = new VbaRibbonRewriteJob { ProjectId = "p", WorkbookPath = Book, BackupPath = "b", BackupSha256 = "x", ClosedWorkbook = true };
        VbaRibbonRecoveryLogic.Message(rewrite, VbaRibbonInterruptedState.Rewritten, duringRun: true)
            .Should().Contain("書き換え済み").And.NotContain("Graft が終了したため").And.NotContain("閉じたまま");
    }

    [Fact(DisplayName = "作業の記録が読めないときの文は、「ブックの中身は変えていません」と言い切らず、確かめるよう案内する")]
    public void 読めない記録の文()
    {
        var text = VbaRibbonRecoveryLogic.Message(null, VbaRibbonInterruptedState.Unknown);

        text.Should().Contain("読めませんでした").And.NotContain("変えていません").And.Contain("確かめて");
    }

    [Fact(DisplayName = "作業の記録の形: 種別は文字列で書く。種別の無い古い記録は「書き換え」として読める")]
    public void 作業の記録の形()
    {
        var json = JsonSerializer.Serialize(RestoreJob(), JsonFileStore.DefaultOptions);
        json.Should().Contain("\"kind\": \"RestoreWorkbook\"").And.Contain("restoreSourceSha256");
        JsonSerializer.Deserialize<VbaRibbonRewriteJob>(json, JsonFileStore.DefaultOptions)!.Kind.Should().Be(VbaRibbonJobKind.RestoreWorkbook);

        const string old = "{ \"version\": 1, \"projectId\": \"p\", \"workbookPath\": \"D:\\\\b\\\\a.xlsm\", \"stage\": 3 }";
        JsonSerializer.Deserialize<VbaRibbonRewriteJob>(old, JsonFileStore.DefaultOptions)!.Kind.Should().Be(VbaRibbonJobKind.Rewrite);
    }

    [Fact(DisplayName = "別の場所のブックの控え: 確認の文面の先頭に、控えを取ったブックといま置き換えるブックを並べて警告する。同じ場所なら出さない")]
    public void 別の場所の控えの文面()
    {
        var plan = new VbaWorkbookRestorePlan(
            "p", @"D:\now\計画.xlsm", "計画.xlsm", @"C:\data\計画-20261122-090000-ribbon.xlsm", "計画-20261122-090000-ribbon.xlsm",
            new DateTime(2026, 11, 22, 9, 0, 0), 1024, "abc", @"C:\data", 2048, DateTime.UtcNow);

        VbaWorkbookRestoreText.Message(plan).Should().NotContain("別の場所");

        var text = VbaWorkbookRestoreText.Message(plan with { OtherLocationWorkbook = @"E:\old\計画.xlsm" });

        text.Should().StartWith("⚠ この控えは、別の場所のブックの控えです。")
            .And.Contain(@"控えを取ったブック: E:\old\計画.xlsm").And.Contain(@"いま置き換えるブック: D:\now\計画.xlsm")
            .And.Contain("どちらか分からないときは、「やめる」を選んでください");
    }

    [Fact(DisplayName = "取り込み直し（Relink の replaceExisting）: 新しい Id 側の .workbooks を消さず、旧 Id 側の控えとマージする。名前が重なるものは新しい側を残し、旧側にも残す")]
    public async Task 取り込み直しで控えをマージする()
    {
        using var ws = new TempWorkspace();
        var oldPaths = new AppPaths(ws.CreateDirectory("old"));
        var newPaths = new AppPaths(ws.CreateDirectory("new"));
        var mirror = Path.Combine(oldPaths.VbaMirrorsDirectory, "計画-1a2b3c4d");
        Directory.CreateDirectory(Path.Combine(mirror, "Modules"));
        File.WriteAllText(Path.Combine(mirror, "Modules", "Module1.bas"), "Sub A()\r\nEnd Sub\r\n");
        var store = new ProjectStore(oldPaths);
        var registered = (await store.RegisterAsync(mirror, "計画")).Value;
        var oldProject = (await store.UpdateAsync(registered.Id, p => p with { Vba = new VbaLink { WorkbookFullName = Book } })).Value;
        // 新しい保存先は、旧保存先の projects.json の写しを持つ（移行でコピー済みの状態）。
        Directory.CreateDirectory(Path.GetDirectoryName(newPaths.ProjectsFilePath)!);
        File.Copy(oldPaths.ProjectsFilePath, newPaths.ProjectsFilePath, overwrite: true);
        var newRoot = VbaMirrorFolderName.RelocateRoot(mirror, oldPaths.VbaMirrorsDirectory, newPaths.VbaMirrorsDirectory)!;
        var newId = ProjectStore.CreateId(newRoot);
        newId.Should().NotBe(oldProject.Id);

        // 旧 Id の控え（付け替え前の名前で、新しい保存先へコピー済み）と、新しい Id 側で既に作った控え。
        var oldDir = newPaths.GetVbaWorkbookBackupDirectory(oldProject.Id);
        var newDir = newPaths.GetVbaWorkbookBackupDirectory(newId);
        ws.WriteText(Path.Combine(oldDir, "計画-20261101-100000-ribbon.xlsm"), "旧の控え");
        ws.WriteText(Path.Combine(oldDir, "計画-20261102-100000-ribbon.xlsm"), "旧の重なる控え");
        ws.WriteText(Path.Combine(oldDir, ".origins", "計画-20261101-100000-ribbon.xlsm.json"), "{}");
        ws.WriteText(Path.Combine(newDir, "計画-20261120-100000-ribbon.xlsm"), "新しい側にしかない控え");
        ws.WriteText(Path.Combine(newDir, "計画-20261102-100000-ribbon.xlsm"), "新の重なる控え");

        var result = VbaDataRelinker.Relink(oldPaths, newPaths, replaceExisting: true);

        result.IsSuccess.Should().BeTrue(string.Join(" / ", result.Issues.Select(i => i.ToDisplayText())));
        File.ReadAllText(Path.Combine(newDir, "計画-20261120-100000-ribbon.xlsm")).Should().Be("新しい側にしかない控え", "新 Id 側の控えを消さない");
        File.ReadAllText(Path.Combine(newDir, "計画-20261101-100000-ribbon.xlsm")).Should().Be("旧の控え", "旧 Id 側の控えも足される");
        File.Exists(Path.Combine(newDir, ".origins", "計画-20261101-100000-ribbon.xlsm.json")).Should().BeTrue("出どころの記録もマージされる");
        File.ReadAllText(Path.Combine(newDir, "計画-20261102-100000-ribbon.xlsm")).Should().Be("新の重なる控え", "名前が重なったら新しい側を優先");
        File.ReadAllText(Path.Combine(oldDir, "計画-20261102-100000-ribbon.xlsm")).Should().Be("旧の重なる控え", "重なって移せなかった旧側の控えは、消さずに残す");
    }
}
