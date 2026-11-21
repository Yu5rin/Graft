using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// Excel VBA 連携 段階 P2 の通しシナリオ（仕様書_VBA連携 10.2）。<see cref="FakeVbaBridge"/> を
/// <c>MainViewModel.VbaBridge</c> に入れて、Windows の COM なしに次を確かめる:
/// Null 環境で機能が見えないこと、作成フロー、手動の Pull／Push、未保存の確認、拡張子の許可（適用が E202 にならない）、
/// プロジェクトの場所の変更・削除、同率判定。ミラー作成や Pull／Push の細部（書く形・基準・控え）は
/// <c>Graft.Tests</c> の <c>VbaSyncServiceTests</c> が固めている。
/// </summary>
public class VbaP2ScenarioTests : IDisposable
{
    private const string Book = @"C:\Work\計画.xlsm";
    private const string OtherBook = @"C:\Work\別.xlsm";

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    private static FakeVbaBridge NewBridge(string book = Book, params FakeVbaBridge.Module[] modules)
    {
        var bridge = new FakeVbaBridge();
        bridge.AddWorkbook(book, modules.Length > 0 ? modules : new[]
        {
            FakeVbaBridge.Std("Module1", "Sub A()\r\n  MsgBox \"hi\"\r\nEnd Sub"),
            FakeVbaBridge.Cls("Cls1", "Option Explicit"),
            FakeVbaBridge.Doc("Sheet1", "' sheet", "集計"),
        });
        return bridge;
    }

    /// <summary>選択画面を台本で進める（実際の画面は <c>VbaWorkbookPickerWindow</c> が同じ ViewModel を使う）。</summary>
    private static async Task<Project> AddWorkbookAsync(ShellHarness h, string book = Book)
    {
        var pane = h.Shell.Graft.ProjectPane;
        pane.ShowWorkbookPickerAsync = async picker =>
        {
            await picker.LoadAsync().ConfigureAwait(true);
            picker.SelectedWorkbook = picker.Workbooks.First(w => w.FullName == book);
            return await picker.ConfirmAsync().ConfigureAwait(true);
        };
        await ShellHarness.ExecuteAsync(pane.AddFromWorkbookCommand).ConfigureAwait(true);
        return pane.SelectedItem!.Project;
    }

    private static string Mirror(Project project, string relative) => Path.Combine(project.Root, relative.Replace('/', Path.DirectorySeparatorChar));

