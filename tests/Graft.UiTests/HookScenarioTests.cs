using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 仕様書6.5「適用後フック」の通しシナリオ（画面あり）。ScenarioTests.csと同じ手法
/// （ProjectPane登録 → 解析 → 適用）で、フック実行結果に応じたonFailure分岐
/// （autoRollback／warn＋manifest記録）を検証する（1ファイル400行上限のためScenarioTests.csとは
/// 別ファイルに分割）。
/// </summary>
public class HookScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-hookscenario", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();

    public HookScenarioTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
    }

    public void Dispose()
    {
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

    [AvaloniaFact(DisplayName = "適用後フックが失敗しonFailure=autoRollbackだと、適用直後に自動で巻き戻る")]
    public async Task 適用後フックの自動ロールバックが動く()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        // 必ず失敗する適用後フック（autoRollback）をプロジェクトへ設定してから読み直す
        // （6.5章: onFailure=autoRollbackは確認なしに直前の状態へロールバックする）。
        await SetPostApplyHooksAsync(projectId, new PostApplyHook
        {
            Name = "失敗フック", Command = "exit 1", OnFailure = HookFailureAction.AutoRollback,
        }).ConfigureAwait(true);
        await shell.Graft.ProjectPane.LoadAsync().ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        var content = await File.ReadAllTextAsync(targetPath).ConfigureAwait(true);
        content.Should().Contain("2行目\n", "フック失敗により適用前の内容へロールバックされているはず");
        content.Should().NotContain("2行目（変更後）", "ロールバック後は変更後の内容が残っていてはならない");
        window.CaptureRenderedFrame().Should().NotBeNull();
    }

    [AvaloniaFact(DisplayName = "適用後フックが成功すると、実行結果が当該リビジョンのmanifestに記録される")]
    public async Task 適用後フックの成功が履歴に記録される()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        await SetPostApplyHooksAsync(projectId, new PostApplyHook
        {
            Name = "成功フック", Command = "exit 0", OnFailure = HookFailureAction.Warn,
        }).ConfigureAwait(true);
        await shell.Graft.ProjectPane.LoadAsync().ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        var revisions = new RevisionStore(new AppPaths(_appDirectory));
        var history = await revisions.ListAsync(projectId).ConfigureAwait(true);
        var latest = history.Value.Should().ContainSingle().Subject;
        latest.Manifest.Hooks.Should().ContainSingle();
        latest.Manifest.Hooks[0].Name.Should().Be("成功フック");
        latest.Manifest.Hooks[0].ExitCode.Should().Be(0);

        var content = await File.ReadAllTextAsync(targetPath).ConfigureAwait(true);
        content.Should().Contain("2行目（変更後）", "警告扱いのため適用は維持されているはず");
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

    private async Task<(ShellViewModel Shell, Avalonia.Controls.Window Window)> OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            new AutoConfirmDialogService(),
            new FakeUiServices(_clipboard),
            openSettings: () => { });

        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();
        await shell.Graft.InitializeAsync().ConfigureAwait(true);
        return (shell, window);
    }

    /// <summary>SEARCH/REPLACE形式のパッチ本文を組み立てる（仕様書4.1）。</summary>
    private static string BuildPatch(string relativePath, string search, string replace)
        => $"""
            <<<< FILE: {relativePath}
            summary: テスト用の変更
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
