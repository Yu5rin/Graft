using System.Diagnostics;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 課題1「設定を変更しても実際の動作に反映されない」の回帰テスト。
///
/// 設定画面は即時反映方式（変更した瞬間にsettings.jsonへ保存）だが、以前は
/// <see cref="MainViewModel"/> がその変更を一切知らず（起動時に一度読むだけの
/// <c>_settings</c>フィールド）、実機で確認したとおり「適用後に自動コミットする」を
/// 実行中にオンへ切り替えても、再起動するまで動作が古いままだった。
///
/// ここでは<see cref="MainViewModel.UpdateSettings"/>（StartupCoordinatorがSettingsViewModelへ
/// 渡すコールバックの先で最終的に呼ばれるAPI）を直接呼び、代表的な設定項目（Git連携・
/// 安全機構・差分表示）が再起動なしで次の操作から効くこと、および適用処理の実行中は
/// 反映を保留し完了後にまとめて反映すること（安全機構・マッチング・Git連携の値が
/// 処理の途中で入れ替わらないこと）を検証する。
/// </summary>
public class LiveSettingsPropagationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-live-settings", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();

    public LiveSettingsPropagationTests()
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

    // ------------------------------------------------------------------
    // 代表的な設定項目（最低3種類）が再起動なしで次の操作から効くこと
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "課題1: Git.AutoCommitを実行中にオンへ変更すると、再起動せず次の適用でコミットされる")]
    public async Task GitAutoCommitを実行中に変更すると次の適用に反映される()
    {
        await InitGitRepoAsync(_projectDirectory).ConfigureAwait(true);
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);
        await RunGitAsync(_projectDirectory, "add", "-A").ConfigureAwait(true);
        await RunGitAsync(_projectDirectory, "commit", "-q", "-m", "初期コミット").ConfigureAwait(true);

        // AutoCommit=falseで起動（≒再起動していない状態を再現）。
        var baseSettings = new Settings { Git = new GitSettings { AutoCommit = false }, ShowPreview = false };
        var shell = await OpenShellAsync(baseSettings).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）", "feat");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        (await RunGitAsync(_projectDirectory, "status", "--porcelain").ConfigureAwait(true)).Should().NotBeEmpty(
            "1回目の適用時点ではまだAutoCommitはオフのまま");

        // 再起動せず、実行中のアプリへ設定変更を反映する（StartupCoordinator.ApplyLiveSettingsChange
        // の先でMainViewModelが受け取るのと同じ経路）。本物の設定画面は変更点だけでなく現在の
        // 設定全体を渡す（SettingsViewModel.CommitAndSaveAsync参照）ため、ここも新規のSettingsを
        // 作り直すのではなくbaseSettingsをwithで一部だけ書き換える（ShowPreview等、他の項目を
        // 既定値へ巻き戻してApplyPreviewWindow等の別機能を誤って有効化しないため）。
        shell.Graft.UpdateSettings(baseSettings with { Git = new GitSettings { AutoCommit = true } });

        _clipboard.Text = BuildPatch("sample.txt", "3行目", "3行目（変更後）", "fix");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        var log = await RunGitAsync(_projectDirectory, "log", "-1", "--pretty=%s").ConfigureAwait(true);
        log.Should().Be("fix: テスト用の変更 (r2)", "再起動なしでAutoCommitがオンになった直後の適用はコミットされるはず");
        (await RunGitAsync(_projectDirectory, "status", "--porcelain").ConfigureAwait(true)).Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "課題1: Safety.MaxFileSizeMBを実行中に下げると、再起動せず次の解析から拒否される")]
    public async Task 安全機構のファイルサイズ上限を実行中に変更すると次の解析に反映される()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        // 既定（10MB）で起動し、まず問題なく適用できることを確認する。
        var shell = await OpenShellAsync(new Settings()).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）", "feat");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.Blocks.Should().ContainSingle(b => b.Plan.CanApply, "既定の上限（10MB）では小さなファイルは問題なく適用できるはず");

        // 再起動せず、上限を0MBへ厳しく変更する（既存の全ファイルが上限超過になる）。
        shell.Graft.UpdateSettings(new Settings { Safety = new SafetySettings { MaxFileSizeMB = 0 } });

        _clipboard.Text = BuildPatch("sample.txt", "3行目", "3行目（変更後）", "fix");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Should().Contain(
            b => !b.Plan.CanApply && b.Plan.Issues.Any(i => i.Code == ErrorCode.E203),
            "再起動なしで上限を0MBへ変更した直後の解析では、既存ファイルはすべてサイズ超過で拒否されるはず");
    }

    [AvaloniaFact(DisplayName = "課題1: 差分表示の折り返しを変更すると、既に開いているdiffの見た目がその場で変わる")]
    public async Task 折り返し設定を変更すると開いているdiffがその場で変わる()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var shell = await OpenShellAsync(new Settings { Diff = new DiffSettings { WordWrap = false } }).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）", "feat");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.SelectedBlock.Should().NotBeNull("解析後は先頭ブロックが自動選択されdiffが開いているはず");
        shell.Graft.Diff.WordWrap.Should().BeFalse("既定は折り返しオフ");

        // 再起動どころか、diffを再読み込みすることすらせずに反映されるべき
        // （「既に開いている画面の表示にも反映が必要」という要件）。
        shell.Graft.UpdateSettings(new Settings { Diff = new DiffSettings { WordWrap = true } });

        shell.Graft.Diff.WordWrap.Should().BeTrue("設定変更が、選択中ブロックを読み直さずその場でdiffの見た目へ反映されるはず");
    }

    // ------------------------------------------------------------------
    // 適用処理の実行中は反映を保留し、完了後にまとめて反映すること
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "課題1: 適用処理の実行中に設定を変更しても今回の適用には反映されず、完了後の次回操作から反映される")]
    public async Task 適用処理実行中の設定変更は保留され完了後に反映される()
    {
        await InitGitRepoAsync(_projectDirectory).ConfigureAwait(true);
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);
        await RunGitAsync(_projectDirectory, "add", "-A").ConfigureAwait(true);
        await RunGitAsync(_projectDirectory, "commit", "-q", "-m", "初期コミット").ConfigureAwait(true);

        var baseSettings = new Settings { Git = new GitSettings { AutoCommit = false }, ShowPreview = false };
        var shell = await OpenShellAsync(baseSettings).ConfigureAwait(true);
        var registered = await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        // 適用後フックで適用処理そのものに時間をかけさせ、実行中に設定変更を割り込ませる余地を作る
        // （HookRunnerは/bin/sh経由でコマンドを実行する。HookScenarioTests参照）。
        await SetPostApplyHooksAsync(registered.Value.Id, new PostApplyHook
        {
            Name = "時間のかかる成功フック", Command = "sleep 0.4 && exit 0", OnFailure = HookFailureAction.Warn,
        }).ConfigureAwait(true);
        // フック追加後にプロジェクト一覧を読み直させ、PostApplyHooksをApplyAsyncが参照できるようにする。
        await shell.Graft.ProjectPane.LoadAsync().ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）", "feat");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        var applyCommand = (AsyncRelayCommand)shell.Graft.ApplyCommand;
        applyCommand.Execute(null); // await せず、フック実行中（約0.4秒）に割り込む。
        applyCommand.IsExecuting.Should().BeTrue("フックがsleepしている間はまだ適用処理の途中のはず");

        // この回の適用は開始時点の設定（AutoCommit=false）のまま最後まで動くべきで、
        // 実行中に割り込ませたこの変更はこの回には一切影響してはならない。
        // baseSettings with { ... } で一部だけ書き換える理由は上のテストと同じ
        // （新規のSettingsを作り直すとShowPreview等が既定値へ巻き戻ってしまうため）。
        shell.Graft.UpdateSettings(baseSettings with { Git = new GitSettings { AutoCommit = true } });

        // 状態（IsExecuting）が変わるまで待つ条件待ちであり、固定の待ち時間ではない。
        // 上限としてのタイムアウトはExecuteAsyncと同じ考え方で別途置く（原因の分かる例外で落とす）。
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                while (applyCommand.IsExecuting)
                {
                    await Task.Delay(10, cts.Token).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException ex)
            {
                throw new TimeoutException(
                    "ApplyCommandの実行が30秒以内に完了しませんでした（IsExecutingがtrueのまま）。", ex);
            }
        }

        (await RunGitAsync(_projectDirectory, "status", "--porcelain").ConfigureAwait(true)).Should().NotBeEmpty(
            "適用処理の実行中に割り込ませた設定変更は、開始済みのこの回の適用には反映されてはならない");

        // 完了後は保留していた変更がまとめて反映され、次の適用からは新しい設定で動くはず。
        _clipboard.Text = BuildPatch("sample.txt", "3行目", "3行目（変更後）", "fix");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        (await RunGitAsync(_projectDirectory, "status", "--porcelain").ConfigureAwait(true)).Should().BeEmpty(
            "適用完了後にまとめて反映された設定変更は、次回の適用から効くはず");
    }

    // ------------------------------------------------------------------
    // ヘルパー
    // ------------------------------------------------------------------

    private async Task SetPostApplyHooksAsync(string projectId, params PostApplyHook[] hooks)
    {
        var projectStore = new ProjectStore(new AppPaths(_appDirectory));
        var projects = (await projectStore.LoadAsync().ConfigureAwait(true)).Value.ToList();
        var index = projects.FindIndex(p => p.Id == projectId);
        projects[index] = projects[index] with { PostApplyHooks = hooks };
        await projectStore.SaveAsync(projects).ConfigureAwait(true);
    }

    /// <summary>
    /// 本物のShellWindowはここでは作らない。理由は二つ:
    ///
    /// 1. このファイルのテストは戻り値のWindowを一切使わない（ShellViewModel/MainViewModelの
    ///    設定反映だけを検証する）。にもかかわらずShellWindowを作ってwindow.Show()すると、
    ///    ShellWindow.OnLoadedが（Loadedイベント経由で非同期に）MainViewModel.InitializeAsyncを
    ///    もう一度呼んでしまう。このメソッド自身も明示的にInitializeAsyncを呼んでいるため、
    ///    「settings.jsonから読み直す処理が2回、順不同に走る」という競合が生まれ、後から呼ばれた
    ///    方が全ての値を（このメソッド内で行ったShowPreview=false化ごと）先に保存した内容へ
    ///    巻き戻してしまう。実際にこれが原因で「実行中に設定変更しても反映される」系のテストが
    ///    5割前後の確率で失敗する事故を確認した（設定変更のタイミングと2回目のInitializeAsyncの
    ///    タイミングがどちらが先かで結果が変わっていた）。ApplyPreviewScenarioTests.csと同じく
    ///    ShellWindowを作らずShellViewModelだけを構築し、InitializeAsyncは明示的に1回だけ呼ぶ。
    /// 2. ShellWindowを介さなければApplyPreviewRequestedの購読者も存在しないため、
    ///    ShowPreviewの既定値（true）を倒し忘れても本物のモーダルダイアログでハングする心配がない
    ///    （それでも倒し忘れに気づけるよう、以下ではShowPreview = falseを明示している）。
    /// </summary>
    private async Task<ShellViewModel> OpenShellAsync(Settings initialSettings)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        // MainViewModel.InitializeAsyncはSettingsStore.LoadAsyncで読み直すため、
        // コンストラクタへnew Settings()を渡すのではなく、あらかじめsettings.jsonへ
        // 初期値を書き込んでおく必要がある（GitAutoCommitScenarioTestsと同じ理由）。
        //
        // ShowPreview（課題1）はこのファイルの各テストの対象外（実行中の設定反映の検証）なので
        // 明示的にfalseにする。呼び出し元がbaseSettingsにShowPreview = falseを持たせたうえで
        // shell.Graft.UpdateSettings(baseSettings with { ... })と一部だけ書き換えて呼ぶ限りは
        // このwith書き換えの効果は呼び出し元にも及ぶが、念のためここでも倒しておく。
        initialSettings = initialSettings with { ShowPreview = false };
        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(initialSettings).ConfigureAwait(true);

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

        await shell.Graft.InitializeAsync().ConfigureAwait(true);
        return shell;
    }

    /// <summary>SEARCH/REPLACE形式のパッチ本文を組み立てる（GitAutoCommitScenarioTestsと同じ形式）。</summary>
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
            // 状態（IsExecuting）が変わるまで待つ条件待ちであり、固定の待ち時間ではない。
            // ただし本物のバグでIsExecutingが永遠にtrueのまま戻らなかった場合に無音のまま
            // 待ち続けないよう、上限としてのタイムアウトを別途置く（原因の分かる例外で落とす）。
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                while (async.IsExecuting)
                {
                    await Task.Delay(10, cts.Token).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException ex)
            {
                throw new TimeoutException(
                    "コマンドの実行が30秒以内に完了しませんでした（IsExecutingがtrueのまま）。", ex);
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

    private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
