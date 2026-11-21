using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// Excel VBA 連携 段階 P4 のシナリオ（仕様書_VBA連携 7.2・10.2）のうち、コンテキスト収集の自動 Pull。
/// 走査の前（<c>InitializeAsync</c>／<c>RefreshAsync</c>）と、窓を開かない依頼文コピーの経路で、Excel の最新が取り込まれる。
/// </summary>
public class VbaP4ContextCollectTests
{
    private const string Module1 = "Modules/Module1.bas";

    private const string ExcelEdited = "Sub A()\r\n  MsgBox \"edited in excel\"\r\nEnd Sub";

    [AvaloniaFact(DisplayName = "コンテキスト収集: InitializeAsync の走査の前に Excel の最新が取り込まれる（履歴には残さない）")]
    public async Task Initializeの前にPullされる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Excel("Module1").Code = ExcelEdited;

        await r.Graft.ContextCollect!.InitializeAsync().ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("edited in excel");
        r.Graft.ContextCollect.Files.Should().NotBeEmpty("取り込みで収集は止まらない");
        r.Graft.ContextCollect.StatusMessage.Should().BeNull();
        (await r.RevisionsAsync().ConfigureAwait(true)).Should().BeEmpty("Pull は履歴に残さない");
    }

    [AvaloniaFact(DisplayName = "コンテキスト収集: 「再走査」（RefreshAsync）でも、走査の前に取り込む")]
    public async Task Refreshの前にもPullされる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        r.Excel("Module1").Code = ExcelEdited;
        await ShellHarness.ExecuteAsync(context.RefreshCommand).ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("edited in excel");
    }

    [AvaloniaFact(DisplayName = "コンテキスト収集: 依頼文コピー（窓を開かない経路）も最新を取り込んでから組み立てる。走査済みでも取り込む")]
    public async Task 依頼文コピーでもPullされる()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Excel("Module1").Code = ExcelEdited;

        // 初回（未走査）: QuickCopy → RefreshAsync → Context.InitializeAsync の経路。
        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);
        r.Mirror(Module1).Should().Contain("edited in excel");

        // 2回目（走査済み）: ドロップダウンを開くたびの RefreshAsync でも、最新を取り込む。
        r.Excel("Module1").Code = "Sub A()\r\n  MsgBox \"second edit\"\r\nEnd Sub";
        r.Graft.PromptCopy.IsOpen = true;
        await ShellHarness.WaitUntilAsync(() => File.ReadAllText(r.MirrorPath(Module1)).Contains("second edit")).ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "コンテキスト収集: Excel に接続できなくても収集は続き、「Excel の最新を取り込めていません」の警告を 1 件出す。ミラーは変えない")]
    public async Task 接続できなくても収集は続く()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var before = r.Mirror(Module1);
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);

        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);

        context.Files.Should().NotBeEmpty("接続できなくても、ミラーの現在の内容で収集を続ける");
        context.StatusMessage.Should().Contain("Excel の最新を取り込めていません");
        context.VbaPullWarning.Should().Be(context.StatusMessage);
        r.Mirror(Module1).Should().Be(before);
    }

    [AvaloniaFact(DisplayName = "コンテキスト収集: 衝突はコンテキスト収集の窓（ShowVbaConflictAsync）を親にして出す。「中止」なら収集は続け、警告を出す")]
    public async Task 衝突を窓の親で出し中止しても収集は続く()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Module1), "Sub A()\r\n  ' graft side\r\nEnd Sub\r\n");
        r.Excel("Module1").Code = ExcelEdited;

        var context = r.Graft.ContextCollect!;
        var windowRequests = new List<VbaConflictRequest>();
        context.ShowVbaConflictAsync = request =>
        {
            windowRequests.Add(request);
            return Task.FromResult<VbaConflictDecision?>(VbaConflictDecision.Abort);
        };

        await context.InitializeAsync().ConfigureAwait(true);

        windowRequests.Should().ContainSingle().Which.Scene.Should().Be(VbaConflictScene.ContextCollect);
        windowRequests[0].CanAdoptExcel.Should().BeTrue();
        windowRequests[0].CanAbort.Should().BeTrue();
        r.Conflicts.Should().BeEmpty("窓が配線されているときは、メインウィンドウ親の既定の窓口を使わない");
        context.Files.Should().NotBeEmpty();
        context.StatusMessage.Should().Contain("Excel の最新を取り込めていません").And.Contain(Module1);
        r.Mirror(Module1).Should().Contain("graft side", "中止ならミラーは変えない");
    }

    [AvaloniaFact(DisplayName = "コンテキスト収集: 窓が無い経路の衝突は、メインウィンドウ側の窓口で出る。「Excel 側を採用」ならミラーが Excel の内容になる")]
    public async Task 窓が無い経路の衝突はメイン側で出る()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Module1), "Sub A()\r\n  ' graft side\r\nEnd Sub\r\n");
        r.Excel("Module1").Code = ExcelEdited;
        r.ConflictAnswer = request => VbaConflictDecision.All(request, VbaConflictChoice.AdoptExcel);

        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);

        r.Conflicts.Should().ContainSingle().Which.Scene.Should().Be(VbaConflictScene.ContextCollect);
        r.Mirror(Module1).Should().Contain("edited in excel");
    }

    [AvaloniaFact(DisplayName = "コンテキスト収集: Vba.PullOnContextOpen=false なら取り込まず、Excel を呼ばない")]
    public async Task 設定でオフなら取り込まない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = new VbaSettings { PullOnContextOpen = false } });
        r.Excel("Module1").Code = ExcelEdited;
        r.Bridge.Calls.Clear();

        await r.Graft.ContextCollect!.InitializeAsync().ConfigureAwait(true);

        r.Mirror(Module1).Should().NotContain("edited in excel");
        r.Bridge.Calls.Should().BeEmpty();
        r.Graft.ContextCollect.StatusMessage.Should().BeNull();
    }

    [AvaloniaFact(DisplayName = "コンテキスト収集: Vba.Enabled=false なら取り込まない（普通のファイルプロジェクトとして収集する）")]
    public async Task 機能を無効にすると取り込まない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = new VbaSettings { Enabled = false } });
        r.Excel("Module1").Code = ExcelEdited;
        r.Bridge.Calls.Clear();

        await r.Graft.ContextCollect!.InitializeAsync().ConfigureAwait(true);

        r.Mirror(Module1).Should().NotContain("edited in excel");
        r.Bridge.Calls.Should().BeEmpty("機能を切っているときは COM を一切呼ばない");
        r.Graft.ContextCollect.Files.Should().NotBeEmpty();
    }

    [AvaloniaFact(DisplayName = "コンテキスト収集: 通常のファイルプロジェクトでは、取り込みの処理自体が付かず、Excel を呼ばない")]
    public async Task 通常のプロジェクトでは取り込まない()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        await File.WriteAllTextAsync(r.H.ProjectPath("note.txt"), "1行目\n").ConfigureAwait(true);
        await pane.SelectAsync(pane.Items.First(i => i.Project.Vba is null)).ConfigureAwait(true);
        r.Bridge.Calls.Clear();

        await r.Graft.ContextCollect!.InitializeAsync().ConfigureAwait(true);
        await r.Graft.PromptCopy!.QuickCopyAsync().ConfigureAwait(true);

        r.Bridge.Calls.Should().BeEmpty();
        r.Graft.ContextCollect.VbaPullWarning.Should().BeNull();
    }
}
