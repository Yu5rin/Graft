using System.Diagnostics;
using System.Text;
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
/// 課題2「Git自動コミットが呼ばれていない」の通しシナリオ（画面あり）。
/// <see cref="GitIntegration.CommitAsync"/>自体は実装済みだったが呼び出し元が無く、
/// 設定画面の<see cref="GitSettings.AutoCommit"/>をオンにしても何も起きなかった不具合の
/// 回帰テスト。ScenarioTests.cs/HookScenarioTests.csと同じ手法（ProjectPane登録→解析→適用）で、
/// 適用後に実際に git commit が作られること・gitリポジトリでないプロジェクトではエラーに
/// ならないこと・適用後フックのロールバック時はコミットしないことを検証する。
/// </summary>
public class GitAutoCommitScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-gitcommit", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();

    public GitAutoCommitScenarioTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
    }

    public void Dispose()
    {
        // gitオブジェクトファイルの読み取り専用属性解除を含む共通の後片付け（不具合5）。
        TempDirectoryCleanup.TryDeleteRecursive(_root);

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "AutoCommitが有効なgitリポジトリでは、適用後に実際にコミットが作られる")]
    public async Task AutoCommit有効時は適用後にコミットが作られる()
    {
        await InitGitRepoAsync(_projectDirectory).ConfigureAwait(true);
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);
        await RunGitAsync(_projectDirectory, "add", "-A").ConfigureAwait(true);
        await RunGitAsync(_projectDirectory, "commit", "-q", "-m", "初期コミット").ConfigureAwait(true);

        var shell = await OpenShellAsync(autoCommit: true).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）", "feat");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        var log = await RunGitAsync(_projectDirectory, "log", "-1", "--pretty=%s").ConfigureAwait(true);
        log.Should().Be("feat: テスト用の変更 (r1)", "type: summary の形式にリビジョン番号を添えたメッセージになるはず");

        var status = await RunGitAsync(_projectDirectory, "status", "--porcelain").ConfigureAwait(true);
        status.Should().BeEmpty("適用した変更はコミットされ、作業ツリーはきれいなはず");
    }

    [AvaloniaFact(DisplayName = "AutoCommitが無効なら、gitリポジトリでもコミットは作られない")]
    public async Task AutoCommit無効時はコミットされない()
    {
        await InitGitRepoAsync(_projectDirectory).ConfigureAwait(true);
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);
        await RunGitAsync(_projectDirectory, "add", "-A").ConfigureAwait(true);
        await RunGitAsync(_projectDirectory, "commit", "-q", "-m", "初期コミット").ConfigureAwait(true);

        var shell = await OpenShellAsync(autoCommit: false).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）", "feat");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        var status = await RunGitAsync(_projectDirectory, "status", "--porcelain").ConfigureAwait(true);
        status.Should().NotBeEmpty("AutoCommitが無効なら変更はコミットされず作業ツリーに残るはず");
    }

    [AvaloniaFact(DisplayName = "AutoCommitが有効でもgitリポジトリでないプロジェクトはエラーにならず適用が完了する")]
    public async Task gitリポジトリでないプロジェクトでもエラーにならない()
    {
        // _projectDirectoryをgit initしない。
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var shell = await OpenShellAsync(autoCommit: true).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）", "feat");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        shell.Graft.State.Should().NotBe(CenterPaneState.Error, "gitリポジトリでないだけで適用自体を失敗にしてはならない");
        var content = await File.ReadAllTextAsync(targetPath).ConfigureAwait(true);
        content.Should().Contain("2行目（変更後）", "コミットに失敗しても、ファイルへの適用自体は成功しているはず");
    }

    [AvaloniaFact(DisplayName = "適用後フックでautoRollbackされた場合、ロールバックされた変更はコミットされない")]
    public async Task ロールバック時はコミットされない()
    {
        await InitGitRepoAsync(_projectDirectory).ConfigureAwait(true);
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);
        await RunGitAsync(_projectDirectory, "add", "-A").ConfigureAwait(true);
        await RunGitAsync(_projectDirectory, "commit", "-q", "-m", "初期コミット").ConfigureAwait(true);
        var beforeLog = await RunGitAsync(_projectDirectory, "log", "-1", "--pretty=%H").ConfigureAwait(true);

        var shell = await OpenShellAsync(autoCommit: true).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        // 6.5: 必ず失敗する適用後フック（autoRollback）を設定する。
        await SetPostApplyHooksAsync(projectId, new PostApplyHook
        {
            Name = "失敗フック", Command = "exit 1", OnFailure = HookFailureAction.AutoRollback,
        }).ConfigureAwait(true);
        await shell.Graft.ProjectPane.LoadAsync().ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）", "feat");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        var content = await File.ReadAllTextAsync(targetPath).ConfigureAwait(true);
        content.Should().NotContain("2行目（変更後）", "フック失敗によりファイルはロールバックされているはず");

        var afterLog = await RunGitAsync(_projectDirectory, "log", "-1", "--pretty=%H").ConfigureAwait(true);
        afterLog.Should().Be(beforeLog, "ロールバックされた変更はコミットされてはならない");

        var status = await RunGitAsync(_projectDirectory, "status", "--porcelain").ConfigureAwait(true);
        status.Should().BeEmpty("ロールバック後は作業ツリーも初期コミットの状態に戻っているはず");
    }

    // ------------------------------------------------------------------
    // 課題3: 自動コミット失敗をlogs/<日付>.logへ記録する
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "gitリポジトリでないプロジェクトへAutoCommit有効で適用すると、失敗理由がlogs/<日付>.logへ記録される")]
    public async Task gitリポジトリでない場合の失敗理由がログへ記録される()
    {
        // _projectDirectoryをgit initしない。
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var shell = await OpenShellAsync(autoCommit: true, logger: logger).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）", "feat");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        // チャネル経由の非同期書き込みを確実にファイルへ反映させてから読む。
        await logger.DisposeAsync().ConfigureAwait(true);

        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        File.Exists(logPath).Should().BeTrue("logs/<日付>.log が作られているはず");
        var logText = await File.ReadAllTextAsync(logPath).ConfigureAwait(true);
        logText.Should().Contain("git-auto-commit", "イベント種別で自動コミット関連のログだと分かるはず");
        logText.Should().Contain("gitリポジトリではない", "失敗理由が「リポジトリでない」と分かる文言で記録されているはず");
    }

    [AvaloniaFact(DisplayName = "AutoCommitが有効なgitリポジトリで実際にコミットが成功すると、成功もログへ記録される")]
    public async Task コミット成功時もログへ記録される()
    {
        await InitGitRepoAsync(_projectDirectory).ConfigureAwait(true);
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);
        await RunGitAsync(_projectDirectory, "add", "-A").ConfigureAwait(true);
        await RunGitAsync(_projectDirectory, "commit", "-q", "-m", "初期コミット").ConfigureAwait(true);

        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var shell = await OpenShellAsync(autoCommit: true, logger: logger).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）", "feat");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        await logger.DisposeAsync().ConfigureAwait(true);

        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        var logText = await File.ReadAllTextAsync(logPath).ConfigureAwait(true);
        logText.Should().Contain("git-auto-commit");
        logText.Should().Contain("コミットしました", "成功時もコミットハッシュ・メッセージを後から追えるよう記録されるはず");
    }

    /// <summary>projects.jsonへ直接、指定プロジェクトの適用後フックを設定する（テスト用の下拵え）。</summary>
    private async Task SetPostApplyHooksAsync(string projectId, params PostApplyHook[] hooks)
    {
        var projectStore = new ProjectStore(new AppPaths(_appDirectory));
        var projects = (await projectStore.LoadAsync().ConfigureAwait(true)).Value.ToList();
        var index = projects.FindIndex(p => p.Id == projectId);
        projects[index] = projects[index] with { PostApplyHooks = hooks };
        await projectStore.SaveAsync(projects).ConfigureAwait(true);
    }

    /// <summary>
    /// 本物のShellWindowはここでは作らない。理由はLiveSettingsPropagationTests.OpenShellAsyncの
    /// コメントと同じ: このファイルの全テストは戻り値のWindowを一切使わない（Shell/MainViewModelの
    /// 状態だけを検証する）にもかかわらず、window.Show()するとShellWindow.OnLoadedが非同期に
    /// MainViewModel.InitializeAsyncをもう一度呼んでしまい、このメソッド自身が呼ぶ明示的な
    /// InitializeAsyncと実行順序が不定なまま二重に走ってしまう（settings.json/projects.jsonの
    /// 読み直しが2回、順不同で走る競合状態）。ShellWindowを作らずShellViewModelだけを構築し、
    /// InitializeAsyncは明示的に1回だけ呼ぶ。
    /// </summary>
    private async Task<ShellViewModel> OpenShellAsync(bool autoCommit, Logger? logger = null)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        // MainViewModel.InitializeAsyncはSettingsStore.LoadAsyncで読み直すため、
        // コンストラクタへnew Settings()を渡すのではなく、あらかじめsettings.jsonへ
        // Git.AutoCommitを書き込んでおく必要がある。ShowPreviewはこのテストの対象外
        // （課題1の適用前プレビュー確認）なので明示的にfalseにし、ApplyCommandが
        // 素通りする従来どおりの挙動のまま検証できるようにする。
        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(
            new Settings { Git = new GitSettings { AutoCommit = autoCommit }, ShowPreview = false }).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            settingsStore,
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            new AutoConfirmDialogService(),
            new FakeUiServices(_clipboard),
            openSettings: () => { });
        // 課題3: 本物の起動処理（StartupCoordinator.StartAsync）と同じく、生成後に設定する
        // nullableプロパティ経由でロガーを渡す。
        shell.Graft.Logger = logger;

        await shell.Graft.InitializeAsync().ConfigureAwait(true);
        return shell;
    }

    /// <summary>
    /// SEARCH/REPLACE形式のパッチ本文を組み立てる（仕様書4.1）。summary/typeはFILEヘッダではなく
    /// 先頭の"&lt;&lt;&lt;&lt; PATCH"メタブロックへ書く必要がある（FILEヘッダ内の"summary:"は
    /// パーサ上ただの無視される行で、PatchMeta.Summaryには反映されない。
    /// tests/Graft.Tests/Fixtures/Patches/patch_meta_full.txt参照）。
    /// </summary>
    private static string BuildPatch(string relativePath, string search, string replace, string type)
        => $"""
            <<<< PATCH
            summary: テスト用の変更
            type: {type}
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

    /// <summary>git init と、テスト実行環境のグローバル設定に依存しないローカルのuser設定を行う。</summary>
    private static async Task InitGitRepoAsync(string root)
    {
        await RunGitAsync(root, "init", "-q").ConfigureAwait(true);
        await RunGitAsync(root, "config", "user.email", "test@example.com").ConfigureAwait(true);
        await RunGitAsync(root, "config", "user.name", "Graft Test").ConfigureAwait(true);
    }

    /// <summary>
    /// テスト側の検証用git実行ヘルパー。製品コードのGitIntegration.RunGitAsyncとは別実装だが、
    /// gitのUTF-8出力を正しく読めないと検証（68行目の日本語コミットメッセージ比較）自体が
    /// Windows上で誤って失敗するため、同じくエンコーディングを明示する（不具合1）。
    /// </summary>
    private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(true);
        await process.WaitForExitAsync().ConfigureAwait(true);
        return stdout.Trim();
    }

    /// <summary>確認をすべて承諾するダイアログ（ScenarioTestsの同名クラスと同じ役割）。</summary>
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
