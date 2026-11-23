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
/// リボンの書き換え（機能 F の段階 F3）の画面を、ヘッドレスで描画して PNG に残す（確認ダイアログ・手動の操作の入口・設定の欄の目視確認用）。
/// 保存先は環境変数 <c>GRAFT_F3_SCREENSHOT_DIR</c>。未設定のときは何も保存せず、描画できることだけを確かめる。
/// <b>同じ名前のファイルが既にあれば上書きしない</b>（前の回の画像を比べられるように残す）。
/// </summary>
public class WorkbookRibbonRewriteScreenshotTests : IDisposable
{
    private const string Ribbon14 = "CustomUI/customUI14.xml";

    private static readonly string XmlB = RibbonPackageBuilder.SampleRibbon
        .Replace("label=\"業務\"", "label=\"業務B\"", StringComparison.Ordinal)
        .Replace("label=\"実行\"", "label=\"集計を実行\"", StringComparison.Ordinal);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-ribbon-f3-shot", Guid.NewGuid().ToString("N"));
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

        var dir = Environment.GetEnvironmentVariable("GRAFT_F3_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path)) return; // 既存のファイルは上書きしない。
        frame!.Save(path);
    }

    private static VbaRibbonRewritePlan Plan(int warnings, bool newPart) => new(
        "p", @"C:\Users\YUGO\Documents\計画.xlsm", "計画.xlsm",
        new[] { new VbaRibbonRewriteTarget(VbaRibbonFormat.Office2010, Ribbon14, XmlB, "h", newPart ? null : RibbonPackageBuilder.SampleRibbon, newPart) },
        Enumerable.Range(1, warnings)
            .Select(i => new VbaRibbonRewriteWarning(Ribbon14, new VbaRibbonFinding(
                Severity.Warning, 12, 6 + i, 11, $"コールバック「OnRun{i}」: VBA に見つかりません（onAction（button btnRun{i}））。")))
            .ToList(),
        @"C:\Users\YUGO\AppData\Local\Graft\vba-sync\0f3a9c.workbooks", 1_234_567, DateTime.UtcNow);

    private Window Show(VbaRibbonRewriteViewModel vm)
    {
        var window = _windows.Track(new VbaRibbonRewriteWindow(vm));
        window.Show();
        return window;
    }

    private static VbaRibbonRewriteViewModel NewViewModel(VbaRibbonRewritePlan plan)
        => new(plan, new Settings(), new AvaloniaUiServices());

    [AvaloniaFact(DisplayName = "画面の確認: 確認ダイアログ（開いていないブックの差し替え・警告なし）")]
    public async Task 確認ダイアログ()
    {
        var window = Show(NewViewModel(Plan(0, newPart: false)));
        await SaveAsync(window, "ribbonF3-confirm.png").ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "画面の確認: 確認ダイアログ（新規の追加・警告 7 件で「ほか 2 件」と「すべて見る」）")]
    public async Task 確認ダイアログ_新規と警告()
    {
        var window = Show(NewViewModel(Plan(7, newPart: true)));
        await SaveAsync(window, "ribbonF3-confirm-new-warnings.png").ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "画面の確認: 確認ダイアログで「差分を見る」を押したところ")]
    public async Task 確認ダイアログ_差分()
    {
        var vm = NewViewModel(Plan(2, newPart: false));
        var window = Show(vm);
        vm.ToggleDiffCommand.Execute(null);
        await SaveAsync(window, "ribbonF3-confirm-diff.png").ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "画面の確認: プロジェクトの右クリック（リボンをブックへ反映… ／ リボンの XML を検査）")]
    public async Task 右クリックの入口()
    {
        Directory.CreateDirectory(_root);
        var book = Path.Combine(_root, "計画.xlsm");
        File.WriteAllBytes(book, RibbonPackageBuilder.ExcelWorkbook(RibbonPackageBuilder.SampleRibbon));
        using var r = await VbaP3Rig.CreateAsync(withWindow: true).ConfigureAwait(true);
        r.RibbonFile = book;
        r.Graft.VbaRibbonWorkbookFile = _ => book;
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Ribbon14), XmlB);
        r.Bridge.CloseWorkbook(VbaP3Rig.Book);
        await r.Graft.ProjectPane.RefreshVbaBadgesAsync(checkWorkbookOpen: true).ConfigureAwait(true);
        r.Graft.ProjectPane.PushRibbonCommand.CanExecute(null).Should().BeTrue();

        var window = r.H.Window!;
        var view = window.GetVisualDescendants().OfType<ProjectPane>().First();
        var list = view.GetVisualDescendants().OfType<ListBox>().First(l => l.ContextMenu is not null);
        list.ContextMenu!.Open(list);
        Dispatcher.UIThread.RunJobs();
        list.ContextMenu.Items.OfType<MenuItem>().Should().Contain(m => Equals(m.Header, "リボンをブックへ反映…") && m.IsVisible)
            .And.Contain(m => Equals(m.Header, "リボンの XML を検査") && m.IsVisible);
        await SaveAsync(window, "ribbonF3-project-menu.png").ConfigureAwait(true);
        list.ContextMenu.Close();
    }

    [AvaloniaFact(DisplayName = "画面の確認: 「リボンの XML を検査」の結果の文")]
    public async Task 検査の結果()
    {
        Directory.CreateDirectory(_root);
        var book = Path.Combine(_root, "計画.xlsm");
        File.WriteAllBytes(book, RibbonPackageBuilder.ExcelWorkbook(RibbonPackageBuilder.SampleRibbon));
        using var r = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        r.RibbonFile = book;
        r.Graft.VbaRibbonWorkbookFile = _ => book;
        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.PullFromExcelCommand).ConfigureAwait(true);
        File.WriteAllText(r.MirrorPath(Ribbon14), XmlB.Replace("size=\"large\"", "size=\"big\"", StringComparison.Ordinal)
            .Replace("onAction=\"OnPick\"", "onAction=\"OnPickMissing\"", StringComparison.Ordinal));

        await ShellHarness.ExecuteAsync(r.Graft.ProjectPane.InspectRibbonCommand).ConfigureAwait(true);
        var (title, message) = r.H.Dialogs.Messages.Last();

        // 実際の知らせの窓（AvaloniaDialogService と同じ VbaChoiceWindow の見た目）で描く。
        var window = _windows.Track(new VbaChoiceWindow(new VbaChoiceRequest(title, message, new[] { new VbaChoiceOption("ok", "OK", IsDefault: true) })));
        window.Show();
        await SaveAsync(window, "ribbonF3-inspect.png").ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "画面の確認: 設定の「VBA 連携」タブの「リボン（customUI）の書き換え」")]
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
        // 節の最後の欄（ブックの控え全体の容量）まで送り、節全体が画面に入るようにする。
        var last = window.GetVisualDescendants().OfType<TextBox>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "ブックの控え全体の容量");
        last.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        await SaveAsync(window, "ribbonF3-settings.png").ConfigureAwait(true);
    }
}
