using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.UiTests.TestSupport;

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
}
