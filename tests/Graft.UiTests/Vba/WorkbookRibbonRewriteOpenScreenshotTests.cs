using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.Tests.TestSupport;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// リボンの書き換え（機能 F の段階 F4: Excel で開いているブック）の画面を、ヘッドレスで描画して PNG に残す
/// （確認ダイアログの開いているブックの文面・処理中の表示・完了の通知の「Excel で開く」・失敗の案内・設定の欄の目視確認用）。
/// 保存先は環境変数 <c>GRAFT_F4_SCREENSHOT_DIR</c>。未設定のときは何も保存せず、描画できることだけを確かめる。
/// <b>同じ名前のファイルが既にあれば上書きしない</b>（前の回の画像を比べられるように残す）。
/// </summary>
public class WorkbookRibbonRewriteOpenScreenshotTests : IDisposable
{
    private const string Ribbon14 = "CustomUI/customUI14.xml";

    private static readonly string XmlB = RibbonPackageBuilder.SampleRibbon
        .Replace("label=\"業務\"", "label=\"業務B\"", StringComparison.Ordinal)
        .Replace("label=\"実行\"", "label=\"集計を実行\"", StringComparison.Ordinal);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-ribbon-f4-shot", Guid.NewGuid().ToString("N"));
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後片付けの失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    private static async Task SaveAsync(Window window, string fileName)
    {
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(40).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }

        var size = new Size(double.IsNaN(window.Width) ? window.Bounds.Width : window.Width, double.IsNaN(window.Height) ? window.Bounds.Height : window.Height);
        window.Measure(size);
        window.Arrange(new Rect(size));
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");

        var dir = Environment.GetEnvironmentVariable("GRAFT_F4_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path)) return; // 既存のファイルは上書きしない。
        frame!.Save(path);
    }

    private static VbaRibbonRewritePlan Plan(bool open, bool unsaved, int warnings = 0, bool newPart = false, int preceding = 0) => new(
        "p", @"C:\Users\YUGO\Documents\計画.xlsm", "計画.xlsm",
        new[] { new VbaRibbonRewriteTarget(VbaRibbonFormat.Office2010, Ribbon14, XmlB, "h", newPart ? null : RibbonPackageBuilder.SampleRibbon, newPart) },
        Enumerable.Range(1, warnings)
            .Select(i => new VbaRibbonRewriteWarning(Ribbon14, new VbaRibbonFinding(
                Severity.Warning, 12, 6 + i, 11, $"コールバック「OnRun{i}」: VBA に見つかりません（onAction（button btnRun{i}））。")))
            .ToList(),
        @"C:\Users\YUGO\AppData\Local\Graft\vba-sync\0f3a9c.workbooks", 1_234_567, DateTime.UtcNow,
        open ? new VbaWorkbookRewriteState(true, Saved: !unsaved) : null, preceding);

    private Window Show(VbaRibbonRewriteViewModel vm)
    {
        var window = _windows.Track(new VbaRibbonRewriteWindow(vm));
        window.Show();
        return window;
    }

    private static VbaRibbonRewriteViewModel NewViewModel(VbaRibbonRewritePlan plan)
        => new(plan, new Settings(), new AvaloniaUiServices());

