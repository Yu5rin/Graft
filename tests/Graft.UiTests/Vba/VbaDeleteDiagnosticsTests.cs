using Xunit;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// 「削除したのに一覧に残る」の実機報告に備えた診断（ログ <c>project-remove</c>）・成功の通知・残ったときの守りのテスト。
/// 原因そのものの修正ではなく、次に利用者が試したときに、どこで止まったかをログから追えることを確かめる。
/// 削除の結果（一覧と projects.json から消える）自体は <see cref="VbaDeleteProjectTests"/> が見ている。
/// </summary>
public class VbaDeleteDiagnosticsTests
{
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(30).ConfigureAwait(true);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>テスト用のロガーを配線する。ログは非同期に書かれるので、読む側（<see cref="ReadLogAsync"/>）が閉じて書き切らせる。</summary>
    private static Logger AttachLogger(VbaP3Rig r)
    {
        var logger = new Logger(r.H.AppPaths, autoCleanupOnStart: false);
        r.Graft.Logger = logger;
        return logger;
    }

    private static async Task<string> ReadLogAsync(VbaP3Rig r, Logger logger)
    {
        await logger.DisposeAsync().ConfigureAwait(true);
        var path = r.H.AppPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        return File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(true) : string.Empty;
    }

    private static async Task<bool> StoredAsync(VbaP3Rig r, string id)
        => (await new ProjectStore(r.H.AppPaths).LoadAsync().ConfigureAwait(true)).Value.Any(p => p.Id == id);

    [AvaloniaFact(DisplayName = "削除の診断: 成功すると通知が出て、ログに開始・選ばれた内容・成功・一覧に残っていないことが残る")]
    public async Task 成功すると通知とログが残る()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var logger = AttachLogger(r);
        var pane = r.Graft.ProjectPane;
        var name = pane.SelectedItem!.Project.DisplayName;
        r.H.Dialogs.ThreeWayAnswer = (_, _) => false; // 履歴も削除する／ミラーと同期状態も削除する。

        r.Graft.Notice.Dismiss(); // 追加時などの通知が残っていると、削除の通知の有無を見分けられない。
        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        pane.Items.Should().NotContain(i => i.Project.Id == r.Project.Id);
        r.Graft.Notice.IsVisible.Should().BeTrue();
        r.Graft.Notice.Text.Should().Contain($"「{name}」を一覧から外しました。").And.Contain("履歴も削除しました。");

