using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// VBA プロジェクトがパスワードでロックされているとき（E803）の窓（<see cref="VbaLockedText"/>）のシナリオ。場面ごとに分ける:
/// 適用の後の Push（既定は「その適用 rN を取り消す」）、適用の前の取込・再検査（既定は「パッチを破棄する」）、手動の「Excel へ反映」
/// （取り消しは出さない）。E803 以外の失敗の窓は従来のまま（<see cref="VbaP3PushTests"/>）。
/// </summary>
public class VbaLockedTests : IDisposable
{
    private const string Module1 = "Modules/Module1.bas";
    private const string Cls1 = "Classes/Cls1.cls";

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string[] EditModule1() => new[] { VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"") };

    private static async Task ParseAndApplyAsync(VbaP3Rig r, params string[] blocks)
    {
        await r.PasteAsync(blocks).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 選択ダイアログの台本: 「VBE を開いてロックを解除する」を押し（窓は閉じない）、ブックのロックを解いてから、
    /// <paramref name="then"/> のボタンを選ぶ。出た要求は <c>r.Choices</c> に残す。
    /// </summary>
    private static void UnlockThenChoose(VbaP3Rig r, string then, List<string>? log = null)
    {
        r.H.Shell.Graft.ChooseVbaAsync = async request =>
        {
            r.Choices.Add(request);
            var vbe = request.Options.Single(o => o.Id == "vbe");
            vbe.KeepOpenAction.Should().NotBeNull("窓を閉じずに実行される操作");
            await vbe.KeepOpenAction!().ConfigureAwait(true);
            log?.Add("vbe");
            r.Bridge.Get(VbaP3Rig.Book)!.Locked = false; // 利用者が VBE でロックを解除した。
            return then;
        };
    }

    // ------------------------------------------------------------------
    // 適用の後の Push が E803
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "適用後の Push が E803: 専用の窓（題名・選択肢の並び）。既定（Enter・Esc・×）は「この適用（rN）を取り消す」で、その適用だけが戻り、以前からの未反映は戻らない")]
    public async Task 適用後のロックは既定で取り消す()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        // 以前から溜まっていた未反映（手編集）。この適用の取り消しでは戻さない。
        File.WriteAllText(r.MirrorPath(Cls1), "Option Explicit\r\nPublic Value As Currency\r\n");
        r.Bridge.FailNext("Write", GraftIssue.Of(ErrorCode.E803));

        await ParseAndApplyAsync(r, EditModule1()).ConfigureAwait(true); // 台本なし＝既定のボタン

        var request = r.Choices.Should().ContainSingle().Subject;
        request.Title.Should().Be("VBA プロジェクトがロックされています");
        request.Message.Should().Contain("r1").And.Contain("E803").And.Contain("以前から溜まっていた未反映は戻しません");
        request.Options.Select(o => o.Label).Should().Equal(
            "中止してこの適用（r1）を取り消す", "VBE を開いてロックを解除する", "解除したのでもう一度反映する", "ファイルの変更は残す");
        request.Default.Id.Should().Be("undo");
        request.Options.Count(o => o.IsDefault).Should().Be(1);

        // その適用 r1 だけが戻る。
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().ContainSingle().Which.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        r.Mirror(Module1).Should().Contain("MsgBox \"hi\"").And.NotContain("hello");
        r.Mirror(Cls1).Should().Contain("Currency", "以前から溜まっていた未反映は戻さない");
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original);
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1, "以前からの未反映だけが残る");
        r.H.Dialogs.Confirms.Should().NotContain(c => c.Title == "取り消しの確認");
        r.Graft.HasApplyUndoNotice.Should().BeFalse("取り消した適用に「元に戻す」は出さない");
    }

    [AvaloniaFact(DisplayName = "適用後の Push が E803: 「VBE を開いてロックを解除する」は窓を閉じずに VBE を前面に出し、解除後の「もう一度反映する」でそのまま反映できる（取り消されない）")]
    public async Task 適用後のロックを解除してもう一度反映する()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.BeforeWrite = () =>
        {
            if (r.Bridge.WriteRequests.Count == 1) r.Bridge.Get(VbaP3Rig.Book)!.Locked = true; // 最初の書き込みだけロックされている
            return Task.CompletedTask;
        };
        UnlockThenChoose(r, "retry");

        await ParseAndApplyAsync(r, EditModule1()).ConfigureAwait(true);

        r.Choices.Should().ContainSingle("もう一度反映して成功したので、窓は 1 回だけ");
        r.Bridge.Calls.Should().Contain($"ShowVbe:{VbaP3Rig.Book}", "VBE を前面に出す機能を使う（VBProject に触れない）");
        r.Excel("Module1").Code.Should().Contain("hello");
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().ContainSingle().Which.Manifest.Status.Should().Be(RevisionStatus.Success);
        r.Graft.ApplyUndoNoticeText.Should().Contain("Excel へ反映済み");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(0);
    }

    [AvaloniaFact(DisplayName = "適用後の Push が E803: 「ファイルの変更は残す」を選ぶと、取り消さず未反映の印を付けて残す")]
    public async Task 適用後のロックでファイルの変更を残す()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.FailNext("Write", GraftIssue.Of(ErrorCode.E803));
        r.ChoiceAnswer = _ => "keep";

        await ParseAndApplyAsync(r, EditModule1()).ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("hello");
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().ContainSingle().Which.Manifest.Status.Should().Be(RevisionStatus.Success);
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1);
        r.Graft.ApplyUndoNoticeText.Should().Contain("Excel には未反映");
    }

    [AvaloniaFact(DisplayName = "E803 以外の失敗（E805）の窓は従来のまま: 題名・選択肢・既定は変わらない")]
    public async Task ほかの失敗の窓は従来のまま()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.FailNext("Write", GraftIssue.Of(ErrorCode.E805));

        await ParseAndApplyAsync(r, EditModule1()).ConfigureAwait(true);

        var request = r.Choices.Should().ContainSingle().Subject;
        request.Title.Should().Be("Excel へ反映できませんでした");
        request.Options.Select(o => o.Label).Should().Equal("ファイルの変更は残す（未反映の印）", "もう一度反映する", "この適用（r1）を取り消す");
        request.Default.Id.Should().Be("keep");
    }

    // ------------------------------------------------------------------
    // 適用の前（解析前の取込・適用直前の再検査）が E803
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "解析前の取込が E803: 専用の窓。既定は「中止してパッチを破棄する」で、接ぎ木パネルが空になり、ミラーも Excel も変わらない")]
    public async Task 解析前のロックは既定でパッチを破棄する()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.Get(VbaP3Rig.Book)!.Locked = true;
        var mirrorBefore = r.Mirror(Module1);

        await r.PasteAsync(EditModule1()).ConfigureAwait(true); // 台本なし＝既定のボタン

        var request = r.Choices.Should().ContainSingle().Subject;
        request.Title.Should().Be("VBA プロジェクトがロックされています");
        request.Options.Select(o => o.Label).Should().Equal(
            "中止してパッチを破棄する", "VBE を開いてロックを解除する", "解除したのでもう一度試す", "ファイルだけ適用する");
        request.Default.Id.Should().Be("discard");

        r.Graft.Blocks.Should().BeEmpty("パッチを破棄したので、接ぎ木パネルは空");
        r.Graft.State.Should().Be(CenterPaneState.Empty);
        r.Graft.PreviewCommand.CanExecute(null).Should().BeFalse("現在のパッチは無い");
        r.Mirror(Module1).Should().Be(mirrorBefore);
        r.WriteCalls.Should().Be(0);
        r.Graft.Notice.Text.Should().Contain("パッチを破棄しました");
    }

    [AvaloniaFact(DisplayName = "解析前の取込が E803: 「解除したのでもう一度試す」は、取込から続けて解析まで進む（窓は 1 回。VBE を開く操作は窓を閉じない）")]
    public async Task 解析前のロックを解除してもう一度試す()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.Get(VbaP3Rig.Book)!.Locked = true;
        var log = new List<string>();
        UnlockThenChoose(r, "retry", log);

        await r.PasteAsync(EditModule1()).ConfigureAwait(true);

        log.Should().Equal("vbe");
        r.Choices.Should().ContainSingle();
        r.Bridge.Calls.Should().Contain($"ShowVbe:{VbaP3Rig.Book}");
        r.Graft.Blocks.Should().ContainSingle("取込から続けて、解析まで進んだ");
        r.Graft.State.Should().Be(CenterPaneState.Content);

        // そのまま適用できる（Excel へ反映される）。
        await r.ApplyAsync().ConfigureAwait(true);
        r.Excel("Module1").Code.Should().Contain("hello");
    }

    [AvaloniaFact(DisplayName = "解析前の取込が E803: 「ファイルだけ適用する」はパッチを残し、適用しても Excel には触れない（未反映の印）")]
    public async Task 解析前のロックでファイルだけ適用する()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.Get(VbaP3Rig.Book)!.Locked = true;
        r.ChoiceAnswer = _ => "fileOnly";

        await ParseAndApplyAsync(r, EditModule1()).ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("hello");
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original);
        r.WriteCalls.Should().Be(0);
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1);
        r.Graft.ApplyUndoNoticeText.Should().Contain("Excel には未反映");
    }

    [AvaloniaFact(DisplayName = "適用直前の再検査が E803: 同じ専用の窓。既定は「パッチを破棄する」で、ファイルも履歴も変わらない")]
    public async Task 適用直前の再検査のロックは既定で破棄する()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.PasteAsync(EditModule1()).ConfigureAwait(true);
        r.Graft.Blocks.Should().ContainSingle();
        r.Bridge.Get(VbaP3Rig.Book)!.Locked = true; // 解析のあと、適用の前にロックされた。

        await r.ApplyAsync().ConfigureAwait(true); // 台本なし＝既定

        var request = r.Choices.Should().ContainSingle().Subject;
        request.Title.Should().Be("VBA プロジェクトがロックされています");
        request.Default.Id.Should().Be("discard");
        r.Graft.Blocks.Should().BeEmpty("パッチを破棄した");
        r.Mirror(Module1).Should().Contain("MsgBox \"hi\"").And.NotContain("hello", "適用されていない");
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "適用直前の再検査が E803: 「解除したのでもう一度試す」は再検査から続けて、そのまま適用して Excel へ反映する")]
    public async Task 適用直前の再検査のロックを解除してもう一度試す()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.PasteAsync(EditModule1()).ConfigureAwait(true);
        r.Bridge.Get(VbaP3Rig.Book)!.Locked = true;
        UnlockThenChoose(r, "retry");

        await r.ApplyAsync().ConfigureAwait(true);

        r.Choices.Should().ContainSingle();
        r.Excel("Module1").Code.Should().Contain("hello");
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().ContainSingle().Which.Manifest.Status.Should().Be(RevisionStatus.Success);
    }

    // ------------------------------------------------------------------
    // 手動の「Excel へ反映」・保存時の自動反映
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "手動の「Excel へ反映」が E803: 取り消しも破棄も出さず、「閉じる」（既定）・「VBE を開く」・「もう一度反映する」だけ。解除後の「もう一度」で反映できる")]
    public async Task 手動の反映のロック()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        File.AppendAllText(r.MirrorPath(Module1), "\r\n' 手で足した行");
        r.Bridge.Get(VbaP3Rig.Book)!.Locked = true;

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushToExcelCommand).ConfigureAwait(true); // 台本なし＝既定

        var request = r.Choices.Should().ContainSingle().Subject;
        request.Title.Should().Be("VBA プロジェクトがロックされています");
        request.Options.Select(o => o.Id).Should().Equal("keep", "vbe", "retry");
        request.Options.Select(o => o.Label).Should().Equal("閉じる", "VBE を開いてロックを解除する", "解除したのでもう一度反映する");
        request.Default.Id.Should().Be("keep");
        r.H.Dialogs.Messages.Should().NotContain(m => m.Title == "Excel へ反映できませんでした", "ロックの窓で案内したので、同じ失敗を重ねて出さない");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1, "未反映のまま残る");

        r.Choices.Clear();
        UnlockThenChoose(r, "retry");
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushToExcelCommand).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("手で足した行");
        r.Choices.Should().ContainSingle();
    }

    [AvaloniaFact(DisplayName = "手動の取込が E803: ロックの窓（VBE を開く・閉じる）で案内する。取り消しは出ない")]
    public async Task 手動の取込のロック()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Bridge.Get(VbaP3Rig.Book)!.Locked = true;

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);

        var request = r.Choices.Should().ContainSingle().Subject;
        request.Title.Should().Be("VBA プロジェクトがロックされています");
        request.Options.Select(o => o.Id).Should().Equal("keep", "vbe");
        request.Message.Should().Contain("Excel から取り込めませんでした");
    }

    [AvaloniaFact(DisplayName = "保存時の自動反映が E803: 通知に「VBE を開く」の操作が付く（保存のたびに窓は出さない）")]
    public async Task 保存時の自動反映のロック()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.UpdateLinkAsync(l => l with { AutoPushOnSave = true }).ConfigureAwait(true);
        r.Bridge.Get(VbaP3Rig.Book)!.Locked = true;

        // 保存（TabSaved）から、まとめの待ちと反映までが終わるのを待つ。
        var tab = (await r.H.Shell.Editor.OpenFileAsync(r.MirrorPath(Module1)).ConfigureAwait(true)).Value;
        r.Graft.Notice.Dismiss();
        tab.Session.Document.Insert(0, "' 編集\r\n");
        (await r.H.Shell.Editor.SaveActiveAsync().ConfigureAwait(true)).IsSuccess.Should().BeTrue();
        await r.Graft.WhenVbaSaveSyncIdleAsync().ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => !r.Graft.IsVbaWriting).ConfigureAwait(true);
        await Task.Delay(30).ConfigureAwait(true);

        r.Graft.Notice.Text.Should().Contain("E803 VBA プロジェクトがロック中");
        r.Graft.Notice.ActionLabel.Should().Be("VBE を開いてロックを解除する");
        r.Choices.Should().BeEmpty("保存のたびに窓は出さない");
        await ShellHarness.ExecuteAsync(r.Graft.Notice.ActionCommand).ConfigureAwait(true);
        r.Bridge.Calls.Should().Contain($"ShowVbe:{VbaP3Rig.Book}");
    }

    // ------------------------------------------------------------------
    // 窓そのもの（Enter・Esc・× と、窓を閉じない操作）
    // ------------------------------------------------------------------

    private Window ShowOwner()
    {
        var owner = _windows.Track(new Window { Width = 1280, Height = 800 });
        owner.Show();
        return owner;
    }

    [AvaloniaFact(DisplayName = "窓: Esc・× で閉じると既定（適用の後は「取り消す」・適用の前は「破棄する」）。「VBE を開く」ボタンは操作を実行しても窓を閉じない")]
    public async Task 窓の既定と窓を閉じない操作()
    {
        var owner = ShowOwner();
        var opened = 0;
        Task OpenVbe()
        {
            opened++;
            return Task.CompletedTask;
        }

        var request = new VbaChoiceRequest(VbaLockedText.Title, "本文", VbaLockedText.Options(VbaLockedScene.AfterApply, 3, OpenVbe));
        var window = _windows.Track(new VbaChoiceWindow(request));
        var pending = window.ShowAndChooseAsync(owner);
        Dispatcher.UIThread.RunJobs();
        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        buttons.Select(b => b.Content).Should().Equal(
            "中止してこの適用（r3）を取り消す", "VBE を開いてロックを解除する", "解除したのでもう一度反映する", "ファイルの変更は残す");
        buttons[0].IsDefault.Should().BeTrue("Enter は「取り消す」");

        buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(30).ConfigureAwait(true);
        opened.Should().Be(1);
        window.IsVisible.Should().BeTrue("窓は閉じない");
        pending.IsCompleted.Should().BeFalse();

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        (await pending.ConfigureAwait(true)).Should().Be("undo", "Esc は既定のボタンと同じ結果");

        var before = new VbaChoiceRequest(VbaLockedText.Title, "本文", VbaLockedText.Options(VbaLockedScene.BeforeApply, null, OpenVbe));
        var closing = _windows.Track(new VbaChoiceWindow(before));
        var pendingClose = closing.ShowAndChooseAsync(owner);
        Dispatcher.UIThread.RunJobs();
        closing.Close(); // ×
        (await pendingClose.ConfigureAwait(true)).Should().Be("discard", "× も既定のボタンと同じ結果");
    }
}
