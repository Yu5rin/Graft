using System.IO;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.UiTests.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 実機の不具合報告（書き込みのたびに末尾へ行が増え、書き戻しも失敗して E809 のまま残る。そのあとの自動取込がミラーを
/// 0 行にした。整形差の警告がモジュールごとに並んだ）への対策の回帰テスト。
/// 項目 3（整形差の知らせを 1 行の info にまとめる）・項目 4（書き戻しに失敗したモジュールを「要確認」として守る）・
/// 項目 5（空の Excel を黙って取り込まない）を <see cref="VbaSyncService"/> と <see cref="FakeVbaBridge"/> で確かめる。
/// </summary>
public class VbaWriteFailureProtectionTests : VbaServiceFixture
{
    private async Task<Project> TwoEditedAsync()
    {
        await CreatedProjectAsync(
            FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"),
            FakeVbaBridge.Std("Module2", "Sub B()\r\nEnd Sub"));
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "Sub A()\r\n  x=1\r\nEnd Sub\r\n");
        File.WriteAllText(MirrorFile("Modules/Module2.bas"), "Sub B()\r\n  y=2\r\nEnd Sub\r\n");
        return NewProject();
    }

    private string ReadLogs()
    {
        var dir = Paths.LogsDirectory;
        return Directory.Exists(dir) ? string.Join("\n", Directory.GetFiles(dir).Select(File.ReadAllText)) : string.Empty;
    }

    // ------------------------------------------------------------------
    // 項目 3: 整形差の知らせ
    // ------------------------------------------------------------------

    [Fact(DisplayName = "項目3: 整形差だけで書けたモジュールは E809 の警告にせず、1 回の反映につき『VBE が N 個のモジュールを整形しました（名前）』の info 1 行にまとめる")]
    public async Task 整形差は1行のinfoにまとめる()
    {
        var project = await TwoEditedAsync();
        Paths.EnsureCoreDirectoriesExist();
        var logger = new Logger(Paths, autoCleanupOnStart: false);
        Service.Logger = logger;
        Bridge.FormatOnWrite = code => code.Replace("x=1", "x = 1").Replace("y=2", "y = 2");

        var push = await Service.PushAsync(project);
        await logger.DisposeAsync();

        push.IsSuccess.Should().BeTrue();
        push.Value.Pushed.Should().HaveCount(2);
        push.Issues.Should().NotContain(i => i.Code == ErrorCode.E809, "整形差は E809 の警告にしない");
        push.Value.Reformatted.Should().Equal("Module1", "Module2");

        var lines = ReadLogs().Split('\n').Where(l => l.Contains("整形しました")).ToList();
        lines.Should().ContainSingle("モジュールごとに出さず、1 回の反映につき 1 行");
        lines[0].Should().Contain("VBE が 2 個のモジュールを整形しました（Module1、Module2）").And.Contain("\"level\":\"info\"");
    }

    [Fact(DisplayName = "項目3: 本当の失敗（別内容・書き戻し失敗）は従来どおり E809 のエラー・警告で目立たせる")]
    public async Task 本当の失敗は目立たせる()
    {
        var project = await TwoEditedAsync();
        Bridge.CorruptOnWrite.Add("Module1");

        var push = await Service.PushAsync(project);

        push.Issues.Should().Contain(i => i.Code == ErrorCode.E809 && i.Severity == Severity.Error && i.Detail!.Contains("書いた"));
    }

    // ------------------------------------------------------------------
    // 項目 4: 要確認
    // ------------------------------------------------------------------

    private async Task<Project> RollbackFailedAsync()
    {
        var project = await TwoEditedAsync();
        Bridge.CorruptOnWrite.Add("Module1");
        Bridge.RollbackFails.Add("Module1");
        var push = await Service.PushAsync(project);
        push.Value.Failed.Should().Equal("Modules/Module1.bas");
        Bridge.CorruptOnWrite.Clear();
        Bridge.RollbackFails.Clear();
        return project;
    }

    [Fact(DisplayName = "項目4: 書き戻しに失敗したモジュールは同期状態に『要確認』として残り、バッジ用の件数にも出る。成功したモジュールには付かない")]
    public async Task 書き戻し失敗は要確認として記録する()
    {
        var project = await RollbackFailedAsync();

        var state = await StateAsync();
        state.Modules["Modules/Module1.bas"].NeedsReview.Should().BeTrue();
        state.Modules["Modules/Module2.bas"].NeedsReview.Should().BeFalse();
        (await Service.GetLocalStatusAsync(project)).NeedsReviewCount.Should().Be(1);
    }

    [Fact(DisplayName = "項目4: 要確認のモジュールは、取込も反映（保存時の自動反映を含む）もせず、理由つきの衝突として返す")]
    public async Task 要確認は取込も反映もしない()
    {
        var project = await RollbackFailedAsync();
        var mirrorBefore = File.ReadAllText(MirrorFile("Modules/Module1.bas"));
        var excelBefore = Excel("Module1").Code;
        var writesBefore = Bridge.WriteRequests.Count;

        var pull = await Service.PullAsync(project);
        var push = await Service.PushAsync(project, ["Modules/Module1.bas"]);
        var saved = await Service.PushSavedAsync(project, ["Modules/Module1.bas"]);
        var check = await Service.CheckAsync(project);

        pull.Value.Pulled.Should().BeEmpty();
        pull.Value.Conflicts.Should().Equal("Modules/Module1.bas");
        pull.Value.ConflictItems.Single().Reason.Should().Be(VbaConflictReason.NeedsReview);
        push.Value.Conflicts.Should().Equal("Modules/Module1.bas");
        saved.Value.Conflicts.Should().Equal("Modules/Module1.bas");
        check.Value.Conflicts.Should().ContainSingle().Which.Reason.Should().Be(VbaConflictReason.NeedsReview);
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Be(mirrorBefore, "取り込まない");
        Excel("Module1").Code.Should().Be(excelBefore, "反映しない");
        Bridge.WriteRequests.Skip(writesBefore).SelectMany(r => r).Should().NotContain(w => w.Name == "Module1");
        pull.Issues.Should().Contain(i => i.Code == ErrorCode.E812 && i.Detail!.Contains("要確認"));
    }

    [Fact(DisplayName = "項目4: ミラーが基準と同じ（利用者が元に戻した）でも、要確認なら壊れたかもしれない Excel を自動で取り込まず、衝突にする（修正前は黙って Pull）")]
    public async Task 要確認は基準と同じミラーでも取り込まない()
    {
        var project = await RollbackFailedAsync();
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "Sub A()\r\nEnd Sub\r\n"); // 基準（元の本文）に戻した
        var mirrorBefore = File.ReadAllText(MirrorFile("Modules/Module1.bas"));
        Excel("Module1").Code.Should().NotBe("Sub A()\r\nEnd Sub", "書き戻しに失敗して壊れたまま");

        var pull = await Service.PullAsync(project);

        pull.Value.Pulled.Should().BeEmpty();
        pull.Value.Conflicts.Should().Equal("Modules/Module1.bas");
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Be(mirrorBefore);
    }

    [Fact(DisplayName = "項目4: 解除 — 手動で『Graft 側で上書き』『Excel 側を採用』を選ぶと、要確認が外れる")]
    public async Task 手動の解決で要確認が外れる()
    {
        var project = await RollbackFailedAsync();

        var push = await Service.PushAsync(project, ["Modules/Module1.bas"], ["Modules/Module1.bas"]);

        push.Value.Pushed.Should().Equal("Modules/Module1.bas");
        (await StateAsync()).Modules["Modules/Module1.bas"].NeedsReview.Should().BeFalse();
        (await Service.GetLocalStatusAsync(project)).NeedsReviewCount.Should().Be(0);

        // もう一度要確認にして、取り込む側でも外れることを確かめる。
        Bridge.CorruptOnWrite.Add("Module1");
        Bridge.RollbackFails.Add("Module1");
        File.WriteAllText(MirrorFile("Modules/Module1.bas"), "Sub A()\r\n  z=3\r\nEnd Sub\r\n");
        await Service.PushAsync(project, ["Modules/Module1.bas"]);
        Bridge.CorruptOnWrite.Clear();
        Bridge.RollbackFails.Clear();
        (await StateAsync()).Modules["Modules/Module1.bas"].NeedsReview.Should().BeTrue();

        var pull = await Service.PullAsync(project, ["Modules/Module1.bas"], adoptConflicts: ["Modules/Module1.bas"]);

        pull.Value.Pulled.Should().Equal("Modules/Module1.bas");
        (await StateAsync()).Modules["Modules/Module1.bas"].NeedsReview.Should().BeFalse();
    }

    [Fact(DisplayName = "項目4: 解除 — 控え（.rescue/）からミラーへ戻したときに要確認が外れる")]
    public async Task 控えから戻すと要確認が外れる()
    {
        var project = await RollbackFailedAsync();
        var listing = await Service.Rescue.ListAsync(project);
        var item = listing.Items.Single(i => i.Kind == VbaRescueKind.Legacy);

        var restored = await Service.Rescue.RestoreAsync(project, item);

        restored.Status.Should().Be(Graft.Features.VbaRescueRestoreStatus.Restored);
        (await StateAsync()).Modules["Modules/Module1.bas"].NeedsReview.Should().BeFalse();
    }

    [Fact(DisplayName = "項目4: 同期状態の JSON — 要確認が無い古いファイルは false として読め、false のときは書かず、true は往復できる")]
    public async Task 同期状態の後方互換()
    {
        var project = await TwoEditedAsync();
        var file = Paths.GetVbaSyncFilePath(project.Id);

        var json = File.ReadAllText(file);
        json.Should().NotContain("needsReview", "false は書かない（古い版が読んでも差が出ない）");
        (await StateAsync()).Modules.Values.Should().OnlyContain(m => !m.NeedsReview);

        var state = await StateAsync();
        var marked = state.WithModule("Modules/Module1.bas", state.Modules["Modules/Module1.bas"] with { NeedsReview = true });
        await States.SaveAsync(project.Id, marked);
        File.ReadAllText(file).Should().Contain("needsReview");
        (await StateAsync()).Modules["Modules/Module1.bas"].NeedsReview.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // 項目 5: 空の Excel
    // ------------------------------------------------------------------

    [Fact(DisplayName = "項目5: Excel 側が空（0 行）でミラーが空でないモジュールは、取り込まずに衝突にする。『Excel 側を採用』を選んだときだけ空にする")]
    public async Task 空のExcelは黙って取り込まない()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"));
        Excel("Module1").Code = string.Empty;
        var mirror = File.ReadAllText(MirrorFile("Modules/Module1.bas"));

        var pull = await Service.PullAsync(project);

        pull.Value.Pulled.Should().BeEmpty();
        pull.Value.Conflicts.Should().Equal("Modules/Module1.bas");
        pull.Value.ConflictItems.Single().Reason.Should().Be(VbaConflictReason.ExcelEmpty);
        pull.Issues.Should().Contain(i => i.Code == ErrorCode.E812 && i.Detail!.Contains("空"));
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Should().Be(mirror, "ミラーは 0 行にならない");
        (await Service.CheckAsync(project)).Value.Conflicts.Should().ContainSingle();

        var adopted = await Service.PullAsync(project, ["Modules/Module1.bas"], adoptConflicts: ["Modules/Module1.bas"]);

        adopted.Value.Pulled.Should().Equal("Modules/Module1.bas");
        File.ReadAllText(MirrorFile("Modules/Module1.bas")).Trim().Should().BeEmpty("利用者が選んだので空にする");
    }

    [Fact(DisplayName = "項目5: 空の Excel を『Graft 側で上書き』で解決すると、ミラーの本文が Excel へ反映される")]
    public async Task 空のExcelをGraft側で上書きできる()
    {
        var project = await CreatedProjectAsync(FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"));
        Excel("Module1").Code = string.Empty;

        var push = await Service.PushAsync(project, ["Modules/Module1.bas"], ["Modules/Module1.bas"]);

        push.Value.Pushed.Should().Equal("Modules/Module1.bas");
        Excel("Module1").Code.Should().Be("Sub A()\r\nEnd Sub");
    }
}
