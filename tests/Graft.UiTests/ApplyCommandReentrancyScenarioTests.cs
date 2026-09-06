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
/// 点検で実測された「適用の多重起動」の通しシナリオ回帰テスト（画面あり）。
///
/// 【修正前に実測した壊れ方】 <c>ApplyCommand.Execute(null)</c> を3回連続で呼ぶと、
/// ・確認ダイアログが3回呼ばれた（＝適用処理が3本同時に走った）
/// ・それでも nextRevision は 2 のままで、記録されたリビジョンは r1 の1件だけ
/// ・back/ には r1_20260906_062744 が1つだけ
/// ＝3つの適用が同じ r1 のバックアップフォルダを同時に読み書きしていた。
/// 利用者から見ると「バックアップと履歴の対応が壊れ、『ここまで戻す』で戻せない世代ができる」
/// という形で現れる。
///
/// 【2つの不具合の合わせ技だった】
/// ・<see cref="AsyncRelayCommand.Execute"/> に自己ガードが無く、キーボード経路
///   （ShellWindow.Keyboard.cs の Ctrl+Enter 等）の直呼びが素通しで多重起動できた。
/// ・<see cref="ProjectStore"/> の「LoadAsync → 加工 → SaveAsync」に排他が無く、
///   多重起動した適用が同じリビジョン番号を払い出してしまった。
/// このテストは前者（＝入口）を、CommandReentrancyGuardTests がコマンド単体で、
/// ProjectStoreConcurrencyTests が後者（＝ストア側）を、それぞれ担当する。ここでは
/// 実機と同じ「接ぎ木パネルの適用を3連打する」形で、両方の修正が効いていることを通しで見る。
///
/// なお実機（Windows）では適用の直後にモーダルダイアログが出るため、2回目のCtrl+Enterが
/// ダイアログ表示前の数msの隙間に入る必要があり、実UIでの再現は確認できていない。
/// ここで固定するのはヘッドレスのViewModelレベルの振る舞いだが、構造として塞いだことの
/// 回帰防止になる（この形の呼び出しが今後増えても壊れない）。
///
/// テストの組み立て（ShellWindowを作らずShellViewModelだけを構築する等）は
/// RevisionNumberingScenarioTests.cs と同じ理由・同じ手順に揃えている。
/// </summary>
public class ApplyCommandReentrancyScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-apply-reentrancy", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly CountingDialogService _dialogs = new();

    public ApplyCommandReentrancyScenarioTests()
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

    [AvaloniaFact(DisplayName = "ApplyCommand.Execute(null)を3回連続で呼んでも、確認ダイアログは1回しか出ない（適用は1本しか走らない）")]
    public async Task 適用の3連打でも確認ダイアログは1回しか呼ばれない()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        _clipboard.Text = BuildPatch("sample.txt", "1行目", "1行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.ApplyCommand.CanExecute(null).Should().BeTrue("解析済みで適用可能な状態から始める");

        // 実機のCtrl+Enter（ShellWindow.Keyboard.cs:228）と同じ直呼びを3連打する。
        shell.Graft.ApplyCommand.Execute(null);
        shell.Graft.ApplyCommand.Execute(null);
        shell.Graft.ApplyCommand.Execute(null);
        await WaitUntilIdleAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        _dialogs.ConfirmCount.Should().Be(1,
            "修正前は確認ダイアログが3回呼ばれた（＝適用処理が3本同時に走り、同じr1のバックアップフォルダを奪い合っていた）");

        // 適用そのものは1回だけ、正常に完了している必要がある。
        var content = await File.ReadAllTextAsync(targetPath).ConfigureAwait(true);
        content.Should().Be("1行目（変更後）\n2行目\n3行目\n");

        var appPaths = new AppPaths(_appDirectory);
        var projects = (await new ProjectStore(appPaths).LoadAsync().ConfigureAwait(true)).Value;
        projects.Single(p => p.Id == projectId).NextRevision.Should().Be(2, "適用は1回だけなのでr1を消費して2になる");

        var revisions = await new RevisionStore(appPaths).ListAsync(projectId).ConfigureAwait(true);
        revisions.Value.Select(r => r.Manifest.Revision).Should().BeEquivalentTo(new[] { 1 });
        Directory.EnumerateDirectories(appPaths.GetProjectBackupDirectory(projectId)).Should().HaveCount(1,
            "3本が同じr1のフォルダを読み書きするのではなく、1本だけが1つのフォルダを作る");
    }

    [AvaloniaFact(DisplayName = "デグレ防止: 1回目の適用が終わってからの2回目は従来どおり適用され、r1・r2と採番される")]
    public async Task 逐次的な適用は従来どおり動く()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        _clipboard.Text = BuildPatch("sample.txt", "1行目", "1行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        _dialogs.ConfirmCount.Should().Be(2, "逐次的な2回の適用では確認も2回出る（多重起動の防止であって連続操作の禁止ではない）");

        var content = await File.ReadAllTextAsync(targetPath).ConfigureAwait(true);
        content.Should().Be("1行目（変更後）\n2行目（変更後）\n3行目\n");

        var appPaths = new AppPaths(_appDirectory);
        var projects = (await new ProjectStore(appPaths).LoadAsync().ConfigureAwait(true)).Value;
        projects.Single(p => p.Id == projectId).NextRevision.Should().Be(3);

        var revisions = await new RevisionStore(appPaths).ListAsync(projectId).ConfigureAwait(true);
        revisions.Value.Select(r => r.Manifest.Revision).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    /// <summary>
    /// RevisionNumberingScenarioTests.OpenShellAsyncと同じ理由でShellWindowは作らず、
    /// ShellViewModelだけを構築してInitializeAsyncを明示的に1回だけ呼ぶ。
    /// </summary>
    private async Task<ShellViewModel> OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        // ShowPreview=falseにして、確認をテキストの確認ダイアログ（IDialogService.ConfirmAsync）
        // に固定する。実測時と同じ経路（「N件を適用します。よろしいですか？」）を数えるため。
        var settings = new Settings { ShowPreview = false };
        await new SettingsStore(appPaths).SaveAsync(settings).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            settings,
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            _dialogs,
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

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        await WaitUntilIdleAsync(command).ConfigureAwait(true);
    }

    private static async Task WaitUntilIdleAsync(System.Windows.Input.ICommand command)
    {
        if (command is not AsyncRelayCommand async)
        {
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (async.IsExecuting)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("コマンドの完了待ちがタイムアウトしました。");
            }

            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    /// <summary>確認をすべて承諾しつつ、確認ダイアログが何回出たかを数えるダイアログ。</summary>
    private sealed class CountingDialogService : IDialogService
    {
        private int _confirmCount;

        /// <summary>「N件を適用します。よろしいですか？」が呼ばれた回数。</summary>
        public int ConfirmCount => Volatile.Read(ref _confirmCount);

        public Task<bool> ConfirmAsync(string title, string message)
        {
            Interlocked.Increment(ref _confirmCount);
            return Task.FromResult(true);
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

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
