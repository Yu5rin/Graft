using System.Linq;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 依頼4対応の回帰テスト（画面あり）。「ネットワークドライブ上のプロジェクトですべての
/// ブロックがE101になる」という実機報告の原因切り分けのため、ドライラン完了時に
/// 対象ファイルごとの診断情報（解決した絶対パス・存在するか・読み取った行数）が
/// logs/&lt;日付&gt;.logへ実際に記録されることを、MainViewModelを含む実際の配線
/// （MainViewModel.DryRunDiagnostics.cs）で確認する。Core層のデータ自体の正しさは
/// tests/Graft.Tests/DryRunFileProbeTests.csが担当する。
/// GitAutoCommitScenarioTests.csと同じ手法（本物のShellWindowは作らずShellViewModelのみ
/// 構築し、ログはLoggerを明示的に注入してDisposeAsync後に読む）を使う。
/// </summary>
public class DryRunFileProbeLoggingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-dryrunprobe", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();

    public DryRunFileProbeLoggingTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
    }

    public void Dispose()
    {
        TempDirectoryCleanup.TryDeleteRecursive(_root);
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "存在するファイルへのドライランは絶対パス・存在あり・行数がログへ記録される")]
    public async Task 存在するファイルの診断情報がログへ記録される()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var shell = await OpenShellAsync(logger).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        await logger.DisposeAsync().ConfigureAwait(true);

        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        File.Exists(logPath).Should().BeTrue("logs/<日付>.log が作られているはず");
        var logText = await File.ReadAllTextAsync(logPath).ConfigureAwait(true);
        logText.Should().Contain("dry-run-file-probe", "イベント種別で診断ログだと分かるはず");
        logText.Should().Contain("sample.txt", "対象パス（相対）が記録されているはず");
        logText.Should().Contain(targetPath, "Graftが実際に確認した絶対パスが記録されているはず");
        logText.Should().Contain("存在=あり", "存在確認の結果が記録されているはず");
        logText.Should().Contain("行数=3", "読み取れた行数（3行）が記録されているはず");
    }

    [AvaloniaFact(DisplayName = "存在しないファイルへのドライランは絶対パス・存在なしがログへ記録される")]
    public async Task 存在しないファイルの診断情報がログへ記録される()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var shell = await OpenShellAsync(logger).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var missingFullPath = Path.Combine(_projectDirectory, "missing.txt");
        _clipboard.Text = BuildPatch("missing.txt", "hello", "world");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        await logger.DisposeAsync().ConfigureAwait(true);

        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        var logText = await File.ReadAllTextAsync(logPath).ConfigureAwait(true);
        logText.Should().Contain("dry-run-file-probe");
        logText.Should().Contain(missingFullPath, "存在しない場合も、確認しに行った絶対パスが分かるように記録されるはず");
        logText.Should().Contain("存在=なし");

        // あわせて画面側もE210（誤解を招くE101ではない）になっていることを確認する。
        var plan = shell.Graft.Blocks.Single(b => b.PathText == "missing.txt");
        plan.IssueText.Should().Contain("E210");
        plan.IssueText.Should().NotContain("E101");
    }

    /// <summary>
    /// 本物のShellWindowは作らない（GitAutoCommitScenarioTests.OpenShellAsyncと同じ理由：
    /// window.Show()するとShellWindow.OnLoadedがMainViewModel.InitializeAsyncを二重に呼ぶ）。
    /// </summary>
    private async Task<ShellViewModel> OpenShellAsync(Logger logger)
    {
        var appPaths = new AppPaths(_appDirectory);
        var settingsStore = new SettingsStore(appPaths);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings { ShowPreview = false },
            settingsStore,
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            new AutoConfirmDialogService(),
            new FakeUiServices(_clipboard),
            openSettings: () => { });
        shell.Graft.Logger = logger;

        await shell.Graft.InitializeAsync().ConfigureAwait(true);
        return shell;
    }

    /// <summary>SEARCH/REPLACE形式のパッチ本文を組み立てる（仕様書4.1）。</summary>
    private static string BuildPatch(string relativePath, string search, string replace)
        => $"""
            <<<< PATCH
            summary: テスト用の変更
            type: fix
            >>>>

            <<<< FILE: {relativePath}
            <<<<<<< SEARCH
            {search}
            =======
            {replace}
            >>>>>>> REPLACE
            >>>> END

            """;

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

    /// <summary>確認をすべて承諾するダイアログ（他のScenarioTestsの同名クラスと同じ役割）。</summary>
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

    /// <summary>テストから内容を差し替えられるクリップボード。</summary>
    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public void SetText(string text) => Text = text;

        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    /// <summary>クリップボードだけ差し替えたUI機能一式。画面情報とタイマーは本物を使う。</summary>
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
