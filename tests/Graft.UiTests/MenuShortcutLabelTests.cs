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
/// メニューのショートカット表記の回帰テスト。
/// ショートカットを持つメニュー項目は、キーを見出しの文字へ埋め込まず（以前は「開く (Enter)」の形）、
/// メニューの右側に出る <c>InputGesture</c> で表す方針の取りこぼし防止。見出しに「(キー)」が混ざると、
/// キーの表記が揃わず、読み上げでも見出しが長くなる。
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

    [AvaloniaFact(DisplayName = "エクスプローラの「開く」はEnterキーのショートカットを持ち、キーは見出しではなくInputGestureで出る")]
    public void エクスプローラの開くはEnter表記を持つ()
    {
        var view = new ExplorerView();
        var tree = view.GetControl<TreeView>("FileTreeView");
        var contextMenu = tree.ContextMenu;
        contextMenu.Should().NotBeNull();

        var menuItem = contextMenu!.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => (m.Header?.ToString() ?? string.Empty).StartsWith("開く", StringComparison.Ordinal));
        menuItem.Header.Should().Be("開く");
        menuItem.InputGesture!.Key.Should().Be(Key.Enter);

        // 表記のEnterと実際のKeyBindingが同じOpenCommandを指していることを確認する
        // （表記と実体が食い違っていないか）。
        var keyBinding = tree.KeyBindings.Single(k => k.Gesture.Key == Key.Enter);
        keyBinding.Command.Should().BeSameAs(menuItem.Command,
            "表記のEnterと実際のKeyBindingのコマンドが一致している必要がある");
    }

    [AvaloniaFact(DisplayName = "タブ見出しの「閉じる」はCtrl+Wのショートカットを持ち、キーは見出しではなくInputGestureで出る")]
    public async Task タブの閉じるはCtrlW表記を持つ()
    {
        var targetPath = Path.Combine(_projectDirectory, "a.txt");
        await File.WriteAllTextAsync(targetPath, "A\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var tab = (await shell.Editor.OpenFileAsync(targetPath).ConfigureAwait(true)).Value;
        Dispatcher.UIThread.RunJobs();

        // 機能改善（タブが増えたときに到達できない問題）: タブ見出しのDataTemplateルートは
        // タブ幅の自動縮小（Title=*列で省略記号表示）のためBorderからGridへ変更した
        // （EditorPane.axaml参照）。ContextMenuの取り付け先もそれに合わせてGridへ移った。
        var tabGrid = window.GetVisualDescendants().OfType<Grid>()
            .Single(g => ReferenceEquals(g.DataContext, tab) && g.ContextMenu is not null);
        var menuItem = tabGrid.ContextMenu!.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => (m.Header?.ToString() ?? string.Empty).StartsWith("閉じる", StringComparison.Ordinal));

        menuItem.Header.Should().Be("閉じる");
        menuItem.InputGesture.Should().Be(new KeyGesture(Key.W, KeyModifiers.Control));
    }

    [AvaloniaFact(DisplayName = "GraftPanelの「チェックを付ける／外す」の見出しにはキーを埋め込まない")]
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

        block.ToggleLabel.Should().Be("チェックを外す");

        block.IsSelected = false;
        block.ToggleLabel.Should().Be("チェックを付ける");
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
