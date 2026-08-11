using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
    private readonly ShownWindowTracker _windows = new();

    public HookScenarioTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
    }

    public void Dispose()
    {
        // 表示したShellWindowを後始末する（ShownWindowTracker参照。閉じ忘れると
        // 「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」がCIで不定期に出る）。
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

    /// <summary>
    /// このファイルの1つ目のテストは <c>window.CaptureRenderedFrame()</c> で実際の描画結果を
    /// 確認するため、本物のShellWindowを実体化する必要がある（LiveSettingsPropagationTestsや
    /// GitAutoCommitScenarioTestsのようにShellViewModelだけに置き換えると、描画そのものの
    /// 検証内容が失われてしまう）。
    ///
    /// そのためwindow.Show()は必須だが、window.Show()はShellWindow.OnLoadedを介して非同期に
    /// MainViewModel.InitializeAsyncを呼ぶ。ここでさらに明示的にInitializeAsyncを呼んでしまうと、
    /// 2つの初期化が実行順序不定のまま同時に走り、settings.json/projects.jsonの読み直しが
    /// 競合する（LiveSettingsPropagationTests.OpenShellAsync参照。実機で5割前後の確率での
    /// 失敗を確認した事故と同じ種類の競合状態）。そこでこのメソッドはInitializeAsyncを
    /// 自分では呼ばず、OnLoaded経由の初期化が完了するのを待つだけにする。ProjectPane.Stateは
    /// InitializeAsyncの最後に呼ばれるProjectPane.LoadAsyncの完了時点で必ずLoading以外へ
    /// 変わるため、これを初期化完了の合図として使う（初期化が1回しか走らないので安全に待てる）。
    /// </summary>
    private async Task<(ShellViewModel Shell, Avalonia.Controls.Window Window)> OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        // ShowPreview（課題1）はこのテストの対象外（適用後フックの検証）なので明示的にfalseにし、
        // ApplyCommandが素通りする従来どおりの挙動のまま検証できるようにする。
        // MainViewModel.InitializeAsync内でSettingsStore.LoadAsyncが改めて読み直すため、
        // BuildShellViewModelへ渡すSettingsは初期値の仮置きに過ぎない。先にsettings.jsonへ
        // 書いておかないと既定値（ShowPreview=true）に戻ってしまい、このテストは本物の
        // ShellWindowを使う（誰も閉じないApplyPreviewWindowのShowDialogで無限に固まる）ため、
        // 他のシナリオテストと同じくここで明示的に保存する。
        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(new Settings { ShowPreview = false }).ConfigureAwait(true);

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

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        await WaitForShellInitializedAsync(shell).ConfigureAwait(true);
        return (shell, window);
    }

    /// <summary>
    /// window.Show()（ShellWindow.OnLoaded経由）が裏で走らせているMainViewModel.InitializeAsyncの
    /// 完了を、それ自身を呼び直すことなく待つ。ProjectPaneViewModelはLoading状態で構築され、
    /// InitializeAsyncの最後で呼ぶProjectPane.LoadAsyncが完了するまでLoadingのまま変わらないため、
    /// これが変わったことをもって初期化完了とみなせる。
    /// </summary>
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
            throw new TimeoutException(
                "ShellWindow.OnLoaded経由の初期化が30秒以内に完了しませんでした（ProjectPane.StateがLoadingのまま）。", ex);
        }
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
