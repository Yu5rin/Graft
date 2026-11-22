using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.UiTests.Vba;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 依頼の観点（段階 Q1）の画面を、ヘッドレスで描画して PNG にして残す（見た目の目視確認用）。
/// <see cref="VbaP4ScreenshotTests"/> と同じ作りで、保存先は環境変数 <c>GRAFT_Q1_SCREENSHOT_DIR</c>。
/// 未設定のときは何も保存せず、描画できることだけを確かめる（通常のテスト実行でファイルを散らかさないため）。
/// </summary>
public class PromptFocusScreenshotTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-q1-shot", Guid.NewGuid().ToString("N"));

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

        var dir = Environment.GetEnvironmentVariable("GRAFT_Q1_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        frame!.Save(Path.Combine(dir, fileName));
    }

    private static void Select(PromptFocusSelectionViewModel focus, params string[] ids)
    {
        foreach (var chip in focus.Groups.SelectMany(g => g.Chips).Where(c => ids.Contains(c.Id))) chip.IsSelected = true;
    }

    [AvaloniaFact(DisplayName = "画面の確認: 依頼文コピーのドロップダウン（通常のプロジェクト）")]
    public async Task 依頼文コピーの観点パネル()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(h.ProjectDirectory, "main.py"), "print(1)\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var copy = h.Shell.Graft.PromptCopy!;
        Select(copy.Context.Focus, "focus-readable", "focus-bugfix", "focus-design-look");

        copy.IsOpen = true;
        await ShellHarness.WaitUntilAsync(() => copy.Templates.Count > 0).ConfigureAwait(true);
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(40).ConfigureAwait(true);
            Dispatcher.UIThread.RunJobs();
        }

        // ポップアップは別の最上位として描かれることがあり、ウィンドウの描画に載らない場合があるため、
        // ドロップダウンの中身（枠・見出し・一覧・チップ）をそのまま別ウィンドウへ移して撮る。
        var window = h.Window!;
        Save(window, "q1-prompt-dropdown-window.png");

        var border = (Border)window.GetVisualDescendants().OfType<PromptFocusPanel>().First()
            .GetVisualAncestors().OfType<Border>().First(b => b.MinWidth >= 280);
        var host = _windows.Track(new Window { Width = 460, Height = 700, DataContext = h.Shell, Background = (Avalonia.Media.IBrush?)window.FindResource("BgBase") });
        var content = (Control)border.Child!;
        border.Child = null;
        host.Content = new Border
        {
            Background = border.Background, BorderBrush = border.BorderBrush, BorderThickness = border.BorderThickness,
            Padding = border.Padding, Margin = new Thickness(16), Child = content, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        host.Show();
        Save(host, "q1-prompt-dropdown.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: コンテキスト収集の窓（通常のプロジェクト）")]
    public async Task コンテキスト収集の観点パネル()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(h.ProjectDirectory, "main.py"), "print(1)\n").ConfigureAwait(true);
        Directory.CreateDirectory(Path.Combine(h.ProjectDirectory, "src"));
        await File.WriteAllTextAsync(Path.Combine(h.ProjectDirectory, "src", "util.py"), "def f():\n    return 1\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var context = h.Shell.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        Select(context.Focus, "focus-readable", "focus-bugfix", "focus-speed");

        var window = _windows.Track(new ContextCollectWindow(context));
        window.Show();
        await Task.Delay(100).ConfigureAwait(true);
        Save(window, "q1-context-collect.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: VBA プロジェクトの17チップ（コンテキスト収集の窓）")]
    public async Task VBAプロジェクトの17チップ()
    {
        using var rig = await VbaP3Rig.CreateAsync().ConfigureAwait(true);
        var context = rig.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        Select(context.Focus, "focus-readable", "focus-maintainable", "focus-vba-64bit", "focus-vba-guard");

        var window = _windows.Track(new ContextCollectWindow(context));
        window.Show();
        await Task.Delay(100).ConfigureAwait(true);
        Save(window, "q1-vba-17chips.png");
    }

    [AvaloniaFact(DisplayName = "画面の確認: 設定の「依頼の観点」タブ（組み込みの閲覧と、複製した自作の編集）")]
    public async Task 設定タブ()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        await new PromptFocusStore(appPaths).SaveAsync(new[]
        {
            new Graft.Core.PromptFocus.PromptFocus
            {
                Id = "custom-sample", Group = Graft.Core.PromptFocus.PromptFocusGroups.Mine, Name = "レビュー用",
                Tooltip = "レビュー前に見てほしい点をそろえる",
                Body = "## レビュー用\n- 変更の意図を最初の3行で説明する。\n- 影響範囲を挙げる。",
            },
        }).ConfigureAwait(true);

        var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync().ConfigureAwait(true);
        var window = _windows.Track(new SettingsWindow(vm) { Width = 1100, Height = 720 });
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "CategoryTabControl");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "依頼の観点"));
        Dispatcher.UIThread.RunJobs();

        // 組み込み（編集できない状態）
        vm.Focuses.SelectedFocus = vm.Focuses.Focuses.First(f => f.Id == "focus-bugfix");
        Dispatcher.UIThread.RunJobs();
        Save(window, "q1-settings-builtin.png");

        // 自作（編集できる状態。変数の警告も出す）
        vm.Focuses.SelectedFocus = vm.Focuses.Focuses.First(f => f.Id == "custom-sample");
        vm.Focuses.SelectedFocus.Body += "\n- {{files}} は展開されません。";
        Dispatcher.UIThread.RunJobs();
        Save(window, "q1-settings-custom.png");
    }
}
