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
/// 実機で確認された不具合2件（世代整理が一度も実行されない・リビジョン番号が採番されない）の
/// 通しシナリオ回帰テスト（画面あり）。ScenarioTests.csと同じ手法
/// （ProjectPane登録 → 解析 → 適用）で、MainViewModel.ApplyAsyncを実際に複数回通し、
/// projects.jsonのnextRevisionとback/配下の世代管理が実機の再現手順どおりに動くことを確認する
/// （1ファイル400行上限のためScenarioTests.csとは別ファイルに分割）。
/// </summary>
public class RevisionNumberingScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-revnum-scenario", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();

    public RevisionNumberingScenarioTests()
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

    [AvaloniaFact(DisplayName = "不具合2: 適用のたびにnextRevisionが1つずつ増えてprojects.jsonへ永続化される")]
    public async Task 適用のたびにnextRevisionが増える()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        _clipboard.Text = BuildPatch("sample.txt", "1行目", "1行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        (await ReadNextRevisionAsync(projectId).ConfigureAwait(true)).Should().Be(2,
            "1回目の適用でr1を使うのでnextRevisionは2になるはず（不具合修正前は常に1のままだった）");

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        (await ReadNextRevisionAsync(projectId).ConfigureAwait(true)).Should().Be(3, "2回目の適用でr2を使うのでnextRevisionは3になるはず");

        var revisions = new RevisionStore(new AppPaths(_appDirectory));
        var history = await revisions.ListAsync(projectId).ConfigureAwait(true);
        history.Value.Select(r => r.Manifest.Revision).Should().BeEquivalentTo(new[] { 1, 2 },
            "不具合修正前は毎回r1のまま採番されず、2回目の適用が1回目を上書きしてしまっていた");
    }

    [AvaloniaFact(DisplayName = "不具合2: 適用に失敗してもnextRevisionは消費される（同一番号のフォルダ衝突を防ぐため）")]
    public async Task 適用に失敗してもnextRevisionは消費される()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "ok.txt"), "hello\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(
            Path.Combine(_projectDirectory, "bad.txt"), "存在しない検索対象は含まれていません\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        // allOrNothing（既定）でok.txtは適用可能・bad.txtはSEARCH不一致で失敗する組み合わせにし、
        // ApplyEngine.ApplyAsync自体をFailで終わらせる（仕様書6章のallOrNothing）。
        _clipboard.Text = BuildPatch("ok.txt", "hello", "world") + "\n" + BuildPatch("bad.txt", "見つからない文字列", "置換後");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.ApplyCommand.CanExecute(null).Should().BeTrue("ok.txt側は適用可能なのでApplyコマンドは実行できるはず");

        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        var content = await File.ReadAllTextAsync(Path.Combine(_projectDirectory, "ok.txt")).ConfigureAwait(true);
        content.Should().Be("hello\n", "allOrNothingで中止されたのでok.txt側も変更されていないはず");
        (await ReadNextRevisionAsync(projectId).ConfigureAwait(true)).Should().Be(2,
            "適用に失敗した場合でも、BackupManager.BeginAsyncが先にバックアップフォルダを作りうるため、" +
            "再試行時の同一番号フォルダ衝突を防ぐ目的で番号は消費する設計とした（MainViewModel.Apply.cs参照）");
    }

    [AvaloniaFact(DisplayName = "不具合1: maxRevisionsを超えると適用のたびに古いリビジョンが自動で削除される")]
    public async Task 適用のたびに世代整理が実行される()
    {
        var settings = new Settings { Backup = new BackupSettings { MaxRevisions = 2, MaxTotalMB = 0, UseRecycleBin = false } };
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "v0\n").ConfigureAwait(true);

        var shell = await OpenShellAsync(settings).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        // 実機の再現手順（値を2→3→4へ変更するパッチを3回適用）をそのまま模す。
        foreach (var (from, to) in new[] { ("v0", "v2"), ("v2", "v3"), ("v3", "v4") })
        {
            _clipboard.Text = BuildPatch("sample.txt", from, to);
            await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
            await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
        }

        var appPaths = new AppPaths(_appDirectory);
        var backupDir = appPaths.GetProjectBackupDirectory(projectId);
        Directory.EnumerateDirectories(backupDir).Should().HaveCount(2,
            "maxRevisions=2のはずが、7.4の世代管理の呼び出し元が存在しない不具合により無制限に溜まってしまっていた");

        var revisions = new RevisionStore(appPaths);
        var history = await revisions.ListAsync(projectId).ConfigureAwait(true);
        history.Value.Where(r => r.IsRestorable).Select(r => r.Manifest.Revision)
            .Should().BeEquivalentTo(new[] { 2, 3 }, "残るのは新しい2件（r2・r3）で、最古のr1は削除されるはず");

        (await ReadNextRevisionAsync(projectId).ConfigureAwait(true)).Should().Be(4, "3回適用したのでnextRevisionは4になるはず");
    }

    private async Task<int> ReadNextRevisionAsync(string projectId)
    {
        var projectStore = new ProjectStore(new AppPaths(_appDirectory));
        var projects = (await projectStore.LoadAsync().ConfigureAwait(true)).Value;
        return projects.Single(p => p.Id == projectId).NextRevision;
    }

    /// <summary>
    /// 本物のShellWindowはここでは作らない。理由はLiveSettingsPropagationTests.OpenShellAsyncの
    /// コメントと同じ: このファイルの全テストは戻り値のWindowを一切使わない（採番・世代整理の
    /// 検証はShell/MainViewModelの状態とprojects.json/back/配下の実ファイルだけで完結する）に
    /// もかかわらず、window.Show()するとShellWindow.OnLoadedが非同期にMainViewModel.InitializeAsync
    /// をもう一度呼んでしまい、このメソッド自身が呼ぶ明示的なInitializeAsyncと実行順序が不定なまま
    /// 二重に走ってしまう（settings.json/projects.jsonの読み直しが2回、順不同で走る競合状態）。
    /// ShellWindowを作らずShellViewModelだけを構築し、InitializeAsyncは明示的に1回だけ呼ぶ。
    /// </summary>
    private async Task<ShellViewModel> OpenShellAsync(Settings? settings = null)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        // ShowPreview（課題1）はこのファイルの各テストの対象外（採番・世代整理の検証）なので
        // 明示的にfalseにし、ApplyCommandが素通りする従来どおりの挙動のまま検証できるようにする。
        settings = (settings ?? new Settings()) with { ShowPreview = false };

        // MainViewModel.InitializeAsync内でSettingsStore.LoadAsyncが改めて読み直すため、
        // BuildShellViewModelへ渡すSettingsは初期値の仮置きに過ぎない。先にsettings.jsonへ
        // 書いておく必要がある。
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
