using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.Tests.TestSupport;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;
using Xunit;

namespace Graft.UiTests.Vba;

/// <summary>
/// リボン（customUI。仕様書_ブックの情報と依頼の観点 15章）の画面を、<see cref="FakeVbaBridge"/> と一時ファイルの zip を使って
/// ヘッドレスで描画し、PNG にして残す（見た目の目視確認用）。保存先は環境変数 <c>GRAFT_RIBBON_SCREENSHOT_DIR</c>。
/// 未設定のときは何も保存せず、描画できることだけを確かめる（通常のテスト実行でファイルを散らかさないため。
/// <see cref="WorkbookInfoSheetViewScreenshotTests"/> と同じ作り）。
/// </summary>
public class WorkbookRibbonScreenshotTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-ribbon-shot", Guid.NewGuid().ToString("N"));

    public WorkbookRibbonScreenshotTests() => Directory.CreateDirectory(_root);

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

    private static void Save(Window window, string fileName)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");

        var dir = Environment.GetEnvironmentVariable("GRAFT_RIBBON_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, fileName));
    }

    private static async Task WaitForIdleAsync(ContextCollectViewModel context)
    {
        await Task.Delay(100).ConfigureAwait(true);
        await ShellHarness.WaitUntilAsync(() => !context.IsScanning).ConfigureAwait(true);
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(40).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private string WriteBook(string name, string? customUi14)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, RibbonPackageBuilder.Workbook(customUi14));
        return path;
    }

    private static async Task<VbaP3Rig> NewRigAsync(string? ribbonFile)
    {
        var rig = await VbaP3Rig.CreateAsync(
            new Settings { ShowPreview = false },
            arrange: bridge =>
            {
                var book = bridge.Get(VbaP3Rig.Book)!;
                book.Info = WorkbookInfoTests.Sample();
                book.Find("Module1")!.Code =
                    "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub\r\nPrivate Sub GetEnabled(control As IRibbonControl, ByRef r)\r\nEnd Sub";
            }).ConfigureAwait(true);
        rig.RibbonFile = ribbonFile;
        return rig;
    }

    private async Task<ContextCollectWindow> OpenWindowAsync(VbaP3Rig r)
    {
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        var window = _windows.Track(new ContextCollectWindow(context) { Height = 1000 });
        window.Show();
        await WaitForIdleAsync(context).ConfigureAwait(true);
        return window;
    }

    [AvaloniaFact(DisplayName = "画面の確認: リボンの行（内容も出す。取得日時・ファイルの更新日時・コールバックの照合・VBA に見つからないもの）")]
    public async Task 行_内容も出す()
    {
        using var r = await NewRigAsync(WriteBook("計画.xlsm", RibbonPackageBuilder.SampleRibbon)).ConfigureAwait(true);
        var window = await OpenWindowAsync(r).ConfigureAwait(true);

        Save(window, "ribbon-row-full.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: リボンの行（構成だけ／出さない）")]
    public async Task 行_構成だけと出さない()
    {
        using var r = await NewRigAsync(WriteBook("計画.xlsm", RibbonPackageBuilder.SampleRibbon)).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.CycleRibbonLevelAsync().ConfigureAwait(true);
        var window = await OpenWindowAsync(r).ConfigureAwait(true);
        Save(window, "ribbon-row-structure.png");

        await context.CycleRibbonLevelAsync().ConfigureAwait(true);
        await WaitForIdleAsync(context).ConfigureAwait(true);
        Save(window, "ribbon-row-hidden.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: リボンの設定が無いブック・読めないブック（理由 1 行）")]
    public async Task 行_読めないとき()
    {
        using var none = await NewRigAsync(WriteBook("なし.xlsm", null)).ConfigureAwait(true);
        var noneWindow = await OpenWindowAsync(none).ConfigureAwait(true);
        Save(noneWindow, "ribbon-row-none.png");

        using var broken = await NewRigAsync(WriteBook("壊れ.xlsm", "<customUI><ribbon></customUI>")).ConfigureAwait(true);
        var brokenWindow = await OpenWindowAsync(broken).ConfigureAwait(true);
        Save(brokenWindow, "ribbon-row-unreadable.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: プレビューに出たリボン（構成・コールバックの照合・XML の全文）")]
    public async Task プレビュー()
    {
        using var r = await NewRigAsync(WriteBook("計画.xlsm", RibbonPackageBuilder.SampleRibbon)).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        // ブックの情報は「構成だけ」にして、リボンの節を画面の上のほうへ寄せる（並びは「前提 → ブックの情報 → リボン」のまま）。
        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        var window = await OpenWindowAsync(r).ConfigureAwait(true);

        await ShellHarness.ExecuteAsync(context.PreviewCommand).ConfigureAwait(true);
        window.SelectPreviewTab();
        await Task.Delay(150).ConfigureAwait(true);
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.Extent.Height > s.Viewport.Height * 2).OrderByDescending(s => s.Extent.Height).First();
        var target = context.PreviewLines.ToList().FindIndex(l => l.Text.Contains("# リボン（customUI）", StringComparison.Ordinal));
        target.Should().BeGreaterThan(0, "プレビューにリボンの節が出ている");
        scroller.Offset = new Vector(0, Math.Max(0, target * (scroller.Extent.Height / Math.Max(1, context.PreviewLines.Count)) - 40));
        await Task.Delay(150).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();
        Save(window, "ribbon-preview.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: 設定の「VBA 連携」タブのリボンの項目")]
    public async Task 設定パネル()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices()) { VbaSupportProbe = () => true };
        await vm.InitializeAsync().ConfigureAwait(true);
        var window = _windows.Track(new SettingsWindow(vm) { Width = 1000, Height = 1100 });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "CategoryTabControl");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "VBA 連携"));
        Dispatcher.UIThread.RunJobs();
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.Extent.Height > s.Viewport.Height).OrderByDescending(s => s.Extent.Height).FirstOrDefault();
        if (scroller is not null) scroller.Offset = new Vector(0, 520);
        for (var i = 0; i < 3; i++) Dispatcher.UIThread.RunJobs();
        Save(window, "ribbon-settings-vba.png");
    }
}
