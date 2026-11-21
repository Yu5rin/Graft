using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// 不具合の修正の回帰テストのうち、画面側（ViewModel・コマンド）を通すもの。<see cref="VbaP3Rig"/> で
/// 実際の起動と同じ依存グラフに <see cref="FakeVbaBridge"/> を入れて確かめる。
/// </summary>
public class VbaFixScenarioTests
{
    [AvaloniaFact(DisplayName = "不具合5 再現: ミラーのファイルを消した直後（取込も反映も走る前）に「Excel から復元」を押すと、0 件ではなく復元される")]
    public async Task ファイルを消した直後でも復元できる()
    {
        using var rig = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        File.Delete(rig.MirrorPath("Classes/Cls1.cls"));

        // バッジは「削除保留 1」を数えている。同期状態の DeletePending の印はまだ保存されていない。
        (await rig.StatusAsync().ConfigureAwait(true)).DeletePendingCount.Should().Be(1);
        (await rig.Graft.VbaSync.GetRestorablePathsAsync(rig.Project).ConfigureAwait(true)).Should().Equal("Classes/Cls1.cls");

        await ShellHarness.ExecuteAsync(rig.Graft.ProjectPane.RestoreFromExcelCommand).ConfigureAwait(true);

        File.Exists(rig.MirrorPath("Classes/Cls1.cls")).Should().BeTrue();
        rig.Graft.Notice.Text.Should().Be("Excel から 1 件復元しました。");
        (await rig.StatusAsync().ConfigureAwait(true)).DeletePendingCount.Should().Be(0);
    }

    [AvaloniaFact(DisplayName = "不具合5: 自動反映をオフにしたプロジェクトでも、ミラーから消えたモジュールを復元できる（同期状態に印がなくても拾う）")]
    public async Task 取り消し直後でも復元できる()
    {
        using var rig = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await rig.UpdateLinkAsync(l => l with { AutoPushAfterApply = false }).ConfigureAwait(true);
        // Excel にあるモジュールの記録を作ってから、ミラーだけ消す（取り消しでファイルが消えた状態の再現）。
        File.Delete(rig.MirrorPath("Modules/Module1.bas"));

        var restorable = await rig.Graft.VbaSync.GetRestorablePathsAsync(rig.Project).ConfigureAwait(true);

        restorable.Should().Equal("Modules/Module1.bas");
        await ShellHarness.ExecuteAsync(rig.Graft.ProjectPane.RestoreFromExcelCommand).ConfigureAwait(true);
        rig.Mirror("Modules/Module1.bas").Should().Contain("MsgBox");
    }

    // ------------------------------------------------------------------
    // 不具合 8: 「ファイルだけ適用する」の選択が次のパッチや再解析に持ち越される
    // ------------------------------------------------------------------

    private const string Module1 = "Modules/Module1.bas";

    private static void ReopenWorkbook(VbaP3Rig rig)
        => rig.Bridge.AddWorkbook(VbaP3Rig.Book,
            FakeVbaBridge.Std("Module1", VbaP3Rig.Module1Original),
            FakeVbaBridge.Cls("Cls1", VbaP3Rig.Cls1Original),
            FakeVbaBridge.Doc("Sheet1", "' sheet", "集計"));

    private static void ChooseFileOnlyWhenOffline(VbaP3Rig rig)
        => rig.ChoiceAnswer = request => request.Title == "Excel と同期できませんでした" ? "fileOnly" : null;

    [AvaloniaFact(DisplayName = "不具合8 再現: ブックを閉じて「ファイルだけ適用する」→ キューへ追加 → ブックを開く → キュー結合 → 適用。選択を持ち越さず、Excel へ反映される")]
    public async Task ファイルだけ適用の選択はキュー結合に持ち越されない()
    {
        using var rig = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        rig.Bridge.CloseWorkbook(VbaP3Rig.Book);
        ChooseFileOnlyWhenOffline(rig);

        await rig.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        rig.Choices.Should().ContainSingle(c => c.Title == "Excel と同期できませんでした");
        await ShellHarness.ExecuteAsync(rig.Graft.AddCurrentPatchToQueueCommand).ConfigureAwait(true);

        ReopenWorkbook(rig);
        rig.Choices.Clear();
        await ShellHarness.ExecuteAsync(rig.Graft.Queue.MergeCommand).ConfigureAwait(true);
        await rig.ApplyAsync().ConfigureAwait(true);

        rig.WriteCalls.Should().Be(1, "前のパッチで選んだ「ファイルだけ」は持ち越されない");
        rig.Excel("Module1").Code.Should().Contain("hello");
        rig.Graft.ApplyUndoNoticeText.Should().Contain("Excel へ反映済み");
    }

