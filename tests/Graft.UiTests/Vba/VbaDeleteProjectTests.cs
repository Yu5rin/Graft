using Xunit;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Features;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// 実機報告「Excel VBA 連携のプロジェクトを右クリックの「削除」で削除しても、一覧にその場で残る」の再現と回帰のテスト。
/// FakeVbaBridge を入れた実際の起動と同じ依存グラフ（<see cref="VbaP3Rig"/>）で、場面を変えて削除し、
/// 一覧（<c>Items</c>）と projects.json の両方から消えることを確かめる。
/// </summary>
public class VbaDeleteProjectTests
{
    private static async Task<bool> StoredAsync(VbaP3Rig r, string id)
        => (await new ProjectStore(r.H.AppPaths).LoadAsync().ConfigureAwait(true)).Value.Any(p => p.Id == id);

    private static async Task SettleAsync()
    {
        // 削除のあとに走りうる非同期の後続（バッジの数え直しなど）が落ち着くのを待つ。
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(30).ConfigureAwait(true);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTheory(DisplayName = "VBA プロジェクトの削除: 選択中でも、一覧と projects.json からその場で消える（履歴・ミラーの選び方によらない）")]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task 選択中のVBAプロジェクトを削除すると消える(bool first, bool second)
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var calls = 0;
        r.H.Dialogs.ThreeWayAnswer = (_, _) => ++calls == 1 ? first : second;
        pane.SelectedItem.Should().NotBeNull();
        pane.SelectedItem!.Project.Id.Should().Be(r.Project.Id);

        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        calls.Should().Be(2, "VBA プロジェクトは3択を2回聞く");
        pane.Items.Should().NotContain(i => i.Project.Id == r.Project.Id, "一覧からその場で消える");
        (await StoredAsync(r, r.Project.Id).ConfigureAwait(true)).Should().BeFalse("projects.json からも消える");
    }

    [AvaloniaFact(DisplayName = "VBA プロジェクトの削除: ほかのプロジェクトが並んでいても消える")]
    public async Task 通常のプロジェクトと並んでいても消える()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        await pane.SelectAsync(pane.Items.First(i => i.Project.Vba is not null)).ConfigureAwait(true);

        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        pane.Items.Should().NotContain(i => i.Project.Id == r.Project.Id);
        (await StoredAsync(r, r.Project.Id).ConfigureAwait(true)).Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "VBA プロジェクトの削除: バッジの数え直しが走っている最中でも消える")]
    public async Task バッジの数え直しと並行しても消える()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;

        pane.RequestVbaBadgeRefresh(checkWorkbookOpen: true);
        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        pane.RequestVbaBadgeRefresh(checkWorkbookOpen: true);
        await SettleAsync().ConfigureAwait(true);

        pane.Items.Should().NotContain(i => i.Project.Id == r.Project.Id);
        (await StoredAsync(r, r.Project.Id).ConfigureAwait(true)).Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "VBA プロジェクトの削除: 唯一のプロジェクトでも消え、一覧は空になる")]
    public async Task 唯一のVBAプロジェクトを削除すると空になる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;

        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        pane.Items.Should().BeEmpty();
        pane.SelectedItem.Should().BeNull();
    }

    [AvaloniaTheory(DisplayName = "VBA プロジェクトの削除: 実際の一覧（ListBox の双方向束縛）を表示していても消える")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 一覧を表示していても消える(bool plainFirst)
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: plainFirst).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var view = new Graft.Views.ProjectPane { DataContext = pane };
        var window = new Avalonia.Controls.Window { Width = 400, Height = 500, Content = view };
        window.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
            await SettleAsync().ConfigureAwait(true);

            pane.Items.Should().NotContain(i => i.Project.Id == r.Project.Id);
            (await StoredAsync(r, r.Project.Id).ConfigureAwait(true)).Should().BeFalse();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>ミラーの Module1 を開いて、未保存の編集を入れる。</summary>
    private static async Task<EditorTabViewModel> OpenDirtyTabAsync(VbaP3Rig r)
    {
        var opened = await r.H.Shell.Editor.OpenFileAsync(r.MirrorPath("Modules/Module1.bas")).ConfigureAwait(true);
        opened.IsSuccess.Should().BeTrue();
        var tab = opened.Value;
        tab.Session.Document.Insert(tab.Session.Document.TextLength, "' 未保存の編集\r\n");
        tab.Session.IsModified.Should().BeTrue("前提: 未保存のタブがある");
        return tab;
    }

    [AvaloniaTheory(DisplayName = "VBA プロジェクトの削除: 未保存のタブの保存確認で「キャンセル」したら、削除自体を中止する（消したのに選択中で残らない）")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 未保存の確認でキャンセルすると削除しない(bool plainFirst)
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: plainFirst).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var tab = await OpenDirtyTabAsync(r).ConfigureAwait(true);
        r.H.Dialogs.ThreeWayAnswer = (title, _) => title == "変更の保存" ? null : true;

        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        pane.Items.Should().Contain(i => i.Project.Id == r.Project.Id, "キャンセルなら何も消さない");
        pane.SelectedItem!.Project.Id.Should().Be(r.Project.Id);
        (await StoredAsync(r, r.Project.Id).ConfigureAwait(true)).Should().BeTrue("登録も残る");
        r.H.Shell.Editor.Tabs.Should().Contain(tab, "タブも閉じない");
        pane.Items.Count(i => i.Project.Id == r.Project.Id).Should().Be(1, "一覧に1件だけ残る");
    }

    [AvaloniaTheory(DisplayName = "VBA プロジェクトの削除: 未保存のタブがあっても「保存しない」なら、タブを閉じて一覧と projects.json から消える")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 未保存のタブがあっても保存しないなら消える(bool plainFirst)
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: plainFirst).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var tab = await OpenDirtyTabAsync(r).ConfigureAwait(true);
        r.H.Dialogs.ThreeWayAnswer = (title, _) => title == "変更の保存" ? false : true;

        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        pane.Items.Should().NotContain(i => i.Project.Id == r.Project.Id);
        pane.SelectedItem?.Project.Id.Should().NotBe(r.Project.Id);
        (await StoredAsync(r, r.Project.Id).ConfigureAwait(true)).Should().BeFalse();
        r.H.Shell.Editor.Tabs.Should().NotContain(tab);
    }

    [AvaloniaFact(DisplayName = "プロジェクトの削除: 消した Id のタブ構成・ペイン幅（layout の projectPaneWidths）も捨て、削除後の切り替えで作り直さない")]
    public async Task 削除すると画面の記憶も消える()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var tab = await OpenDirtyTabAsync(r).ConfigureAwait(true);
        r.H.Dialogs.ThreeWayAnswer = (title, _) => title == "変更の保存" ? false : true; // 保存しないで閉じる。
        // 前提: プロジェクトを切り替えるとタブ構成が記憶される（切り替え前の状態の書き戻し）。
        r.Graft.Layout.ProjectPaneWidths[r.Project.Id] = new Graft.ViewModels.ProjectPaneLayout();
        r.Graft.Layout.ProjectPaneWidths.Should().ContainKey(r.Project.Id);

        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        pane.Items.Should().NotContain(i => i.Project.Id == r.Project.Id);
        r.Graft.Layout.ProjectPaneWidths.Should().NotContainKey(r.Project.Id,
            "同じ場所を登録し直すと同じ Id になるので、残すと前のタブが開き直る");
        r.H.Shell.Editor.Tabs.Should().NotContain(tab);
    }

    /// <summary>
    /// 削除した VBA プロジェクトが「選んで切り替え中」のまま削除が済むところまで進める。
    /// 手順: 通常のプロジェクトを選び直して落ち着かせる → 切り替えの完了を止める口（gate）を仕掛ける →
    /// VBA プロジェクトを選ぶ（切り替えがツリーの読み込みのあとで止まる）→ 削除する。
    /// 戻り値の gate を完了させると、止めていた切り替えが、削除のあとで続きを実行する。
    /// </summary>
    private static async Task<TaskCompletionSource> DeleteWhileSwitchPendingAsync(VbaP3Rig r)
    {
        var pane = r.Graft.ProjectPane;
        await pane.SelectAsync(pane.Items.First(i => i.Project.Vba is null)).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);
        r.H.Dialogs.ThreeWayAnswer = (_, _) => true;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        r.H.Shell.AfterProjectTreeLoadedAsync = _ => gate.Task;
        await pane.SelectAsync(pane.Items.First(i => i.Project.Id == r.Project.Id)).ConfigureAwait(true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        return gate;
    }

    [AvaloniaFact(DisplayName = "プロジェクトの削除: 選んだ切り替えが削除のあとまで終わらなくても、消した Id の記憶を作り直さない（遅い CI で出た競合）")]
    public async Task 切り替えの完了が削除より遅くても記憶を作り直さない()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;

        var gate = await DeleteWhileSwitchPendingAsync(r).ConfigureAwait(true);
        pane.Items.Should().NotContain(i => i.Project.Id == r.Project.Id, "前提: 削除は済んだ");

        // 止めていた切り替えを、削除のあとで完了させる。ここで消した Id の項目や _currentProjectId を戻してはいけない。
        gate.SetResult();
        await SettleAsync().ConfigureAwait(true);
        r.H.Shell.Graft.Layout.ProjectPaneWidths.Should().NotContainKey(r.Project.Id,
            "遅れて終わった切り替えが、消した Id の項目を作り直してはいけない");

        // 削除のあとの切り替え（削除したプロジェクトを「切り替え前」として書き戻す経路）でも、作り直さない。
        r.H.Shell.AfterProjectTreeLoadedAsync = null;
        r.H.Shell.CaptureCurrentProjectState();
        await SettleAsync().ConfigureAwait(true);
        r.H.Shell.Graft.Layout.ProjectPaneWidths.Should().NotContainKey(r.Project.Id);
    }

    [AvaloniaFact(DisplayName = "プロジェクトの削除: 同じ場所を登録し直したら、以後は普通にタブ構成が記憶される（削除済みの印の外し忘れを防ぐ）")]
    public async Task 登録し直すと再びタブ構成が記憶される()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var shell = r.H.Shell;
        var oldId = r.Project.Id;

        var gate = await DeleteWhileSwitchPendingAsync(r).ConfigureAwait(true);
        gate.SetResult();
        shell.AfterProjectTreeLoadedAsync = null;
        await SettleAsync().ConfigureAwait(true);
        shell.Graft.Layout.ProjectPaneWidths.Should().NotContainKey(oldId, "前提: 削除で記憶は消えている");

        // 同じブックを登録し直す（同じ場所なので同じ Id になる）。
        var again = await r.ReAddWorkbookAsync().ConfigureAwait(true);
        again.Id.Should().Be(oldId, "前提: 登録し直すと同じ Id");
        await SettleAsync().ConfigureAwait(true);

        // タブを開いて、ほかのプロジェクトへ切り替える。切り替え前の構成が、登録し直した Id へ記憶されること。
        var opened = await shell.Editor.OpenFileAsync(r.MirrorPath("Modules/Module1.bas")).ConfigureAwait(true);
        opened.IsSuccess.Should().BeTrue();
        await pane.SelectAsync(pane.Items.First(i => i.Project.Vba is null)).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        shell.Graft.Layout.ProjectPaneWidths.Should().ContainKey(oldId, "登録し直したあとは、普通に記憶される");
        shell.Graft.Layout.ProjectPaneWidths[oldId].OpenTabs.Should().Contain(t => t.RelativePath.EndsWith("Module1.bas"));
    }
}
