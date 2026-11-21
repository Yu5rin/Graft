using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// Excel VBA 連携 段階 P4 のシナリオ（仕様書_VBA連携 7.6・10.2）のうち、プロジェクトペイン:
/// 右クリックのチェック項目（VbaLink の更新）、「VBA」バッジ（ブックの開閉・未反映・削除保留・連携オフ）。
/// </summary>
public class VbaP4ProjectPaneTests
{
    private const string Module1 = "Modules/Module1.bas";

    // ------------------------------------------------------------------
    // チェック項目
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "チェック項目: 「適用後に自動反映」をオフにすると VbaLink が保存され、適用しても Excel へ反映されず「未反映」の印が付く")]
    public async Task 自動反映をオフにすると適用しても反映されない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        pane.IsAutoPushChecked.Should().BeTrue("既定は自動反映する");

        pane.IsAutoPushChecked = false;
        await pane.WaitForVbaOptionAsync().ConfigureAwait(true);

        pane.IsAutoPushChecked.Should().BeFalse();
        var stored = (await new ProjectStore(r.H.AppPaths).LoadAsync().ConfigureAwait(true)).Value.Single(p => p.Id == r.Project.Id);
        stored.Vba!.AutoPushAfterApply.Should().BeFalse("ProjectStore 経由で projects.json に保存される");
        stored.Vba.WorkbookFullName.Should().Be(VbaP3Rig.Book, "ほかの項目は変わらない");

        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);

        r.Mirror(Module1).Should().Contain("hello");
        r.Excel("Module1").Code.Should().Be(VbaP3Rig.Module1Original, "自動反映しない設定では Excel に触れない");
        (await r.StatusAsync().ConfigureAwait(true)).UnpushedCount.Should().Be(1);
    }

    [AvaloniaFact(DisplayName = "チェック項目: 「反映後にブックを保存する」をオンにすると、自動反映のあとにブックが保存される")]
    public async Task 反映後の保存をオンにするとブックが保存される()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        pane.IsSaveAfterPushChecked.Should().BeFalse("既定は保存しない");

        await pane.SetSaveWorkbookAfterPushAsync(true).ConfigureAwait(true);

        pane.IsSaveAfterPushChecked.Should().BeTrue();
        await r.PasteAsync(VbaP3Rig.Edit(Module1, "  MsgBox \"hi\"", "  MsgBox \"hello\"")).ConfigureAwait(true);
        await r.ApplyAsync().ConfigureAwait(true);

        r.Excel("Module1").Code.Should().Contain("hello");
        r.Bridge.Get(VbaP3Rig.Book)!.SaveCount.Should().Be(1);
    }

    [AvaloniaFact(DisplayName = "チェック項目: 選択を変えても、再読み込みしても、選択中のプロジェクトの値が出る。連続して切り替えても値が食い違わない")]
    public async Task 連続した切り替えでも値が食い違わない()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;

        pane.IsAutoPushChecked = false;
        pane.IsSaveAfterPushChecked = true;
        await pane.WaitForVbaOptionAsync().ConfigureAwait(true);
        await pane.SetAutoPushAfterApplyAsync(false).ConfigureAwait(true); // 直前の保存が終わってから走る。

        var stored = (await new ProjectStore(r.H.AppPaths).LoadAsync().ConfigureAwait(true)).Value.Single(p => p.Id == r.Project.Id);
        stored.Vba!.AutoPushAfterApply.Should().BeFalse();
        stored.Vba.SaveWorkbookAfterPush.Should().BeTrue("2つの項目の更新が互いを上書きしない");

        // 通常のプロジェクトを選ぶと、チェック項目は意味を持たない（メニューも出ない）。
        await pane.SelectAsync(pane.Items.First(i => i.Project.Vba is null)).ConfigureAwait(true);
        pane.IsSelectedVbaProject.Should().BeFalse();
        pane.IsAutoPushChecked.Should().BeFalse();
        await pane.SetAutoPushAfterApplyAsync(true).ConfigureAwait(true);
        (await new ProjectStore(r.H.AppPaths).LoadAsync().ConfigureAwait(true)).Value.Single(p => p.Vba is null).Vba
            .Should().BeNull("VBA プロジェクトでないものは変わらない");
    }

    [AvaloniaFact(DisplayName = "チェック項目: 右クリックメニューに2つのチェック項目があり、VbaLink の値を映す。VBA 連携を切っているときは無効になる")]
    public async Task メニューのチェック項目はVbaLinkを映す()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        await pane.SetSaveWorkbookAfterPushAsync(true).ConfigureAwait(true);

        var view = new ProjectPane { DataContext = pane };
        var window = new Window { Width = 400, Height = 500, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.GetVisualDescendants().OfType<ListBox>().First(l => l.ContextMenu is not null);
            // メニューを開いて、バインドを解決させる（閉じるとコマンドなどの束縛が外れるので、開いたまま読む）。
            list.ContextMenu!.Open(list);
            Dispatcher.UIThread.RunJobs();

            var items = list.ContextMenu.Items.OfType<MenuItem>().ToList();
            var autoPush = items.Single(i => Equals(i.Header, "適用後に Excel へ自動反映する"));
            var save = items.Single(i => Equals(i.Header, "反映後にブックを保存する"));
            autoPush.ToggleType.Should().Be(MenuItemToggleType.CheckBox);
            autoPush.IsChecked.Should().BeTrue();
            save.IsChecked.Should().BeTrue();
            autoPush.IsVisible.Should().BeTrue("VBA プロジェクトを選んでいるので出る");
            autoPush.IsEnabled.Should().BeTrue();

            r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = new VbaSettings { Enabled = false } });
            Dispatcher.UIThread.RunJobs();
            autoPush.IsEnabled.Should().BeFalse("連携を切っているときは操作できない");
            save.IsEnabled.Should().BeFalse();
        }
        finally
        {
            window.Close();
        }
    }

    // ------------------------------------------------------------------
    // バッジ
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "バッジ: ブックが開いていれば「VBA」（グレーでない）。閉じると、グレーでツールチップ「ブックが開いていません」。開き直すと戻る")]
    public async Task バッジはブックの開閉を映す()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var item = pane.Items.Single(i => i.Project.Vba is not null);

        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        item.IsVbaBadgeVisible.Should().BeTrue();
        item.VbaBadgeText.Should().Be("VBA");
        item.IsVbaBadgeGray.Should().BeFalse();

        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        item.IsVbaBadgeGray.Should().BeTrue();
        item.VbaTooltip.Should().Contain("ブックが開いていません");
        item.AutomationName.Should().Contain("ブックが開いていません", "色だけに頼らず読み上げにも出す");

        r.Bridge.AddWorkbook(VbaP3Rig.Book, FakeVbaBridge.Std("Module1", VbaP3Rig.Module1Original));
        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        item.IsVbaBadgeGray.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "バッジ: 件数（未反映・削除保留）は Excel に接続せずに数える。ファイルを編集すると「未反映 1」、消すと「削除保留 1」")]
    public async Task バッジの件数はExcelに接続せずに数える()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var item = pane.Items.Single(i => i.Project.Vba is not null);
        r.Bridge.Calls.Clear();

        File.WriteAllText(r.MirrorPath(Module1), "Sub A()\r\n  ' edited\r\nEnd Sub\r\n");
        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);
        item.VbaStatusText.Should().Be("未反映 1");
        item.HasVbaStatus.Should().BeTrue();
        item.VbaTooltip.Should().Contain("未反映のモジュール: 1 件");

        File.Delete(r.MirrorPath("Classes/Cls1.cls"));
        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);
        item.VbaStatusText.Should().Be("未反映 1・削除保留 1");
        item.VbaTooltip.Should().Contain("削除保留のモジュール: 1 件");

        r.Bridge.Calls.Should().BeEmpty("件数の調べ直しでは Excel に接続しない");
    }

    [AvaloniaFact(DisplayName = "バッジ: 一覧を読み直しても（LoadAsync）、バッジの状態は消えずに保たれる")]
    public async Task 一覧の再読み込みでバッジが消えない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);

        await pane.LoadAsync().ConfigureAwait(true);

        var item = pane.Items.Single(i => i.Project.Vba is not null);
        item.IsVbaBadgeVisible.Should().BeTrue();
        item.IsVbaBadgeGray.Should().BeTrue("作り直した行にも前回の結果を引き継ぐ（ちらつかせない）");
    }

    [AvaloniaFact(DisplayName = "バッジ: Vba.Enabled=false なら「VBA 連携オフ」。Excel には問い合わせず、有効に戻すと元に戻る。通常のプロジェクトにはバッジが出ない")]
    public async Task 連携オフのバッジ()
    {
        using var r = await VbaP3Rig.CreateAsync(plainProjectFirst: true).ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var item = pane.Items.Single(i => i.Project.Vba is not null);
        var plain = pane.Items.Single(i => i.Project.Vba is null);
        plain.IsVbaBadgeVisible.Should().BeFalse();

        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = new VbaSettings { Enabled = false } });
        r.Bridge.Calls.Clear();
        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);

        item.VbaBadgeText.Should().Be("VBA 連携オフ");
        item.IsVbaBadgeGray.Should().BeTrue();
        r.Bridge.Calls.Should().BeEmpty("機能を切っているときは Excel に問い合わせない");
        plain.IsVbaBadgeVisible.Should().BeFalse();

        r.Graft.UpdateSettings(r.Graft.CurrentSettings with { Vba = new VbaSettings { Enabled = true } });
        pane.Items.Single(i => i.Project.Vba is not null).VbaBadgeText.Should().Be("VBA");
    }

    [AvaloniaFact(DisplayName = "バッジ: この環境で使えない（Null ブリッジ相当）ときは、VBA プロジェクトの行にもバッジを出さない")]
    public async Task 使えない環境ではバッジを出さない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        r.Bridge.IsSupported = false;

        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);

        pane.Items.Single(i => i.Project.Vba is not null).IsVbaBadgeVisible.Should().BeFalse();
        pane.IsVbaFeatureAvailable.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "バッジ: 前面化などの調べ直しの依頼は、短い間隔では Excel への問い合わせを連打しない（定期監視もしない）")]
    public async Task 調べ直しの依頼は連打しない()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        r.Bridge.Calls.Clear();

        pane.RequestVbaBadgeRefresh();
        await ShellHarness.WaitUntilAsync(() => r.Bridge.Calls.Count(c => c == "List") >= 1).ConfigureAwait(true);
        for (var i = 0; i < 5; i++) pane.RequestVbaBadgeRefresh();
        await Task.Delay(300).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();

        r.Bridge.Calls.Count(c => c == "List").Should().Be(1, "3秒以内の再依頼では開閉を調べ直さない");
    }

    [AvaloniaFact(DisplayName = "バッジ: 手動の取込・反映の後に開閉を調べ直す（取り込みに失敗してブックが閉じていたら、グレーになる）")]
    public async Task 手動の取込の後にバッジが更新される()
    {
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var pane = r.Graft.ProjectPane;
        var item = pane.Items.Single(i => i.Project.Vba is not null);
        await pane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        item.IsVbaBadgeGray.Should().BeFalse();

        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        await ShellHarness.ExecuteAsync(pane.PullFromExcelCommand).ConfigureAwait(true);

        r.H.Dialogs.Messages.Should().NotBeEmpty("取り込みの失敗は案内される");
        item.IsVbaBadgeGray.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // Null プラットフォーム
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "Null プラットフォーム: 「Excel ブックから追加」・右クリックの VBA 項目・VBE で開く・バッジのどれも出ない")]
    public async Task Nullプラットフォームでは何も出ない()
    {
        using var harness = await ShellHarness.CreateAsync().ConfigureAwait(true);
        // 既定の PlatformServices.Current.Vba（Linux では Null 実装）のまま。
        var pane = harness.Shell.Graft.ProjectPane;
        await harness.RegisterProjectAsync().ConfigureAwait(true);

        harness.Shell.Graft.VbaBridge.IsSupported.Should().BeFalse();
        pane.IsVbaFeatureAvailable.Should().BeFalse("追加ボタンとコマンドパレットの項目が出ない");
        pane.IsSelectedVbaProject.Should().BeFalse();
        pane.CanShowInVbe("Modules/Module1.bas").Should().BeFalse();
        pane.Items.Should().OnlyContain(i => !i.IsVbaBadgeVisible);
        harness.Shell.Editor.CanShowInVbe!("Modules/Module1.bas").Should().BeFalse();
    }
}
