using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 起動時の「未完了の適用を検出しました」で承諾したあとの扱い
/// （<see cref="StartupCoordinator.OfferRollbackAsync"/>）の回帰テスト。
/// 以前は復元が何も戻さずに成功を返し、それだけで rolled_back の印を付けていたため、書き換わったファイルが
/// 残ったまま以後は検出もされなかった。戻せたときだけ印を付け、戻せなかったときは利用者に伝える。
/// </summary>
public class StartupRollbackOfferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-startup-rollback", Guid.NewGuid().ToString("N"));
    private readonly string _projectRoot;
    private readonly AppPaths _paths;
    private const string ProjectId = "p_test01";

    public StartupRollbackOfferTests()
    {
        _projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(_projectRoot);
        _paths = new AppPaths(Path.Combine(_root, "app"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(InProgressRevisionIssue Issue, string FolderPath)> CreateInterruptedAsync()
    {
        File.WriteAllText(Path.Combine(_projectRoot, "a.txt"), "original\n");
        var initial = new RevisionManifest
        {
            Revision = 1, ProjectId = ProjectId, AppliedAt = DateTimeOffset.Now, Status = RevisionStatus.InProgress,
        };
        var began = await new BackupManager(_paths).BeginAsync(ProjectId, _projectRoot, initial);
        (await began.Value.StoreAsync("a.txt")).IsSuccess.Should().BeTrue();
        File.WriteAllText(Path.Combine(_projectRoot, "a.txt"), "half-way\n");

        var summary = (await new RevisionStore(_paths).ReadAsync(ProjectId, 1)).Value;
        var issue = new InProgressRevisionIssue
        {
            ProjectId = ProjectId, ProjectName = "テスト", ProjectRoot = _projectRoot, Revisions = new[] { summary },
        };
        return (issue, began.Value.FolderPath);
    }

    [Fact(DisplayName = "承諾すると退避から実際に戻り、rolled_backの印が付いて、次回は検出されない")]
    public async Task 承諾すると実際に戻り印が付く()
    {
        var (issue, _) = await CreateInterruptedAsync();
        var dialogs = new RecordingDialogs { ConfirmResult = true };

        await StartupCoordinator.OfferRollbackAsync(issue, dialogs, new RevisionRestorer(_paths));

        File.ReadAllText(Path.Combine(_projectRoot, "a.txt")).Should().Be("original\n");
        var store = new RevisionStore(_paths);
        (await store.ReadAsync(ProjectId, 1)).Value.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        (await store.FindInterruptedAsync(ProjectId)).Value.Offer.Should().BeEmpty();
        dialogs.Messages.Should().BeEmpty("確認事項が無ければ、確認ダイアログ以外は出さない");
    }

    [Fact(DisplayName = "戻せなかったときは、印を付けず、戻せなかったことを利用者に伝える")]
    public async Task 戻せなければ伝えて印を付けない()
    {
        var (issue, folder) = await CreateInterruptedAsync();
        Directory.Delete(Path.Combine(folder, "files"), recursive: true);
        var dialogs = new RecordingDialogs { ConfirmResult = true };

        await StartupCoordinator.OfferRollbackAsync(issue, dialogs, new RevisionRestorer(_paths));

        File.ReadAllText(Path.Combine(_projectRoot, "a.txt")).Should().Be("half-way\n");
        var store = new RevisionStore(_paths);
        (await store.ReadAsync(ProjectId, 1)).Value.Manifest.Status.Should().Be(RevisionStatus.InProgress);
        dialogs.Messages.Should().ContainSingle().Which.Title.Should().Contain("元の状態に戻せませんでした");
    }

    [Fact(DisplayName = "承諾しなければ何も変えない")]
    public async Task 承諾しなければ何もしない()
    {
        var (issue, _) = await CreateInterruptedAsync();
        var dialogs = new RecordingDialogs { ConfirmResult = false };

        await StartupCoordinator.OfferRollbackAsync(issue, dialogs, new RevisionRestorer(_paths));

        File.ReadAllText(Path.Combine(_projectRoot, "a.txt")).Should().Be("half-way\n");
        (await new RevisionStore(_paths).ReadAsync(ProjectId, 1)).Value.Manifest.Status.Should().Be(RevisionStatus.InProgress);
    }

    private sealed class RecordingDialogs : IDialogService
    {
        public bool ConfirmResult { get; init; }
        public List<(string Title, string Message)> Messages { get; } = new();

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(ConfirmResult);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(null);

        public Task<string?> PromptAsync(string title, string message, string? initial = null) => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message)
        {
            Messages.Add((title, message));
            return Task.CompletedTask;
        }
    }
}
