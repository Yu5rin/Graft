using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 修正依頼文のコピー経路の検証。
/// 右クリックメニューの「修正依頼プロンプトをコピー」は、ヘルプ文が「このブロックの」なのに、
/// 引数を渡していなかったため失敗ブロックすべてがコピーされていた。メニューはその行のブロック
/// だけを、ボタン（「修正を依頼」）は従来どおり失敗ブロックすべてをコピーすることを確かめる。
/// 併せて、切れたパッチの継続依頼が受け取った形式に合わせて出ることも、画面の流れで確かめる
/// （文面そのものの検証は Graft.Tests の RecoveryPromptTests）。
/// </summary>
public class RecoveryPromptScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-recovery-prompt", Guid.NewGuid().ToString("N"));
    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly RecordingDialogService _dialogs = new();

    public RecoveryPromptScenarioTests()
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

    private async Task<ShellViewModel> ParseTwoFailedBlocksAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "a.txt"), "a1\na2\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "b.txt"), "b1\nb2\n").ConfigureAwait(true);
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text =
            "<<<< PATCH\nsummary: test\ntype: fix\n>>>>\n\n" +
            "<<<< FILE: a.txt\n<<<<<<< SEARCH\nA-ONLY-SEARCH\n=======\nA-REPLACE\n>>>>>>> REPLACE\n\n" +
            "<<<< FILE: b.txt\n<<<<<<< SEARCH\nB-ONLY-SEARCH\n=======\nB-REPLACE\n>>>>>>> REPLACE\n\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.Blocks.Should().HaveCount(2).And.OnlyContain(b => b.IsError);
        return shell;
    }

    [AvaloniaFact(DisplayName = "右クリックの「修正依頼プロンプトをコピー」: 押した行のブロックだけがコピーされる")]
    public async Task 右クリックは1ブロックだけをコピーする()
    {
        var shell = await ParseTwoFailedBlocksAsync().ConfigureAwait(true);
        var blockB = shell.Graft.Blocks.Single(b => b.Plan.Path == "b.txt");

        shell.CopyBlockRecoveryPromptCommand.CanExecute(blockB).Should().BeTrue();
        await shell.Graft.CopyRecoveryPromptForBlockAsync(blockB).ConfigureAwait(true);

        _clipboard.Text.Should().Contain("■ b.txt").And.Contain("B-ONLY-SEARCH").And.Contain("B-REPLACE");
        _clipboard.Text.Should().NotContain("a.txt").And.NotContain("A-ONLY-SEARCH",
            "別のブロックの内容が混ざってはならない（修正前は失敗ブロックすべてがコピーされた）");
        _dialogs.Messages.Last().Message.Should().Contain("「b.txt」");
    }

    [AvaloniaFact(DisplayName = "右クリックのコマンドを実行すると、押した行のブロックだけがクリップボードへ入る")]
    public async Task 右クリックのコマンド経由でも1ブロックだけ()
    {
        var shell = await ParseTwoFailedBlocksAsync().ConfigureAwait(true);
        var blockA = shell.Graft.Blocks.Single(b => b.Plan.Path == "a.txt");

        shell.CopyBlockRecoveryPromptCommand.Execute(blockA);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (_dialogs.Messages.Count == 0 && !cts.IsCancellationRequested) await Task.Delay(10).ConfigureAwait(true);

        _clipboard.Text.Should().Contain("A-ONLY-SEARCH").And.NotContain("B-ONLY-SEARCH");
    }

    [AvaloniaFact(DisplayName = "右クリックのコマンド: 成功したブロックでは実行できない")]
    public async Task 成功ブロックでは実行できない()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "ok.txt"), "x\ny\n").ConfigureAwait(true);
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        _clipboard.Text = "<<<< FILE: ok.txt\n<<<<<<< SEARCH\nx\n=======\nX\n>>>>>>> REPLACE\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        var ok = shell.Graft.Blocks.Single();
        ok.IsOk.Should().BeTrue();
        shell.CopyBlockRecoveryPromptCommand.CanExecute(ok).Should().BeFalse();

        var before = _clipboard.Text;
        await shell.Graft.CopyRecoveryPromptForBlockAsync(ok).ConfigureAwait(true);
        _clipboard.Text.Should().Be(before, "失敗していないブロックでは何もコピーしない");
    }

    [AvaloniaFact(DisplayName = "「修正を依頼」ボタン: 従来どおり失敗ブロックすべてがコピーされる")]
    public async Task ボタンは失敗ブロックすべてをコピーする()
    {
        var shell = await ParseTwoFailedBlocksAsync().ConfigureAwait(true);

        await ExecuteAsync(shell.Graft.CopyRecoveryPromptCommand).ConfigureAwait(true);

        _clipboard.Text.Should().Contain("■ a.txt").And.Contain("A-ONLY-SEARCH");
        _clipboard.Text.Should().Contain("■ b.txt").And.Contain("B-ONLY-SEARCH");
        _clipboard.Text.Should().Contain("同じGraft形式で出力してください");
        _dialogs.Messages.Last().Message.Should().Contain("2件の失敗ブロック");
    }

    [AvaloniaFact(DisplayName = "継続依頼: 標準SR形式で受け取った切れたパッチの続きは、標準SR形式で頼む")]
    public async Task 継続依頼は受け取った形式に合わせる()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = "a.txt\n<<<<<<< SEARCH\nfoo\n=======\nbar\n>>>>>>> REPLACE\nb.txt\n<<<<<<< SEARCH\nbaz\n=======\nqu";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        _clipboard.Text.Should().Contain("以下の続きから、同じ標準SEARCH/REPLACE形式で出力してください。");
        _clipboard.Text.Should().NotContain("Graft形式");
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
            _dialogs, new FakeUiServices(_clipboard), openSettings: () => { });

        await shell.Graft.InitializeAsync().ConfigureAwait(true);
        return shell;
    }

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting) await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private sealed class RecordingDialogService : IDialogService
    {
        public List<(string Title, string Message)> Messages { get; } = new();
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel) => Task.FromResult<bool?>(true);
        public Task<string?> PromptAsync(string title, string message, string? initial = null) => Task.FromResult<string?>(initial ?? "test");
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add((title, message));
            return Task.CompletedTask;
        }
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