        var log = await ReadLogAsync(r, logger).ConfigureAwait(true);
        log.Should().Contain("project-remove").And.Contain("削除を開始しました").And.Contain(r.Project.Id);
        log.Should().Contain("履歴も削除=はい").And.Contain("登録の削除は成功しました");
        log.Should().Contain("一覧に残っている=いいえ");
    }

    [AvaloniaFact(DisplayName = "削除の診断: 履歴を残す選択なら、通知に「履歴も削除しました」は付かない")]
    public async Task 履歴を残すなら通知に履歴の句が付かない()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        r.H.Dialogs.ThreeWayAnswer = (_, _) => true; // 履歴は残す／登録だけ外す。

        r.Graft.Notice.Dismiss(); // 追加時などの通知が残っていると、削除の通知の有無を見分けられない。
        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        r.Graft.Notice.Text.Should().EndWith("を一覧から外しました。").And.NotContain("履歴も削除しました");
    }

    [AvaloniaFact(DisplayName = "削除の診断: 1つ目の確認でキャンセルすると、何も消さず通知も出さず、その理由がログに残る")]
    public async Task 一つ目の確認でキャンセルすると理由が残る()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var logger = AttachLogger(r);
        var pane = r.Graft.ProjectPane;
        r.H.Dialogs.ThreeWayAnswer = (_, _) => null;

        r.Graft.Notice.Dismiss(); // 追加時などの通知が残っていると、削除の通知の有無を見分けられない。
        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        pane.Items.Should().Contain(i => i.Project.Id == r.Project.Id);
        (await StoredAsync(r, r.Project.Id).ConfigureAwait(true)).Should().BeTrue();
        r.Graft.Notice.IsVisible.Should().BeFalse("何も消していないので、「外しました」とは知らせない");

        var log = await ReadLogAsync(r, logger).ConfigureAwait(true);
        log.Should().Contain("削除を開始しました").And.Contain("1つ目の確認（履歴の扱い）でキャンセル");
        log.Should().NotContain("登録の削除は成功しました");
    }

    [AvaloniaFact(DisplayName = "削除の診断: ミラーと同期状態の確認でキャンセルすると、何も消さず、その理由がログに残る")]
    public async Task 二つ目の確認でキャンセルすると理由が残る()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var logger = AttachLogger(r);
        var pane = r.Graft.ProjectPane;
        var calls = 0;
        r.H.Dialogs.ThreeWayAnswer = (_, _) => ++calls == 1 ? true : null;

        r.Graft.Notice.Dismiss(); // 追加時などの通知が残っていると、削除の通知の有無を見分けられない。
        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        calls.Should().Be(2);
        pane.Items.Should().Contain(i => i.Project.Id == r.Project.Id);
        (await StoredAsync(r, r.Project.Id).ConfigureAwait(true)).Should().BeTrue();
        r.Graft.Notice.IsVisible.Should().BeFalse();

        var log = await ReadLogAsync(r, logger).ConfigureAwait(true);
        log.Should().Contain("VBA=はい").And.Contain("ミラーと同期状態の確認でキャンセル");
    }

    [AvaloniaFact(DisplayName = "削除の診断: 未保存タブの確認でキャンセルすると、何も消さず通知も出さず、その理由がログに残る")]
    public async Task 未保存の確認でキャンセルすると理由が残る()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var logger = AttachLogger(r);
        var pane = r.Graft.ProjectPane;
        await pane.SelectAsync(pane.Items.First(i => i.Project.Id == r.Project.Id)).ConfigureAwait(true);
        var opened = await r.H.Shell.Editor.OpenFileAsync(r.MirrorPath("Modules/Module1.bas")).ConfigureAwait(true);
        var tab = opened.Value;
        tab.Session.Document.Insert(tab.Session.Document.TextLength, "' 未保存の編集\r\n");
        r.H.Dialogs.ThreeWayAnswer = (title, _) => title == "変更の保存" ? null : true;

        r.Graft.Notice.Dismiss(); // 追加時などの通知が残っていると、削除の通知の有無を見分けられない。
        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        pane.Items.Should().Contain(i => i.Project.Id == r.Project.Id);
        r.Graft.Notice.IsVisible.Should().BeFalse();

        var log = await ReadLogAsync(r, logger).ConfigureAwait(true);
        log.Should().Contain("未保存タブの確認（切り替えの確認）でキャンセル");
    }

    [AvaloniaFact(DisplayName = "削除の診断: 登録の削除が成功したのに一覧に残ったときは、黙らずダイアログで知らせ、ログに error を残し、成功の通知は出さない")]
    public async Task 一覧に残ったときは黙らず知らせる()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var logger = AttachLogger(r);
        var pane = r.Graft.ProjectPane;
        await pane.SelectAsync(pane.Items.First(i => i.Project.Vba is null)).ConfigureAwait(true);
        var target = pane.SelectedItem!.Project;
        r.H.Dialogs.ThreeWayAnswer = (_, _) => true;

        // 実機で起きていることの再現ではない（原因が分かっていないため）。「登録を外したあと、一覧を読み直す前に、
        // 同じ場所が登録し直された」状況を作って、守りの分岐（成功を返したのに一覧に残る）だけを確かめる。
        pane.ProjectRemoved += (_, _) =>
            Task.Run(() => new ProjectStore(r.H.AppPaths).RegisterAsync(target.Root, target.Name)).GetAwaiter().GetResult();

        r.Graft.Notice.Dismiss(); // 追加時などの通知が残っていると、削除の通知の有無を見分けられない。
        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);
        await SettleAsync().ConfigureAwait(true);

        pane.Items.Should().Contain(i => i.Project.Id == target.Id, "前提: 一覧に残っている");
        r.H.Dialogs.Messages.Should().Contain(m => m.Title == "プロジェクトを一覧から外せませんでした" && m.Message.Contains("logs"));
        r.Graft.Notice.IsVisible.Should().BeFalse("外れていないのに「外しました」と知らせない");

        var log = await ReadLogAsync(r, logger).ConfigureAwait(true);
        log.Should().Contain("\"level\":\"error\"").And.Contain("一覧の読み直しのあとも残っています");
    }
}