    [AvaloniaFact(DisplayName = "不具合8 再現: ブックを閉じて「ファイルだけ適用する」→ ブックを開いて再解析 → 適用。再解析の時点で選択が下り、Excel へ反映される")]
    public async Task ファイルだけ適用の選択は再解析で下りる()
    {
        using var rig = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        rig.Bridge.CloseWorkbook(VbaP3Rig.Book);
        ChooseFileOnlyWhenOffline(rig);

        await rig.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

        ReopenWorkbook(rig);
        await ShellHarness.ExecuteAsync(rig.Graft.PreviewCommand).ConfigureAwait(true);
        await rig.ApplyAsync().ConfigureAwait(true);

        rig.WriteCalls.Should().Be(1);
        rig.Excel("Module1").Code.Should().Contain("hello");
    }

    [AvaloniaFact(DisplayName = "不具合8: 解析結果を破棄したあとに貼った次のパッチへ、「ファイルだけ適用する」の選択は持ち越されない")]
    public async Task 破棄のあとの次のパッチへ持ち越されない()
    {
        using var rig = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        rig.Bridge.CloseWorkbook(VbaP3Rig.Book);
        ChooseFileOnlyWhenOffline(rig);
        await rig.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);

        await ShellHarness.ExecuteAsync(rig.Graft.DiscardCommand).ConfigureAwait(true);
        ReopenWorkbook(rig);
        await rig.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"second\"")).ConfigureAwait(true);
        await rig.ApplyAsync().ConfigureAwait(true);

        rig.Excel("Module1").Code.Should().Contain("second");
    }

    [AvaloniaFact(DisplayName = "不具合8: 「ファイルだけ適用する」を選んだ適用そのものは、従来どおり Excel へ書かず「未反映」で残る（対照）")]
    public async Task ファイルだけ適用は従来どおり未反映で残る()
    {
        using var rig = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        rig.Bridge.CloseWorkbook(VbaP3Rig.Book);
        ChooseFileOnlyWhenOffline(rig);

        await rig.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        await rig.ApplyAsync().ConfigureAwait(true);

        rig.WriteCalls.Should().Be(0);
        rig.Mirror(Module1).Should().Contain("hello");
        rig.Graft.ApplyUndoNoticeText.Should().Contain("Excel には未反映");
        (await rig.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // 不具合 9: 取り消し後の反映・手動の反映の最中に、塞がれていない操作がある
    // ------------------------------------------------------------------

    private static async Task<(TaskCompletionSource Release, Task Restoring)> StartBlockedRestoreAsync(VbaP3Rig rig)
    {
        await rig.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        await rig.ApplyAsync().ConfigureAwait(true);

        // 履歴の取り消し（適用ではない）の後の Excel への反映を、書き込みの手前で止める。
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        rig.Bridge.BeforeWrite = async () =>
        {
            reached.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };
        rig.Graft.History.SelectedItem = rig.Graft.History.Items.Single(i => i.RevisionLabel == "r1");
        var restoring = ShellHarness.ExecuteAsync(rig.Graft.History.RestoreCommand);
        await ShellHarness.WaitUntilAsync(() => reached.Task.IsCompleted).ConfigureAwait(true);
        return (release, restoring);
    }

    [AvaloniaFact(DisplayName = "不具合9 再現: 取り消し後の反映の最中は、適用中の旗が立たなくても「Excel へ書いている最中」になり、反映・復元・プロジェクトの削除と場所の変更のコマンドが無効になる")]
    public async Task 反映の最中はコマンドが無効になる()
    {
        using var rig = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = rig.Graft.ProjectPane;
        var (release, restoring) = await StartBlockedRestoreAsync(rig).ConfigureAwait(true);

        rig.Graft.IsApplyInProgress.Should().BeFalse("取り消しは適用ではない。以前はこの旗しか見ていなかった");
        rig.Graft.IsVbaWriting.Should().BeTrue();
        rig.Graft.VbaSync.IsWritingToExcel.Should().BeTrue();
        pane.PushToExcelCommand.CanExecute(null).Should().BeFalse();
        pane.PullFromExcelCommand.CanExecute(null).Should().BeFalse();
        pane.RestoreFromExcelCommand.CanExecute(null).Should().BeFalse();
        pane.DeleteProjectCommand.CanExecute(null).Should().BeFalse("反映中に削除すると、ミラーがごみ箱へ行く途中で同期状態が書かれる");
        pane.RelocateProjectCommand.CanExecute(null).Should().BeFalse();
        pane.ActivateWorkbookCommand.CanExecute(null).Should().BeTrue("前面化は書き込みと競合しない");

        release.SetResult();
        await restoring.ConfigureAwait(true);

        rig.Graft.IsVbaWriting.Should().BeFalse();
        pane.PushToExcelCommand.CanExecute(null).Should().BeTrue();
        pane.DeleteProjectCommand.CanExecute(null).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "不具合9 再現: 反映の最中に閉じようとすると、反映が終わるまで閉じるのを保留する（書き込みの途中でプロセスが終わらない）")]
    public async Task 反映の最中は閉じるのを保留する()
    {
        using var rig = await VbaP3Rig.CreateAsync(withWindow: true).ConfigureAwait(true);
        var (release, restoring) = await StartBlockedRestoreAsync(rig).ConfigureAwait(true);

        rig.H.Window!.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        rig.H.Window.IsVisible.Should().BeTrue("Excel へ書いている最中は閉じない");

        release.SetResult();
        await restoring.ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => !rig.H.Window.IsVisible).ConfigureAwait(true);

        rig.H.Window.IsVisible.Should().BeFalse("反映が終わったら閉じる");
        rig.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original, "書き込みは最後まで終わっている");
    }

    [AvaloniaFact(DisplayName = "不具合9 再現: 反映の最中の設定の変更は保留し、反映が終わってから反映する")]
    public async Task 反映の最中の設定の変更は保留される()
    {
        using var rig = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var (release, restoring) = await StartBlockedRestoreAsync(rig).ConfigureAwait(true);
        var before = rig.Bridge.ComTimeoutMs;

        rig.Graft.UpdateSettings(new Settings { ShowPreview = false, Vba = new VbaSettings { ComTimeoutMs = 9000, CallTimeoutMs = 40000 } });

        rig.Bridge.ComTimeoutMs.Should().Be(before, "書き込みの最中は設定を差し替えない");

        release.SetResult();
        await restoring.ConfigureAwait(true);

        rig.Bridge.ComTimeoutMs.Should().Be(9000);
        rig.Bridge.CallTimeoutMs.Should().Be(40000);
    }

    [AvaloniaFact(DisplayName = "不具合9: 手動の反映（プロジェクトペイン）の最中も「Excel へ書いている最中」になり、同じコマンドをもう一度受け付けない")]
    public async Task 手動の反映の最中も旗が立つ()
    {
        using var rig = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = rig.Graft.ProjectPane;
        File.WriteAllText(rig.MirrorPath(Module1), "Sub A()\r\n  Debug.Print 1\r\nEnd Sub\r\n");
        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        rig.Bridge.BeforeWrite = async () =>
        {
            reached.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        var pushing = ShellHarness.ExecuteAsync(pane.PushToExcelCommand);
        await ShellHarness.WaitUntilAsync(() => reached.Task.IsCompleted).ConfigureAwait(true);

        rig.Graft.IsVbaWriting.Should().BeTrue();
        pane.PushToExcelCommand.CanExecute(null).Should().BeFalse();
        pane.DeleteProjectCommand.CanExecute(null).Should().BeFalse();

        release.SetResult();
        await pushing.ConfigureAwait(true);
        rig.Graft.IsVbaWriting.Should().BeFalse();
        rig.Excel("Module1").Code.Should().Contain("Debug.Print 1");
    }
}
