using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Update;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 実画面での点検で見つかった「誤解を招く表示・詰む文言」のうち、画面（ViewModel）まで
/// 通さないと確かめられないものの回帰テスト。
/// </summary>
public class MisleadingMessageUiRegressionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-misleading-messages", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();

    public MisleadingMessageUiRegressionTests()
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

    // ==================================================================
    // B-6: 更新の「最終確認」が、失敗した確認も成功と同じに見せる
    // ==================================================================

    /// <summary>
    /// 修正前の挙動（対照）: 確認が3回連続で失敗しても、画面には
    /// 「最終確認: 2026/09/06 06:26」とだけ表示されていた（失敗はログのwarnにしか残らない）。
    /// これは「確認した＝最新だった」と読めてしまい、しかも起動時の失敗は画面に何も出さない
    /// 方針のため、オフラインが続くと利用者は何日でも更新が止まっていることに気づけない。
    /// </summary>
    [AvaloniaFact(DisplayName = "B-6: 更新確認に失敗したら「最終確認」に（確認できませんでした）が併記される")]
    public async Task 更新確認の失敗が最終確認に併記される()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();

        var vm = new SettingsViewModel(
            appPaths, new NullDialogService(), new AvaloniaUiServices(),
            releaseFeed: new FakeReleaseFeed(null)); // nullを返す＝通信に失敗した扱い。
        await vm.InitializeAsync();
        vm.UpdateCheckOnStartup = true;

        await vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        vm.UpdateLastCheckedAt.Should().NotBeNull("「確認しようとした」事実は従来どおり残す");
        vm.UpdateLastCheckSucceeded.Should().BeFalse();
        vm.UpdateLastCheckedText.Should().Contain("（確認できませんでした）",
            "失敗した確認を成功と同じ見た目にしてはならない（B-6）");
    }

    [AvaloniaFact(DisplayName = "B-6: 更新確認に成功したら「最終確認」は日時だけ（余計な注記を付けない）")]
    public async Task 更新確認の成功時は日時だけを表示する()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();

        var vm = new SettingsViewModel(
            appPaths, new NullDialogService(), new AvaloniaUiServices(),
            releaseFeed: new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v0.0.1" }));
        await vm.InitializeAsync();
        vm.UpdateCheckOnStartup = true;

        await vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        vm.UpdateLastCheckSucceeded.Should().BeTrue();
        vm.UpdateLastCheckedText.Should().StartWith("最終確認: ").And.NotContain("確認できませんでした");
    }

    [AvaloniaFact(DisplayName = "B-6: 失敗のあとに成功すれば、注記は消える")]
    public async Task 失敗のあと成功すれば注記が消える()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();

        var failing = new SettingsViewModel(
            appPaths, new NullDialogService(), new AvaloniaUiServices(), releaseFeed: new FakeReleaseFeed(null));
        await failing.InitializeAsync();
        failing.UpdateCheckOnStartup = true;
        await failing.CheckForUpdateOnStartupAsync(isRestartLaunch: false);
        failing.UpdateLastCheckedText.Should().Contain("（確認できませんでした）");

        var succeeding = new SettingsViewModel(
            appPaths, new NullDialogService(), new AvaloniaUiServices(),
            releaseFeed: new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v0.0.1" }));
        await succeeding.InitializeAsync();
        succeeding.UpdateLastCheckedText.Should().Contain("（確認できませんでした）",
            "画面を開き直した直後は、前回失敗したという記録がそのまま出るはず");

        succeeding.UpdateCheckOnStartup = true;
        await succeeding.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        succeeding.UpdateLastCheckedText.Should().NotContain("確認できませんでした");
    }

    // ==================================================================
    // B-2: 設定JSONの解析エラーの表示
    // ==================================================================

    [AvaloniaFact(DisplayName = "B-2: JSONタブの保存に失敗すると、日本語＋行番号のエラーが表示される")]
    public async Task JSONタブのエラーが日本語で表示される()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync();

        vm.JsonText = "{\n  \"theme\": \"dark\",\n  \"editor\": @\n}\n";
        vm.SaveJsonCommand.Execute(null);
        await WaitUntilAsync(() => vm.JsonParseError is not null);

        vm.JsonParseError.Should().NotBeNull();
        vm.JsonParseError!.Should().StartWith("E406").And.Contain("3行目").And.Contain("「@」");
        vm.JsonParseError.Should().NotContain("invalid start of a value",
            ".NETの英語メッセージがそのまま画面へ出てはならない（B-2）");
    }

    // ==================================================================
    // B-8: 失敗通知に OK／キャンセルの確認ダイアログを使っている
    // ==================================================================

    /// <summary>
    /// 修正前の挙動（対照）: 復元の失敗を伝えるだけの通知に<c>ConfirmAsync</c>を使っており、
    /// 何も選べないのに「キャンセル」ボタンが並び、押しても何も起きなかった
    /// （戻り値を捨てているためOKでもキャンセルでも同じ）。
    /// </summary>
    [AvaloniaFact(DisplayName = "B-8: 復元の失敗通知はOKのみのメッセージで、確認ダイアログを使わない")]
    public async Task 復元の失敗通知は確認ダイアログを使わない()
    {
        var dialogs = new RecordingDialogService();
        var shell = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await ApplyFullAsync(shell, "sample.txt", "v1").ConfigureAwait(true); // r1

        var history = shell.Graft.History;
        var row = history.Items.Single(i => i.RevisionLabel == "r1");
        history.SelectedItem = row;

        // 履歴一覧を読み込んだ後にバックアップの実体だけを消す。行は復元可能なまま残るので、
        // 実際に復元を試みた時点で初めてE405で失敗する（＝失敗通知の経路へ入れる）。
        Directory.Delete(row.Revision.FolderPath, recursive: true);

        await ExecuteAsync(history.RestoreCommand).ConfigureAwait(true);

        dialogs.MessageTitles.Should().Contain("取り消せません",
            "失敗はOKのみのメッセージで伝えること（B-8）");
        dialogs.ConfirmTitles.Should().NotContain("取り消せません",
            "単なる通知に「キャンセル」を並べてはならない（B-8）");
    }

    // ==================================================================
    // B-9: 適用完了の「（N件は適用できませんでした）」に理由がない
    // ==================================================================

    [AvaloniaFact(DisplayName = "B-9: 部分適用の完了メッセージは、失敗した理由の在りかを案内する")]
    public async Task 部分適用の完了メッセージが理由の在りかを示す()
    {
        var dialogs = new RecordingDialogService();
        var shell = await OpenShellAsync(dialogs, applyMode: "partial").ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "ok.txt"), "before\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(_projectDirectory, "ng.txt"), "まったく違う内容\n").ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text =
            "<<<< FILE: ok.txt\n<<<<<<< SEARCH\nbefore\n=======\nafter\n>>>>>>> REPLACE\n" +
            "<<<< FILE: ng.txt\n<<<<<<< SEARCH\n存在しない行\n=======\nどうでもよい\n>>>>>>> REPLACE\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        var completion = dialogs.Messages.Single(m => m.Title == "適用が完了しました").Message;
        completion.Should().Contain("1件は適用できませんでした");
        completion.Should().Contain("接ぎ木パネル",
            "「なぜ適用できなかったのか」をどこで見られるのかを必ず添えること（B-9）");
    }

    // ==================================================================
    // B-5: フック失敗が「終了コード N」だけ
    // ==================================================================

    /// <summary>
    /// 修正前の挙動（対照）: フックが失敗しても「・ビルド: 終了コード 1」としか出さず、
    /// <see cref="HookRunner"/>が集めていた標準出力・標準エラー（<see cref="HookResult.Output"/>）は
    /// 画面に一切出していなかった。利用者は「何が失敗したのか」を知る手段が無かった。
    /// </summary>
    [AvaloniaFact(DisplayName = "B-5: フック失敗の通知に、出力の末尾数行が併記される")]
    public async Task フック失敗の通知に出力の末尾が併記される()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var dialogs = new RecordingDialogService();
        var shell = await OpenShellAsync(dialogs).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var projectId = shell.Graft.ProjectPane.SelectedItem!.Project.Id;

        // 7行出力してから失敗するフック。末尾5行だけが出る（先頭の行は出ない）ことを確かめる。
        // echo と && と exit はcmd.exe・/bin/shのどちらでも同じ意味で動く。
        await SetPostApplyHooksAsync(projectId, new PostApplyHook
        {
            Name = "ビルド",
            Command = "echo LINE1 && echo LINE2 && echo LINE3 && echo LINE4 && echo LINE5 && echo LINE6 && echo BUILDFAILEDMARK && exit 1",
            OnFailure = HookFailureAction.Warn,
        }).ConfigureAwait(true);
        await shell.Graft.ProjectPane.LoadAsync().ConfigureAwait(true);

        _clipboard.Text = "<<<< FILE: sample.txt\n<<<<<<< SEARCH\n2行目\n=======\n2行目（変更後）\n>>>>>>> REPLACE\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        var failure = dialogs.Messages.Single(m => m.Title == "適用後フックが失敗しました").Message;
        failure.Should().Contain("終了コード 1", "従来の情報は残す");
        failure.Should().Contain("BUILDFAILEDMARK", "出力の末尾を併記すること（B-5）");
        failure.Should().Contain("全7行", "抜粋であることと全体の行数を明示すること");
        failure.Should().NotContain("LINE1", "末尾5行に絞ること（ビルド出力は数千行になりうる）");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    /// <summary>projects.jsonへ直接、指定プロジェクトの適用後フックを設定する（テスト用の下拵え）。</summary>
    private async Task SetPostApplyHooksAsync(string projectId, params PostApplyHook[] hooks)
    {
        var projectStore = new ProjectStore(new AppPaths(_appDirectory));
        var projects = (await projectStore.LoadAsync().ConfigureAwait(true)).Value.ToList();
        var index = projects.FindIndex(p => p.Id == projectId);
        projects[index] = projects[index] with { PostApplyHooks = hooks };
        await projectStore.SaveAsync(projects).ConfigureAwait(true);
    }

    private async Task<ShellViewModel> OpenShellAsync(IDialogService dialogs, string? applyMode = null)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var settings = applyMode is null
            ? new Settings { ShowPreview = false }
            : new Settings { ShowPreview = false, ApplyMode = applyMode };
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition()) return;
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    /// <summary>確認・メッセージのどちらで通知されたかを区別して記録するダイアログ。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public List<(string Title, string Message)> Messages { get; } = new();
        public List<string> MessageTitles => Messages.ConvertAll(m => m.Title);
        public List<string> ConfirmTitles { get; } = new();

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmTitles.Add(title);
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

        public FakeUiServices(IClipboardAccess clipboard) => Clipboard = clipboard;

        public IClipboardAccess Clipboard { get; }

        public IScreenInfo Screens => _inner.Screens;

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }

    /// <summary>
    /// Atomフィードは常に「使えない」を返し、GitHub Releases APIだけで確認する経路を
    /// たどらせる（このテストファイルの関心はAtom優先のオーケストレーションではないため。
    /// そちらはUpdateCheckerTests側で固定する）。
    /// </summary>
    private sealed class FakeReleaseFeed : IReleaseFeed
    {
        private readonly GitHubReleaseInfo? _response;

        public FakeReleaseFeed(GitHubReleaseInfo? response) => _response = response;

        public Task<AtomFeedTag?> TryGetLatestTagFromAtomAsync(string checkUrl, CancellationToken ct)
            => Task.FromResult<AtomFeedTag?>(null);

        public Task<ReleaseFetchResult> GetLatestReleaseAsync(string checkUrl, string userAgent, CancellationToken ct)
            => Task.FromResult(_response is null
                ? ReleaseFetchResult.Fail(ReleaseFetchFailureReason.Unknown)
                : ReleaseFetchResult.Ok(_response));
    }
}
