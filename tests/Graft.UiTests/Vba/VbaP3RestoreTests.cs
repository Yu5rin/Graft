using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// Excel VBA 連携 段階 P3 のシナリオ（仕様書_VBA連携 10.2）のうち、取り消し・復元の後の Push（7.4）と、
/// 手動の取込・反映の衝突ダイアログ（7.5）。取り消し・復元の経路は 3 つあり、どれも Excel へ Push され、
/// <c>RevisionRestored</c> と二重にならない（Write の呼び出し回数で確かめる）。
/// </summary>
public class VbaP3RestoreTests
{
    private const string Module1 = "Modules/Module1.bas";

    private static readonly string HelloEdit = VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"");

    private static async Task ApplyHelloAsync(VbaP3Rig r)
    {
        await r.PasteAsync(HelloEdit).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);
        r.Excel("Module1").Code.Should().Contain("hello", "前提: 適用で Excel へ反映済み");
    }

    // ------------------------------------------------------------------
    // 取り消し・復元の 3 経路（7.4）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "経路1 単発の取り消し（Ctrl+Z／ステータスバーの「元に戻す」）: Excel も元の内容へ Push される。Push は 1 回だけ")]
    public async Task 取り消しの後にPushされる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await ApplyHelloAsync(r).ConfigureAwait(true);
        r.WriteCalls.Should().Be(1);

        await ShellHarness.ExecuteAsync(r.Graft.UndoCommand).ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("MsgBox \"hi\"");
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original);
        r.WriteCalls.Should().Be(2, "取り消しの Push は 1 回（RevisionRestored と二重にならない）");
        r.Conflicts.Should().BeEmpty();
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(0);
        r.Graft.Notice.Text.Should().Contain("取り消しました");
    }

    [AvaloniaFact(DisplayName = "経路1 履歴ペインの「このリビジョンを取り消す」も同じ経路で Push される")]
    public async Task 履歴ペインの取り消しでもPushされる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await ApplyHelloAsync(r).ConfigureAwait(true);
        r.Graft.History.SelectedItem = r.Graft.History.Items.Single(i => i.RevisionLabel == "r1");

        await ShellHarness.ExecuteAsync(r.Graft.History.RestoreCommand).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original);
        r.WriteCalls.Should().Be(2);
    }

    [AvaloniaFact(DisplayName = "経路2 「ここまで戻す」: 戻した結果が Excel へ Push される。Push は 1 回だけ")]
    public async Task ここまで戻すの後にPushされる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await ApplyHelloAsync(r).ConfigureAwait(true);
        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hello\"", "  MsgBox \"bye\"")).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);
        r.Excel("Module1").Code.Should().Contain("bye");
        r.WriteCalls.Should().Be(2);

        r.Graft.History.SelectedItem = r.Graft.History.Items.Single(i => i.RevisionLabel == "r1");
        await ShellHarness.ExecuteAsync(r.Graft.History.RestoreThroughCommand).ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("hello");
        r.Excel("Module1").Code.Should().Contain("hello").And.NotContain("bye");
        r.WriteCalls.Should().Be(3);
    }

    [AvaloniaFact(DisplayName = "経路3 適用後フック失敗の巻き戻し: Excel は新内容、ミラーだけ旧内容になることを避けるため、巻き戻しの後に Push される")]
    public async Task フック失敗の巻き戻しの後にPushされる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.SetHooksAsync(new PostApplyHook { Name = "失敗フック", Command = "exit 1", OnFailure = HookFailureAction.AutoRollback }).ConfigureAwait(true);

        await r.PasteAsync(HelloEdit).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("MsgBox \"hi\"", "フック失敗でミラーは巻き戻された");
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original, "Excel も巻き戻しの後の内容へ戻る");
        r.WriteCalls.Should().Be(2, "適用の Push と、巻き戻しの Push");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(0);
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().ContainSingle().Which.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
    }

    [AvaloniaFact(DisplayName = "取り消しの Push 直前の衝突: 衝突ダイアログ（復元の場面）に「Excel 側を採用」は出ない。「反映しない」なら Excel は触らず、未反映の印が付く")]
    public async Task 取り消しの衝突で反映しない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await ApplyHelloAsync(r).ConfigureAwait(true);
        r.Excel("Module1").Code += "\r\n' vbe edit"; // VBE での編集
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.Skip);

        await ShellHarness.ExecuteAsync(r.Graft.UndoCommand).ConfigureAwait(true);

        var request = r.Conflicts.Should().ContainSingle().Subject;
        request.Scene.Should().Be(VbaConflictScene.AfterRestore);
        request.CanAdoptExcel.Should().BeFalse("復元の意図を打ち消すので出さない");
        request.CanUndoApply.Should().BeFalse();
        request.CanSkip.Should().BeTrue();
        r.Mirror(Module1).Should().Contain("MsgBox \"hi\"");
        r.Excel("Module1").Code.Should().Contain("vbe edit").And.Contain("hello");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1);
        r.Choices.Should().ContainSingle(c => c.Title == "反映しなかったモジュールがあります")
            .Which.Options.Select(o => o.Id).Should().Equal(new[] { "keep", "retry" }, "復元の後は「この適用を取り消す」を出さない");
    }

    [AvaloniaFact(DisplayName = "取り消しの Push 直前の衝突: 「Graft 側で上書き」なら Excel が元の内容になり、VBE で編集していた内容は控えに残る")]
    public async Task 取り消しの衝突でGraft側で上書きする()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await ApplyHelloAsync(r).ConfigureAwait(true);
        r.Excel("Module1").Code += "\r\n' vbe edit";
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.OverwriteExcel);

        await ShellHarness.ExecuteAsync(r.Graft.UndoCommand).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original);
        r.RescueTexts().Should().ContainSingle().Which.Should().Contain("vbe edit");
    }

    [AvaloniaFact(DisplayName = "新規モジュールを含む適用を取り消すと、Excel 側に残る旨を知らせ、削除保留になる（次の自動 Pull で復活しない）")]
    public async Task 新規モジュールの取り消しは削除保留になる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.PasteAsync(VbaP3Rig.Create("Modules/NewMod.bas", "Sub NewOne()\nEnd Sub")).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);
        r.Excel("NewMod").Code.Should().Contain("NewOne");
        r.H.Dialogs.Messages.Clear();

        await ShellHarness.ExecuteAsync(r.Graft.UndoCommand).ConfigureAwait(true);

        File.Exists(r.MirrorPath("Modules/NewMod.bas")).Should().BeFalse();
        r.Excel("NewMod").Should().NotBeNull("Excel 側のモジュールは残る");
        r.H.Dialogs.Messages.Should().ContainSingle(m => m.Title == "Excel 側にモジュールが残っています")
            .Which.Message.Should().Contain("Modules/NewMod.bas").And.Contain("削除保留");
        (await r.StatusAsync().ConfigureAwait(true)).DeletePendingCount.Should().Be(1);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        File.Exists(r.MirrorPath("Modules/NewMod.bas")).Should().BeFalse("自動 Pull では復活しない");
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.RestoreFromExcelCommand).ConfigureAwait(true);
        File.Exists(r.MirrorPath("Modules/NewMod.bas")).Should().BeTrue("「Excel から復元」で戻る");
    }

    [AvaloniaFact(DisplayName = "自動反映をオフ（VbaLink.AutoPushAfterApply=false）にすると、適用も取り消しも Excel へ Push せず、未反映の印だけが付く")]
    public async Task 自動反映をオフにすると何も送らない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.UpdateLinkAsync(link => link with { AutoPushAfterApply = false }).ConfigureAwait(true);

        await r.PasteAsync(HelloEdit).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);

        r.WriteCalls.Should().Be(0);
        r.Graft.ApplyUndoNoticeText.Should().Be("r1 として適用しました — 元に戻す");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1);

        await ShellHarness.ExecuteAsync(r.Graft.UndoCommand).ConfigureAwait(true);
        r.WriteCalls.Should().Be(0);
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original);
    }

    // ------------------------------------------------------------------
    // 手動の取込・反映の衝突ダイアログ（7.5。P2 の通知からの差し替え）
    // ------------------------------------------------------------------

    private static async Task<VbaP3Rig> ManualConflictAsync()
    {
        var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Module1), "graft side\r\n");
        r.Excel("Module1").Code = "excel side";
        return r;
    }

    [AvaloniaFact(DisplayName = "手動の取込の衝突: 衝突ダイアログ（手動取込の場面）→「Excel 側を採用」でミラーが Excel の内容になる")]
    public async Task 手動の取込の衝突でExcel側を採用する()
    {
        using var r = await ManualConflictAsync().ConfigureAwait(true);
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.AdoptExcel);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);

        r.Conflicts.Should().ContainSingle().Which.Scene.Should().Be(VbaConflictScene.ManualPull);
        r.Mirror(Module1).Should().Contain("excel side").And.NotContain("graft side");
        r.Graft.Notice.Text.Should().Be("Excel から 1 件取り込みました。");
        r.H.Dialogs.Messages.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "手動の反映の衝突: 衝突ダイアログ（手動反映の場面）→「Graft 側で上書き」で Excel が上書きされ、Excel 側の本文は控えられる")]
    public async Task 手動の反映の衝突でGraft側で上書きする()
    {
        using var r = await ManualConflictAsync().ConfigureAwait(true);
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.OverwriteExcel);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushToExcelCommand).ConfigureAwait(true);

        r.Conflicts.Should().ContainSingle().Which.Scene.Should().Be(VbaConflictScene.ManualPush);
        r.Excel("Module1").Code.Should().Be("graft side");
        r.RescueTexts().Should().ContainSingle().Which.Should().Be("excel side");
        r.Graft.Notice.Text.Should().Be("Excel へ 1 件反映しました。");
    }

    [AvaloniaFact(DisplayName = "手動の取込・反映の衝突: 「中止」なら何もせず、E812 でどちらも変わっていないことを伝える")]
    public async Task 手動の衝突で中止する()
    {
        using var r = await ManualConflictAsync().ConfigureAwait(true);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushToExcelCommand).ConfigureAwait(true);

        r.Conflicts.Select(c => c.Scene).Should().Equal(VbaConflictScene.ManualPull, VbaConflictScene.ManualPush);
        r.H.Dialogs.Messages.Should().HaveCount(2).And.OnlyContain(m => m.Message.Contains("E812") && m.Message.Contains(Module1));
        r.Mirror(Module1).Should().Contain("graft side");
        r.Excel("Module1").Code.Should().Be("excel side");
    }
}
