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
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// シートごとの見せ方（仕様書_ブックの情報と依頼の観点 5.2。v0.4）の画面を、<see cref="FakeVbaBridge"/> を使って
/// ヘッドレスで描画し、PNG にして残す（見た目の目視確認用）。保存先は環境変数 <c>GRAFT_SHEET_SCREENSHOT_DIR</c>。
/// 未設定のときは何も保存せず、描画できることだけを確かめる（通常のテスト実行でファイルを散らかさないため。
/// <see cref="WorkbookInfoScreenshotTests"/> と同じ作り）。
/// </summary>
public class WorkbookInfoSheetViewScreenshotTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-sheet-shot", Guid.NewGuid().ToString("N"));

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

        var dir = Environment.GetEnvironmentVariable("GRAFT_SHEET_SCREENSHOT_DIR");
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

    /// <summary>3 枚のシート（集計・設定・明細）。明細は 1200 行 × 60 列で、「全部」の上限で切れる。</summary>
    private static VbaWorkbookInfoSnapshot ThreeSheets(FakeVbaBridge.Workbook workbook)
    {
        workbook.SheetGrids["Sheet3"] = Enumerable.Range(1, 1200)
            .Select(r => Enumerable.Range(1, 60).Select(c => $"{r}-{c}").ToArray())
            .ToArray();
        var sample = WorkbookInfoTests.Sample();
        var detail = new VbaSheetInfo(
            "明細", "Sheet3", VbaSheetVisibility.Visible, "A1:BH1200", Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
        return sample with { Sheets = sample.Sheets.Append(detail).ToArray() };
    }

    private static async Task<VbaP3Rig> NewRigAsync(bool withWindow = false)
    {
        var rig = await VbaP3Rig.CreateAsync(
            new Settings { ShowPreview = false },
            arrange: bridge => bridge.Get(VbaP3Rig.Book)!.Info = ThreeSheets(bridge.Get(VbaP3Rig.Book)!),
            withWindow: withWindow).ConfigureAwait(true);
        rig.ChoiceAnswer = _ => "give";
        return rig;
    }

    private async Task<ContextCollectWindow> OpenWindowAsync(VbaP3Rig r)
    {
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        var window = _windows.Track(new ContextCollectWindow(context) { Height = 900 });
        window.Show();
        await WaitForIdleAsync(context).ConfigureAwait(true);
        return window;
    }

    [AvaloniaFact(DisplayName = "画面の確認: シートごとの見せ方の一覧（既定は全シート構成だけ）")]
    public async Task 一覧の既定()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var window = await OpenWindowAsync(r).ConfigureAwait(true);

        Save(window, "sheet-list-default.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: 見せ方の選択（見出し＋N 行・全部で切れた注記・範囲を指定・出さない）と、値が渡る注意")]
    public async Task 一覧で見せ方を選ぶ()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet3", VbaSheetViewMode.All).ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet2", VbaSheetViewMode.None).ConfigureAwait(true);
        var window = await OpenWindowAsync(r).ConfigureAwait(true);

        Save(window, "sheet-list-modes.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: 範囲を指定の入力（空のとき・不正な入力の誤り・正しい入力）")]
    public async Task 範囲の入力と検証()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet3", VbaSheetViewMode.Range).ConfigureAwait(true);
        var window = await OpenWindowAsync(r).ConfigureAwait(true);
        Save(window, "sheet-range-empty.png");

        var row = context.WorkbookInfo.Sheets.Single(s => s.CodeName == "Sheet3");
        row.RangeText = "売上";
        await ShellHarness.WaitUntilAsync(() => row.RangeError.Contains("名前付き範囲")).ConfigureAwait(true);
        await Task.Delay(60).ConfigureAwait(true);
        Save(window, "sheet-range-invalid.png");

        row.RangeText = "B10:E40";
        await ShellHarness.WaitUntilAsync(() => !row.HasRangeError && row.PassesValues).ConfigureAwait(true);
        await Task.Delay(60).ConfigureAwait(true);
        Save(window, "sheet-range-valid.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: 値が渡る見せ方を選ぶ確認ダイアログ（動詞のボタン。既定は「やめる」）")]
    public async Task 確認ダイアログ()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        r.ChoiceAnswer = null;

        await context.SetSheetViewModeAsync("Sheet3", VbaSheetViewMode.All).ConfigureAwait(true);

        var request = r.Choices.Should().ContainSingle().Subject;
        var dialog = _windows.Track(new VbaChoiceWindow(request));
        dialog.Show();
        await Task.Delay(100).ConfigureAwait(true);
        dialog.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        dialog.Width = Math.Max(dialog.MinWidth, dialog.DesiredSize.Width);
        dialog.Height = dialog.DesiredSize.Height;
        Save(dialog, "sheet-confirm-dialog.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: プレビューに出たシートごとの値（見出し＋N 行・全部の切り詰め注記）")]
    public async Task プレビュー()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet3", VbaSheetViewMode.Range).ConfigureAwait(true);
        await context.SetSheetRangeAsync("Sheet3", "B10:E14").ConfigureAwait(true);
        var window = await OpenWindowAsync(r).ConfigureAwait(true);

        await ShellHarness.ExecuteAsync(context.PreviewCommand).ConfigureAwait(true);
        window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 1;
        await Task.Delay(150).ConfigureAwait(true);
        // シートの値の節まで、プレビューをスクロールする（先頭は前提・構成で、値の節は下にある）。
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.Extent.Height > s.Viewport.Height * 2).OrderByDescending(s => s.Extent.Height).First();
        scroller.Offset = new Vector(0, 560);
        await Task.Delay(150).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();
        Save(window, "sheet-preview.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: 依頼文コピーのドロップダウンの、値が渡るシートの注意")]
    public async Task ドロップダウンの注意()
    {
        using var r = await NewRigAsync(withWindow: true).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet3", VbaSheetViewMode.All).ConfigureAwait(true);

        var copy = r.Graft.PromptCopy!;
        copy.IsOpen = true;
        await ShellHarness.WaitUntilAsync(() => copy.Templates.Count > 0).ConfigureAwait(true);
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(40).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }

        var window = r.H.Window!;
        var border = (Border)window.GetVisualDescendants().OfType<PromptFocusPanel>().First()
            .GetVisualAncestors().OfType<Border>().First(b => b.MinWidth >= 280);
        var host = _windows.Track(new Window { Width = 460, Height = 760, DataContext = r.H.Shell, Background = (Avalonia.Media.IBrush?)window.FindResource("BgBase") });
        var content = (Control)border.Child!;
        border.Child = null;
        host.Content = new Border
        {
            Background = border.Background, BorderBrush = border.BorderBrush, BorderThickness = border.BorderThickness,
            Padding = border.Padding, Margin = new Thickness(16), Child = content, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        host.Show();
        Save(host, "sheet-dropdown-notice.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: 設定の「VBA 連携」タブ（見出し＋N 行の N の既定・全部の最大行数と列数）")]
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
        Save(window, "sheet-settings-vba.png");
    }
}
