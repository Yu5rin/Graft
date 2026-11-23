using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Tests.TestSupport;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// 段階 F5 の画面（確認ダイアログの「VBA の反映に続けて」と Auto_Open の注意、衝突ダイアログのリボンの項目、「ブックの控え」、
/// 丸ごと戻す操作の強い確認）をヘッドレスで描画して PNG に残す（目視確認用）。保存先は環境変数 <c>GRAFT_F5_SCREENSHOT_DIR</c>
/// （<c>ribbonF5-*.png</c>）。未設定のときは何も保存せず、描画できることだけを確かめる。<b>同じ名前のファイルがあれば上書きしない</b>。
/// </summary>
public class WorkbookRibbonFlowScreenshotTests : IDisposable
{
    private const string Ribbon14 = "CustomUI/customUI14.xml";

    private static readonly string XmlB = RibbonPackageBuilder.SampleRibbon.Replace("label=\"業務\"", "label=\"業務B\"", StringComparison.Ordinal);

    private readonly ShownWindowTracker _windows = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-ribbon-f5-shot", Guid.NewGuid().ToString("N"));

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

        var dir = Environment.GetEnvironmentVariable("GRAFT_F5_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path)) return;
        frame!.Save(path);
    }

    private T Show<T>(T window) where T : Window
    {
        _windows.Track(window);
        window.Show();
        return window;
    }

