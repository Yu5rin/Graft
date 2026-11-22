using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests.Vba;

/// <summary>
/// ブックの情報（段階 Q2）の画面を、<see cref="FakeVbaBridge"/> を使ってヘッドレスで描画し、PNG にして残す
/// （見た目の目視確認用）。<see cref="VbaP4ScreenshotTests"/> と同じ作りで、保存先は環境変数
/// <c>GRAFT_Q2_SCREENSHOT_DIR</c>。未設定のときは何も保存せず、描画できることだけを確かめる
/// （通常のテスト実行でファイルを散らかさないため）。
/// </summary>
public class WorkbookInfoScreenshotTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-q2-shot", Guid.NewGuid().ToString("N"));

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

        var dir = Environment.GetEnvironmentVariable("GRAFT_Q2_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, fileName));
    }

    /// <summary>窓が開いた直後の走査（<c>InitializeAsync</c>）が終わるまで待つ。</summary>
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

    private static async Task<VbaP3Rig> NewRigAsync(bool withWindow = false)
        => await VbaP3Rig.CreateAsync(
            new Settings { ShowPreview = false },
            arrange: bridge => bridge.Get(VbaP3Rig.Book)!.Info = WorkbookInfoTests.Sample(),
            withWindow: withWindow).ConfigureAwait(true);

    [AvaloniaFact(DisplayName = "画面の確認: コンテキスト収集の窓の「ブックの情報」の行（内容も出す／構成だけ／出さない）")]
    public async Task ブックの情報の行()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        context.Focus.Groups.SelectMany(g => g.Chips).First(c => c.Id == "focus-readable").IsSelected = true;

        var window = _windows.Track(new ContextCollectWindow(context));
        window.Show();
        await WaitForIdleAsync(context).ConfigureAwait(true);
        Save(window, "q2-workbook-info-row-full.png");

        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        Save(window, "q2-workbook-info-row-structure-only.png");

        await context.CycleWorkbookInfoLevelAsync().ConfigureAwait(true);
        Save(window, "q2-workbook-info-row-hidden.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: 値の見本をオンにしたときの確認ダイアログ（実際のダイアログ）")]
    public void 見本オンの確認ダイアログ()
    {
        Window? captured = null;
        AvaloniaDialogService.OnButtonRowBuiltForTests = (window, _) => captured = window;
        try
        {
            // 窓に載る文は ContextCollectViewModel.ToggleWorkbookSampleValuesAsync と同じ。
            _ = new AvaloniaDialogService().ConfirmAsync(
                "値の見本を AI に渡しますか",
                "各シートの先頭 5 行の値が、依頼文に含まれ、AI に渡ります。" + Environment.NewLine
                + "個人情報や社外秘が含まれていないか確認してください。");
            captured.Should().NotBeNull();
            Dispatcher.UIThread.RunJobs();
            Save(captured!, "q2-sample-confirm-dialog.png");
        }
        finally
        {
            AvaloniaDialogService.OnButtonRowBuiltForTests = null;
            captured?.Close();
        }
    }

    [AvaloniaFact(DisplayName = "画面の確認: 値の見本がオンの間の注意（行と、依頼文コピーのドロップダウン）")]
    public async Task 見本オンの注意()
    {
        using var r = await NewRigAsync(withWindow: true).ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);

        // コンテキスト収集の窓
        var collect = _windows.Track(new ContextCollectWindow(context));
        collect.Show();
        await WaitForIdleAsync(context).ConfigureAwait(true);
        Save(collect, "q2-sample-on-row.png");

        // 依頼文コピーのドロップダウン（ポップアップは窓の描画に載らないことがあるため、中身を別窓へ移して撮る）。
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
        Save(host, "q2-sample-on-dropdown.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: プレビューに出たブックの情報（値の見本あり）")]
    public async Task プレビューの内容()
    {
        using var r = await NewRigAsync().ConfigureAwait(true);
        var context = r.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.ToggleWorkbookSampleValuesAsync().ConfigureAwait(true);
        context.Focus.Groups.SelectMany(g => g.Chips).First(c => c.Id == "focus-readable").IsSelected = true;

        var window = _windows.Track(new ContextCollectWindow(context) { Height = 900 });
        window.Show();
        // 窓が開いた直後の初期化（走査）が終わってからプレビューを作る（走査中はボタンが無効で、結果も作り直される）。
        await WaitForIdleAsync(context).ConfigureAwait(true);
        await ShellHarness.ExecuteAsync(context.PreviewCommand).ConfigureAwait(true);
        window.FindControl<TabControl>("MainTabs")!.SelectedIndex = 1;
        await Task.Delay(150).ConfigureAwait(true);
        Save(window, "q2-preview-workbook-info.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: 設定の「VBA 連携」タブ（ブックの情報の項目）")]
    public async Task 設定パネル()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices()) { VbaSupportProbe = () => true };
        await vm.InitializeAsync().ConfigureAwait(true);
        var window = _windows.Track(new SettingsWindow(vm) { Width = 1000, Height = 900 });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "CategoryTabControl");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "VBA 連携"));
        Dispatcher.UIThread.RunJobs();
        Save(window, "q2-settings-vba.png");
    }
}
