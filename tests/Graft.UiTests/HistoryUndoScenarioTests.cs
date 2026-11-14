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
/// 取り消したリビジョンが success のまま残る不具合の通しシナリオテスト。
/// 以前は、適用→取り消し→同じパッチをもう一度解析すると「r1で適用済み」(E302)で適用できず、
/// 「直前を取り消す」を続けて押すと同じリビジョンをまた取り消そうとしていた。
/// HistoryRestoreThroughScenarioTests と同じ手法（ProjectPane登録→貼り付け→解析→適用）で実際に通す。
/// </summary>
public class HistoryUndoScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-undo-scenario", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly RecordingDialogService _dialogs = new();

    public HistoryUndoScenarioTests()
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

    [AvaloniaFact(DisplayName = "取り消したあと、同じパッチをもう一度解析しても「適用済み」にならず、適用し直せる")]
    public async Task 取り消したパッチを貼り直して適用できる()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(target, "original\n").ConfigureAwait(true);
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "sample.txt", "patched").ConfigureAwait(true); // r1
        (await File.ReadAllTextAsync(target).ConfigureAwait(true)).Should().Be("patched\n");

        (await shell.Graft.History.UndoLatestAsync().ConfigureAwait(true)).Should().BeTrue();
        (await File.ReadAllTextAsync(target).ConfigureAwait(true)).Should().Be("original\n");
        var r1 = shell.Graft.History.Items.Single(i => i.RevisionLabel == "r1");
        r1.Revision.Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        r1.RestorabilityText.Should().Be("取り消し済み", "履歴の一覧で、取り消し済みとそれと分かる表示にする");
        r1.CanRestore.Should().BeFalse();

        // 同じパッチをもう一度貼り付けて解析する。
        _clipboard.Text = "<<<< FILE: sample.txt MODE=FULL\npatched\n>>>> END\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.AlreadyAppliedRevision.Should().BeNull("取り消し済みのr1と同じパッチでも、適用済みとは扱わない");
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        (await File.ReadAllTextAsync(target).ConfigureAwait(true)).Should().Be("patched\n");
        shell.Graft.History.Items.Select(i => i.RevisionLabel).Should().Contain("r2");
    }

    [AvaloniaFact(DisplayName = "「直前を取り消す」を続けて押すと、同じリビジョンではなく、さらにひとつ前のリビジョンを取り消す")]
    public async Task 直前を取り消すを続けると順にさかのぼる()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(target, "v0\n").ConfigureAwait(true);
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "v2").ConfigureAwait(true); // r2

        (await shell.Graft.History.UndoLatestAsync().ConfigureAwait(true)).Should().BeTrue();
        (await File.ReadAllTextAsync(target).ConfigureAwait(true)).Should().Be("v1\n");

        (await shell.Graft.History.UndoLatestAsync().ConfigureAwait(true)).Should().BeTrue("2回目はr1を取り消す");
        (await File.ReadAllTextAsync(target).ConfigureAwait(true)).Should().Be("v0\n");

        (await shell.Graft.History.UndoLatestAsync().ConfigureAwait(true)).Should().BeFalse("取り消せるリビジョンはもう無い");
        (await File.ReadAllTextAsync(target).ConfigureAwait(true)).Should().Be("v0\n", "3回目で何かを書き戻してはならない");
    }

    [AvaloniaFact(DisplayName = "ここまで戻すで取り消したリビジョンも取り消し済みになり、戻しすぎたときは、その操作を取り消せば元の状態に戻る")]
    public async Task ここまで戻すの取り消し済み表示と戻しすぎの取り消し()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(target, "v0\n").ConfigureAwait(true);
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "v2").ConfigureAwait(true); // r2
        await ApplyFullAsync(shell, "sample.txt", "v3").ConfigureAwait(true); // r3

        var history = shell.Graft.History;
        history.SelectedItem = history.Items.Single(i => i.RevisionLabel == "r1");
        await ExecuteAsync(history.RestoreThroughCommand).ConfigureAwait(true); // r4（r2・r3を取り消す）
        (await File.ReadAllTextAsync(target).ConfigureAwait(true)).Should().Be("v1\n");

        var store = new RevisionStore(new AppPaths(_appDirectory));
        var list = (await store.ListAsync(projectId).ConfigureAwait(true)).Value;
        list.Single(r => r.Manifest.Revision == 2).Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        list.Single(r => r.Manifest.Revision == 3).Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        list.Single(r => r.Manifest.Revision == 1).Manifest.Status.Should().Be(RevisionStatus.Success);
        history.Items.Single(i => i.RevisionLabel == "r3").RestorabilityText.Should().Be("取り消し済み");

        // 戻しすぎた: r4（この操作自体）を取り消すと、v3の状態に戻り、r2・r3は有効に戻る。
        history.SelectedItem = history.Items.Single(i => i.RevisionLabel == "r4");
        await ExecuteAsync(history.RestoreCommand).ConfigureAwait(true);
        (await File.ReadAllTextAsync(target).ConfigureAwait(true)).Should().Be("v3\n");

        list = (await store.ListAsync(projectId).ConfigureAwait(true)).Value;
        list.Single(r => r.Manifest.Revision == 4).Manifest.Status.Should().Be(RevisionStatus.RolledBack);
        list.Single(r => r.Manifest.Revision == 3).Manifest.Status.Should().Be(RevisionStatus.Success);
        list.Single(r => r.Manifest.Revision == 2).Manifest.Status.Should().Be(RevisionStatus.Success);
    }

    // ------------------------------------------------------------------
    // ヘルパ（HistoryRestoreThroughScenarioTestsと同じ構成）
    // ------------------------------------------------------------------

    private async Task<ShellViewModel> OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

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

    private async Task ApplyFullAsync(ShellViewModel shell, string relativePath, string content)
    {
        _clipboard.Text = $"<<<< FILE: {relativePath} MODE=FULL\n{content}\n>>>> END\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
    }

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

    private sealed class RecordingDialogService : IDialogService
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

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public void SetText(string text) => Text = text;

        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

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
