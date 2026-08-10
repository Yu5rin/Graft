using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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

namespace Graft.UiTests;

/// <summary>
/// ファイル単位の変更履歴（エクスプローラの右クリックメニュー「このファイルの変更履歴」）の
/// 通しシナリオテスト。RevisionNumberingScenarioTests.csと同じ手法
/// （ProjectPane登録 → クリップボード貼り付け → 解析 → 適用）で実際に複数リビジョンの
/// 履歴を作り、ExplorerViewModel.ShowFileHistoryCommand → ShellViewModel →
/// HistoryPaneViewModel.ShowHistoryForFile の経路を実際に通して検証する。
/// DeleteUndoTestsと同じく実際の<see cref="ShellWindow"/>を開く（右クリックからサイドバーの
/// 履歴ビューが実際に開いてアクティブになることまで検証するため。ShellWindowが
/// Graft.RequestFocusHistoryを購読して初めてサイドビュー切り替えが起きる配線のため、
/// ShellViewModelだけでは検証できない）。
/// </summary>
public class FileHistoryFilterScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-file-history-scenario", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly ShownWindowTracker _windows = new();

    public FileHistoryFilterScenarioTests()
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

    [AvaloniaFact(DisplayName = "対象ファイルを含むリビジョンだけが一覧に出て、絞り込み中であることが明示される")]
    public async Task 対象ファイルを含むリビジョンだけが一覧に出る()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "a.txt"), "a-v1\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "b.txt"), "b-v1\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count >= 2).ConfigureAwait(true);

        await ApplyFullAsync(shell, "a.txt", "a-v2").ConfigureAwait(true); // r1: a.txtのみ
        await ApplyFullAsync(shell, "b.txt", "b-v2").ConfigureAwait(true); // r2: b.txtのみ
        await ApplyFullAsync(shell, "a.txt", "a-v3").ConfigureAwait(true); // r3: a.txtのみ

        var aNode = shell.Explorer.RootNodes.Single(n => n.Name == "a.txt");
        shell.Explorer.ShowFileHistoryCommand.CanExecute(aNode).Should().BeTrue("a.txtはファイルなので実行できるはず");
        shell.Explorer.ShowFileHistoryCommand.Execute(aNode);

        var history = shell.Graft.History;
        history.IsFileFiltered.Should().BeTrue();
        history.FileFilterBannerText.Should().Contain("a.txt");
        history.Items.Select(i => i.RevisionLabel).Should().BeEquivalentTo(new[] { "r1", "r3" },
            "a.txtを変更したリビジョン（r1・r3）だけが残り、b.txtだけのr2は含まれないはず");

        // サイドバーの履歴ビューが開いてアクティブになっていること（右クリックからの入口の要件）。
        shell.IsHistoryActive.Should().BeTrue();

        // 絞り込みを解除すると全件へ戻る。
        history.ClearFileFilterCommand.Execute(null);
        history.IsFileFiltered.Should().BeFalse();
        history.Items.Select(i => i.RevisionLabel).Should().BeEquivalentTo(new[] { "r1", "r2", "r3" });
    }

    [AvaloniaFact(DisplayName = "履歴の無いファイルを選ぶと一覧は0件になり、専用の空状態メッセージが出る")]
    public async Task 履歴の無いファイルは空状態になる()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "a.txt"), "a-v1\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "untouched.txt"), "変更されない\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count >= 2).ConfigureAwait(true);

        await ApplyFullAsync(shell, "a.txt", "a-v2").ConfigureAwait(true); // untouched.txtには一切触れない

        var untouchedNode = shell.Explorer.RootNodes.Single(n => n.Name == "untouched.txt");
        shell.Explorer.ShowFileHistoryCommand.Execute(untouchedNode);

        var history = shell.Graft.History;
        history.IsFileFiltered.Should().BeTrue();
        history.Items.Should().BeEmpty("untouched.txtは一度も変更されていないはず");
        history.State.Should().Be(HistoryPaneState.Empty);
        history.EmptyStateMessage.Should().Contain("untouched.txt").And.Contain("変更履歴はありません");
    }

    [AvaloniaFact(DisplayName = "フォルダには「このファイルの変更履歴」を実行できない")]
    public async Task フォルダには実行できない()
    {
        Directory.CreateDirectory(Path.Combine(_projectDirectory, "sub"));
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "sub", "inner.txt"), "x\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count >= 1).ConfigureAwait(true);

        var folderNode = shell.Explorer.RootNodes.Single(n => n.Name == "sub");
        folderNode.IsDirectory.Should().BeTrue();
        shell.Explorer.ShowFileHistoryCommand.CanExecute(folderNode).Should().BeFalse("フォルダは対象外のはず");
    }

    [AvaloniaFact(DisplayName = "絞り込み中にリビジョンを選ぶと、履歴差分タブにはそのファイルだけの差分が表示される（他ファイルは含まれない）")]
    public async Task 選択したリビジョンの対象ファイルだけの差分が表示される()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "a.txt"), "a-v1\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "b.txt"), "b-v1\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count >= 2).ConfigureAwait(true);

        // r1: a.txtとb.txtの両方を1リビジョンで変更する（FULL形式を2ファイル分連結）。
        _clipboard.Text =
            "<<<< FILE: a.txt MODE=FULL\na-v2\n>>>> END\n" +
            "<<<< FILE: b.txt MODE=FULL\nb-v2\n>>>> END\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        var aNode = shell.Explorer.RootNodes.Single(n => n.Name == "a.txt");
        shell.Explorer.ShowFileHistoryCommand.Execute(aNode);

        var history = shell.Graft.History;
        history.Items.Should().ContainSingle().Which.RevisionLabel.Should().Be("r1");

        history.SelectedItem = history.Items.Single();
        // OnRevisionSelectedはasync voidのため、HistoryDiffへ反映されるまで少し待つ。
        await WaitForAsync(() => shell.Graft.HistoryDiff.Files.Count > 0).ConfigureAwait(true);

        shell.Graft.HistoryDiff.Files.Should().ContainSingle(
            "r1はa.txt・b.txtの2ファイルを変更したが、a.txtで絞り込み中なのでa.txtの分だけが表示されるはず");
        var file = shell.Graft.HistoryDiff.Files.Single();
        file.PathText.Should().Be("a.txt");
        file.Plan.AfterText.Should().Be("a-v2\n");
        file.Plan.BeforeText.Should().Be("a-v1\n");
    }

    [AvaloniaFact(DisplayName = "エクスプローラの右クリックメニュー「このファイルの変更履歴」にはHelpTip.Standardが付いている")]
    public async Task 右クリックメニューにHelpTipが付いている()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        // 非表示のサイドビューは視覚ツリーが未実現のことがあるため、DeleteUndoTestsと同じく
        // 先にエクスプローラへ切り替えてからレイアウトを1回進める。
        shell.SelectSideView(SideViewKind.Explorer);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var tree = _lastWindow!.GetControl<ExplorerView>("ExplorerViewControl").GetControl<TreeView>("FileTreeView");
        var contextMenu = tree.ContextMenu;
        contextMenu.Should().NotBeNull("ExplorerView.axamlのTreeView.ContextMenuに定義されているはず");

        var menuItem = contextMenu!.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => Equals(m.Header, "このファイルの変更履歴"));

        HelpTip.GetStandard(menuItem).Should().NotBeNull("エクスプローラの右クリックメニュー項目にはHelpTip.Standardを付ける方針のため");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private ShellWindow? _lastWindow;

    private async Task<ShellViewModel> OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var settings = new Settings { ShowPreview = false };
        await new SettingsStore(appPaths).SaveAsync(settings).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            settings,
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            new AutoConfirmDialogService(),
            new FakeUiServices(_clipboard),
            openSettings: () => { });

        // ShellWindow.OnLoadedがGraft.InitializeAsync()を非同期に呼ぶ（DeleteUndoTests.
        // OpenShellAsyncのコメントと同じ理由でここでは明示的に呼ばない）。
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);
        _lastWindow = window;
        return shell;
    }

    /// <summary>FULL形式のパッチを貼り付け→解析→適用まで実際に通す。</summary>
    private async Task ApplyFullAsync(ShellViewModel shell, string relativePath, string content)
    {
        _clipboard.Text = $"<<<< FILE: {relativePath} MODE=FULL\n{content}\n>>>> END\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
    }

    /// <summary>非同期コマンドを実行し、完了するまで待つ。</summary>
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

    /// <summary>条件が満たされるまで、非同期処理（async void含む）の完了をポーリングで待つ。</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < 5000)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10).ConfigureAwait(true);
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
