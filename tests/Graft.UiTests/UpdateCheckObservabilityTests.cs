using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core.Update;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 利用者からの指摘（v1.0.11・「起動時に更新チェックされているか分からない」／v1.0.12・
/// 「起動するたびに確認してほしい」「Graft自身の再起動では確認しないでほしい」）の回帰テスト。
///
/// 【調査結果（v1.0.11）】起動時チェックの配線自体（<see cref="Views.StartupCoordinator.StartAsync"/>
/// から<see cref="SettingsViewModel.CheckForUpdateOnStartupAsync"/>を経て
/// <see cref="UpdateChecker.CheckOnStartupAsync"/>まで）は正しく繋がっており、settings.jsonの
/// 読み込み（<see cref="SettingsViewModel.InitializeAsync"/>）も呼び出しより前に完了している。
/// つまり「動いていない」という不具合ではなかった。しかし確認の結果を記録するログが一切無く、
/// 「バージョン情報」タブにも確認日時の表示が無かったため、利用者からは「動いているのかどうか
/// 区別できない」状態だった。
///
/// 【仕様変更（v1.0.12）】設定画面のチェックボックスの文言が最初から「起動時に更新を確認する」
/// であり、実態（前回確認から24時間未満ならスキップ）と食い違っていた。文言どおり「起動の
/// たびに必ず確認する」よう絞り込みを廃止した。ただし「起動のたびに確認する」の対象は
/// あくまで利用者が自分の意思でGraftを起動したときの話であり、テーマ変更・データ保存先の
/// 移動・自動更新の適用後などGraft自身が自己再起動して起動したとき
/// （<see cref="Infra.AppRestart.IsRestartLaunch"/>）は対象外とする（利用者からの追加要望）。
///
/// ここでは、最新版だった／新版が見つかった／設定オフでスキップした／自己再起動でスキップ
/// した／通信に失敗した、の5通りすべてがlogs/&lt;日付&gt;.logへ区別して記録されること、
/// 起動時チェックが（自己再起動でない限り）呼ばれるたびに必ず通信すること、および
/// 「最終確認」表示（<see cref="SettingsViewModel.UpdateLastCheckedText"/>）が実際の確認結果を
/// 反映することを固定する。
/// </summary>
public class UpdateCheckObservabilityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-update-observability", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "初期表示: 一度も確認していなければ「最終確認: 未確認」")]
    public async Task 未確認なら未確認と表示される()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        var vm = new SettingsViewModel(
            appPaths, new NullDialogService(), new AvaloniaUiServices(),
            releaseFeed: new FakeReleaseFeed(null));
        await vm.InitializeAsync();

        vm.UpdateLastCheckedText.Should().Be("最終確認: 未確認");
        vm.UpdateLastCheckedAt.Should().BeNull();
    }

    [AvaloniaFact(DisplayName = "起動時チェック: 最新版だった場合、ログに記録され「最終確認」表示が更新される")]
    public async Task 起動時チェックで最新版なら記録され最終確認が更新される()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        await using var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var vm = new SettingsViewModel(
            appPaths, new NullDialogService(), new AvaloniaUiServices(),
            releaseFeed: new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v0.0.1" }));
        await vm.InitializeAsync();
        vm.Logger = logger;
        vm.UpdateCheckOnStartup = true;

        await vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        vm.UpdateLastCheckedAt.Should().NotBeNull("実際に通信して確認したので更新されるはず");
        vm.UpdateLastCheckedText.Should().StartWith("最終確認: ").And.NotBe("最終確認: 未確認");

        var lines = await ReadLogLinesAsync(logger, appPaths);
        lines.Should().Contain(l => l.Contains("起動時の更新確認") && l.Contains("最新版です"),
            "「確認したのに何も起きない」を「確認した結果、最新でした」と分かる形にする、という要件");
    }

    [AvaloniaFact(DisplayName = "起動時チェック: 新しいバージョンが見つかった場合もログに記録される")]
    public async Task 起動時チェックで新版があればログに記録される()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        await using var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var vm = new SettingsViewModel(
            appPaths, new NullDialogService(), new AvaloniaUiServices(),
            releaseFeed: new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v99.0.0", HtmlUrl = "https://example.invalid/release" }));
        await vm.InitializeAsync();
        vm.Logger = logger;
        vm.UpdateCheckOnStartup = true;

        await vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        var lines = await ReadLogLinesAsync(logger, appPaths);
        lines.Should().Contain(l => l.Contains("起動時の更新確認") && l.Contains("新しいバージョンが見つかりました") && l.Contains("v99.0.0"));
    }

    [AvaloniaFact(DisplayName = "起動時チェック: 通信に失敗した場合もログに記録される")]
    public async Task 起動時チェックで失敗した場合もログに記録される()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        await using var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var vm = new SettingsViewModel(
            appPaths, new NullDialogService(), new AvaloniaUiServices(),
            releaseFeed: new FakeReleaseFeed(null)); // nullを返す＝要件どおり「通信に失敗」扱い。
        await vm.InitializeAsync();
        vm.Logger = logger;
        vm.UpdateCheckOnStartup = true;

        await vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        var lines = await ReadLogLinesAsync(logger, appPaths);
        lines.Should().Contain(l => l.Contains("起動時の更新確認") && l.Contains("通信に失敗"));
        // 失敗しても「確認しようとした」事実は記録される（UpdateChecker.CheckNowAsyncの契約）。
        vm.UpdateLastCheckedAt.Should().NotBeNull();
    }

    [AvaloniaFact(DisplayName = "起動時チェック: 「起動時に更新を確認する」がオフなら通信せずログにのみ記録される")]
    public async Task 設定オフならスキップしログに記録される()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        await using var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var feed = new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v0.0.1" });
        var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices(), releaseFeed: feed);
        await vm.InitializeAsync();
        vm.Logger = logger;
        vm.UpdateCheckOnStartup = false;

        await vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        feed.CallCount.Should().Be(0, "設定がオフのときは通信そのものが発生してはいけない");
        vm.UpdateLastCheckedAt.Should().BeNull("通信していないので最終確認日時も更新されない");

        var lines = await ReadLogLinesAsync(logger, appPaths);
        lines.Should().Contain(l => l.Contains("起動時の更新確認") && l.Contains("オフのためスキップ"));
    }

    [AvaloniaFact(DisplayName = "起動時チェック: 前回確認から間もなくても（利用者による起動なら）必ず通信する")]
    public async Task 前回確認から間もなくても起動時チェックは通信する()
    {
        // 仕様変更（v1.0.12）の回帰テスト: かつては「前回確認から24時間未満ならスキップ」する
        // 絞り込みがあったが、設定画面のチェックボックスの文言「起動時に更新を確認する」と
        // 実態が食い違っていたため廃止した。「つい先ほど確認済み」の状態を意図的に作っても、
        // 通常の起動（自己再起動ではない）なら必ず通信することを固定する。
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        await new UpdateCheckStateStore(appPaths).SaveAsync(new UpdateCheckState { LastCheckedAt = DateTimeOffset.Now.AddMinutes(-1) });

        await using var logger = new Logger(appPaths, autoCleanupOnStart: false);
        var feed = new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v0.0.1" });
        var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices(), releaseFeed: feed);
        await vm.InitializeAsync();
        vm.Logger = logger;
        vm.UpdateCheckOnStartup = true;

        vm.UpdateLastCheckedAt.Should().NotBeNull("前回確認済みの状態がInitializeAsyncの時点で読み込まれているはず");

        await vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        feed.CallCount.Should().Be(1, "起動時チェックは前回確認からの経過時間に関わらず必ず通信するはず");
        var lines = await ReadLogLinesAsync(logger, appPaths);
        lines.Should().Contain(l => l.Contains("起動時の更新確認") && l.Contains("最新版です"));
    }

    [AvaloniaFact(DisplayName = "起動時チェック: 自己再起動（テーマ変更・データ保存先の移動・自動更新の適用後など）では通信せずログにのみ記録される")]
    public async Task 自己再起動ならスキップしログに記録される()
    {
        // 利用者からの追加要望の回帰テスト: 「起動のたびに確認する」の対象は利用者が自分の
        // 意思でGraftを起動したときだけであり、Graft自身が自己再起動して起動したとき
        // （isRestartLaunch: true。実際の判定はAppRestart.IsRestartLaunchが起動引数
        // --graft-restartedの有無から行い、StartupCoordinator経由でここへ渡ってくる）は、
        // 設定「起動時に更新を確認する」がオンでも通信そのものを行わない。
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        await using var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var feed = new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v0.0.1" });
        var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices(), releaseFeed: feed);
        await vm.InitializeAsync();
        vm.Logger = logger;
        vm.UpdateCheckOnStartup = true;

        await vm.CheckForUpdateOnStartupAsync(isRestartLaunch: true);

        feed.CallCount.Should().Be(0, "自己再起動では設定がオンでも通信そのものが発生してはいけない");
        vm.UpdateLastCheckedAt.Should().BeNull("通信していないので最終確認日時も更新されない");

        var lines = await ReadLogLinesAsync(logger, appPaths);
        lines.Should().Contain(l => l.Contains("起動時の更新確認") && l.Contains("自己再起動のためスキップ"));
    }

    [AvaloniaFact(DisplayName = "「今すぐ更新を確認」（手動）でも起動時とは区別してログに記録される")]
    public async Task 手動確認は起動時と区別してログに記録される()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        await using var logger = new Logger(appPaths, autoCleanupOnStart: false);

        var vm = new SettingsViewModel(
            appPaths, new NullDialogService(), new AvaloniaUiServices(),
            releaseFeed: new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v0.0.1" }));
        await vm.InitializeAsync();
        vm.Logger = logger;

        vm.CheckForUpdateNowCommand.Execute(null);
        await WaitUntilAsync(() => Task.FromResult(!vm.IsUpdateBusy));

        var lines = await ReadLogLinesAsync(logger, appPaths);
        lines.Should().Contain(l => l.Contains("手動の更新確認") && l.Contains("最新版です"));
        lines.Should().NotContain(l => l.Contains("起動時の更新確認"));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition().ConfigureAwait(true)) return;
            await Task.Delay(10).ConfigureAwait(true);
        }

        // 最終試行。失敗すればAssertion側のShould()で明確な失敗として表れる。
    }

    private static async Task<string[]> ReadLogLinesAsync(Logger logger, AppPaths appPaths)
    {
        // ShutdownLoggingTestsと同じ理由: Loggerはチャネル経由で非同期に書き込むため、
        // DisposeAsyncで書き込みタスクの完了を待ってから読む。
        await logger.DisposeAsync();

        var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
        File.Exists(logPath).Should().BeTrue();
        return await File.ReadAllLinesAsync(logPath);
    }

    private sealed class FakeReleaseFeed : IReleaseFeed
    {
        private readonly GitHubReleaseInfo? _response;
        public int CallCount { get; private set; }

        public FakeReleaseFeed(GitHubReleaseInfo? response) => _response = response;

        public Task<GitHubReleaseInfo?> GetLatestReleaseAsync(string checkUrl, string userAgent, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_response);
        }
    }

    private sealed class NullDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(false);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