    [AvaloniaFact(DisplayName = "画面の確認: 確認ダイアログ（開いているブック・未保存の変更あり）")]
    public async Task 開いている_未保存あり()
    {
        var window = Show(NewViewModel(Plan(open: true, unsaved: true, warnings: 2)));
        window.FindControl<Button>("RewriteButton")!.Content.Should().Be("保存して書き換える");
        await SaveAsync(window, "ribbonF4-confirm-open-unsaved.png").ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "画面の確認: 確認ダイアログ（開いているブック・未保存なし）。⚠ の段落が無い")]
    public async Task 開いている_未保存なし()
    {
        var window = Show(NewViewModel(Plan(open: true, unsaved: false)));
        window.FindControl<TextBlock>("UnsavedWarningText")!.IsVisible.Should().BeFalse();
        await SaveAsync(window, "ribbonF4-confirm-open-saved.png").ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "画面の確認: 確認ダイアログ（VBA の反映に続けて・新規の追加・警告 7 件）")]
    public async Task 開いている_続けて_新規_警告()
    {
        var window = Show(NewViewModel(Plan(open: true, unsaved: true, warnings: 7, newPart: true, preceding: 3)));
        await SaveAsync(window, "ribbonF4-confirm-open-after-vba-new.png").ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "画面の確認: 確認ダイアログ（開いているブックで「差分を見る」を押したところ）")]
    public async Task 開いている_差分()
    {
        var vm = NewViewModel(Plan(open: true, unsaved: true, warnings: 1));
        var window = Show(vm);
        vm.ToggleDiffCommand.Execute(null);
        await SaveAsync(window, "ribbonF4-confirm-open-diff.png").ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "画面の確認: 確認ダイアログ（開いていないブック。F3 から変わっていないことの見比べ用）")]
    public async Task 開いていない()
    {
        var window = Show(NewViewModel(Plan(open: false, unsaved: false)));
        window.FindControl<Button>("RewriteButton")!.Content.Should().Be("書き換える");
        await SaveAsync(window, "ribbonF4-confirm-closed.png").ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "画面の確認: 設定の「VBA 連携」タブの「保存・開き直しの時間切れ」の欄")]
    public async Task 設定の欄()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices()) { VbaSupportProbe = () => true };
        await vm.InitializeAsync().ConfigureAwait(true);
        var window = _windows.Track(new SettingsWindow(vm) { Width = 1000, Height = 700 });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "CategoryTabControl");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "VBA 連携"));
        Dispatcher.UIThread.RunJobs();
        var last = window.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "ブックの保存・開き直しの時間切れ");
        // 欄の下の説明文まで画面に入れる（欄の高さに余白を足して送る）。
        last.BringIntoView(new Rect(0, 0, last.Bounds.Width, last.Bounds.Height + 70));
        Dispatcher.UIThread.RunJobs();
        await SaveAsync(window, "ribbonF4-settings.png").ConfigureAwait(true);
    }

    private async Task<VbaP3Rig> ArrangeMainAsync(bool closeBook)
    {
        Directory.CreateDirectory(_root);
        var book = Path.Combine(_root, "計画.xlsm");
        File.WriteAllBytes(book, RibbonPackageBuilder.ExcelWorkbook(RibbonPackageBuilder.SampleRibbon));
        var r = await VbaP3Rig.CreateAsync(withWindow: true).ConfigureAwait(true);
        r.RibbonFile = book;
        r.Graft.VbaRibbonWorkbookFile = _ => book;
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Ribbon14), XmlB);
        if (closeBook) r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        await r.Graft.ProjectPane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        return r;
    }

    [AvaloniaFact(DisplayName = "画面の確認: 書き換えの処理中（中央ペインが処理中の表示で、段階の名前が通知に出ている）")]
    public async Task 処理中の表示()
    {
        using var r = await ArrangeMainAsync(closeBook: false).ConfigureAwait(true);
        r.Graft.ProjectPane.SetRibbonRewriteBusy.Should().NotBeNull();
        r.Graft.ProjectPane.SetRibbonRewriteBusy!(true);
        r.Graft.ProjectPane.ShowRibbonProgress!(VbaRibbonRewriteText.ProgressSaveAndClose);
        r.Graft.State.Should().Be(CenterPaneState.Loading);

        await SaveAsync(r.H.Window!, "ribbonF4-progress.png").ConfigureAwait(true);
        r.Graft.ProjectPane.SetRibbonRewriteBusy!(false);
    }

    [AvaloniaFact(DisplayName = "画面の確認: 完了の通知に「Excel で開く」（開いていなかったブックへ書き換えたあと）")]
    public async Task 完了の通知()
    {
        using var r = await ArrangeMainAsync(closeBook: true).ConfigureAwait(true);
        r.Graft.ProjectPane.ShowRibbonRewriteAsync = _ => Task.FromResult(true);
        r.Graft.ProjectPane.CreateRibbonRewriteViewModel = plan => new VbaRibbonRewriteViewModel(plan, new Settings(), new AvaloniaUiServices());

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        r.Graft.Notice.ActionLabel.Should().Be("Excel で開く");
        await SaveAsync(r.H.Window!, "ribbonF4-notice-open-in-excel.png").ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "画面の確認: 開き直せなかったときの案内（E822・［Excel で開く］）")]
    public async Task 開き直せなかった案内()
    {
        using var r = await ArrangeMainAsync(closeBook: false).ConfigureAwait(true);
        r.Graft.ProjectPane.ShowRibbonRewriteAsync = _ => Task.FromResult(true);
        r.Graft.ProjectPane.CreateRibbonRewriteViewModel = plan => new VbaRibbonRewriteViewModel(plan, new Settings(), new AvaloniaUiServices());
        r.Bridge.ExcelQuitBeforeReopen = true;
        r.H.Dialogs.ActionAnswer = (_, _, _) => false;

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        var (title, message, action) = r.H.Dialogs.Actions.Should().ContainSingle().Subject;
        // 実際の知らせの窓（VbaChoiceWindow の見た目）で描く。
        var window = _windows.Track(new VbaChoiceWindow(new VbaChoiceRequest(title, message, new[]
        {
            new VbaChoiceOption("open", action),
            new VbaChoiceOption("close", "閉じる", IsDefault: true),
        })));
        window.Show();
        await SaveAsync(window, "ribbonF4-failure-reopen.png").ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "画面の確認: 止めるブック（読み取り専用で開いている）の案内（E819）")]
    public async Task 止めるブックの案内()
    {
        using var r = await ArrangeMainAsync(closeBook: false).ConfigureAwait(true);
        r.Bridge.Get(VbaP3Rig.Book)!.IsReadOnly = true;

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PushRibbonCommand).ConfigureAwait(true);

        var (title, message) = r.H.Dialogs.Messages.Last();
        var window = _windows.Track(new VbaChoiceWindow(new VbaChoiceRequest(title, message, new[] { new VbaChoiceOption("ok", "OK", IsDefault: true) })));
        window.Show();
        await SaveAsync(window, "ribbonF4-stopped-readonly.png").ConfigureAwait(true);
    }
}
