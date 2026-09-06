using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 不具合5（実機点検）の回帰テスト: ブロックのチェックを外しても、ステータスバーの
/// 「N件適用可 / M件要確認」（<see cref="MainViewModel.StatusSummaryText"/>）が変わらなかった不具合。
///
/// 【原因】 修正前はドライラン時点の<see cref="DryRunResult.ApplicableCount"/>
/// （チェックの状態を見ない集計）をそのまま表示していた。
/// 【修正】 <see cref="MainViewModel.Blocks"/>の現在のチェック状態を都度数え直す形にし、
/// いずれかの行のチェックが変わるたびに再評価されるよう購読した（MainViewModel.ReplaceBlocks・
/// OnAnyBlockPropertyChanged参照）。
/// </summary>
public class StatusSummarySelectionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-status-summary-selection", Guid.NewGuid().ToString("N"));
    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();

    public StatusSummarySelectionTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "不具合5回帰: 2ブロックのうち1件のチェックを外すと、ステータスバーの表示が「1件を適用（1件は対象外）」に変わる")]
    public async Task チェックを外すとステータスバーの件数が追従する()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildFullPatch("src/app.py", "print(1)\n") + "\n" + BuildFullPatch("README.md", "hello\n");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Should().HaveCount(2);
        shell.Graft.StatusSummaryText.Should().Be("2件適用可 / 0件要確認", "解析直後は両方チェック済みのはず");

        var readme = shell.Graft.Blocks.Single(b => b.PathText == "README.md");
        readme.IsSelected = false;

        shell.Graft.StatusSummaryText.Should().Be("1件を適用（1件は対象外） / 0件要確認",
            "チェックを外した時点で、実際に適用される件数が分かる表示へ即座に変わるはず");

        readme.IsSelected = true;
        shell.Graft.StatusSummaryText.Should().Be("2件適用可 / 0件要確認", "チェックを戻せば元の表示に戻るはず");
    }

    [AvaloniaFact(DisplayName = "不具合5: マッチ失敗（自動的に未選択）のブロックは「対象外」の件数に含めない")]
    public async Task 失敗ブロックは対象外の件数に含まれない()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "exists.py"), "line1\n").ConfigureAwait(true);

        _clipboard.Text = BuildSrPatch("exists.py", "no such text", "replacement");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Should().ContainSingle();
        // 「対象外」（チェックを外した件数）と「失敗」（マッチ失敗などそもそも適用不可能な件数、
        // 別担当がUI点検・項目9で追加したDryRunResult.FailedCount由来の表示）は別の軸のため、
        // マッチ失敗の1件は「対象外」には含めず「失敗」側にのみ表れる。
        shell.Graft.StatusSummaryText.Should().Be("0件適用可 / 0件要確認 / 1件失敗",
            "マッチ失敗はユーザーがチェックを外したわけではないので「対象外」には含めない（「失敗」側の集計で表れる）");
    }

    private async Task<ShellViewModel> OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var settings = new Settings { ShowPreview = false };
        await new SettingsStore(appPaths).SaveAsync(settings).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths, settings, new SettingsStore(appPaths), new Graft.Features.PatchQueue(appPaths),
            new Graft.Features.ProjectStore(appPaths), new RevisionStore(appPaths), new RevisionRestorer(appPaths),
            new AutoConfirmDialogService(), new FakeUiServices(_clipboard), openSettings: () => { });

        await shell.Graft.InitializeAsync().ConfigureAwait(true);
        return shell;
    }

    private static string BuildFullPatch(string relativePath, string content)
        => $"<<<< FILE: {relativePath} MODE=FULL\n{content}\n>>>> END\n";

    private static string BuildSrPatch(string relativePath, string search, string replace)
        => "<<<< PATCH\nsummary: test\ntype: fix\n>>>>\n\n<<<< FILE: " + relativePath +
           "\n<<<<<<< SEARCH\n" + search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n>>>> END\n\n";

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting) await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private sealed class AutoConfirmDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel) => Task.FromResult<bool?>(true);
        public Task<string?> PromptAsync(string title, string message, string? initial = null) => Task.FromResult<string?>(initial ?? "test");
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
        public FakeUiServices(IClipboardAccess clipboard) { Clipboard = clipboard; }
        public IClipboardAccess Clipboard { get; }
        public IScreenInfo Screens => _inner.Screens;
        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }
}