    // ------------------------------------------------------------------
    // Null 環境で機能が見えない
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "Null 環境（Linux）: 「Excel ブックから追加」ボタンも、コマンドパレットの VBA 項目も出ず、コマンドも実行できない")]
    public async Task Null環境で機能が見えない()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var pane = h.Shell.Graft.ProjectPane;

        h.Shell.Graft.VbaBridge.IsSupported.Should().BeFalse("Linux の実装は NullVbaBridge");
        pane.IsVbaFeatureAvailable.Should().BeFalse();
        pane.AddFromWorkbookCommand.CanExecute(null).Should().BeFalse();

        var window = _windows.Track(new Window { Content = new ProjectPane { DataContext = pane }, Width = 320, Height = 400 });
        window.Show();
        FindButton(window, "Excel ブックから追加").IsVisible.Should().BeFalse("使えない環境ではボタンごと出さない");

        h.Shell.CommandPalette.Open();
        h.Shell.CommandPalette.Results.Select(r => r.Title).Should().NotContain(t => t.Contains("Excel"));
    }

    [AvaloniaFact(DisplayName = "対応する環境（Fake）: ボタンもコマンドパレットの項目も出る。設定で無効にすると、また出なくなる")]
    public async Task 対応環境では見える()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        h.Shell.Graft.VbaBridge = NewBridge();
        var pane = h.Shell.Graft.ProjectPane;

        var window = _windows.Track(new Window { Content = new ProjectPane { DataContext = pane }, Width = 320, Height = 400 });
        window.Show();
        FindButton(window, "Excel ブックから追加").IsVisible.Should().BeTrue();
        pane.AddFromWorkbookCommand.CanExecute(null).Should().BeTrue();

        h.Shell.CommandPalette.Open();
        h.Shell.CommandPalette.Results.Select(r => r.Title).Should().Contain(new[]
        {
            "Excel ブックからプロジェクトを追加", "Excel から取込（選択中の VBA プロジェクト）",
            "Excel へ反映（選択中の VBA プロジェクト）", "Excel のブックを前面に出す（選択中の VBA プロジェクト）",
        });

        h.Shell.Graft.UpdateSettings(h.Shell.Graft.CurrentSettings with { Vba = new VbaSettings { Enabled = false } });
        pane.IsVbaFeatureAvailable.Should().BeFalse("Vba.Enabled=false なら機能ごと隠す");
    }

    private static Button FindButton(Window window, string automationName)
        => window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == automationName);

    // ------------------------------------------------------------------
    // 作成フロー（7.1）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "作成フロー: ブックを選ぶとミラーと同期基準が作られ、VBA の紐付けと常設コンテキストの既定文つきで登録・選択される")]
    public async Task 作成フロー()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        h.Shell.Graft.VbaBridge = NewBridge();

        var project = await AddWorkbookAsync(h).ConfigureAwait(true);

        project.Name.Should().Be("計画");
        project.Vba.Should().NotBeNull();
        project.Vba!.WorkbookFullName.Should().Be(Book);
        project.Vba.AutoPushAfterApply.Should().BeTrue("VbaLink の既定");
        project.Root.Should().Be(Path.Combine(h.AppPaths.VbaMirrorsDirectory, VbaMirrorFolderName.Build(Book)), "既定の場所はデータ保存先の vba-mirrors 配下");
        project.StandingContext.Should().Be(VbaStandingContext.Default).And.Contain("DELETE / RENAME");
        File.ReadAllText(Mirror(project, "Modules/Module1.bas")).Should().Be("Sub A()\r\n  MsgBox \"hi\"\r\nEnd Sub\r\n");
        File.Exists(Mirror(project, "Classes/Cls1.cls")).Should().BeTrue();
        File.Exists(Mirror(project, "Documents/Sheet1.cls")).Should().BeTrue();
        File.Exists(h.AppPaths.GetVbaSyncFilePath(project.Id)).Should().BeTrue("同期基準が作られている");
        h.Shell.Graft.ProjectPane.Items.Select(i => i.Project.Id).Should().Contain(project.Id);
        h.Shell.Graft.ProjectPane.IsSelectedVbaProject.Should().BeTrue();
        h.Shell.Graft.Notice.Text.Should().Contain("3 件");
    }

    [AvaloniaFact(DisplayName = "作成フロー: OneDrive（URL）のブックは理由つきで選べず、読み取り専用は選べるが注意が付く。ロック中・保護ビューも無効")]
    public async Task 選べないブックは理由つきで無効()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        bridge.AddWorkbook("https://contoso.sharepoint.com/sites/x/一覧.xlsm").Availability = VbaWorkbookAvailability.UrlLocation;
        bridge.Get("https://contoso.sharepoint.com/sites/x/一覧.xlsm")!.UnavailableReason =
            "OneDrive／SharePoint 上のブックは使えません。ローカルのフォルダへ保存してから開き直してください。";
        bridge.AddWorkbook(@"C:\Work\lock.xlsm").Availability = VbaWorkbookAvailability.Locked;
        bridge.AddWorkbook(@"C:\Work\pv.xlsm").Availability = VbaWorkbookAvailability.ProtectedView;
        bridge.AddWorkbook(@"C:\Work\ro.xlsm").IsReadOnly = true;
        h.Shell.Graft.VbaBridge = bridge;
        var picker = new VbaWorkbookPickerViewModel(h.Shell.Graft.VbaSync, h.Dialogs);

        await picker.LoadAsync().ConfigureAwait(true);

        var url = picker.Workbooks.Single(w => w.FullName.StartsWith("https://", StringComparison.Ordinal));
        url.IsSelectable.Should().BeFalse();
        url.StatusText.Should().Be("OneDrive／SharePoint");
        url.ReasonText.Should().Contain("OneDrive／SharePoint 上のブックは使えません");
        picker.Workbooks.Single(w => w.Name == "lock.xlsm").IsSelectable.Should().BeFalse();
        picker.Workbooks.Single(w => w.Name == "pv.xlsm").IsSelectable.Should().BeFalse();
        var readOnly = picker.Workbooks.Single(w => w.Name == "ro.xlsm");
        readOnly.IsSelectable.Should().BeTrue();
        readOnly.ReasonText.Should().Contain("読み取り専用");

        picker.SelectedWorkbook = url;
        picker.CanConfirm.Should().BeFalse("選べないブックでは追加できない");
        (await picker.ConfirmAsync().ConfigureAwait(true)).Should().BeFalse();
        picker.Note.Should().Contain("一覧に出ないブックは", "5.2 の注意を常に添える");
    }

    [AvaloniaFact(DisplayName = "作成フロー: アクセス未許可（E802）は選択画面にその場で案内を出し、何も作らない")]
    public async Task E802はその場に案内を出す()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        h.Shell.Graft.VbaBridge = bridge;
        bridge.FailNext("List", GraftIssue.Of(ErrorCode.E802));
        var picker = new VbaWorkbookPickerViewModel(h.Shell.Graft.VbaSync, h.Dialogs);

        await picker.LoadAsync().ConfigureAwait(true);

        picker.HasError.Should().BeTrue();
        picker.Error!.Code.Should().Be(ErrorCode.E802);
        picker.ErrorText.Should().Contain("トラストセンター");
        picker.Workbooks.Should().BeEmpty();

        await picker.LoadAsync().ConfigureAwait(true);
        picker.HasError.Should().BeFalse("更新で読み込み直せる");
        picker.Workbooks.Should().HaveCount(1);
    }

    [AvaloniaFact(DisplayName = "作成フロー: 読み取りに失敗したら画面に残り、ミラーもプロジェクトも作られない")]
    public async Task 作成の失敗は何も残さない()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        h.Shell.Graft.VbaBridge = bridge;
        var picker = new VbaWorkbookPickerViewModel(h.Shell.Graft.VbaSync, h.Dialogs);
        await picker.LoadAsync().ConfigureAwait(true);
        bridge.FailNext("Read", GraftIssue.Of(ErrorCode.E805));

        (await picker.ConfirmAsync().ConfigureAwait(true)).Should().BeFalse();

        picker.Error!.Code.Should().Be(ErrorCode.E805);
        picker.Result.Should().BeNull();
        Directory.Exists(h.AppPaths.VbaMirrorsDirectory).Should().BeFalse();
        h.Shell.Graft.ProjectPane.Items.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "作成フロー: 同じ場所に既存のミラーがあれば選択肢が出る。再追加で「既存を使う」と、利用者が編集した常設コンテキストは保たれる")]
    public async Task 再追加は既存のミラーを使える()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        h.Shell.Graft.VbaBridge = bridge;
        var first = await AddWorkbookAsync(h).ConfigureAwait(true);
        var pane = h.Shell.Graft.ProjectPane;
        await h.Shell.Graft.ProjectPane.RemoveWithoutConfirmationAsync(first.Id, deleteHistory: false).ConfigureAwait(true);
        File.WriteAllText(Mirror(first, "Modules/Module1.bas"), "Sub A()\r\n  ' graft side\r\nEnd Sub\r\n");

        var picker = new VbaWorkbookPickerViewModel(h.Shell.Graft.VbaSync, h.Dialogs);
        await picker.LoadAsync().ConfigureAwait(true);
        picker.ExistingMirrorFound.Should().BeTrue("登録を外してもミラーは残っている");
        picker.ReuseExistingMirror.Should().BeTrue("既定は既存を使う（三方比較で続ける）");

        var readsBefore = bridge.Calls.Count(c => c.StartsWith("Read:", StringComparison.Ordinal));
        (await picker.ConfirmAsync().ConfigureAwait(true)).Should().BeTrue();
        bridge.Calls.Count(c => c.StartsWith("Read:", StringComparison.Ordinal)).Should().Be(readsBefore, "既存を使うときは Excel から読み直さない");
        picker.Result!.ModuleCount.Should().Be(-1);
        File.ReadAllText(Mirror(first, "Modules/Module1.bas")).Should().Contain("graft side", "ミラーは作り直さない");

        // 作り直すを選ぶと、既存はごみ箱へ送られ、Excel の内容になる。
        picker.RebuildMirror = true;
        picker.ExistingMirrorFound.Should().BeTrue();
        pane.Items.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // 手動の Pull／Push（7.2・7.2b）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "手動の取込: Excel 側の変更がミラーへ入り、通知が出て、履歴（リビジョン）は作られない。もう一度押すと「変更なし」")]
    public async Task 手動の取込()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        h.Shell.Graft.VbaBridge = bridge;
        var project = await AddWorkbookAsync(h).ConfigureAwait(true);
        bridge.Get(Book)!.Find("Module1")!.Code = "Sub A()\r\n  Debug.Print 1\r\nEnd Sub";

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);

        File.ReadAllText(Mirror(project, "Modules/Module1.bas")).Should().Contain("Debug.Print 1");
        h.Shell.Graft.Notice.Text.Should().Be("Excel から 1 件取り込みました。");
        Directory.Exists(h.AppPaths.GetProjectBackupDirectory(project.Id)).Should().BeFalse("Pull は履歴に残さない");
        h.Dialogs.Messages.Should().BeEmpty();

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        h.Shell.Graft.Notice.Text.Should().Contain("新しい変更はありませんでした");
    }

    [AvaloniaFact(DisplayName = "手動の反映: ミラーを手で編集すると Excel へ届く。適用でも同じ（適用で変わったミラーを「Excel へ反映」）")]
    public async Task 手動の反映()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        h.Shell.Graft.VbaBridge = bridge;
        var project = await AddWorkbookAsync(h).ConfigureAwait(true);

        File.WriteAllText(Mirror(project, "Modules/Module1.bas"), "Sub A()\r\n  ' edited\r\nEnd Sub\r\n");
        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.PushToExcelCommand).ConfigureAwait(true);

        bridge.Get(Book)!.Find("Module1")!.Code.Should().Be("Sub A()\r\n  ' edited\r\nEnd Sub");
        h.Shell.Graft.Notice.Text.Should().Be("Excel へ 1 件反映しました。");

        // 適用（P2 では適用後の自動 Push はまだ無い）→ 手動の Push で届く。
        await h.ParseAsync("Modules/Module1.bas", "  ' edited", "  ' edited by ai").ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand).ConfigureAwait(true);
        File.ReadAllText(Mirror(project, "Modules/Module1.bas")).Should().Contain("edited by ai");
        bridge.Get(Book)!.Find("Module1")!.Code.Should().NotContain("by ai", "P3 まで、適用後の自動反映は無い");

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.PushToExcelCommand).ConfigureAwait(true);
        bridge.Get(Book)!.Find("Module1")!.Code.Should().Contain("edited by ai");
    }

    [AvaloniaFact(DisplayName = "衝突: 取込も反映も行わず、衝突のモジュール名と「今後の版で対応」を通知する（P3 で衝突ダイアログへ差し替える）")]
    public async Task 衝突は通知だけ()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        h.Shell.Graft.VbaBridge = bridge;
        var project = await AddWorkbookAsync(h).ConfigureAwait(true);
        File.WriteAllText(Mirror(project, "Modules/Module1.bas"), "graft side\r\n");
        bridge.Get(Book)!.Find("Module1")!.Code = "excel side";

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.PushToExcelCommand).ConfigureAwait(true);

        h.Dialogs.Messages.Should().HaveCount(2);
        h.Dialogs.Messages.Should().OnlyContain(m => m.Message.Contains("Modules/Module1.bas") && m.Message.Contains("両方が変更"));
        File.ReadAllText(Mirror(project, "Modules/Module1.bas")).Should().Be("graft side\r\n");
        bridge.Get(Book)!.Find("Module1")!.Code.Should().Be("excel side");
    }

    [AvaloniaFact(DisplayName = "ブックが開いていない（E804）: 理由とパスを出し、「Excel で開く」でそのブックを開く操作に渡る")]
    public async Task ブックが開いていないとき()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        h.Shell.Graft.VbaBridge = bridge;
        await AddWorkbookAsync(h).ConfigureAwait(true);
        bridge.CloseWorkbook(Book);
        var opened = new List<string>();
        h.Shell.Graft.ProjectPane.OpenWorkbookInExcel = opened.Add;

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);

        h.Dialogs.Messages.Should().ContainSingle().Which.Message.Should().Contain("E804").And.Contain(Book);
        opened.Should().Equal(Book);
    }

    [AvaloniaFact(DisplayName = "ブックを前面化: 結び付けたブックへ要求が届く。削除保留は「Excel から復元」で戻る")]
    public async Task 前面化と復元()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        h.Shell.Graft.VbaBridge = bridge;
        var project = await AddWorkbookAsync(h).ConfigureAwait(true);

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.ActivateWorkbookCommand).ConfigureAwait(true);
        bridge.Calls.Should().Contain($"Activate:{Book}");

        File.Delete(Mirror(project, "Classes/Cls1.cls"));
        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        File.Exists(Mirror(project, "Classes/Cls1.cls")).Should().BeFalse("削除保留は自動 Pull で復活しない");

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.RestoreFromExcelCommand).ConfigureAwait(true);
        File.Exists(Mirror(project, "Classes/Cls1.cls")).Should().BeTrue();
        h.Shell.Graft.Notice.Text.Should().Be("Excel から 1 件復元しました。");

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.RestoreFromExcelCommand).ConfigureAwait(true);
        h.Shell.Graft.Notice.Text.Should().Contain("復元するモジュールはありません");
    }

    [AvaloniaFact(DisplayName = "右クリックメニュー: VBA プロジェクトを選んでいるときだけ VBA の項目が出る")]
    public async Task 右クリックメニューの出し分け()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        h.Shell.Graft.VbaBridge = NewBridge();
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var pane = h.Shell.Graft.ProjectPane;
        var view = new ProjectPane { DataContext = pane };
        var window = _windows.Track(new Window { Content = view, Width = 320, Height = 400 });
        window.Show();
        MenuItem[] VbaItems()
        {
            // メニューの項目は、開かれるまで DataContext を持たない（IsVisible の束縛が効かない）。右クリック相当で開いてから見る。
            view.ListBoxElement.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            Dispatcher.UIThread.RunJobs();
            return view.ListBoxElement.ContextMenu!.Items.OfType<MenuItem>()
                .Where(m => m.Header is "Excel から取込" or "Excel へ反映" or "Excel から復元" or "ブックを前面化").ToArray();
        }

        VbaItems().Should().HaveCount(4);
        VbaItems().Should().OnlyContain(m => !m.IsVisible, "普通のフォルダのプロジェクトでは出さない");
        pane.PullFromExcelCommand.CanExecute(null).Should().BeFalse();

        await AddWorkbookAsync(h).ConfigureAwait(true);

        VbaItems().Should().OnlyContain(m => m.IsVisible);
        pane.PullFromExcelCommand.CanExecute(null).Should().BeTrue();
        pane.SelectedItem = pane.Items.First(i => i.Project.Vba is null);
        VbaItems().Should().OnlyContain(m => !m.IsVisible);
    }

    // ------------------------------------------------------------------
    // 未保存の確認（7.2）
    // ------------------------------------------------------------------

    private static async Task<(EditorTabViewModel Tab, Project Project)> OpenDirtyModuleAsync(ShellHarness h, FakeVbaBridge bridge)
    {
        h.Shell.Graft.VbaBridge = bridge;
        var project = await AddWorkbookAsync(h).ConfigureAwait(true);
        var tab = (await h.Shell.Editor.OpenFileAsync(Mirror(project, "Modules/Module1.bas")).ConfigureAwait(true)).Value;
        bridge.Get(Book)!.Find("Module1")!.Code = "Sub A()\r\n  ' excel edit\r\nEnd Sub";
        tab.Session.Document.Insert(0, "' unsaved\r\n");
        tab.Session.IsModified.Should().BeTrue("前提: 取り込み先に未保存の編集がある");
        return (tab, project);
    }

    [AvaloniaFact(DisplayName = "未保存の確認: 取り込み先に未保存のタブがあると確認が出て、断ると取り込まれず、編集も残る")]
    public async Task 未保存を断ると取り込まない()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var (tab, project) = await OpenDirtyModuleAsync(h, NewBridge()).ConfigureAwait(true);
        h.Dialogs.ConfirmAnswer = (title, _) => title != "未保存の変更を保存します";

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);

        h.Dialogs.Confirms.Should().ContainSingle(c => c.Title == "未保存の変更を保存します").Which.Message.Should().Contain("Module1.bas");
        File.ReadAllText(Mirror(project, "Modules/Module1.bas")).Should().NotContain("excel edit", "取り込まれない");
        tab.Session.IsModified.Should().BeTrue("未保存の編集はそのまま残る");
        h.Dialogs.Messages.Should().Contain(m => m.Message.Contains("未保存の編集があるため"));
    }

    [AvaloniaFact(DisplayName = "未保存の確認: 保存に応じると保存されてから計画が作り直され、両側が変わったので衝突として扱われる（ミラーは上書きされない）")]
    public async Task 保存に応じると計画を作り直す()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var (tab, project) = await OpenDirtyModuleAsync(h, NewBridge()).ConfigureAwait(true);

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);

        tab.Session.IsModified.Should().BeFalse("保存された");
        var disk = File.ReadAllText(Mirror(project, "Modules/Module1.bas"));
        disk.Should().Contain("' unsaved").And.NotContain("excel edit", "利用者の編集が Excel の内容で上書きされない");
        h.Dialogs.Messages.Should().Contain(m => m.Message.Contains("Modules/Module1.bas") && m.Message.Contains("両方が変更"));
    }

    [AvaloniaFact(DisplayName = "未保存の確認: 取り込み先のタブが未保存でなければ、確認なしで取り込まれ、開いているタブも更新される")]
    public async Task 保存済みなら確認なしで取り込む()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        h.Shell.Graft.VbaBridge = bridge;
        var project = await AddWorkbookAsync(h).ConfigureAwait(true);
        var tab = (await h.Shell.Editor.OpenFileAsync(Mirror(project, "Modules/Module1.bas")).ConfigureAwait(true)).Value;
        bridge.Get(Book)!.Find("Module1")!.Code = "Sub A()\r\n  ' excel edit\r\nEnd Sub";

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);

        h.Dialogs.Confirms.Should().NotContain(c => c.Title == "未保存の変更を保存します");
        File.ReadAllText(Mirror(project, "Modules/Module1.bas")).Should().Contain("excel edit");
        tab.Session.IsModified.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // 拡張子の許可（4.1）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "拡張子: VBA プロジェクトでは .bas を適用でき（E202 にならない）、同じファイルでも普通のプロジェクトでは E202 で止まる")]
    public async Task VBAプロジェクトだけ拡張子が許可される()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        h.Shell.Graft.VbaBridge = NewBridge();
        var project = await AddWorkbookAsync(h).ConfigureAwait(true);

        await h.ParseAsync("Modules/Module1.bas", "  MsgBox \"hi\"", "  MsgBox \"hello\"").ConfigureAwait(true);

        h.Shell.Graft.Blocks.Should().ContainSingle().Which.Plan.CanApply.Should().BeTrue(
            string.Join(" / ", h.Shell.Graft.Blocks.SelectMany(b => b.Plan.Issues).Select(i => i.ToDisplayText())));
        await ShellHarness.ExecuteAsync(h.Shell.Graft.ApplyCommand).ConfigureAwait(true);
        File.ReadAllText(Mirror(project, "Modules/Module1.bas")).Should().Contain("MsgBox \"hello\"");

        // 対照: 普通のフォルダのプロジェクトに同じ拡張子のファイルがあっても、全体の許可リストに無いので E202。
        await File.WriteAllTextAsync(h.ProjectPath("Module9.bas"), "Sub B()\r\nEnd Sub\r\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await h.Shell.Graft.ProjectPane.SelectAsync(h.Shell.Graft.ProjectPane.Items.First(i => i.Project.Vba is null)).ConfigureAwait(true);
        await h.ParseAsync("Module9.bas", "Sub B()", "Sub B() ' x").ConfigureAwait(true);
        h.Shell.Graft.Blocks.Should().ContainSingle().Which.Plan.CanApply.Should().BeFalse();
        h.Shell.Graft.Blocks[0].Plan.Issues.Should().Contain(i => i.Code == ErrorCode.E202);
    }

    [AvaloniaFact(DisplayName = "拡張子: VBA プロジェクトのクイックオープンの候補に .bas／.cls が出る")]
    public async Task クイックオープンにも出る()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        h.Shell.Graft.VbaBridge = NewBridge();
        var project = await AddWorkbookAsync(h).ConfigureAwait(true);

        var files = await new QuickOpenFileEnumerator().EnumerateAsync(project, h.Shell.Graft.CurrentSettings).ConfigureAwait(true);

        files.Should().Contain(new[] { "Modules/Module1.bas", "Classes/Cls1.cls", "Documents/Sheet1.cls" });
    }

    // ------------------------------------------------------------------
    // 場所の変更・削除（4.4）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "場所の変更: Id が変わっても同期基準が付いてくるので、続けて Pull できる（衝突にならない）")]
    public async Task 場所の変更で同期基準が付いてくる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        h.Shell.Graft.VbaBridge = bridge;
        var project = await AddWorkbookAsync(h).ConfigureAwait(true);
        var moved = Path.Combine(h.Root, "moved-mirror");
        CopyDirectory(project.Root, moved);
        h.Dialogs.PickFolderAnswer = _ => moved;

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.RelocateProjectCommand).ConfigureAwait(true);

        var after = h.Shell.Graft.ProjectPane.SelectedItem!.Project;
        after.Id.Should().NotBe(project.Id);
        after.Root.Should().Be(Path.GetFullPath(moved));
        File.Exists(h.AppPaths.GetVbaSyncFilePath(project.Id)).Should().BeFalse();
        File.Exists(h.AppPaths.GetVbaSyncFilePath(after.Id)).Should().BeTrue();

        bridge.Get(Book)!.Find("Module1")!.Code = "Sub A()\r\n  ' after move\r\nEnd Sub";
        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        File.ReadAllText(Path.Combine(moved, "Modules", "Module1.bas")).Should().Contain("after move");
        h.Dialogs.Messages.Should().BeEmpty("基準が付いてきたので衝突の通知は出ない");
    }

    [AvaloniaFact(DisplayName = "削除: 既定（Enter）は「登録だけ外す」でミラーも同期状態も残る。「ミラーと同期状態も削除する」を選ぶと同期状態が消える")]
    public async Task 削除の確認()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        h.Shell.Graft.VbaBridge = NewBridge();
        var project = await AddWorkbookAsync(h).ConfigureAwait(true);
        var pane = h.Shell.Graft.ProjectPane;

        // 1回目: 履歴は残す、登録だけ外す（どちらも各ダイアログの既定の選択肢）。
        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);

        h.Dialogs.ThreeWays.Should().HaveCount(2);
        h.Dialogs.ThreeWays[1].Title.Should().Be("ミラーと同期状態の扱い");
        pane.Items.Should().NotContain(i => i.Project.Id == project.Id);
        Directory.Exists(project.Root).Should().BeTrue("ミラーは残る");
        File.Exists(h.AppPaths.GetVbaSyncFilePath(project.Id)).Should().BeTrue("同期状態は残る");

        // 同じブックを再追加すると同じ Id に戻る。2回目: ミラーと同期状態も削除する（2つ目のダイアログで「いいえ」）。
        var again = await AddWorkbookAsync(h).ConfigureAwait(true);
        again.Id.Should().Be(project.Id);
        var answers = new Queue<bool?>(new bool?[] { true, false });
        h.Dialogs.ThreeWayAnswer = (_, _) => answers.Dequeue();

        await ShellHarness.ExecuteAsync(pane.DeleteProjectCommand).ConfigureAwait(true);

        File.Exists(h.AppPaths.GetVbaSyncFilePath(project.Id)).Should().BeFalse("同期状態は削除された");
    }

    [AvaloniaFact(DisplayName = "削除: 2つ目のダイアログでキャンセルすると、何も削除されない")]
    public async Task 削除のキャンセル()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        h.Shell.Graft.VbaBridge = NewBridge();
        var project = await AddWorkbookAsync(h).ConfigureAwait(true);
        var answers = new Queue<bool?>(new bool?[] { true, null });
        h.Dialogs.ThreeWayAnswer = (_, _) => answers.Dequeue();

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.DeleteProjectCommand).ConfigureAwait(true);

        h.Shell.Graft.ProjectPane.Items.Should().Contain(i => i.Project.Id == project.Id);
        File.Exists(h.AppPaths.GetVbaSyncFilePath(project.Id)).Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // 同率判定（7.3-0）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "同率判定: VBA プロジェクトが2つあり、どちらのミラーにも同じ名前のモジュールがあっても、現在のプロジェクトのまま確認なしで読み込まれる")]
    public async Task VBAプロジェクトが2つでも誤って切り替わらない()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        bridge.AddWorkbook(OtherBook, FakeVbaBridge.Std("Module1", "Sub A()\r\n  MsgBox \"hi\"\r\nEnd Sub"));
        h.Shell.Graft.VbaBridge = bridge;
        var first = await AddWorkbookAsync(h, Book).ConfigureAwait(true);
        var second = await AddWorkbookAsync(h, OtherBook).ConfigureAwait(true);
        var pane = h.Shell.Graft.ProjectPane;

        foreach (var current in new[] { first, second, first })
        {
            await pane.SelectAsync(pane.Items.First(i => i.Project.Id == current.Id)).ConfigureAwait(true);
            await h.ParseAsync("Modules/Module1.bas", "  MsgBox \"hi\"", "  MsgBox \"hello\"").ConfigureAwait(true);

            h.Dialogs.ThreeWays.Should().BeEmpty("一致率が同率なら現在のプロジェクトを優先し、切替の確認を出さない");
            pane.SelectedItem!.Project.Id.Should().Be(current.Id);
            h.Shell.Graft.Blocks.Should().ContainSingle();
            h.Shell.Graft.DiscardCommand.Execute(null);
        }
    }

    // ------------------------------------------------------------------
    // 時間切れと設定の伝播（5.3・8章）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "時間切れ: 戻ってこない Excel でも、取込は E805 で終わり、コマンドは実行中のまま固まらない")]
    public async Task 戻らないExcelはE805で終わる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        h.Shell.Graft.VbaBridge = bridge;
        await AddWorkbookAsync(h).ConfigureAwait(true);
        h.Shell.Graft.UpdateSettings(h.Shell.Graft.CurrentSettings with { Vba = new VbaSettings { CallTimeoutMs = 300 } });
        bridge.Hang = true;

        await ShellHarness.ExecuteAsync(h.Shell.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);

        h.Shell.Graft.ProjectPane.PullFromExcelCommand.CanExecute(null).Should().BeTrue("実行中のまま残らない");
        h.Dialogs.Messages.Should().ContainSingle().Which.Message.Should().Contain("E805");
    }

    [AvaloniaFact(DisplayName = "設定: Vba.ComTimeoutMs／CallTimeoutMs は、設定の保存のたびにブリッジへ伝わる（次の呼び出しから効く）")]
    public async Task 時間切れの設定がブリッジへ伝わる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();

        h.Shell.Graft.VbaBridge = bridge;
        bridge.CallTimeoutMs.Should().Be(30000, "既定値が渡る");

        h.Shell.Graft.UpdateSettings(h.Shell.Graft.CurrentSettings with { Vba = new VbaSettings { ComTimeoutMs = 2000, CallTimeoutMs = 9000 } });

        bridge.ComTimeoutMs.Should().Be(2000);
        bridge.CallTimeoutMs.Should().Be(9000);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
