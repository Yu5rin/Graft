using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 「履歴だけを削除…」（プロジェクトの登録とファイルは残し、変更履歴だけを消す）の通しシナリオ。
/// 右クリックメニューからの実行、確認（既定はキャンセル）、止める条件（適用中・中断した適用）、
/// 消したあとの後始末（履歴ペインが空になる・適用直後の「元に戻す」通知が消える・番号が続く）を確かめる。
/// ProjectStore 単体の挙動は tests/Graft.Tests/ProjectStoreClearHistoryTests.cs。
/// </summary>
public class ClearHistoryScenarioTests
{
    private const string Title = "履歴を削除";

    /// <summary>プロジェクトを登録し、2回適用して r1・r2 の履歴を作る。</summary>
    private static async Task<(ShellHarness H, string Id)> CreateWithHistoryAsync()
    {
        var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "1行目\n2行目\n3行目\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await h.ParseAsync("a.txt", "1行目", "1行目（変更後）").ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand).ConfigureAwait(true);
        await h.ParseAsync("a.txt", "2行目", "2行目（変更後）").ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand).ConfigureAwait(true);
        var id = h.Shell.Graft.ProjectPane.SelectedItem!.Project.Id;
        (await new RevisionStore(h.AppPaths).ListAsync(id).ConfigureAwait(true)).Value.Should().HaveCount(2, "前提: 履歴が2件ある");
        return (h, id);
    }

    private static async Task<int> StoredNextRevisionAsync(ShellHarness h, string id)
        => (await new ProjectStore(h.AppPaths).LoadAsync().ConfigureAwait(true)).Value.Single(p => p.Id == id).NextRevision;

    [AvaloniaFact(DisplayName = "履歴だけを削除: 右クリックメニューの項目から実行すると、履歴フォルダと履歴ペインが空になり、登録・ファイル・版の番号は残る")]
    public async Task メニューから実行すると履歴だけが消える()
    {
        var (h, id) = await CreateWithHistoryAsync().ConfigureAwait(true);
        using var _ = h;
        var pane = h.Shell.Graft.ProjectPane;
        var nextBefore = await StoredNextRevisionAsync(h, id).ConfigureAwait(true);
        h.Shell.Graft.History.Items.Should().HaveCount(2, "前提: 履歴ペインに2件出ている");

        var view = new ProjectPane { DataContext = pane };
        var window = new Window { Width = 400, Height = 500, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.GetVisualDescendants().OfType<ListBox>().First(l => l.ContextMenu is not null);
            list.ContextMenu!.Open(list);
            Dispatcher.UIThread.RunJobs();

            var item = list.ContextMenu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "履歴だけを削除…"));
            item.IsEnabled.Should().BeTrue();
            await ShellHarness.ExecuteAsync(item.Command!).ConfigureAwait(true);
        }
        finally
        {
            window.Close();
        }

        var confirm = h.Dialogs.Confirms.Should().ContainSingle(c => c.Title == Title).Subject;
        confirm.Message.Should().StartWith("『project』の変更履歴（2件・合計 ");
        confirm.Message.Should().Contain("MB）を削除します。ファイルそのものは変わりません。削除すると、取り消し・ここまで戻すができなくなります。");
        confirm.Message.Should().NotContain("Excel", "VBA でないプロジェクトには、Excel の一文は添えない");

        Directory.Exists(h.AppPaths.GetProjectBackupDirectory(id)).Should().BeFalse("履歴フォルダ（history.jsonl を含む）は消える");
        h.Shell.Graft.History.Items.Should().BeEmpty("履歴ペインは読み直されて空になる");
        pane.Items.Should().ContainSingle(i => i.Project.Id == id, "プロジェクトの登録は残る");
        File.ReadAllText(h.ProjectPath("a.txt")).Should().Be("1行目（変更後）\n2行目（変更後）\n3行目\n", "ファイルそのものは変わらない");
        (await StoredNextRevisionAsync(h, id).ConfigureAwait(true)).Should().Be(nextBefore, "版の番号は戻さない");
    }

    [AvaloniaFact(DisplayName = "履歴だけを削除: 消したあとの適用は、番号を再利用せず r3 から続く")]
    public async Task 消したあとの番号は続く()
    {
        var (h, id) = await CreateWithHistoryAsync().ConfigureAwait(true);
        using var _ = h;

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.ClearHistoryCommand).ConfigureAwait(true);
        await h.ParseAsync("a.txt", "3行目", "3行目（変更後）").ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand).ConfigureAwait(true);

        var revisions = (await new RevisionStore(h.AppPaths).ListAsync(id).ConfigureAwait(true)).Value;
        revisions.Select(r => r.Manifest.Revision).Should().Equal(new[] { 3 }, "r1・r2 を消したあとも、次は r3");
    }

    [AvaloniaFact(DisplayName = "履歴だけを削除: 確認でキャンセルすれば何も消えない（確認の既定はキャンセル側）")]
    public async Task キャンセルなら何も消えない()
    {
        var (h, id) = await CreateWithHistoryAsync().ConfigureAwait(true);
        using var _ = h;
        h.Dialogs.ConfirmAnswer = (title, _) => title != Title;

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.ClearHistoryCommand).ConfigureAwait(true);

        h.Dialogs.Confirms.Should().Contain(c => c.Title == Title);
        Directory.Exists(h.AppPaths.GetProjectBackupDirectory(id)).Should().BeTrue();
        (await new RevisionStore(h.AppPaths).ListAsync(id).ConfigureAwait(true)).Value.Should().HaveCount(2);
        h.Shell.Graft.History.Items.Should().HaveCount(2);
    }

    [AvaloniaFact(DisplayName = "履歴だけを削除: 適用の実行中は実行せず、理由を知らせる（履歴は残る）")]
    public async Task 適用中は実行できない()
    {
        var (h, id) = await CreateWithHistoryAsync().ConfigureAwait(true);
        using var _ = h;
        await h.ParseAsync("a.txt", "3行目", "3行目（変更後）").ConfigureAwait(true);

        // 適用の途中（確認ダイアログで止まっている間）に、履歴の削除を試みる。
        var release = new TaskCompletionSource<bool>();
        h.Dialogs.ConfirmAnswerAsync = (title, _) => title == "適用の確認" ? release.Task : null;
        var applying = ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand);
        await ShellHarness.WaitUntilAsync(() => h.Shell.Graft.IsApplyInProgress).ConfigureAwait(true);

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.ClearHistoryCommand).ConfigureAwait(true);

        h.Dialogs.Messages.Should().Contain(m => m.Title == "履歴を削除できません" && m.Message.Contains("適用"), "理由を知らせる");
        h.Dialogs.Confirms.Should().NotContain(c => c.Title == Title, "確認まで進まない");
        Directory.Exists(h.AppPaths.GetProjectBackupDirectory(id)).Should().BeTrue();

        release.SetResult(true);
        await applying.ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "履歴だけを削除: 中断した適用（in_progress）が残っているあいだは実行しない")]
    public async Task 中断した適用があると実行しない()
    {
        var (h, id) = await CreateWithHistoryAsync().ConfigureAwait(true);
        using var _ = h;
        var project = h.Shell.Graft.ProjectPane.SelectedItem!.Project;
        var began = await new BackupManager(h.AppPaths).BeginAsync(project.Id, project.Root, new RevisionManifest
        {
            Revision = 9, ProjectId = project.Id, Summary = "中断されたリビジョン", Type = "fix",
            AppliedAt = DateTimeOffset.Now.AddMinutes(5), PatchHash = "dummy", Status = RevisionStatus.InProgress,
        }).ConfigureAwait(true);
        began.IsSuccess.Should().BeTrue();

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.ClearHistoryCommand).ConfigureAwait(true);

        h.Dialogs.Messages.Should().Contain(m => m.Title == "履歴を削除できません" && m.Message.Contains("r9"));
        h.Dialogs.Confirms.Should().NotContain(c => c.Title == Title);
        Directory.Exists(h.AppPaths.GetProjectBackupDirectory(id)).Should().BeTrue("中断した適用の手がかりは消さない");
    }

    [AvaloniaFact(DisplayName = "履歴だけを削除: 適用直後の「元に戻す」通知が出ていれば消す（残すと押しても E405 になる）")]
    public async Task 元に戻す通知が消える()
    {
        var (h, _) = await CreateWithHistoryAsync().ConfigureAwait(true);
        using var _ = h;
        h.Shell.Graft.HasApplyUndoNotice.Should().BeTrue("前提: 適用直後で通知が出ている");
        h.Shell.Graft.LastAppliedRevision.Should().Be(2);

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.ClearHistoryCommand).ConfigureAwait(true);

        h.Shell.Graft.HasApplyUndoNotice.Should().BeFalse();
        h.Shell.Graft.LastAppliedRevision.Should().BeNull();
    }

    [AvaloniaFact(DisplayName = "履歴だけを削除: 履歴が無いプロジェクトでは、削除する物が無いと知らせるだけで確認は出さない")]
    public async Task 履歴が無ければ知らせるだけ()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.ClearHistoryCommand).ConfigureAwait(true);

        h.Dialogs.Messages.Should().Contain(m => m.Title == Title && m.Message.Contains("削除する変更履歴がありません"));
        h.Dialogs.Confirms.Should().NotContain(c => c.Title == Title);
    }

    [AvaloniaFact(DisplayName = "履歴だけを削除: コマンドパレットに「選択中のプロジェクトの履歴だけを削除…」が出る")]
    public async Task コマンドパレットに載る()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);

        h.Shell.CommandPalette.Open();
        h.Shell.CommandPalette.Query = "履歴だけ";

        h.Shell.CommandPalette.Results.Select(r => r.Title).Should().Contain("選択中のプロジェクトの履歴だけを削除…");
    }

    [AvaloniaFact(DisplayName = "履歴だけを削除: VBA プロジェクトでは確認に Excel の一文が添わり、同期状態・Excel 側の控えは消えない")]
    public async Task VBAプロジェクトでは同期記録に触れない()
    {
        using var r = await Vba.VbaP3Rig.CreateAsync().ConfigureAwait(true);
        await r.PasteAsync(Vba.VbaP3Rig.Edit("Modules/Module1.bas", "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);
        var id = r.Project.Id;
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().NotBeEmpty("前提: 履歴がある");
        var syncFile = r.H.AppPaths.GetVbaSyncFilePath(id);
        File.Exists(syncFile).Should().BeTrue("前提: 同期状態がある");
        var rescueDir = r.H.AppPaths.GetVbaRescueDirectory(id);
        Directory.CreateDirectory(rescueDir);
        File.WriteAllText(Path.Combine(rescueDir, "memo.bas"), "' Excel 側の控え");

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.ClearHistoryCommand).ConfigureAwait(true);

        r.H.Dialogs.Confirms.Should().ContainSingle(c => c.Title == Title)
            .Which.Message.Should().EndWith("\nExcel との同期記録と Excel 側の控えは消しません。");
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().BeEmpty();
        File.Exists(syncFile).Should().BeTrue("同期状態（衝突判定・未反映の基準）は残る");
        File.Exists(Path.Combine(rescueDir, "memo.bas")).Should().BeTrue("Excel 側の控えも残る");
        r.Graft.ProjectPane.Items.Should().ContainSingle(i => i.Project.Id == id);
    }
}
