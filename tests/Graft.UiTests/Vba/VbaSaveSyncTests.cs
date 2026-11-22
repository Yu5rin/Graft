using System.Diagnostics;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// 保存時の自動反映（機能 D。仕様書_ブックの情報と依頼の観点 8章・11.2）のシナリオ。実際の起動と同じ依存グラフ
/// （<see cref="ShellHarness"/>）に <see cref="FakeVbaBridge"/> をつなぎ、エディタでミラーのモジュールを編集して
/// 保存（<c>SaveActiveAsync</c>・<c>SaveAllAsync</c>）したときに、そのモジュールだけが Excel へ送られること、
/// 衝突・Excel 側の事情ではダイアログを出さずに通知とバッジだけにすること、保存の通知のハンドラが重い仕事で
/// 止まらないことを確かめる。
/// </summary>
public class VbaSaveSyncTests
{
    private const string Module1 = "Modules/Module1.bas";
    private const string Cls1 = "Classes/Cls1.cls";

    private static async Task<EditorTabViewModel> OpenAsync(VbaP3Rig r, string relative)
        => (await r.H.Shell.Editor.OpenFileAsync(r.MirrorPath(relative)).ConfigureAwait(true)).Value;

    private static void Edit(EditorTabViewModel tab, string marker)
        => tab.Session.Document.Insert(0, $"' {marker}\r\n");

    private static async Task SaveActiveAsync(VbaP3Rig r)
    {
        var saved = await r.H.Shell.Editor.SaveActiveAsync().ConfigureAwait(true);
        saved.IsSuccess.Should().BeTrue();
    }

