using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.Tests.TestSupport;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// リボンの書き換えのうち、<b>Excel で開いているブック</b>の閉じ開き（機能 F の段階 F4。仕様書_ブックの情報と依頼の観点 16.5・16.6・16.9・16.10）の
/// 画面のシナリオ。保存 → 閉じる → （ファイルの書き換え）→ 開き直す の呼び出しの順序、確認ダイアログの文面、止めるブックの案内、
/// 「もう一度試す」「Excel で開く」、処理中の表示を、実際の起動と同じ依存グラフ（<see cref="ShellHarness"/>）で確かめる。
/// </summary>
public partial class WorkbookRibbonRewriteTests
{
    private static string[] CloseOpenCalls(VbaP3Rig r)
        => r.Bridge.Calls.Where(c => c.StartsWith("SaveClose:", StringComparison.Ordinal) || c.StartsWith("Reopen:", StringComparison.Ordinal))
            .Select(c => c[..c.IndexOf(':', StringComparison.Ordinal)]).ToArray();

    [AvaloniaFact(DisplayName = "開いているブック（未保存あり）: 確認で「保存して書き換える」→ 保存 → 閉じる → 書き換え → 開き直し。処理中の表示と通知")]
    public async Task 開いているブックへ反映()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, close: false).ConfigureAwait(true);
        r.Bridge.Get(VbaP3Rig.Book)!.Saved = false;
        var progress = new List<string>();
        r.Graft.ProjectPane.ShowRibbonProgress = progress.Add;
        CenterPaneState? stateWhileClosed = null;
        r.Bridge.OnSaveAndClose = _ => stateWhileClosed = r.Graft.State;
        var before = r.Graft.State;

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        var vm = shown.Should().ContainSingle("確認は毎回出す").Subject;
        vm.LeadText.Should().Contain("Excel で開いているブックは書き換えられないため、Graft が次の順に行います");
        vm.StepsText.Should().Contain("1. ブックを保存して閉じる").And.Contain("4. ブックを開き直す");
        vm.HasUnsavedWarning.Should().BeTrue();
        vm.UnsavedWarningText.Should().Contain("まだ保存していない変更");
        vm.HasOpenNotices.Should().BeTrue();
        vm.OpenNoticesText.Should().Contain("Workbook_BeforeClose").And.Contain("元に戻す");
        vm.RewriteButtonText.Should().Be("保存して書き換える");

        CloseOpenCalls(r).Should().Equal("SaveClose", "Reopen");
        BookRibbon().Should().Be(XmlB);
        r.Bridge.HeldTickets.Should().BeEmpty("券は開き直しで手放す");
        r.Bridge.Get(VbaP3Rig.Book).Should().NotBeNull("開き直した");
        r.Bridge.Get(VbaP3Rig.Book)!.RestoredView.Should().NotBeNull();
        progress.Should().Equal("ブックを保存して閉じています", "控えを残しています", "リボンを書き換えています", "ブックを開き直しています");
        stateWhileClosed.Should().Be(CenterPaneState.Loading, "処理中の表示にする");
        r.Graft.State.Should().Be(before, "終わったら元の表示へ戻す");
        r.Graft.Notice.Text.Should().StartWith("リボンをブックへ反映しました（CustomUI/customUI14.xml。控え: 計画-");
        r.Graft.Notice.HasAction.Should().BeTrue("開き直したので「Excel で開く」は要らず、控えのフォルダを開く「控えを見る」を添える（16.10）");
        r.Graft.Notice.ActionLabel.Should().Be("控えを見る");
        Item(r).VbaStatusText.Should().NotContain("リボン未反映");
        r.Graft.VbaSync.IsWritingToExcel.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "開いているブック（未保存なし）: ⚠ の段落は出さず、ボタンは同じ「保存して書き換える」")]
    public async Task 未保存なしの確認()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, close: false).ConfigureAwait(true);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        var vm = shown.Should().ContainSingle().Subject;
        vm.HasUnsavedWarning.Should().BeFalse();
        vm.UnsavedWarningText.Should().BeEmpty();
        vm.RewriteButtonText.Should().Be("保存して書き換える");
        vm.StepsText.Should().Contain("1. ブックを保存して閉じる");
        r.Bridge.Get(VbaP3Rig.Book)!.SaveCount.Should().BeGreaterThanOrEqualTo(1, "Saved が True でも、閉じる前に必ず保存する");
    }

    [AvaloniaFact(DisplayName = "確認で「中止」（開いているブック）: 保存も閉じるも呼ばれず、ブックは開いたまま")]
    public async Task 中止なら閉じない()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, confirm: false, close: false).ConfigureAwait(true);
        var before = File.ReadAllBytes(BookFile);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        shown.Should().ContainSingle();
        CloseOpenCalls(r).Should().BeEmpty();
        r.Bridge.Get(VbaP3Rig.Book).Should().NotBeNull();
        File.ReadAllBytes(BookFile).Should().Equal(before);
        Item(r).VbaStatusText.Should().Contain("リボン未反映");
    }

    [AvaloniaFact(DisplayName = "止めるブック（読み取り専用で開いている）: 確認を出さず E819 で案内し、保存も閉じるも呼ばない。「もう一度試す」は出さない")]
    public async Task 読み取り専用は止める()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, close: false).ConfigureAwait(true);
        r.Bridge.Get(VbaP3Rig.Book)!.IsReadOnly = true;
        var before = File.ReadAllBytes(BookFile);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        shown.Should().BeEmpty();
        var (title, message) = r.H.Dialogs.Messages.Last();
        title.Should().Be("リボンをブックへ反映できません");
        message.Should().Contain("E819").And.Contain("読み取り専用").And.Contain("ブックは何も変えていません");
        r.H.Dialogs.Actions.Should().BeEmpty("読み取り専用は押し直しても同じ。開いているときの E819 には「もう一度試す」を出さない");
        CloseOpenCalls(r).Should().BeEmpty();
        File.ReadAllBytes(BookFile).Should().Equal(before);
    }

    [AvaloniaFact(DisplayName = "マクロの実行中（E813）: 「もう一度試す」を出す。選ぶと、止まったマクロを終えたあとに最初からやり直せる")]
    public async Task マクロの実行中は試し直せる()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, close: false).ConfigureAwait(true);
        r.Bridge.Get(VbaP3Rig.Book)!.DesignMode = false;
        var asked = 0;
        r.H.Dialogs.ActionAnswer = (_, _, action) =>
        {
            asked++;
            action.Should().Be("もう一度試す");
            r.Bridge.Get(VbaP3Rig.Book)!.DesignMode = true; // 利用者がマクロを終えた。
            return true;
        };

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        asked.Should().Be(1);
        r.H.Dialogs.Actions.Single().Message.Should().Contain("E813").And.Contain("マクロが実行中");
        shown.Should().ContainSingle("やり直しで確認をもう一度出す");
        BookRibbon().Should().Be(XmlB);
    }

    [AvaloniaFact(DisplayName = "Excel が応答しない（E805）: 「もう一度試す」を出す。選ばなければそれで終わり（ブックは変えない）")]
    public async Task 応答しないときは試し直せる()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, close: false).ConfigureAwait(true);
        var before = File.ReadAllBytes(BookFile);
        r.Bridge.FailNext("Inspect", GraftIssue.Of(ErrorCode.E805, "セルを編集中です"));
        r.H.Dialogs.ActionAnswer = (_, _, _) => false;

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        r.H.Dialogs.Actions.Should().ContainSingle(a => a.Action == "もう一度試す" && a.Message.Contains("E805", StringComparison.Ordinal));
        shown.Should().BeEmpty();
        CloseOpenCalls(r).Should().BeEmpty();
        File.ReadAllBytes(BookFile).Should().Equal(before);
    }

    [AvaloniaFact(DisplayName = "Workbook_BeforeClose が閉じるのを取り消した（E820）: ブックは開いたままで、ファイルは変えない。開き直しは呼ばない")]
    public async Task 閉じるのを取り消された()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, close: false).ConfigureAwait(true);
        r.Bridge.CloseCancelledByMacro = true;
        var before = File.ReadAllBytes(BookFile);

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        r.H.Dialogs.Messages.Last().Message.Should().Contain("E820").And.Contain("ブックのファイルは変えていません");
        CloseOpenCalls(r).Should().Equal("SaveClose");
        r.Bridge.Get(VbaP3Rig.Book).Should().NotBeNull("開いたまま");
        File.ReadAllBytes(BookFile).Should().Equal(before);
        r.Bridge.HeldTickets.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "開き直しの失敗（E822）: 書き換えは済んだ知らせと、「Excel で開く」つきの案内。選ぶとブックを Excel で開く")]
    public async Task 開き直せなければExcelで開く()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown, close: false).ConfigureAwait(true);
        r.Bridge.ExcelQuitBeforeReopen = true;
        var opened = new List<string>();
        r.Graft.ProjectPane.OpenWorkbookInExcel = opened.Add;
        r.H.Dialogs.ActionAnswer = (_, _, action) => action == "Excel で開く";

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        BookRibbon().Should().Be(XmlB, "書き換えは済んでいる");
        r.Graft.Notice.Text.Should().StartWith("リボンをブックへ反映しました");
        var action = r.H.Dialogs.Actions.Should().ContainSingle(a => a.Action == "Excel で開く").Subject;
        action.Title.Should().Be("ブックを開き直せませんでした");
        action.Message.Should().Contain("E822");
        opened.Should().Equal(VbaP3Rig.Book);
        r.Bridge.HeldTickets.Should().BeEmpty();
        Item(r).VbaStatusText.Should().NotContain("リボン未反映", "基準は更新済み");
    }

    [AvaloniaFact(DisplayName = "完了の通知の「Excel で開く」（開いていなかったブック）: 押すとブックを Excel で開く")]
    public async Task 完了の通知からExcelで開く()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        var opened = new List<string>();
        r.Graft.ProjectPane.OpenWorkbookInExcel = opened.Add;

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        r.Graft.Notice.ActionLabel.Should().Be("Excel で開く");
        r.Graft.Notice.ActionCommand.Execute(null);
        opened.Should().Equal(VbaP3Rig.Book);
    }

    [AvaloniaFact(DisplayName = "設定: Vba.WorkbookSaveOpenTimeoutMs は、設定の保存のたびにブリッジへ伝わる（保存・開き直しだけの上限）")]
    public async Task 保存開き直しの時間切れが伝わる()
    {
        var shown = new List<VbaRibbonRewriteViewModel>();
        using var r = await ArrangeAsync(shown).ConfigureAwait(true);
        r.Bridge.SaveOpenTimeoutMs.Should().Be(120000, "既定値が渡る");

        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = r.Graft.CurrentSettings.Vba with { WorkbookSaveOpenTimeoutMs = 45000 } });

        r.Bridge.SaveOpenTimeoutMs.Should().Be(45000);
        r.Bridge.CallTimeoutMs.Should().Be(30000, "ほかの呼び出しの上限は変わらない");
    }

    [AvaloniaFact(DisplayName = "設定画面: 保存・開き直しの時間切れの欄が読み込まれ、変えると settings に入る（範囲外は既定へ戻る）")]
    public async Task 設定の欄()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "app-settings"));
        appPaths.EnsureCoreDirectoriesExist();
        var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync().ConfigureAwait(true);
        vm.VbaWorkbookSaveOpenTimeoutMsText.Should().Be("120000");

        vm.VbaWorkbookSaveOpenTimeoutMsText = "90000";
        await vm.FlushPendingSaveAsync().ConfigureAwait(true);

        var reloaded = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices());
        await reloaded.InitializeAsync().ConfigureAwait(true);
        reloaded.VbaWorkbookSaveOpenTimeoutMsText.Should().Be("90000");
    }
}
