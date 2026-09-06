using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 不具合3（実機点検）の回帰テスト: 対象ファイルが存在しないブロック（E210）で、
/// 「SEARCH部を編集すれば直せる」というインライン編集の誘導と「E210 ファイルが見つからない」
/// という矛盾する2つの診断が同時に出ていた不具合。
///
/// 【原因】 <see cref="DiffViewModel.BuildInlineEdits"/>はCanApply=falseのSearchReplaceBlockで
/// あれば理由を問わず（SEARCH不一致でもファイル不在でも）インライン編集パネル用の
/// <see cref="InlineEditViewModel"/>を作っていた。ファイルが存在しない場合、
/// <see cref="BlockPlan.BeforeText"/>はnull（<see cref="DiffViewModel.BuildInlineEdits"/>内では
/// 空文字列扱い）になるため、「実際のファイル内容」が常に空欄のインライン編集パネルが出て、
/// 編集しても永久にマッチしない状態だった。
///
/// 【修正】 DiffViewModelにE210専用の<see cref="DiffViewModel.IsMissingFile"/>を追加し、
/// この場合はインライン編集パネル（HasInlineEdits）を出さないようにした
/// （DiffView.axamlのMissingFile用バナーに差し替え）。
/// </summary>
public class MissingFileDiagnosisTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-missing-file-diagnosis", Guid.NewGuid().ToString("N"));
    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();

    public MissingFileDiagnosisTests()
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

    [AvaloniaFact(DisplayName = "不具合3回帰: 存在しないファイルのブロックはインライン編集を出さず、IsMissingFileがtrueになる")]
    public async Task 存在しないファイルはインライン編集を出さない()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildSrPatch("src/missing.py", "hello", "world");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        var block = shell.Graft.Blocks.Should().ContainSingle().Subject;
        block.IssueText.Should().Contain("E210", "ブロック一覧にはこれまでどおりE210が出る必要がある");
        block.IssueText.Should().NotContain("E101", "誤解を招くE101を出してはならない（既存のE210対応を踏襲）");

        shell.Graft.SelectedBlock = block;

        shell.Graft.Diff.IsMissingFile.Should().BeTrue("対象ファイルが存在しないブロックのはず");
        shell.Graft.Diff.MissingFileDetailText.Should().Contain("E210");
        shell.Graft.Diff.HasInlineEdits.Should().BeFalse(
            "ファイルが無い以上SEARCH部を編集しても直らないため、インライン編集パネルを出してはならない");
    }

    [AvaloniaFact(DisplayName = "対照: SEARCH不一致（ファイルは存在する）は従来どおりインライン編集を出す")]
    public async Task ファイルはあるが不一致の場合は従来どおり()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var target = Path.Combine(_projectDirectory, "exists.py");
        await File.WriteAllTextAsync(target, "print('existing content')\n").ConfigureAwait(true);

        _clipboard.Text = BuildSrPatch("exists.py", "no such text", "replacement");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        var block = shell.Graft.Blocks.Should().ContainSingle().Subject;
        block.IssueText.Should().Contain("E101");

        shell.Graft.SelectedBlock = block;

        shell.Graft.Diff.IsMissingFile.Should().BeFalse("ファイル自体は存在するため対象外のはず");
        shell.Graft.Diff.HasInlineEdits.Should().BeTrue("従来どおりインライン編集パネルを出す必要がある");
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
