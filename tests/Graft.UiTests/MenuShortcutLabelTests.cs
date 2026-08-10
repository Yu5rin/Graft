using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// F: メニューのショートカット表記の統一の回帰テスト。
/// ショートカットを持つメニュー項目は「見出し (キー)」の形式（半角スペース＋括弧、
/// 既存の「名前の変更 (F2)」「削除 (Delete)」と同じ表記）で統一する方針の取りこぼし防止。
///
/// 対象は仕様書どおり既存4画面（エクスプローラ・タブ・履歴・プロジェクト）＋今回追加した項目。
/// 履歴・プロジェクトの既存項目にはショートカットに対応する項目が無い（ShellWindow.Keyboard.cs・
/// ShortcutsWindow.axaml参照）ため、ここでは実際にショートカットを持つ項目
/// （エクスプローラの「開く」= Enter、タブの「閉じる」= Ctrl+W、GraftPanelの
/// チェック切替 = Space）だけを検証する。
/// </summary>
public class MenuShortcutLabelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-shortcut-label", Guid.NewGuid().ToString("N"));
    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly ShownWindowTracker _windows = new();

    public MenuShortcutLabelTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
    }

    public void Dispose()
    {
        _windows.Dispose();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "エクスプローラの「開く」はEnterキーのショートカットを持ち、表記に (Enter) が付いている")]
    public void エクスプローラの開くはEnter表記を持つ()
    {
        var view = new ExplorerView();
        var tree = view.GetControl<TreeView>("FileTreeView");
        var contextMenu = tree.ContextMenu;
        contextMenu.Should().NotBeNull();

        var menuItem = contextMenu!.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => (m.Header?.ToString() ?? string.Empty).StartsWith("開く", StringComparison.Ordinal));
        menuItem.Header.Should().Be("開く (Enter)");

        // 表記のEnterと実際のKeyBindingが同じOpenCommandを指していることを確認する
        // （表記と実体が食い違っていないか）。
        var keyBinding = tree.KeyBindings.Single(k => k.Gesture.Key == Key.Enter);
        keyBinding.Command.Should().BeSameAs(menuItem.Command,
            "表記のEnterと実際のKeyBindingのコマンドが一致している必要がある");
    }

    [AvaloniaFact(DisplayName = "タブ見出しの「閉じる」はCtrl+Wのショートカットを持ち、表記に (Ctrl+W) が付いている")]
    public async Task タブの閉じるはCtrlW表記を持つ()
    {
        var targetPath = Path.Combine(_projectDirectory, "a.txt");
        await File.WriteAllTextAsync(targetPath, "A\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var tab = (await shell.Editor.OpenFileAsync(targetPath).ConfigureAwait(true)).Value;
        Dispatcher.UIThread.RunJobs();

        var tabBorder = window.GetVisualDescendants().OfType<Border>()
            .Single(b => ReferenceEquals(b.DataContext, tab) && b.ContextMenu is not null);
        var menuItem = tabBorder.ContextMenu!.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => (m.Header?.ToString() ?? string.Empty).StartsWith("閉じる", StringComparison.Ordinal));

        menuItem.Header.Should().Be("閉じる (Ctrl+W)");
    }

    [AvaloniaFact(DisplayName = "GraftPanelの「チェックを付ける／外す」はSpaceキーのショートカットを持ち、表記に (Space) が付いている")]
    public void GraftPanelのチェック切替はSpace表記を持つ()
    {
        var plan = new BlockPlan
        {
            Block = new DeleteBlock { Path = "sample.txt" },
            Path = "sample.txt",
            Operation = EntryOperation.Modify,
            CanApply = true,
            IsSelected = true,
        };
        var block = new BlockItemViewModel(plan);

        block.ToggleLabel.Should().Be("チェックを外す (Space)");

        block.IsSelected = false;
        block.ToggleLabel.Should().Be("チェックを付ける (Space)");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private async Task<(ShellViewModel Shell, ShellWindow Window)> OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(new Settings { ShowPreview = false }).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths, new Settings { ShowPreview = false }, settingsStore, new PatchQueue(appPaths),
            new ProjectStore(appPaths), new RevisionStore(appPaths), new RevisionRestorer(appPaths),
            new NullDialogService(), new AvaloniaUiServices(), openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        await WaitForShellInitializedAsync(shell).ConfigureAwait(true);
        return (shell, window);
    }

    private static async Task WaitForShellInitializedAsync(ShellViewModel shell)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (shell.Graft.ProjectPane.State == ProjectPaneState.Loading)
            {
                await Task.Delay(10, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException("初期化が30秒以内に完了しませんでした。", ex);
        }
    }
}