    [AvaloniaFact(DisplayName = "画面の確認: リボンの確認（VBA の反映に続けて・開いているブック・未保存あり・Auto_Open あり）")]
    public async Task 確認_VBAに続けて()
    {
        var plan = new VbaRibbonRewritePlan(
            "p", @"C:\Users\YUGO\Documents\計画.xlsm", "計画.xlsm",
            new[] { new VbaRibbonRewriteTarget(VbaRibbonFormat.Office2010, Ribbon14, XmlB, "h", RibbonPackageBuilder.SampleRibbon, false) },
            Array.Empty<VbaRibbonRewriteWarning>(), @"C:\Users\YUGO\AppData\Local\Graft\vba-sync\0f3a9c.workbooks", 1_234_567, DateTime.UtcNow,
            new VbaWorkbookRewriteState(true, Saved: false), 3, new[] { "Auto_Open" });
        var window = Show(new VbaRibbonRewriteWindow(new VbaRibbonRewriteViewModel(plan, new Settings(), new AvaloniaUiServices())));
        window.FindControl<TextBlock>("OpenNoticesText")!.Text.Should().Contain("Auto_Open は、Graft の開き直しでは動きません");
        await SaveAsync(window, "ribbonF5-confirm-after-vba.png").ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "画面の確認: 衝突ダイアログ（リボンとモジュールの衝突。左はブックのファイル側）")]
    public async Task 衝突_リボン()
    {
        var items = new[]
        {
            VbaConflictItem.ForRibbon(Ribbon14, RibbonPackageBuilder.SampleRibbon.Replace("label=\"業務\"", "label=\"業務C\"", StringComparison.Ordinal), XmlB,
                VbaConflictReason.BothChanged),
            new VbaConflictItem("Modules/Module1.bas", VbaModuleKind.Standard, "Module1", "Sub A()\r\n  MsgBox \"vbe\"\r\nEnd Sub", "Sub A()\r\n  MsgBox \"graft\"\r\nEnd Sub"),
        };
        var vm = new VbaConflictViewModel(new VbaConflictRequest(VbaConflictScene.ManualPull, items), new Settings(), new AvaloniaUiServices());
        var window = Show(new VbaConflictWindow(vm));
        vm.DiffCaptionText.Should().Contain("リボンはブックのファイル側");
        vm.GuideText.Should().Contain("リボン（CustomUI/）");
        await SaveAsync(window, "ribbonF5-conflict-ribbon.png").ConfigureAwait(true);
    }

    private VbaRescueViewModel NewRescueViewModel(int backups)
    {
        Directory.CreateDirectory(_root);
        var paths = new AppPaths(_root);
        var project = new Project { Id = "p1", Name = "計画", Root = Path.Combine(_root, "mirror"), Vba = new VbaLink { WorkbookFullName = @"C:\Work\計画.xlsm" } };
        var vm = new VbaRescueViewModel(project, new VbaRescueService(paths, new VbaSyncStateStore(paths)), new Settings(), new AvaloniaUiServices());
        var items = Enumerable.Range(0, backups)
            .Select(i => new VbaWorkbookBackupItem(
                $"計画-2026112{3 - i}-1000{i}0-ribbon.xlsm", $@"C:\x\{i}", new VbaWorkbookBackupFileName("計画", new DateTime(2026, 11, 23 - i, 10, 0, i), 1, ".xlsm"), 4_800_000L + i * 1000))
            .ToList();
        vm.ListWorkbookBackups = _ => items;
        vm.ReplaceWorkbookFromBackupAsync = (_, _) => Task.FromResult(string.Empty);
        vm.DeleteWorkbookBackup = _ => (true, null);
        vm.ExportWorkbookBackup = (_, _) => (true, null);
        vm.WorkbookBackupDirectory = @"C:\Users\YUGO\AppData\Local\Graft\vba-sync\p1.workbooks";
        return vm;
    }

    [AvaloniaFact(DisplayName = "画面の確認: 控えの窓の「ブックの控え」（一覧・説明・ボタン）")]
    public async Task 控えの窓_ブックの控え()
    {
        var vm = NewRescueViewModel(3);
        var window = Show(new VbaRescueWindow(vm));
        vm.ShowWorkbooks = true;
        window.FindControl<Button>("ReplaceWorkbookButton")!.IsVisible.Should().BeTrue();
        window.FindControl<Button>("DeleteButton")!.IsVisible.Should().BeFalse();
        vm.WorkbookItems.Should().HaveCount(3);
        await SaveAsync(window, "ribbonF5-rescue-workbooks.png").ConfigureAwait(true);
    }

    [AvaloniaFact(DisplayName = "画面の確認: 控えの窓の「ブックの控え」が空のとき・モジュールの控えへ戻したとき")]
    public async Task 控えの窓_空()
    {
        var vm = NewRescueViewModel(0);
        var window = Show(new VbaRescueWindow(vm));
        vm.ShowWorkbooks = true;
        window.FindControl<TextBlock>("WorkbooksEmptyText")!.IsVisible.Should().BeTrue();
        await SaveAsync(window, "ribbonF5-rescue-workbooks-empty.png").ConfigureAwait(true);
        vm.ShowWorkbooks = false;
        window.FindControl<Button>("RestoreButton")!.IsVisible.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "画面の確認: 「この控えでブックを置き換える」の強い確認（開いているブック・未保存あり。既定は「やめる」）")]
    public async Task 丸ごと戻す_確認()
    {
        var plan = new VbaWorkbookRestorePlan(
            "p1", @"C:\Users\YUGO\Documents\計画.xlsm", "計画.xlsm", @"C:\x\a.xlsm", "計画-20261123-100000-ribbon.xlsm",
            new DateTime(2026, 11, 23, 10, 0, 0), 4_800_000, "abc", @"C:\Users\YUGO\AppData\Local\Graft\vba-sync\p1.workbooks", 4_900_000, DateTime.UtcNow,
            new VbaWorkbookRewriteState(true, Saved: false));
        var request = new VbaChoiceRequest(
            VbaWorkbookRestoreText.Title, VbaWorkbookRestoreText.Message(plan),
            new[]
            {
                new VbaChoiceOption("replace", VbaWorkbookRestoreText.ConfirmButton),
                new VbaChoiceOption("cancel", VbaWorkbookRestoreText.CancelButton, IsDefault: true),
            });
        var window = Show(new VbaChoiceWindow(request));
        await SaveAsync(window, "ribbonF5-restore-confirm.png").ConfigureAwait(true);
    }
}
