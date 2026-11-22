using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Themes;
using Graft.UiTests.TestSupport;
using Graft.Views;
using static Graft.UiTests.TestSupport.CollectLayoutFixtures;

namespace Graft.UiTests;

/// <summary>
/// 「AIに渡すファイルを選ぶ」画面（コンテキスト収集の窓）の新しい配置（設計案A）を、ヘッドレスで描画して
/// PNG にして残す（見た目の目視確認用）。保存先は環境変数 <c>GRAFT_COLLECT_LAYOUT_DIR</c>。
/// 未設定のときは何も保存せず、描画できることだけを確かめる（通常のテスト実行でファイルを散らかさないため。
/// <see cref="Vba.WorkbookInfoSheetViewScreenshotTests"/> と同じ作り）。ファイル名は <c>layoutA-*.png</c>。
/// 保存先に同名のファイルがあるときは、前の画像を上書きしないよう失敗させる。
/// あわせて、ファイル一覧に見えている行数（設計案の表と同じ測り方）を <c>layoutA-rows.txt</c> へ書く。
/// </summary>
public class CollectLayoutScreenshotTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-collect-layout", Guid.NewGuid().ToString("N"));

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

    private static void Save(ContextCollectWindow window, string tag)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");

        var dir = Environment.GetEnvironmentVariable("GRAFT_COLLECT_LAYOUT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"layoutA-{tag}.png");
        if (File.Exists(path)) throw new InvalidOperationException($"既存のファイルは上書きしない: {path}");
        frame!.Save(path);

        var list = window.FindControl<ListBox>("FileListBox")!;
        File.AppendAllText(
            Path.Combine(dir, "layoutA-rows.txt"),
            $"{tag}: 窓 {window.Width}x{window.Height}、ファイル一覧の幅 {list.Bounds.Width:0}px、見える行数 {VisibleFileRows(window):0.0}{Environment.NewLine}");
    }

    [AvaloniaFact(DisplayName = "画面の確認（設計案A）: 通常のプロジェクト（既定の窓・760×720・最小・広い窓・観点をたたんだ状態・除外を開いた状態）")]
    public async Task 通常のプロジェクト()
    {
        // 目安を下げて、状況の文（トークン数の助言）が出た状態で撮る。
        using var h = await ShellHarness.CreateAsync(new Settings
        {
            ShowPreview = false,
            Context = new ContextSettings { TokenWarnThreshold = 5000 },
        }).ConfigureAwait(true);
        var context = await NormalAsync(h).ConfigureAwait(true);

        var window = await OpenAsync(_windows, context).ConfigureAwait(true);
        Save(window, "normal");
        window.Close();

        window = await OpenAsync(_windows, context, 760, 720).ConfigureAwait(true);
        Save(window, "normal-760x720");
        window.Close();

        window = await OpenAsync(_windows, context, window.MinWidth, window.MinHeight).ConfigureAwait(true);
        Save(window, "normal-min");
        window.Close();

        window = await OpenAsync(_windows, context, 1280, 800).ConfigureAwait(true);
        Save(window, "normal-1280x800");
        window.Close();

        // 観点をたたんだ状態。
        window = await OpenAsync(_windows, context).ConfigureAwait(true);
        window.FindControl<PromptFocusPanel>("FocusPanel")!.IsCollapsed = true;
        await SettleAsync(context).ConfigureAwait(true);
        Save(window, "normal-focus-collapsed");
        window.Close();

        // 除外パターンを 2 件入れて、折りたたみを開いた状態。
        foreach (var pattern in new[] { "*.log", "lib/vendor/" })
        {
            context.NewExcludePattern = pattern;
            await ShellHarness.ExecuteAsync(context.AddExcludeCommand).ConfigureAwait(true);
        }

        window = await OpenAsync(_windows, context).ConfigureAwait(true);
        window.FindControl<Expander>("ExcludesExpander")!.IsExpanded = true;
        await SettleAsync(context).ConfigureAwait(true);
        Save(window, "normal-excludes-open");
    }

    [AvaloniaFact(DisplayName = "画面の確認（設計案A）: VBA プロジェクト（シート 5 枚・リボンの行つき。既定の窓・760×720・最小・広い窓）")]
    public async Task VBAプロジェクト()
    {
        using var rig = await VbaRigAsync(_root).ConfigureAwait(true);
        var context = rig.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet3", VbaSheetViewMode.Range).ConfigureAwait(true);
        Select(context.Focus, "focus-readable", "focus-maintainable", "focus-vba-64bit");

        var window = await OpenAsync(_windows, context).ConfigureAwait(true);
        Save(window, "vba");
        window.Close();

        window = await OpenAsync(_windows, context, 760, 720).ConfigureAwait(true);
        Save(window, "vba-760x720");
        var tabs = window.FindControl<TabControl>("MainTabs")!;
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "ブックの情報"));
        await SettleAsync(context).ConfigureAwait(true);
        Save(window, "vba-760x720-booktab");
        window.Close();

        window = await OpenAsync(_windows, context, window.MinWidth, window.MinHeight).ConfigureAwait(true);
        Save(window, "vba-min");
        tabs = window.FindControl<TabControl>("MainTabs")!;
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "ブックの情報"));
        await SettleAsync(context).ConfigureAwait(true);
        Save(window, "vba-min-booktab");
        window.Close();

        window = await OpenAsync(_windows, context, 1280, 800).ConfigureAwait(true);
        Save(window, "vba-1280x800");
        window.Close();

        // 広い窓で高さだけが足りない（860 以上 × 最小の高さ）: ブックの情報の側が縦にスクロールする。
        window = await OpenAsync(_windows, context, 960, window.MinHeight).ConfigureAwait(true);
        Save(window, "vba-960x560");
    }

    [AvaloniaFact(DisplayName = "画面の確認（設計案A）: 明るいテーマ（VBA・既定の窓と 760×720）")]
    public async Task 明るいテーマ()
    {
        var original = ThemeManager.SelectedTheme;
        try
        {
            ThemeManager.SetTheme(AppTheme.Light);

            using var rig = await VbaRigAsync(_root).ConfigureAwait(true);
            var context = rig.Graft.ContextCollect!;
            await context.InitializeAsync().ConfigureAwait(true);
            await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
            Select(context.Focus, "focus-readable", "focus-maintainable", "focus-vba-64bit");

            var window = await OpenAsync(_windows, context).ConfigureAwait(true);
            Save(window, "light-vba");
            window.Close();

            window = await OpenAsync(_windows, context, 760, 720).ConfigureAwait(true);
            Save(window, "light-vba-760x720");
        }
        finally
        {
            ThemeManager.SetTheme(original);
        }
    }
}
