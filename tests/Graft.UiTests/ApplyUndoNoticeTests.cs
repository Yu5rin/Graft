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
/// 製品としての使い勝手3件のうち機能2（適用直後の「元に戻す」通知）の回帰テスト。
///
/// RevisionNumberingScenarioTests.csと同じ手法（ShellWindowは作らずShellViewModelだけを構築し、
/// InitializeAsyncを明示的に1回だけ呼ぶ）で、MainViewModel.ApplyAsyncを実際に通す。
/// 「元に戻す」は既存の単発復元経路（History.UndoLatestAsync→RevisionRestorer.RestoreAsync）を
/// そのままMainViewModel.UndoCommand経由で再利用しているだけであることを、実際にファイル内容が
/// 戻ることまで確認して担保する（並行実装が紛れ込んでいないことの確認）。
/// </summary>
public class ApplyUndoNoticeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-apply-undo-notice", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly AutoConfirmDialogService _dialogs = new();

    public ApplyUndoNoticeTests()
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

    [AvaloniaFact(DisplayName = "適用に成功すると「rN として適用しました — 元に戻す」の通知が出る")]
    public async Task 適用成功で通知が出る()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        shell.Graft.HasApplyUndoNotice.Should().BeFalse("適用前は通知が出ていてはならない");

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        shell.Graft.HasApplyUndoNotice.Should().BeTrue("適用に成功したので通知が出るはず");
        shell.Graft.ApplyUndoNoticeText.Should().Be("r1 として適用しました — 元に戻す");
    }

    [AvaloniaFact(DisplayName = "通知の「元に戻す」（UndoCommand）を実行すると、既存の単発復元経路で実際にファイルが元へ戻る")]
    public async Task 通知の元に戻すで実際に戻る()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
        shell.Graft.HasApplyUndoNotice.Should().BeTrue();

        // 通知のボタンはGraft.UndoCommand（Ctrl+Zと同じ経路）にそのまま結び付いている
        // （StatusBarView.axaml参照）。AutoConfirmDialogServiceが「復元の確認」を自動承諾する。
        await ExecuteAsync(shell.Graft.UndoCommand).ConfigureAwait(true);

        var content = await File.ReadAllTextAsync(targetPath).ConfigureAwait(true);
        content.Should().Be("1行目\n2行目\n3行目\n", "既存の単発復元経路で適用前の内容へ戻るはず");
        shell.Graft.HasApplyUndoNotice.Should().BeFalse("元に戻した時点で、もう有効ではない通知は消えているはず");
    }

    [AvaloniaFact(DisplayName = "allOrNothingで、選択されている失敗ブロックがあるために適用に失敗した場合は通知を出さない")]
    public async Task 適用失敗では通知が出ない()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "ok.txt"), "hello\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(
            Path.Combine(_projectDirectory, "bad.txt"), "存在しない検索対象は含まれていません\n").ConfigureAwait(true);

        var shell = await OpenShellAsync(new Settings { ShowPreview = false, ApplyMode = "allOrNothing" }).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        // ok.txt側は適用可能・bad.txt側はSEARCH不一致で失敗する組み合わせにする。実機不具合対応
        // （ApplyEngine.ApplyAsyncのfatal判定にIsSelectedを見るよう修正）後は、bad.txt側は
        // 自動的にチェックが外れているため、そのままではok.txt側だけが成功してしまう
        // （その正常系はチェックを外すと案内が出て履歴は増えないテストの隣、
        // 部分適用でも成功の通知が出て失敗件数が併記されるテストで確認済み）。
        // ここではallOrNothing自体の安全機構（選択されている失敗ブロックがあれば中止する）を
        // 検証したいので、通常のUI操作（Space・チェックボックス）ではできないが、
        // BlockItemViewModel.IsSelectedを直接操作して失敗ブロックを強制的に選択状態にする。
        _clipboard.Text = BuildPatch("ok.txt", "hello", "world") + "\n" + BuildPatch("bad.txt", "見つからない文字列", "置換後");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        var badBlock = shell.Graft.Blocks.Single(b => b.PathText == "bad.txt");
        badBlock.CanToggle.Should().BeFalse("失敗ブロックは通常のUI操作では選択できないはず");
        badBlock.IsSelected = true;

        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        shell.Graft.HasApplyUndoNotice.Should().BeFalse("適用全体が失敗した場合は通知を出してはならない");
    }

    // ------------------------------------------------------------------
    // 実機不具合対応（Windows実機・v1.0.6）: 「一部だけを適用すると、通常は履歴に関するお知らせが
    // 出るはずが、エラーが表示される」の回帰テスト。MainViewModel.ApplyAsyncを実際に通し、
    // 画面から見える結果（通知・完了ダイアログの文言・履歴件数）まで確認する。
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "既定の適用モード（allOrNothing）で3ブロック中2つ成功・1つ失敗しても、成功の通知が出て完了メッセージに失敗件数が併記される（エラーにならない）")]
    public async Task 部分適用でも成功の通知が出て失敗件数が併記される()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "a.txt"), "aaa\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "b.txt"), "bbb\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(
            Path.Combine(_projectDirectory, "c.txt"), "存在しない検索対象は含まれていません\n").ConfigureAwait(true);

        // 明示的に既定値どおりのallOrNothingを指定する（コードの既定値が将来変わっても
        // このテストの意図が揺らがないようにするため）。
        var shell = await OpenShellAsync(new Settings { ShowPreview = false, ApplyMode = "allOrNothing" }).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildPatch("a.txt", "aaa", "aaa-changed")
            + "\n" + BuildPatch("b.txt", "bbb", "bbb-changed")
            + "\n" + BuildPatch("c.txt", "見つからない文字列", "置換後");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.ApplyCommand.CanExecute(null).Should().BeTrue("a.txt・b.txt側は適用可能なのでApplyコマンドは実行できるはず");

        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        shell.Graft.State.Should().NotBe(CenterPaneState.Error,
            "適用可能なブロックが残っている限り、エラー画面へ遷移してはならない");
        shell.Graft.HasApplyUndoNotice.Should().BeTrue("成功したブロックがある以上、通知が出るはず");
        shell.Graft.ApplyUndoNoticeText.Should().Be("r1 として適用しました — 元に戻す");
        _dialogs.Shown.Should().Contain(s => s.Title == "適用が完了しました" && s.Message.Contains("1件は適用できませんでした"),
            "エラーではなく、成功の完了メッセージに失敗件数が併記されるはず");

        (await File.ReadAllTextAsync(Path.Combine(_projectDirectory, "a.txt")).ConfigureAwait(true)).Should().Be("aaa-changed\n");
        (await File.ReadAllTextAsync(Path.Combine(_projectDirectory, "b.txt")).ConfigureAwait(true)).Should().Be("bbb-changed\n");
    }

    [AvaloniaFact(DisplayName = "適用可能なブロックのチェックを全部外すと、適用対象がない旨の案内が出て、通知も履歴も残らない")]
    public async Task チェックを全部外すと案内が出て履歴は増えない()
    {
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "a.txt"), "aaa\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        _clipboard.Text = BuildPatch("a.txt", "aaa", "aaa-changed");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        foreach (var block in shell.Graft.Blocks) block.IsSelected = false;

        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        _dialogs.Shown.Should().Contain(s => s.Title == "適用対象がありません",
            "チェックを全部外した状態で適用しても、空のリビジョンを作らず案内を出すはず");
        shell.Graft.HasApplyUndoNotice.Should().BeFalse("何も適用していないので通知は出ないはず");
        (await File.ReadAllTextAsync(Path.Combine(_projectDirectory, "a.txt")).ConfigureAwait(true)).Should().Be("aaa\n",
            "チェックを外したブロックの対象ファイルは書き換わっていないはず");

        var history = await new RevisionStore(new AppPaths(_appDirectory)).ListAsync(projectId).ConfigureAwait(true);
        history.Value.Should().BeEmpty("空のリビジョンを履歴に残してはならない");
    }

    /// <summary>
    /// RevisionNumberingScenarioTests.OpenShellAsyncと同じ理由（window.Show()経由の
    /// ShellWindow.OnLoadedとの二重初期化を避けるため）で、ShellWindowを作らずに
    /// ShellViewModelだけを構築する。
    /// </summary>
    private async Task<ShellViewModel> OpenShellAsync(Settings? settingsOverride = null)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var settings = settingsOverride ?? new Settings { ShowPreview = false };
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

    /// <summary>確認をすべて承諾するダイアログ（他のシナリオテストと同じ役割）。
    /// ShowMessageAsyncの内容を<see cref="Shown"/>へ記録し、完了メッセージの文言を検証できるようにする。</summary>
    private sealed class AutoConfirmDialogService : IDialogService
    {
        public List<(string Title, string Message)> Shown { get; } = new();

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message)
        {
            Shown.Add((title, message));
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
