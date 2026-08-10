using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 課題2「中断検出のダイアログが二重に出る」の回帰テスト。
///
/// 実機確認では、適用が中断された状態（manifestのstatusがin_progress）で起動すると、
/// 同じ事象について2枚のダイアログが別々に出ていた。
///   1枚目: StartupCoordinator.Validation.cs の OfferRollbackAsync（起動時検証の集約経路。
///          プロジェクト名・リビジョン番号を示し、ロールバックを提案する。これは正しい挙動）。
///   2枚目: MainViewModel.OnProjectSelected が独自に行っていたin_progress検出
///          （CheckInProgressAsync）。「r1が完了しないまま終了した可能性があります。
///          バックアップフォルダを確認してください」という対処不能な通知で、しかも
///          1枚目でロールバックした後も状態を知らないまま出続けていた。
///
/// 対処として2枚目（MainViewModel側の独自検出）を削除した。ここではその削除を回帰テストで
/// 固定する：in_progressのリビジョンを持つプロジェクトを選択しても、MainViewModelは
/// もう自分ではダイアログを出さないこと。
///
/// なお1枚目（StartupCoordinator.OfferRollbackAsync）自体はStartupCoordinatorが内部で
/// `new AvaloniaDialogService()`を直接生成するため（差し替え不可）、実際に1回だけ出ることの
/// 確認は実機（Xvfb）で行う。ここでは1枚目が示す文言（具体的で実行可能）が変わっていないことを
/// <see cref="StartupReport.BuildRollbackPrompt"/> の出力で固定する。
/// </summary>
public class InProgressRevisionDialogTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-inprogress-dialog", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly ShownWindowTracker _windows = new();

    public InProgressRevisionDialogTests()
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

    [AvaloniaFact(DisplayName = "課題2: in_progressのリビジョンを持つプロジェクトを選択しても、MainViewModelはもう独自の確認ダイアログを出さない")]
    public async Task プロジェクト選択時にMainViewModel独自の確認は出ない()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var dialogs = new RecordingDialogService();

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            dialogs,
            new AvaloniaUiServices(),
            openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        await shell.Graft.InitializeAsync().ConfigureAwait(true);

        var registered = await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        registered.IsSuccess.Should().BeTrue();
        var project = registered.Value;

        // 適用が中断された状態を再現する: BeginAsyncでmanifest.jsonをin_progressのまま残し、
        // CompleteAsyncを呼ばない（実機の再現手順と同じ。タスク冒頭のPythonスクリプトで
        // status書き換える代わりに、実際にBackupManagerで同じ状態を作る）。
        var backup = new BackupManager(appPaths);
        var initial = new RevisionManifest
        {
            Revision = 1, ProjectId = project.Id, Summary = "中断されたリビジョン",
            Type = "fix", AppliedAt = DateTimeOffset.Now, PatchHash = "dummyhash",
            Status = RevisionStatus.InProgress,
        };
        var began = await backup.BeginAsync(project.Id, project.Root, initial).ConfigureAwait(true);
        began.IsSuccess.Should().BeTrue(string.Join(",", began.Issues.Select(i => i.ToDisplayText())));

        // 登録直後の自動選択（in_progress作成前）とは別に、選択をやり直してOnProjectSelectedを
        // 再度発火させる（実機での「プロジェクトを選び直す」「再起動後に前回選択が復元される」に相当）。
        shell.Graft.ProjectPane.SelectedItem = null;
        shell.Graft.ProjectPane.SelectedItem = shell.Graft.ProjectPane.Items.Single(i => i.Project.Id == project.Id);

        // OnProjectSelectedはasync voidのため、内部のHistory.LoadAsync等が終わるまで少し待つ。
        // 待った後にダイアログが1件も呼ばれていないことを確認する（以前はここで
        // 「前回の適用が未完了です」という2枚目の重複ダイアログが出ていた）。
        await Task.Delay(300).ConfigureAwait(true);

        dialogs.ConfirmCalls.Should().BeEmpty(
            "MainViewModelはもうin_progress検出を自前で行わない。起動時検証（StartupCoordinator）側の" +
            "1枚のダイアログへ一本化されているはず（重複していた2枚目の削除）");
        dialogs.ShownMessages.Should().BeEmpty();
    }

    [Fact(DisplayName = "課題2: 起動時検証が示す唯一のダイアログ（1枚目）は、具体的なプロジェクト名・リビジョン番号を示しロールバックを提案する文言のまま")]
    public void 起動時検証のロールバック提案文言は具体的で実行可能である()
    {
        var issue = new InProgressRevisionIssue
        {
            ProjectId = "p_1", ProjectName = "中断検証", ProjectRoot = "/tmp/dummy",
            Revisions = new[]
            {
                new RevisionSummary
                {
                    Manifest = new RevisionManifest
                    {
                        Revision = 1, ProjectId = "p_1", Summary = "テスト", AppliedAt = DateTimeOffset.Now,
                        Status = RevisionStatus.InProgress,
                    },
                    FolderPath = "/tmp/dummy/back/p_1/r1",
                    IsRestorable = true,
                },
            },
        };

        var prompt = StartupReport.BuildRollbackPrompt(issue);

        prompt.Should().Contain("中断検証", "対処不能な「r1が…」だけの文言ではなく、プロジェクト名を明示するはず");
        prompt.Should().Contain("r1");
        prompt.Should().Contain("ロールバックしますか", "「確認してください」ではなく、実行可能な提案（ロールバック）である必要がある");
        prompt.Should().NotContain("バックアップフォルダを確認してください",
            "削除した2枚目（MainViewModel.CheckInProgressAsync）の対処不能な文言が復活していないことの確認");
    }

    /// <summary>ConfirmAsync/ShowMessageAsyncの呼び出しをすべて記録するテスト用IDialogService。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public List<(string Title, string Message)> ConfirmCalls { get; } = new();

        public List<string> ShownMessages { get; } = new();

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmCalls.Add((title, message));
            return Task.FromResult(true);
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(false);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message)
        {
            ShownMessages.Add(message);
            return Task.CompletedTask;
        }
    }
}
