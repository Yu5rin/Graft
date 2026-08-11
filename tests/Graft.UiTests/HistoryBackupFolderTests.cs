using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
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
/// E: 履歴の右クリックメニュー「バックアップフォルダを開く」の回帰テスト。
/// 「本当に退避されているか」を自分の目で確かめられる安心材料であるため、
/// 対象のback/フォルダが実際にディスク上へ存在することと、無効化条件（選択なし・
/// フォルダの実体が無い）を確認する。実際にファイルマネージャを起動する部分
/// （PlatformServices.Current.FileManager.Reveal）はプロセスを起動する副作用があるため、
/// 既存のRevealCommand系のテスト（ExplorerViewModel等）と同じ方針でCanExecuteロジックと
/// メニュー配線のみを検証する。
/// </summary>
public class HistoryBackupFolderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-backup-folder", Guid.NewGuid().ToString("N"));
    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly ShownWindowTracker _windows = new();

    public HistoryBackupFolderTests()
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

    [AvaloniaFact(DisplayName = "適用済みリビジョンを選ぶと、実際にback/配下に存在するフォルダを対象にOpenBackupFolderCommandが有効になる")]
    public async Task 実在するバックアップフォルダで有効になる()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(target, "v1\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1

        var history = shell.Graft.History;
        history.SelectedItem = history.Items.Single(i => i.RevisionLabel == "r1");

        history.OpenBackupFolderCommand.CanExecute(null).Should().BeTrue();
        Directory.Exists(history.SelectedItem.Revision.FolderPath).Should().BeTrue(
            "OpenBackupFolderCommandが開く対象のフォルダは実際にディスク上へ存在している必要がある");
    }

    [AvaloniaFact(DisplayName = "選択が無いときはOpenBackupFolderCommandが無効になる")]
    public async Task 選択なしでは無効になる()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(target, "v1\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true);

        shell.Graft.History.SelectedItem = null;

        shell.Graft.History.OpenBackupFolderCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "バックアップの実体がディスク上に見つからないリビジョンでは無効になる")]
    public void 実体が無いと無効になる()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var history = new HistoryPaneViewModel(
            new RevisionStore(appPaths), new RevisionRestorer(appPaths), new ProjectStore(appPaths),
            new NullDialogService());

        var manifest = new RevisionManifest { Revision = 1, ProjectId = "p1", AppliedAt = DateTimeOffset.Now };
        var missingFolder = Path.Combine(_root, "does-not-exist");
        var summary = new RevisionSummary { Manifest = manifest, FolderPath = missingFolder, IsRestorable = false };
        history.SelectedItem = new RevisionRowViewModel(summary);

        Directory.Exists(missingFolder).Should().BeFalse("前提: このフォルダは実際には作っていない");
        history.OpenBackupFolderCommand.CanExecute(null).Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "履歴の右クリックメニューに「バックアップフォルダを開く」が並び、HelpTip.Standardを持つ")]
    public async Task 右クリックメニューに項目が並びHelpTipを持つ()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(target, "v1\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellWithWindowAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true);

        shell.SelectSideView(SideViewKind.History);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var historyPane = window.GetControl<HistoryPane>("HistoryPaneControl");
        var contextMenu = historyPane.ListBoxElement.ContextMenu;
        contextMenu.Should().NotBeNull();

        // ContextMenu内部のバインディング（Command等）はPopupを開いた時点で初めてDataContextが
        // 伝播するため（EditorSelectionPromptTests.OpenContextMenuAndFindPromptItemと同じ理由）、
        // ContextRequestedイベントを実際に発火させてから読む。
        historyPane.ListBoxElement.RaiseEvent(
            new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var menuItem = contextMenu!.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => Equals(m.Header?.ToString(), "バックアップフォルダを開く"));

        menuItem.Command.Should().BeSameAs(shell.Graft.History.OpenBackupFolderCommand);
        HelpTip.GetStandard(menuItem).Should().NotBeNull("追加したメニュー項目にはHelpTip.Standardが必要");
    }

    // ------------------------------------------------------------------
    // ヘルパ（HistoryRestoreThroughScenarioTests.csと同じ構成）
    // ------------------------------------------------------------------

    private async Task<ShellViewModel> OpenShellAsync()
    {
        var (shell, _) = await BuildShellAsync().ConfigureAwait(true);
        return shell;
    }

    private async Task<(ShellViewModel Shell, ShellWindow Window)> OpenShellWithWindowAsync()
    {
        var (shell, window) = await BuildShellAsync(withWindow: true).ConfigureAwait(true);
        return (shell, window!);
    }

    private async Task<(ShellViewModel Shell, ShellWindow? Window)> BuildShellAsync(bool withWindow = false)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var settings = new Settings { ShowPreview = false };
        await new SettingsStore(appPaths).SaveAsync(settings).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths, settings, new SettingsStore(appPaths), new PatchQueue(appPaths),
            new ProjectStore(appPaths), new RevisionStore(appPaths), new RevisionRestorer(appPaths),
            new AutoConfirmDialogService(), new FakeUiServices(_clipboard), openSettings: () => { });

        ShellWindow? window = null;
        if (withWindow)
        {
            window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
            window.Show();
        }
        else
        {
            // 画面を開かない場合はInitializeAsyncを直接待つ（HistoryRestoreThroughScenarioTests.csと同じ）。
            await shell.Graft.InitializeAsync().ConfigureAwait(true);
            return (shell, null);
        }

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

    private async Task ApplyFullAsync(ShellViewModel shell, string relativePath, string content)
    {
        _clipboard.Text = $"<<<< FILE: {relativePath} MODE=FULL\n{content}\n>>>> END\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
    }

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting)
            {
                await Task.Delay(10).ConfigureAwait(true);
            }
        }
    }

    private sealed class AutoConfirmDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    private sealed class NullDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public void SetText(string text) => Text = text;

        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    private sealed class FakeUiServices : IUiServices
    {
        private readonly AvaloniaUiServices _inner = new();

        public FakeUiServices(IClipboardAccess clipboard)
        {
            Clipboard = clipboard;
        }

        public IClipboardAccess Clipboard { get; }

        public IScreenInfo Screens => _inner.Screens;

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }
}
