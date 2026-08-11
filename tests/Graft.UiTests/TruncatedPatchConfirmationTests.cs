using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 課題2「途中で切れたパッチの続きプロンプトを、確認なしにクリップボードへ上書きコピーして
/// いた」不具合の回帰テスト。実機で3MBのパッチが消失したことが確認されている
/// （利用者が貼り付けていた元のパッチが、事後報告のみのダイアログでは守れなかった）。
/// 現在は事前の確認ダイアログへ変更し、キャンセルすれば元のクリップボードの内容が
/// 保たれることを検証する。解析できたブロックが有る場合／無い場合の両方の経路を確認する。
/// </summary>
public class TruncatedPatchConfirmationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-truncated-confirm", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly ShownWindowTracker _windows = new();

    public TruncatedPatchConfirmationTests()
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

    [AvaloniaFact(DisplayName = "解析できたブロックが無い切断パッチ: キャンセルすると元のクリップボード内容が保たれる")]
    public async Task ブロック無しの切断パッチをキャンセルすると元のクリップボードが保たれる()
    {
        var dialogs = new RecordingDialogService { ConfirmResult = false };
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var original = BuildTruncatedPatch(includeCompleteBlock: false);
        _clipboard.Text = original;

        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        dialogs.ConfirmCallCount.Should().Be(1, "コピー前に必ず確認が挟まるはず");
        dialogs.LastConfirmMessage.Should().Contain("解析できたブロックは無かった");
        dialogs.LastConfirmMessage.Should().Contain("失われます", "コピーで元の内容が失われることが事前に伝わる必要がある");
        _clipboard.Text.Should().Be(original, "キャンセルしたのでクリップボードの内容（元のパッチ）が保たれているはず");
    }

    [AvaloniaFact(DisplayName = "解析できたブロックが有る切断パッチ: キャンセルすると元のクリップボード内容が保たれる")]
    public async Task ブロック有りの切断パッチをキャンセルすると元のクリップボードが保たれる()
    {
        var dialogs = new RecordingDialogService { ConfirmResult = false };
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var original = BuildTruncatedPatch(includeCompleteBlock: true);
        _clipboard.Text = original;

        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        dialogs.ConfirmCallCount.Should().Be(1);
        dialogs.LastConfirmMessage.Should().Contain("解析できた1件をキューへ追加しました");
        dialogs.LastConfirmMessage.Should().Contain("失われます");
        _clipboard.Text.Should().Be(original, "キャンセルしたのでクリップボードの内容（元のパッチ）が保たれているはず");
    }

    [AvaloniaFact(DisplayName = "確認で「コピーする」を選ぶと、続きを依頼するプロンプトでクリップボードが上書きされる")]
    public async Task 確認して同意すると続きプロンプトへ上書きされる()
    {
        var dialogs = new RecordingDialogService { ConfirmResult = true };
        var (shell, _) = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var original = BuildTruncatedPatch(includeCompleteBlock: true);
        _clipboard.Text = original;

        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        dialogs.ConfirmCallCount.Should().Be(1);
        _clipboard.Text.Should().NotBe(original, "同意したので続きを依頼するプロンプトへ上書きされているはず");
        _clipboard.Text.Should().NotBeNullOrEmpty();
    }

    // window.Show()はShellWindow.OnLoaded経由で非同期にshell.Graft.InitializeAsync()を呼ぶ。
    // ここでさらに明示的に呼ぶと初期化が二重に走り、settings.json/projects.jsonの読み直しが
    // 競合する（ScenarioTests.OpenShellAsync参照、実機で5割前後の確率での失敗を確認した
    // 事故と同じ種類の競合状態）。自分では呼ばず、OnLoaded経由の初期化完了を
    // ShellWindowLoadWaiterで待つ（非同期I/Oを行わなくなったため、呼び出し側を変えずに
    // 済むようasyncを外しTask.FromResultで包む）。
    private Task<(ShellViewModel Shell, Avalonia.Controls.Window Window)> OpenShellAsync(IDialogService dialogs)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new Graft.Features.PatchQueue(appPaths),
            new Graft.Features.ProjectStore(appPaths),
            new Graft.Core.RevisionStore(appPaths),
            new Graft.Core.RevisionRestorer(appPaths),
            dialogs,
            new FakeUiServices(_clipboard),
            openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);
        return Task.FromResult<(ShellViewModel, Avalonia.Controls.Window)>((shell, window));
    }

    /// <summary>
    /// 終了マーカー（&gt;&gt;&gt;&gt; END）を欠いたまま終わる切断パッチ。includeCompleteBlockが
    /// trueのときは、切断より前に完全な1ブロック（DELETE）を含める。
    /// </summary>
    private static string BuildTruncatedPatch(bool includeCompleteBlock)
    {
        var sb = new System.Text.StringBuilder();
        if (includeCompleteBlock)
        {
            sb.AppendLine("<<<< DELETE: src/complete.py");
        }
        sb.AppendLine("<<<< FILE: src/incomplete.py");
        sb.AppendLine("<<<<<<< SEARCH");
        sb.AppendLine("old_value = 1");
        sb.AppendLine("=======");
        sb.AppendLine("new_value = 2");
        // ">>>>>>> REPLACE" と ">>>> END" を欠いたまま終わる = 切断として検出される。
        return sb.ToString();
    }

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

    /// <summary>ConfirmAsyncの呼び出し回数・引数・戻り値を制御できるダイアログ。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; }

        public int ConfirmCallCount { get; private set; }

        public string? LastConfirmMessage { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmCallCount++;
            LastConfirmMessage = message;
            return Task.FromResult(ConfirmResult);
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => throw new InvalidOperationException($"想定外の3択確認ダイアログが呼ばれました: {title} / {message}");

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message)
            => throw new InvalidOperationException($"想定外の通知ダイアログが呼ばれました（事前確認に一本化されたはず）: {title} / {message}");
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
