using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core.Vba;
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
}
