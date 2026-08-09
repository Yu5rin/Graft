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
/// 「ここまで戻す」（HistoryPaneViewModel.RestoreThroughCommand・RevisionRestorer.RestoreThroughAsync）の
/// 通しシナリオテスト。RevisionNumberingScenarioTests.csと同じ手法
/// （ProjectPane登録 → クリップボード貼り付け → 解析 → 適用）で実際にMainViewModel.ApplyCommandを
/// 複数回通してリビジョン履歴を作り、HistoryPaneViewModel経由で「ここまで戻す」を実行する。
/// 画面（Window）は開かず、ShellViewModelだけを構築する（理由はRevisionNumberingScenarioTests.
/// OpenShellAsyncのコメントと同じ）。
/// </summary>
public class HistoryRestoreThroughScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-restore-through-scenario", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly RecordingDialogService _dialogs = new();

    public HistoryRestoreThroughScenarioTests()
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

    [AvaloniaFact(DisplayName = "ここまで戻す: 確認ダイアログに取り消すリビジョン数と影響ファイルが含まれる")]
    public async Task 確認ダイアログに件数とファイルが含まれる()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(target, "v1\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "v2").ConfigureAwait(true); // r2
        await ApplyFullAsync(shell, "sample.txt", "v3").ConfigureAwait(true); // r3

        var history = shell.Graft.History;
        history.SelectedItem = history.Items.Single(i => i.RevisionLabel == "r1");
        history.RestoreThroughCommand.CanExecute(null).Should().BeTrue("r1より新しいr2・r3があるので実行できるはず");

        await ExecuteAsync(history.RestoreThroughCommand).ConfigureAwait(true);

        var confirmMessage = _dialogs.ConfirmMessages.Should().ContainSingle(m => m.Title == "ここまで戻す確認").Which.Message;
        confirmMessage.Should().Contain("r1").And.Contain("r2").And.Contain("r3").And.Contain("sample.txt");
        confirmMessage.Should().Contain("2件", "取り消し対象（r3・r2）の件数が含まれるはず");
    }

    [AvaloniaFact(DisplayName = "ここまで戻す: 実行するとr1適用直後の内容まで正確に戻り、この操作自体が新規リビジョンとして記録される")]
    public async Task 実行すると選択リビジョン直後の内容に戻る()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(target, "v1\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "v2").ConfigureAwait(true); // r2
        await ApplyFullAsync(shell, "sample.txt", "v3").ConfigureAwait(true); // r3

        shell.Graft.History.SelectedItem = shell.Graft.History.Items.Single(i => i.RevisionLabel == "r1");
        await ExecuteAsync(shell.Graft.History.RestoreThroughCommand).ConfigureAwait(true);

        var content = await File.ReadAllTextAsync(target).ConfigureAwait(true);
        content.Should().Be("v1\n", "r1適用直後の内容と完全に一致するはず");

        var revisions = new RevisionStore(new AppPaths(_appDirectory));
        var list = await revisions.ListAsync(projectId).ConfigureAwait(true);
        list.Value.Select(r => r.Manifest.Revision).Should().Contain(4, "まとめ戻し自体がr4として新規リビジョンに記録されるはず");
        var r4 = list.Value.Single(r => r.Manifest.Revision == 4);
        r4.Manifest.Status.Should().Be(RevisionStatus.Success);
        r4.IsRestorable.Should().BeTrue("あとから「このリビジョンを取り消す」で元に戻せる必要がある");

        shell.Graft.History.Items.Should().Contain(i => i.RevisionLabel == "r4", "一覧が再読み込みされているはず");
    }

    // ------------------------------------------------------------------
    // 不具合回帰: 空リビジョン抑止が働いた場合にnextRevisionの番号が欠番にならない
    // （ProjectStore.ReleaseRevisionAsync・HistoryPaneViewModel.RestoreThroughAsync）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "ここまで戻す: 変更が1件も無かった場合はnextRevisionを消費前の値へ戻し、欠番を作らない")]
    public async Task 変化ゼロならnextRevisionを消費前へ戻し欠番にならない()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(target, "same\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        await ApplyFullAsync(shell, "sample.txt", "same").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "changed").ConfigureAwait(true); // r2
        // r3はr1と同一内容へ戻す。FULL形式で同じパッチ本文を再送するとE302の二重適用検知
        // （パッチ本文のハッシュで判定）に引っかかるため、パッチ本文が異なるSR形式で
        // 同じ結果（r1と同一内容）になるようにする。
        await ApplySrAsync(shell, "sample.txt", "changed", "same").ConfigureAwait(true); // r3

        var projectStore = new ProjectStore(new AppPaths(_appDirectory));
        var beforeNextRevision = (await projectStore.LoadAsync().ConfigureAwait(true))
            .Value.Single(p => p.Id == projectId).NextRevision;
        beforeNextRevision.Should().Be(4, "r1〜r3を消費した直後なので次に使う番号は4のはず");

        shell.Graft.History.SelectedItem = shell.Graft.History.Items.Single(i => i.RevisionLabel == "r1");
        await ExecuteAsync(shell.Graft.History.RestoreThroughCommand).ConfigureAwait(true);

        _dialogs.Notices.Should().Contain(n => n.Title == "変更はありませんでした",
            "r3の内容はr1と同一のため、取り消し（r3を戻す）後は実質的な変化が無いはず");

        var afterNextRevision = (await projectStore.LoadAsync().ConfigureAwait(true))
            .Value.Single(p => p.Id == projectId).NextRevision;
        afterNextRevision.Should().Be(beforeNextRevision,
            "空リビジョン抑止で記録しなかった番号（4）は返却され、projects.jsonのnextRevisionは操作前と同じ値のままのはず");

        // 続けて通常の適用を行うと、欠番なく次の番号（r4）が使われる。
        await ApplyFullAsync(shell, "sample.txt", "next").ConfigureAwait(true);

        shell.Graft.History.Items.Select(i => i.RevisionLabel).Should().Contain("r4",
            "返却した番号（4）が欠番にならず、次の適用でそのまま使われるはず");
        shell.Graft.History.Items.Select(i => i.RevisionLabel).Should().NotContain("r5",
            "番号が返却されず欠番のまま進んでいれば、本来r4になるはずの適用がr5になってしまう");
    }

    [AvaloniaFact(DisplayName = "ここまで戻す: 正常に記録された場合は従来どおりnextRevisionが消費されたまま進む")]
    public async Task 正常記録時はnextRevisionが消費されたまま()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(target, "v1\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "v2").ConfigureAwait(true); // r2
        await ApplyFullAsync(shell, "sample.txt", "v3").ConfigureAwait(true); // r3

        shell.Graft.History.SelectedItem = shell.Graft.History.Items.Single(i => i.RevisionLabel == "r1");
        await ExecuteAsync(shell.Graft.History.RestoreThroughCommand).ConfigureAwait(true);

        shell.Graft.History.Items.Should().Contain(i => i.RevisionLabel == "r4", "まとめ戻し自体がr4として記録されているはず");

        var projectStore = new ProjectStore(new AppPaths(_appDirectory));
        var nextRevision = (await projectStore.LoadAsync().ConfigureAwait(true))
            .Value.Single(p => p.Id == projectId).NextRevision;
        nextRevision.Should().Be(5,
            "r4として実際に記録された（空リビジョン抑止は働いていない）ので、消費した番号は返却せずそのまま進んだままのはず");
    }

    [AvaloniaFact(DisplayName = "ここまで戻す: 最新リビジョンを選んでいるときはボタンが無効化される")]
    public async Task 最新リビジョン選択時は無効()
    {
        var target = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(target, "v1\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "v2").ConfigureAwait(true); // r2

        var history = shell.Graft.History;
        history.SelectedItem = history.Items.Single(i => i.RevisionLabel == "r2");

        history.RestoreThroughCommand.CanExecute(null).Should().BeFalse("r2は最新のため取り消す対象が無いはず");
    }

    // ------------------------------------------------------------------
    // ヘルパ
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

    /// <summary>
    /// FULL形式のパッチを貼り付け→解析→適用まで実際に通す。summaryはRequireSummary（既定true）に
    /// より空なら入力ダイアログ（RecordingDialogService.PromptAsync）が出るが、そちらが
    /// 既定値を返すため明示的な指定は不要（FULL形式の本文は末尾マーカーまで一切加工されず
    /// そのままファイル内容になるため、SR形式のBuildPatchと違い"summary:"行は混ぜられない）。
    /// </summary>
    private async Task ApplyFullAsync(ShellViewModel shell, string relativePath, string content)
    {
        _clipboard.Text = $"<<<< FILE: {relativePath} MODE=FULL\n{content}\n>>>> END\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
    }

    /// <summary>
    /// SEARCH/REPLACE形式のパッチを貼り付け→解析→適用まで実際に通す。ApplyFullAsyncと違い
    /// パッチ本文に既存内容の一部（search）を含むため、結果として同じ内容になる場合でも
    /// パッチ本文のハッシュ（E302二重適用検知の判定材料）はFULL形式と異なる値になる。
    /// </summary>
    private async Task ApplySrAsync(ShellViewModel shell, string relativePath, string search, string replace)
    {
        _clipboard.Text = $"<<<< FILE: {relativePath}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
    }

    /// <summary>非同期コマンドを実行し、完了するまで待つ（RevisionNumberingScenarioTestsと同じ役割）。</summary>
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

    /// <summary>確認をすべて承諾しつつ、確認・通知メッセージの内容を記録するダイアログサービス。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public List<(string Title, string Message)> ConfirmMessages { get; } = new();
        public List<(string Title, string Message)> Notices { get; } = new();

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmMessages.Add((title, message));
            return Task.FromResult(true);
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message)
        {
            Notices.Add((title, message));
            return Task.CompletedTask;
        }
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
