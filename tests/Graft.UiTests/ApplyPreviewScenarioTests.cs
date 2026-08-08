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
/// 課題1（設定「適用前にプレビューを表示する」/ Settings.ShowPreview）の回帰テスト。
///
/// 実際のApplyPreviewWindowの見た目・操作は実機確認（Xvfbでのスクリーンショット）で担保し、
/// ここではMainViewModel.ApplyAsyncが設定に応じて<see cref="MainViewModel.ApplyPreviewRequested"/>
/// を発火するかどうか、および発火時の結果（適用する／キャンセルする）が実際の書き込み可否へ
/// 正しく反映されることを検証する。ShellWindowを作らずMainViewModel/ShellViewModel側だけで
/// 検証しているのは、実際にApplyPreviewWindowを開くとheadless環境ではShowDialogが誰にも
/// 閉じられずテストがハングするため（このイベントはViewがダイアログを閉じるまで待つ設計。
/// ShellWindow.OnApplyPreviewRequested参照）。ここではテスト自身をViewの代わりに見立てて
/// イベントを直接購読し、Completionへ結果を書き込む。
/// </summary>
public class ApplyPreviewScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-apply-preview", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();

    public ApplyPreviewScenarioTests()
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

    [AvaloniaFact(DisplayName = "課題1: ShowPreview=trueだと適用前にApplyPreviewRequestedが発火し、確認してはじめて書き込まれる")]
    public async Task ShowPreview有効だとプレビュー確認を経て適用される()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var shell = await BuildShellAsync(showPreview: true).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var requestedCount = 0;
        IReadOnlyList<BlockPlan>? capturedPlans = null;
        shell.Graft.ApplyPreviewRequested += (_, e) =>
        {
            requestedCount++;
            capturedPlans = e.PlansToApply;
            e.Completion.TrySetResult(true); // 「適用」を押した想定。
        };

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        requestedCount.Should().Be(1, "ShowPreview有効時は書き込み前に必ずプレビュー確認を経由するはず");
        capturedPlans.Should().ContainSingle().Which.Path.Should().Be("sample.txt",
            "実際に書き込まれる対象（チェック済みかつ適用可能なブロック）だけが渡されるはず");

        var applied = await File.ReadAllTextAsync(targetPath).ConfigureAwait(true);
        applied.Should().Contain("2行目（変更後）", "プレビューで「適用」を選んだので実際に書き込まれるはず");
    }

    [AvaloniaFact(DisplayName = "課題1: プレビューで「キャンセル」を選ぶと、書き込まれずリビジョン番号も消費されない")]
    public async Task ShowPreviewでキャンセルすると書き込まれない()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var shell = await BuildShellAsync(showPreview: true).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        shell.Graft.ApplyPreviewRequested += (_, e) => e.Completion.TrySetResult(false); // 「キャンセル」を押した想定。

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        var applied = await File.ReadAllTextAsync(targetPath).ConfigureAwait(true);
        applied.Should().Be("1行目\n2行目\n3行目\n", "プレビューでキャンセルしたので書き込まれてはならない");

        var projectStore = new ProjectStore(new AppPaths(_appDirectory));
        var projects = (await projectStore.LoadAsync().ConfigureAwait(true)).Value;
        projects.Single(p => p.Id == projectId).NextRevision.Should().Be(1,
            "書き込み前の段階でキャンセルしているので、リビジョン番号を消費してはならない");

        // 課題1: バックアップフォルダ（back/<プロジェクトID>/r<番号>_.../）はApplyEngine.ApplyAsync
        // （BackupManager.BeginAsync）の中でのみ作られる。プレビューのキャンセルはApplyEngine.ApplyAsync
        // 自体を一度も呼ばないため（MainViewModel.Apply.csのconfirmedチェックで早期return）、
        // そもそも作られようがないはずだが、「副作用が残らないこと」の最重要要件のため明示的に確認する。
        var backupProjectDirectory = Path.Combine(new AppPaths(_appDirectory).BackupRootDirectory, projectId);
        Directory.Exists(backupProjectDirectory).Should().BeFalse(
            "プレビューでキャンセルした場合、バックアップフォルダが一切作られていてはならない");
    }

    [AvaloniaFact(DisplayName = "課題1: ShowPreview=falseだとApplyPreviewRequestedは発火せず、従来どおりのテキスト確認だけで適用される")]
    public async Task ShowPreview無効だとプレビューを挟まない()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var shell = await BuildShellAsync(showPreview: false).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var requested = false;
        shell.Graft.ApplyPreviewRequested += (_, e) =>
        {
            requested = true;
            e.Completion.TrySetResult(true);
        };

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        requested.Should().BeFalse("ShowPreview無効時はプレビュー確認を挟まないはず（現在の挙動のまま）");
        var applied = await File.ReadAllTextAsync(targetPath).ConfigureAwait(true);
        applied.Should().Contain("2行目（変更後）", "ShowPreview無効でも通常のテキスト確認（AutoConfirmDialogService）を経て適用されるはず");
    }

    [AvaloniaFact(DisplayName = "課題1関連: 設定画面での変更はUpdateSettings経由で実行中のセッションへ即座に反映される")]
    public async Task 設定画面での変更が実行中のセッションへ反映される()
    {
        // 実機検証で発見: MainViewModel.ApplyAsync等が参照するSettingsはInitializeAsync時点の
        // キャッシュのままで、設定画面（SettingsViewModel）での変更はsettings.jsonへ即時反映
        // 方式で保存されるにもかかわらず、StartupCoordinator.ApplyLiveSettingsChangeが
        // MainViewModelへ通知していなかったため、アプリを再起動するまでShowPreviewの
        // トグルが一切効かなかった（StartupCoordinator.ApplyLiveSettingsChange参照）。
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var shell = await BuildShellAsync(showPreview: true).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        // 設定画面を開き直さず、StartupCoordinatorが呼ぶのと同じ経路（UpdateSettings）を
        // 直接呼んで「保存済みだが未反映」の状態を再現する。
        shell.Graft.UpdateSettings(new Settings { ShowPreview = false });

        var requested = false;
        shell.Graft.ApplyPreviewRequested += (_, e) =>
        {
            requested = true;
            e.Completion.TrySetResult(true);
        };

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        requested.Should().BeFalse("UpdateSettingsを呼んだ時点で、以後のApplyAsyncは新しい設定を見るはず（再起動不要）");
        var applied = await File.ReadAllTextAsync(targetPath).ConfigureAwait(true);
        applied.Should().Contain("2行目（変更後）");
    }

    private async Task<ShellViewModel> BuildShellAsync(bool showPreview)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        // MainViewModel.InitializeAsync内でSettingsStore.LoadAsyncが改めて読み直すため、
        // BuildShellViewModelへ渡すSettingsは初期値の仮置きに過ぎない。先にsettings.jsonへ
        // 書いておく必要がある（他のシナリオテストと同じ手法）。
        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(new Settings { ShowPreview = showPreview }).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings { ShowPreview = showPreview },
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

    /// <summary>確認をすべて承諾するダイアログ（他のシナリオテストと同じ役割）。</summary>
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
