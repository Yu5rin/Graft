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
/// 主要シナリオを画面ありで通しで動かす（仕様書v2.1 18章「UIの自動検証」・20章 L5）。
///
/// ViewTestsが「画面が壊れずに描けること」、StartupTestsが「本番と同じ依存で組めること」を
/// 見るのに対し、ここでは利用者の操作の流れ（プロジェクト登録 → 解析 → ブロック選択 →
/// 差分表示 → 適用）をViewModel経由で実行し、UIが追随することまで確認する。
/// </summary>
public class ScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-scenario", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();

    public ScenarioTests()
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

    [AvaloniaFact(DisplayName = "プロジェクト登録から解析・差分表示・適用まで通しで動く")]
    public async Task 解析から適用まで通しで動く()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);

        // 1. プロジェクトを登録すると一覧に現れ、選択される。
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        shell.Graft.ProjectPane.Items.Should().ContainSingle();
        shell.Graft.ProjectPane.SelectedItem.Should().NotBeNull();
        window.CaptureRenderedFrame().Should().NotBeNull();

        // 2. クリップボードのパッチを解析すると、ブロック一覧に反映される。
        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Should().ContainSingle("パッチ1件がブロックとして並ぶ必要がある");
        shell.Graft.Blocks[0].IsOk.Should().BeTrue("マッチできる内容なので適用可になる必要がある");
        shell.IsGraftPanelOpen.Should().BeTrue("解析すると接ぎ木パネルが自動展開される（9.2）");
        window.CaptureRenderedFrame().Should().NotBeNull();

        // 3. ブロックを選ぶと、差分がエディタ領域のタブとして開く（9.2/4.8）。
        shell.Graft.SelectedBlock = shell.Graft.Blocks[0];
        shell.Editor.ActiveTab.Should().NotBeNull();
        shell.Editor.ActiveTab!.Kind.Should().Be(EditorTabKind.Diff);
        window.CaptureRenderedFrame().Should().NotBeNull();

        // 4. 適用するとファイルが書き換わり、履歴が1件増える。
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        var applied = await File.ReadAllTextAsync(targetPath).ConfigureAwait(true);
        applied.Should().Contain("2行目（変更後）");
        applied.Should().NotContain("2行目\n", "元の行は置き換わっている必要がある");
        window.CaptureRenderedFrame().Should().NotBeNull();
    }

    [AvaloniaFact(DisplayName = "エクスプローラからファイルを開くとエディタのタブになる")]
    public async Task エクスプローラからファイルを開ける()
    {
        var targetPath = Path.Combine(_projectDirectory, "open-me.txt");
        await File.WriteAllTextAsync(targetPath, "開いた内容\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        shell.SelectSideView(SideViewKind.Explorer);
        window.CaptureRenderedFrame().Should().NotBeNull();

        var opened = await shell.Editor.OpenFileAsync(targetPath, preview: false).ConfigureAwait(true);
        opened.IsSuccess.Should().BeTrue();

        shell.Editor.Tabs.Should().ContainSingle();
        shell.Editor.ActiveTab!.Session.Document.Text.Should().Contain("開いた内容");
        window.CaptureRenderedFrame().Should().NotBeNull();
    }

    [AvaloniaFact(DisplayName = "解析に失敗したパッチはエラーとして表示される")]
    public async Task 解析に失敗したパッチはエラーになる()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        // ファイル内に存在しない内容をSEARCH部に置くとマッチに失敗する（E101）。
        _clipboard.Text = BuildPatch("sample.txt", "存在しない行", "置換後");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Should().ContainSingle();
        shell.Graft.Blocks[0].IsError.Should().BeTrue("マッチできないブロックは失敗として示される必要がある");
        shell.Graft.Blocks[0].IssueText.Should().Contain("E101");
        window.CaptureRenderedFrame().Should().NotBeNull();
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

        // AsyncRelayCommand は ICommand.Execute が void のため、実行中かどうかで完了を待つ。
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting)
            {
                await Task.Delay(10).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// 確認をすべて承諾するダイアログ。実際の操作では利用者が「はい」を押す場面にあたる
    /// （Null実装は常に否定を返すため、適用まで進めない）。
    /// </summary>
    private sealed class AutoConfirmDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    /// <summary>テストから内容を差し替えられるクリップボード。</summary>
    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public void SetText(string text) => Text = text;

        public string? GetText() => Text;
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