    /// <summary>保存の通知から、まとめの待ち（500ms）と反映までが終わるのを待つ。</summary>
    private static async Task SettleAsync(VbaP3Rig r)
    {
        await r.Graft.WhenVbaSaveSyncIdleAsync().ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => !r.Graft.IsVbaWriting).ConfigureAwait(true);
        await Task.Delay(30).ConfigureAwait(true);
    }

    private static async Task<VbaP3Rig> CreateWithAutoPushOnSaveAsync(bool on = true)
    {
        var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        if (on) await r.UpdateLinkAsync(l => l with { AutoPushOnSave = true }).ConfigureAwait(true);
        return r;
    }

    [AvaloniaFact(DisplayName = "オフ（既定）: 保存しても Excel に触れない。保存済みで「未反映」に残る")]
    public async Task オフなら触れない()
    {
        using var r = await CreateWithAutoPushOnSaveAsync(on: false).ConfigureAwait(true);
        r.Project.Vba!.AutoPushOnSave.Should().BeFalse("既定はオフ");
        var tab = await OpenAsync(r, Module1).ConfigureAwait(true);
        var callsBefore = r.Bridge.Calls.Count;

        Edit(tab, "編集 1");
        await SaveActiveAsync(r).ConfigureAwait(true);
        await SettleAsync(r).ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true); // まとめの時間（500ms）を超えて待っても、何も起きない。

        r.Bridge.Calls.Count.Should().Be(callsBefore, "Excel への呼び出しが 1 回も起きない");
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original);
        r.Mirror(Module1).Should().Contain("編集 1");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1);
    }

    [AvaloniaFact(DisplayName = "オン: 保存したモジュールだけが反映される（読むのも書くのもそのモジュールだけ）")]
    public async Task 保存したモジュールだけが反映される()
    {
        using var r = await CreateWithAutoPushOnSaveAsync().ConfigureAwait(true);
        var tab = await OpenAsync(r, Module1).ConfigureAwait(true);
        r.Bridge.WriteRequests.Clear();
        r.Bridge.ReadNameRequests.Clear();
        r.Graft.Notice.Dismiss(); // プロジェクト追加時の通知が残っていることがあるので、先に消しておく。

        Edit(tab, "編集 1");
        await SaveActiveAsync(r).ConfigureAwait(true);
        await SettleAsync(r).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("編集 1");
        r.Excel("Cls1").Code.Should().Be(VbaP3Rig.Cls1Original, "保存していないモジュールには触れない");
        r.Bridge.WriteRequests.Should().ContainSingle().Which.Select(w => w.Name).Should().Equal("Module1");
        r.Bridge.ReadNameRequests.Should().ContainSingle("全件読みにならない（8.4）").Which.Should().Equal("Module1");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(0, "バッジの未反映の件数が減る");
        r.H.Dialogs.Messages.Should().BeEmpty("成功したときは通知もダイアログも出さない");
        r.Graft.Notice.IsVisible.Should().BeFalse();
        r.RescueFileNames().Should().ContainSingle(n => n.EndsWith("-auto.txt"), "反映前の自動控えは同じ経路で残る");
    }

    [AvaloniaFact(DisplayName = "連続保存: 1 回の反映にまとまる")]
    public async Task 連続保存は1回にまとまる()
    {
        using var r = await CreateWithAutoPushOnSaveAsync().ConfigureAwait(true);
        var tab = await OpenAsync(r, Module1).ConfigureAwait(true);
        r.Bridge.WriteRequests.Clear();

        for (var i = 1; i <= 3; i++)
        {
            Edit(tab, $"編集 {i}");
            await SaveActiveAsync(r).ConfigureAwait(true);
            await Task.Delay(50).ConfigureAwait(true); // 500ms より十分短い間隔で保存が続く。
        }

        await SettleAsync(r).ConfigureAwait(true);

        r.Bridge.WriteRequests.Should().ContainSingle("3 回の保存が 1 回の反映になる");
        r.Excel("Module1").Code.Should().Contain("編集 3").And.Contain("編集 1");
    }

    [AvaloniaFact(DisplayName = "すべて保存: 複数のモジュールが 1 回の反映にまとまる")]
    public async Task すべて保存は1回にまとまる()
    {
        using var r = await CreateWithAutoPushOnSaveAsync().ConfigureAwait(true);
        var module = await OpenAsync(r, Module1).ConfigureAwait(true);
        var cls = await OpenAsync(r, Cls1).ConfigureAwait(true);
        r.Bridge.WriteRequests.Clear();
        r.Bridge.ReadNameRequests.Clear();

        Edit(module, "編集 A");
        Edit(cls, "編集 B");
        var saved = await r.H.Shell.Editor.SaveAllAsync().ConfigureAwait(true);
        saved.IsSuccess.Should().BeTrue();
        await SettleAsync(r).ConfigureAwait(true);

        r.Bridge.WriteRequests.Should().ContainSingle().Which.Select(w => w.Name).Should().BeEquivalentTo(new[] { "Module1", "Cls1" });
        r.Bridge.ReadNameRequests.Should().ContainSingle().Which.Should().BeEquivalentTo(new[] { "Module1", "Cls1" });
    }

    [AvaloniaFact(DisplayName = "ミラーの外のファイル・同期対象でないファイルを保存しても、何も送らない")]
    public async Task 対象外のファイルは送らない()
    {
        using var r = await CreateWithAutoPushOnSaveAsync().ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath("memo.txt"), "メモ\r\n");
        var tab = await OpenAsync(r, "memo.txt").ConfigureAwait(true);
        var callsBefore = r.Bridge.Calls.Count;

        Edit(tab, "追記");
        await SaveActiveAsync(r).ConfigureAwait(true);
        await SettleAsync(r).ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true);

        r.Bridge.Calls.Count.Should().Be(callsBefore);
    }

    [AvaloniaFact(DisplayName = "衝突: 送らず、ダイアログも出さない。バッジに「衝突 1」が出て、衝突なしで反映できたら消える")]
    public async Task 衝突は送らずバッジに出る()
    {
        using var r = await CreateWithAutoPushOnSaveAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var tab = await OpenAsync(r, Module1).ConfigureAwait(true);
        r.Excel("Module1").Code = "Sub A()\r\n  ' VBE で編集\r\nEnd Sub";
        r.Bridge.WriteRequests.Clear();

        Edit(tab, "Graft で編集");
        await SaveActiveAsync(r).ConfigureAwait(true);
        await SettleAsync(r).ConfigureAwait(true);

        r.Bridge.WriteRequests.Should().BeEmpty("衝突は送らない");
        r.Excel("Module1").Code.Should().Contain("VBE で編集").And.NotContain("Graft で編集");
        r.Choices.Should().BeEmpty();
        r.Conflicts.Should().BeEmpty("保存のたびに割り込まない: 衝突ダイアログを出さない");
        r.H.Dialogs.Messages.Should().BeEmpty();
        pane.VbaSaveConflictCount(r.Project.Id).Should().Be(1);
        var item = pane.Items.First(i => i.Project.Id == r.Project.Id);
        item.VbaStatusText.Should().Contain("衝突 1");
        item.VbaTooltip.Should().Contain("衝突");

        // 手動の「Excel へ反映」の衝突画面で「Graft 側で上書き」を選ぶと、印も消える。
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.OverwriteExcel);
        await ShellHarness.ExecuteAsync(pane.PushToExcelCommand).ConfigureAwait(true);

        r.Conflicts.Should().ContainSingle("手動の反映では衝突画面で解決できる");
        r.Excel("Module1").Code.Should().Contain("Graft で編集");
        pane.VbaSaveConflictCount(r.Project.Id).Should().Be(0);
        pane.Items.First(i => i.Project.Id == r.Project.Id).VbaStatusText.Should().NotContain("衝突");
    }

    [AvaloniaFact(DisplayName = "衝突の印は、同じモジュールが衝突なしで反映できた時点で消える")]
    public async Task 衝突の印は次の成功で消える()
    {
        using var r = await CreateWithAutoPushOnSaveAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var tab = await OpenAsync(r, Module1).ConfigureAwait(true);
        r.Excel("Module1").Code = "Sub A()\r\n  ' VBE で編集\r\nEnd Sub";

        Edit(tab, "編集 1");
        await SaveActiveAsync(r).ConfigureAwait(true);
        await SettleAsync(r).ConfigureAwait(true);
        pane.VbaSaveConflictCount(r.Project.Id).Should().Be(1);

        // 利用者が Excel 側を取り込み直して（Pull）衝突を解いたあと、もう一度保存する。
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.AdoptExcel);
        await ShellHarness.ExecuteAsync(pane.PullFromExcelCommand).ConfigureAwait(true);
        pane.VbaSaveConflictCount(r.Project.Id).Should().Be(0, "手動の取込で衝突を解決すると印が消える");

        await r.H.Shell.Editor.ReloadIfOpenAsync(new[] { r.MirrorPath(Module1) }).ConfigureAwait(true);
        Edit(tab, "編集 2");
        await SaveActiveAsync(r).ConfigureAwait(true);
        await SettleAsync(r).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("編集 2");
        pane.VbaSaveConflictCount(r.Project.Id).Should().Be(0);
    }

    [AvaloniaFact(DisplayName = "E813（マクロの実行中）: 通知だけでダイアログは出ない。ファイルは保存済みで「未反映」のまま")]
    public async Task マクロ実行中は通知だけ()
    {
        using var r = await CreateWithAutoPushOnSaveAsync().ConfigureAwait(true);
        var tab = await OpenAsync(r, Module1).ConfigureAwait(true);
        r.Bridge.Get(VbaP3Rig.Book)!.DesignMode = false;
        r.Bridge.WriteRequests.Clear();

        Edit(tab, "編集 1");
        await SaveActiveAsync(r).ConfigureAwait(true);
        await SettleAsync(r).ConfigureAwait(true);

        r.H.Dialogs.Messages.Should().BeEmpty("ダイアログを出さない");
        r.Choices.Should().BeEmpty();
        r.Conflicts.Should().BeEmpty();
        r.Graft.Notice.IsVisible.Should().BeTrue();
        r.Graft.Notice.Text.Should().Contain("保存しました").And.Contain("自動反映は行いませんでした").And.Contain("E813");
        r.Mirror(Module1).Should().Contain("編集 1", "ファイルは保存済み");
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original);
        r.Bridge.WriteRequests.Should().ContainSingle("書こうとしたが、1 件も書かずに全体が失敗した（自動の再試行はしない）");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1);
    }

    [AvaloniaFact(DisplayName = "E805（Excel が応答しない）: 通知だけ。保存した Graft の編集は固まらない")]
    public async Task 応答しないときは通知だけ()
    {
        using var r = await CreateWithAutoPushOnSaveAsync().ConfigureAwait(true);
        var tab = await OpenAsync(r, Module1).ConfigureAwait(true);
        r.Bridge.Configure(5000, 300);
        r.Bridge.HangOperations.Add("Read");

        Edit(tab, "編集 1");
        await SaveActiveAsync(r).ConfigureAwait(true);
        await SettleAsync(r).ConfigureAwait(true);

        r.H.Dialogs.Messages.Should().BeEmpty();
        r.Graft.Notice.Text.Should().Contain("E805");
        r.Mirror(Module1).Should().Contain("編集 1");
    }

    [AvaloniaFact(DisplayName = "ブックが開いていない（E804）: 通知だけ")]
    public async Task ブックが閉じていると通知だけ()
    {
        using var r = await CreateWithAutoPushOnSaveAsync().ConfigureAwait(true);
        var tab = await OpenAsync(r, Module1).ConfigureAwait(true);
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);

        Edit(tab, "編集 1");
        await SaveActiveAsync(r).ConfigureAwait(true);
        await SettleAsync(r).ConfigureAwait(true);

        r.H.Dialogs.Messages.Should().BeEmpty();
        r.Choices.Should().BeEmpty("「ファイルだけ適用する」のような選択ダイアログも出さない");
        r.Graft.Notice.Text.Should().Contain("E804");
    }

    [AvaloniaFact(DisplayName = "E811（Shift_JIS で表せない文字）: 通知だけ。モジュール名が添えられる")]
    public async Task 文字コードの問題は通知だけ()
    {
        using var r = await CreateWithAutoPushOnSaveAsync().ConfigureAwait(true);
        var tab = await OpenAsync(r, Module1).ConfigureAwait(true);

        Edit(tab, "絵文字 😀");
        await SaveActiveAsync(r).ConfigureAwait(true);
        await SettleAsync(r).ConfigureAwait(true);

        r.H.Dialogs.Messages.Should().BeEmpty();
        r.Graft.Notice.Text.Should().Contain("E811").And.Contain("Module1");
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original);
    }

    [AvaloniaFact(DisplayName = "適用中の保存: 何もしない（適用後の反映がまとめて扱う）")]
    public async Task 適用中の保存は何もしない()
    {
        using var r = await CreateWithAutoPushOnSaveAsync().ConfigureAwait(true);
        var cls = await OpenAsync(r, Cls1).ConfigureAwait(true);
        var inWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        r.Bridge.BeforeWrite = async () =>
        {
            inWrite.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        await r.PasteAsync(VbaP3Rig.Edit("Modules/Module1.bas", "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        var applying = r.ApplyAsync();
        await inWrite.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        r.Graft.IsApplyInProgress.Should().BeTrue("前提: 適用の途中（Excel への書き込み待ち）");

        Edit(cls, "適用中の編集");
        await SaveActiveAsync(r).ConfigureAwait(true);
        release.SetResult();
        await applying.ConfigureAwait(true);
        await SettleAsync(r).ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("hello", "適用後の反映は通常どおり");
        r.Excel("Cls1").Code.Should().Be(VbaP3Rig.Cls1Original, "適用中に保存したものは、保存の通知からは送らない");
        r.Mirror(Cls1).Should().Contain("適用中の編集");
    }

    [AvaloniaFact(DisplayName = "手動の反映の最中の保存: 手動の反映が終わってから送る（同時に書かない）")]
    public async Task 手動反映中の保存は終わってから送る()
    {
        using var r = await CreateWithAutoPushOnSaveAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var cls = await OpenAsync(r, Cls1).ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Module1), "Sub A()\r\n  ' 手動で送る\r\nEnd Sub\r\n");

        var inFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        r.Bridge.BeforeWrite = async () =>
        {
            if (Interlocked.Increment(ref writes) != 1) return;
            inFirstWrite.TrySetResult();
            await release.Task.ConfigureAwait(false);
        };

        pane.PushToExcelCommand.Execute(null);
        await inFirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        r.Graft.IsVbaWriting.Should().BeTrue("前提: 手動の反映が Excel へ書いている最中");

        Edit(cls, "反映中の保存");
        await SaveActiveAsync(r).ConfigureAwait(true);
        await Task.Delay(700).ConfigureAwait(true); // まとめの時間を過ぎても、先の書き込みが終わるまで 2 つ目は走らない。
        writes.Should().Be(1, "同時に 2 つ書かない");

        release.SetResult();
        await ShellHarness.WaitUntilAsync(() => r.Excel("Cls1").Code.Contains("反映中の保存")).ConfigureAwait(true);
        await SettleAsync(r).ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("手動で送る");
        r.Excel("Cls1").Code.Should().Contain("反映中の保存", "手動の反映が終わってから、保存したものが送られる");
        writes.Should().Be(2);
    }

    [AvaloniaFact(DisplayName = "保存→反映の遅延: Excel が 10 秒遅くても、保存の通知のハンドラは 50ms 以内に戻る（保存を遅くしない）")]
    public async Task ハンドラは重い仕事を同期でしない()
    {
        using var r = await CreateWithAutoPushOnSaveAsync().ConfigureAwait(true);
        var tab = await OpenAsync(r, Module1).ConfigureAwait(true);

        // ウォームアップ（最初の呼び出しの JIT・型の初期化を、計測に含めない）。
        Edit(tab, "ウォームアップ");
        await SaveActiveAsync(r).ConfigureAwait(true);
        await SettleAsync(r).ConfigureAwait(true);

        r.Bridge.Delay = TimeSpan.FromSeconds(10);
        var callsBefore = r.Bridge.Calls.Count;
        Edit(tab, "計測");
        var saved = await r.H.Shell.Editor.SaveActiveAsync().ConfigureAwait(true); // ハンドラ込みの保存。
        saved.IsSuccess.Should().BeTrue();

        // ハンドラ単体の所要時間。同じタブの保存通知を直接もう一度流す。
        var watch = Stopwatch.StartNew();
        r.Graft.OnEditorTabSavedForVba(this, tab);
        watch.Stop();

        watch.ElapsedMilliseconds.Should().BeLessThan(50, "ハンドラは判定とキューへの追加だけ。Excel の遅さが保存に響かない");
        r.Bridge.Calls.Count.Should().Be(callsBefore, "ハンドラの時点では、Excel には 1 回も触れていない（まとめの待ちの先で行う）");
    }

    [AvaloniaFact(DisplayName = "VBA でないプロジェクト・オフのプロジェクトでは、ハンドラは何もしない（キューも作らない）")]
    public async Task 通常のプロジェクトでは何もしない()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        File.WriteAllText(h.ProjectPath("a.txt"), "x\n");
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var tab = (await h.Shell.Editor.OpenFileAsync(h.ProjectPath("a.txt")).ConfigureAwait(true)).Value;

        tab.Session.Document.Insert(0, "y");
        var saved = await h.Shell.Editor.SaveActiveAsync().ConfigureAwait(true);

        saved.IsSuccess.Should().BeTrue();
        h.Shell.Graft.Notice.IsVisible.Should().BeFalse();
        var idle = h.Shell.Graft.WhenVbaSaveSyncIdleAsync();
        idle.IsCompleted.Should().BeTrue("溜まっているものが無い");
    }

    [AvaloniaFact(DisplayName = "右クリックのチェック項目: 切り替えが VbaLink に保存され、選び直しても保たれる")]
    public async Task チェック項目の保存()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        pane.IsAutoPushOnSaveChecked.Should().BeFalse("既定はオフ");

        pane.IsAutoPushOnSaveChecked = true;
        await pane.WaitForVbaOptionAsync().ConfigureAwait(true);

        pane.IsAutoPushOnSaveChecked.Should().BeTrue();
        pane.SelectedItem!.Project.Vba!.AutoPushOnSave.Should().BeTrue();
        (await new Graft.Features.ProjectStore(r.H.AppPaths).LoadAsync().ConfigureAwait(true)).Value
            .Single(p => p.Id == r.Project.Id).Vba!.AutoPushOnSave.Should().BeTrue("projects.json に保存される");
    }
}
