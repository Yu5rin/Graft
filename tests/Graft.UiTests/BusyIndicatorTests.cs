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
/// 「時間のかかる操作の最中に待機表示が出る」ことの回帰テスト（点検指摘A-4／A-5／A-6）。
///
/// 応答性そのもの（UIスレッドが塞がらないこと）は<see cref="UiResponsivenessTests"/>が
/// 実測で押さえている。こちらは「操作中であることが利用者に見える状態になるか」を、
/// 表示へ直結しているViewModelのプロパティの遷移で固定する。
/// いずれも「一度でもその状態になったか」を<c>PropertyChanged</c>で拾う形にしてあるのは、
/// 処理が速く終わる小さなテストデータでも確実に判定できるようにするため。
/// </summary>
public class BusyIndicatorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-busy-indicator", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();

    public BusyIndicatorTests()
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

    [AvaloniaFact(DisplayName = "A-6: パッチ本文の解析中も中央ペインが「読み込み中」になる（解析失敗時はLoading→Errorの順）")]
    public async Task 解析中は中央ペインが読み込み中になる()
    {
        var shell = await OpenShellAsync(new AlwaysYesDialogService()).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var states = new List<CenterPaneState>();
        shell.Graft.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.State)) states.Add(shell.Graft.State);
        };

        // 解析できない文章を渡す。解析が失敗する経路は、従来 State=Loading を一度も
        // 通らずに直接 Error になっていた（Loadingが立つのはドライラン直前だったため）。
        // 修正後は「解析中（Loading）→ 失敗（Error）」の順に必ず遷移する。
        _clipboard.Text = "これはパッチではないただの文章です。\n2行目。\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.State.Should().Be(CenterPaneState.Error, "解析できない入力なので最終状態はエラーのはず");
        states.Should().Contain(
            CenterPaneState.Loading, "解析そのものの間も待機表示（Loading）を出す必要がある");
        states.Should().ContainInOrder(new[] { CenterPaneState.Loading, CenterPaneState.Error });
    }

    [AvaloniaFact(DisplayName = "A-5: 履歴の復元中も履歴ペインが「読み込み中」になる（復元の完了通知より前に立つ）")]
    public async Task 復元中は履歴ペインが読み込み中になる()
    {
        var shell = await OpenShellAsync(new AlwaysYesDialogService()).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "sample.txt", "v2").ConfigureAwait(true); // r2

        var history = shell.Graft.History;
        var sawLoading = false;
        var sawLoadingBeforeRestored = false;
        history.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HistoryPaneViewModel.State) && history.State == HistoryPaneState.Loading)
            {
                sawLoading = true;
            }
        };
        // 復元の完了通知（RevisionRestored）は、復元処理が終わった直後・一覧の再読み込み
        // （LoadAsync。これ自体もLoadingを立てる）より前に飛ぶ。したがって「この時点で
        // 既にLoadingを見たか」を見れば、一覧の再読み込みによるLoadingと、復元中の
        // Loadingとを取り違えずに判定できる。修正前はここが必ずfalseだった。
        history.RevisionRestored += (_, _) => sawLoadingBeforeRestored = sawLoading;

        history.SelectedItem = history.Items.Single(i => i.RevisionLabel == "r2");
        sawLoading = false; // 選択に伴う一覧更新までの遷移は判定に含めない。
        await ExecuteAsync(history.RestoreCommand).ConfigureAwait(true);

        sawLoadingBeforeRestored.Should().BeTrue(
            "復元（実ファイルの書き戻し）の最中も、一覧の読み込みと同じく待機表示を出す必要がある");
        (await File.ReadAllTextAsync(Path.Combine(_projectDirectory, "sample.txt")).ConfigureAwait(true))
            .Should().Contain("v1", "復元自体はこれまで通り成功する必要がある");
    }

    [AvaloniaFact(DisplayName = "A-4: ごみ箱への削除中もエクスプローラに待機表示が出る")]
    public async Task 削除中はエクスプローラに待機表示が出る()
    {
        var targetPath = Path.Combine(_projectDirectory, "delete-me.txt");
        await File.WriteAllTextAsync(targetPath, "削除される内容").ConfigureAwait(true);

        var dialogs = new AlwaysYesDialogService();
        var ui = new AvaloniaUiServices();
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var editor = new EditorPaneViewModel(new Settings(), dialogs, ui);
        var explorer = new ExplorerViewModel(appPaths, editor, dialogs, new Settings(), ui);
        await explorer.SetProjectAsync(
            new Project { Id = "p_del", Name = "削除", Root = _projectDirectory }).ConfigureAwait(true);

        var node = explorer.RootNodes.Single(n => n.Name == "delete-me.txt");
        var sawLoading = false;
        explorer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExplorerViewModel.IsLoading) && explorer.IsLoading) sawLoading = true;
        };

        explorer.DeleteCommand.Execute(node); // 内部はfire-and-forget（RelayCommand）
        await WaitForAsync(() => !File.Exists(targetPath) && !explorer.IsLoading).ConfigureAwait(true);

        sawLoading.Should().BeTrue(
            "退避コピー（DeleteUndoStore.StageAsync）とごみ箱送りの間は待機表示を出す必要がある");
        explorer.IsLoading.Should().BeFalse("完了後は必ず下ろす");
        explorer.HasDeleteUndoNotice.Should().BeTrue("削除後の取り消し案内はこれまで通り出る必要がある");

        explorer.Dispose();
    }

    // ------------------------------------------------------------------
    // ヘルパ（RestoreAppliedAfterChangeConfirmTestsと同じ組み立て方）
    // ------------------------------------------------------------------

    private async Task<ShellViewModel> OpenShellAsync(IDialogService dialogs)
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
            dialogs,
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

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1000; i++)
        {
            if (condition()) return;
            await Task.Delay(10).ConfigureAwait(true);
        }

        throw new TimeoutException("条件が10秒以内に成立しませんでした。");
    }

    /// <summary>確認はすべて承諾する最小のダイアログ（削除・復元をテストから通すため）。</summary>
    private sealed class AlwaysYesDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

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

        public FakeUiServices(IClipboardAccess clipboard) => Clipboard = clipboard;

        public IClipboardAccess Clipboard { get; }

        public IScreenInfo Screens => _inner.Screens;

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }
}
