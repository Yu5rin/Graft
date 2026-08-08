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
/// 仕様書4.1「ファイルからのパッチ解析」の通しシナリオ（画面あり）。ScenarioTests.csと同じ手法
/// （ProjectPane登録 → 解析）だが、クリップボードではなくファイルからの読み込みを起点にする点が
/// 異なる（1ファイル400行上限のためScenarioTests.csとは別ファイルに分割）。
///
/// 解析部分（テキストを受け取って解析する内部経路）はクリップボード経路と共有しているため、
/// ここではGraft形式・unified diff形式の両方がファイル経由でも解析できることを確認し、
/// 形式ごとの詳細な解析仕様の検証はPatchParserTests/UnifiedDiffAdapterTestsに委ねる。
/// </summary>
public class FileParseScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-fileparse", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;

    public FileParseScenarioTests()
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

    [AvaloniaFact(DisplayName = "ファイルから解析するとGraft形式のパッチが読み込まれる")]
    public async Task ファイルから解析するとGraft形式が読み込まれる()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var patchPath = Path.Combine(_root, "patch.md");
        await File.WriteAllTextAsync(patchPath, BuildPatch("sample.txt", "2行目", "2行目（変更後）")).ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await shell.Graft.LoadPatchFromFileAsync(patchPath).ConfigureAwait(true);

        shell.Graft.Blocks.Should().ContainSingle("ファイルから読み込んだパッチもブロックとして並ぶ必要がある");
        shell.Graft.Blocks[0].IsOk.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "ファイルから解析するとunified diff形式のパッチも読み込まれる")]
    public async Task ファイルから解析するとunified_diff形式も読み込まれる()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var diff = "--- a/sample.txt\n+++ b/sample.txt\n@@ -1,3 +1,3 @@\n 1行目\n-2行目\n+2行目（変更後）\n 3行目\n";
        var patchPath = Path.Combine(_root, "patch.diff");
        await File.WriteAllTextAsync(patchPath, diff).ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await shell.Graft.LoadPatchFromFileAsync(patchPath).ConfigureAwait(true);

        shell.Graft.Blocks.Should().ContainSingle("unified diff形式もブロックとして並ぶ必要がある");
        shell.Graft.Blocks[0].IsOk.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "1MB超のファイルは明確なエラーで拒否される")]
    public async Task サイズ上限超のファイルは拒否される()
    {
        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var hugePath = Path.Combine(_root, "huge.txt");
        await File.WriteAllTextAsync(hugePath, new string('a', 1024 * 1024 + 1)).ConfigureAwait(true);

        await shell.Graft.LoadPatchFromFileAsync(hugePath).ConfigureAwait(true);

        shell.Graft.State.Should().Be(CenterPaneState.Error);
        shell.Graft.CenterError!.Code.Should().Be(ErrorCode.E203);
    }

    [AvaloniaFact(DisplayName = "バイナリファイルは明確なエラーで拒否される")]
    public async Task バイナリファイルは拒否される()
    {
        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var binPath = Path.Combine(_root, "binary.dat");
        await File.WriteAllBytesAsync(binPath, new byte[] { 0, 1, 2, 3, 0, 0, 0 }).ConfigureAwait(true);

        await shell.Graft.LoadPatchFromFileAsync(binPath).ConfigureAwait(true);

        shell.Graft.State.Should().Be(CenterPaneState.Error);
        shell.Graft.CenterError!.Code.Should().Be(ErrorCode.E703);
    }

    [AvaloniaFact(DisplayName = "「ファイルを選んで解析する」コマンドはダイアログで選んだファイルを解析する")]
    public async Task ファイル選択コマンドで解析できる()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var patchPath = Path.Combine(_root, "patch.md");
        await File.WriteAllTextAsync(patchPath, BuildPatch("sample.txt", "2行目", "2行目（変更後）")).ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync(new PickFileDialogService(patchPath)).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ExecuteAsync(shell.Graft.ParseFromFileCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Should().ContainSingle("ダイアログで選んだファイルの内容がブロックとして並ぶ必要がある");
    }

    private async Task<(ShellViewModel Shell, Avalonia.Controls.Window Window)> OpenShellAsync(IDialogService? dialogs = null)
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
            dialogs ?? new AutoConfirmDialogService(),
            new FakeUiServices(),
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

    /// <summary>
    /// 4.1: 「ファイルを選んで解析する」コマンドの検証用。PickFileAsyncで常に指定パスを返す
    /// 以外はAutoConfirmDialogServiceと同じ（確認はすべて承諾）。
    /// </summary>
    private sealed class PickFileDialogService : IDialogService
    {
        private readonly string _path;

        public PickFileDialogService(string path) => _path = path;

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(_path);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    /// <summary>クリップボードは使わないが、IUiServicesの他機能（画面情報・タイマー）は本物を使う。</summary>
    private sealed class FakeUiServices : IUiServices
    {
        private readonly AvaloniaUiServices _inner = new();

        public IClipboardAccess Clipboard => _inner.Clipboard;

        public IScreenInfo Screens => _inner.Screens;

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }
}
