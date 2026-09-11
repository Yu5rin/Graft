using FluentAssertions;
using Graft.Core.Update;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="UpdateChecker"/>の要件を固定する:
/// - 起動時チェック（<see cref="UpdateChecker.CheckOnStartupAsync"/>）は、呼ばれれば必ず通信する
///   （v1.0.12。設定画面のチェックボックスの文言「起動時に更新を確認する」どおり、絞り込み無し。
///   「確認するかどうか」自体の制御はSettingsViewModel.Update.cs側の責務で、このクラスは
///   前回いつ確認したかによる絞り込みを一切行わない）。
/// - 通信の失敗は例外を投げず「確認できなかった」で済ませる（起動を妨げない）。
/// - バージョンの数値比較（UpdateVersionTests側でも別途固定）。
/// - Atomフィード優先のオーケストレーション（実機不具合対応。GitHub APIの回数上限を、
///   更新が無い大多数の確認では消費しない。詳しい経緯は<see cref="UpdateChecker.CheckNowAsync"/>
///   のコメント、および別リポジトリpaneのUpdateService.cs/UpdateCheckLogic.cs参照）。
/// HTTP通信は一切行わず、<see cref="IReleaseFeed"/>をフェイクに差し替える。
/// </summary>
public class UpdateCheckerTests
{
    private const string CheckUrl = "https://api.github.com/repos/Yu5rin/Graft/releases/latest";
    private const string UserAgent = "Graft/1.0.7";

    [Fact(DisplayName = "起動時チェックは、前回確認から1分しか経っていなくても必ず通信する")]
    public async Task 前回確認から1分しか経っていなくても起動時チェックは通信する()
    {
        // 仕様変更（v1.0.12）の回帰テスト: かつては「前回確認から24時間未満ならNotDueを返し
        // 通信しない」絞り込みがあったが、廃止した。前回確認からごく短時間しか経っていない
        // 状況を意図的に作り、それでも必ず通信することを固定する。
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        await stateStore.SaveAsync(new UpdateCheckState { LastCheckedAt = now.AddMinutes(-1) });

        var feed = FakeReleaseFeed.WithApiOnly(new GitHubReleaseInfo { TagName = "v9.9.9" });
        var checker = new UpdateChecker(feed, stateStore, () => now);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
        feed.ApiCallCount.Should().Be(1, "起動時チェックは前回確認からの経過時間に関わらず必ず通信するはず");
    }

    [Fact(DisplayName = "一度も確認していない（状態ファイルが無い）場合も起動時チェックが通信する")]
    public async Task 未確認なら起動時チェックが通信する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var feed = FakeReleaseFeed.WithApiOnly(new GitHubReleaseInfo { TagName = "v1.0.7" });
        var checker = new UpdateChecker(feed, stateStore);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpToDate);
        feed.ApiCallCount.Should().Be(1);
    }

    [Fact(DisplayName = "起動時チェックを連続して呼んでも、そのたびに通信し前回確認日時が更新される")]
    public async Task 起動時チェックを連続して呼ぶとそのたびに通信する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var current = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var feed = FakeReleaseFeed.WithApiOnly(new GitHubReleaseInfo { TagName = "v1.0.7" });
        var checker = new UpdateChecker(feed, stateStore, () => current);

        (await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent)).Status.Should().Be(UpdateCheckStatus.UpToDate);
        feed.ApiCallCount.Should().Be(1);

        // 1分後: 絞り込みが無いため、間隔に関わらずもう一度通信するはず。
        current = current.AddMinutes(1);
        (await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent)).Status.Should().Be(UpdateCheckStatus.UpToDate);
        feed.ApiCallCount.Should().Be(2, "起動時チェックは呼ばれるたびに通信するはず");

        var state = await stateStore.LoadAsync();
        state.LastCheckedAt.Should().Be(current, "前回確認日時は直近の呼び出し時刻へ更新されているはず");
    }

    [Fact(DisplayName = "手動確認（CheckNowAsync）も必ず通信する")]
    public async Task 手動確認は絞り込みを無視する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        await stateStore.SaveAsync(new UpdateCheckState { LastCheckedAt = now });

        var feed = FakeReleaseFeed.WithApiOnly(new GitHubReleaseInfo { TagName = "v1.0.7" });
        var checker = new UpdateChecker(feed, stateStore, () => now);

        var result = await checker.CheckNowAsync(CheckUrl, "1.0.7", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpToDate);
        feed.ApiCallCount.Should().Be(1);
    }

    [Fact(DisplayName = "通信に失敗しても例外を投げず、確認できなかった扱いになる（起動を妨げない）")]
    public async Task 通信失敗は例外を投げず確認できなかった扱いになる()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var feed = FakeReleaseFeed.Failing(ReleaseFetchFailureReason.Unknown); // 通信失敗を模す。
        var checker = new UpdateChecker(feed, stateStore);

        var act = async () => await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent);

        (await act.Should().NotThrowAsync()).Which.Status.Should().Be(UpdateCheckStatus.Failed);
    }

    [Fact(DisplayName = "IReleaseFeedが想定外の例外を投げても、UpdateCheckerは外へ伝播させない")]
    public async Task フェイクが例外を投げても外へ伝播しない()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var feed = new ThrowingReleaseFeed();
        var checker = new UpdateChecker(feed, stateStore);

        var act = async () => await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent);

        (await act.Should().NotThrowAsync()).Which.Status.Should().Be(UpdateCheckStatus.Failed);
    }

    [Fact(DisplayName = "新しいバージョンがあればUpdateAvailableを返す")]
    public async Task 新しいバージョンがあれば通知する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var feed = FakeReleaseFeed.WithApiOnly(new GitHubReleaseInfo { TagName = "v1.0.10" });
        var checker = new UpdateChecker(feed, stateStore);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.9", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
        result.Release!.TagName.Should().Be("v1.0.10");
    }

    [Fact(DisplayName = "リリースのタグが解釈できない場合はFailedを返す")]
    public async Task タグが不正ならFailedになる()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var feed = FakeReleaseFeed.WithApiOnly(new GitHubReleaseInfo { TagName = "release-notes" });
        var checker = new UpdateChecker(feed, stateStore);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.Failed);
    }

    [Fact(DisplayName = "Atomが現在と同じか古いタグを返すとき、APIを呼ばずに最新版として終える（節約の本体）")]
    public async Task Atomで新しくないと分かればAPIを呼ばない()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        // Atomは「現在と同じ」タグを返す。APIが呼ばれたら失敗させ、呼ばれていないことを保証する。
        var feed = FakeReleaseFeed.WithAtomAndApi(
            new AtomFeedTag("v1.0.7", "https://github.com/Yu5rin/Graft/releases/tag/v1.0.7"),
            ReleaseFetchResult.Fail(ReleaseFetchFailureReason.Unknown, exceptionTypeName: "テストでは呼ばれないはず"));
        var checker = new UpdateChecker(feed, stateStore);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpToDate);
        feed.ApiCallCount.Should().Be(0, "Atomで新しくないと分かった時点でAPIの回数上限を消費してはならない");
        feed.AtomCallCount.Should().Be(1);
    }

    [Fact(DisplayName = "Atomが新しいタグを返しAPIも成功すれば、従来どおり配布物の詳細を含むUpdateAvailableになる")]
    public async Task Atomで新しいと分かりAPIも成功すれば詳細付きで通知する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var release = new GitHubReleaseInfo
        {
            TagName = "v1.0.10",
            HtmlUrl = "https://github.com/Yu5rin/Graft/releases/tag/v1.0.10",
            Assets = new[] { new GitHubReleaseAsset { Name = "Graft-1.0.10-win-x64.zip", BrowserDownloadUrl = "https://example.invalid/a.zip" } },
        };
        var feed = FakeReleaseFeed.WithAtomAndApi(
            new AtomFeedTag("v1.0.10", "https://github.com/Yu5rin/Graft/releases/tag/v1.0.10"),
            ReleaseFetchResult.Ok(release));
        var checker = new UpdateChecker(feed, stateStore);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.9", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
        result.Release!.FindAssetByNameSuffix("win-x64.zip").Should().NotBeNull();
        feed.AtomCallCount.Should().Be(1);
        feed.ApiCallCount.Should().Be(1);
    }

    [Fact(DisplayName = "Atomが新しいタグを返すがAPIが403で失敗する場合、詳細は取れなくても新版があることとリリースページURLを伝える")]
    public async Task Atomで新しいと分かりAPIが403で失敗すれば詳細なしで案内する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        const string releaseUrl = "https://github.com/Yu5rin/Graft/releases/tag/v1.0.10";
        var feed = FakeReleaseFeed.WithAtomAndApi(
            new AtomFeedTag("v1.0.10", releaseUrl),
            ReleaseFetchResult.Fail(ReleaseFetchFailureReason.RateLimited, 403));
        var checker = new UpdateChecker(feed, stateStore);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.9", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpdateAvailableNoDetails);
        result.Release!.TagName.Should().Be("v1.0.10");
        result.Release!.HtmlUrl.Should().Be(releaseUrl, "自動更新できない場合、手動更新へ誘導するためリリースページのURLが必要");
        result.ErrorMessage.Should().Contain("v1.0.10").And.Contain("自動更新はできません").And.Contain("リリースページ");
        result.DiagnosticDetail.Should().Contain("403");

        var state = await stateStore.LoadAsync();
        state.LastCheckSucceeded.Should().BeTrue("新しい版があることは判定できているので、確認は成功したものとして記録する");
    }

    [Fact(DisplayName = "Atomが失敗しAPIも403で失敗する場合、回数の上限であることが利用者に伝わる文言になる")]
    public async Task Atomが使えずAPIも403なら回数上限の文言になる()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var feed = FakeReleaseFeed.WithAtomAndApi(
            atomTag: null,
            ReleaseFetchResult.Fail(ReleaseFetchFailureReason.RateLimited, 403));
        var checker = new UpdateChecker(feed, stateStore);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.9", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.Failed);
        result.ErrorMessage.Should().Contain("回数の上限");
        result.ErrorMessage.Should().NotContain("新しい版", "Atomから新版が分かっていない以上、断定した案内をしてはならない");
        result.DiagnosticDetail.Should().Contain("403");
    }

    [Fact(DisplayName = "タイムアウトと名前解決不能で、それぞれ区別できる文言になる")]
    public async Task タイムアウトと名前解決不能は文言が異なる()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);

        var timeoutFeed = FakeReleaseFeed.WithAtomAndApi(null, ReleaseFetchResult.Fail(ReleaseFetchFailureReason.TimedOut));
        var timeoutResult = await new UpdateChecker(timeoutFeed, stateStore).CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent);

        var dnsFeed = FakeReleaseFeed.WithAtomAndApi(null, ReleaseFetchResult.Fail(ReleaseFetchFailureReason.NameResolutionFailed));
        var dnsResult = await new UpdateChecker(dnsFeed, stateStore).CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent);

        timeoutResult.ErrorMessage.Should().Contain("時間内に応答");
        dnsResult.ErrorMessage.Should().Contain("サーバー名を解決");
        timeoutResult.ErrorMessage.Should().NotBe(dnsResult.ErrorMessage);
    }

    [Fact(DisplayName = "プロキシの認証が必要な場合（407）は、プロキシに触れる文言になる")]
    public async Task プロキシ認証が必要な場合は専用の文言になる()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var feed = FakeReleaseFeed.WithAtomAndApi(null, ReleaseFetchResult.Fail(ReleaseFetchFailureReason.ProxyAuthenticationRequired, 407));
        var checker = new UpdateChecker(feed, stateStore);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent);

        result.ErrorMessage.Should().Contain("プロキシ");
    }

    private sealed class FakeReleaseFeed : IReleaseFeed
    {
        private readonly AtomFeedTag? _atomResponse;
        private readonly ReleaseFetchResult _apiResponse;

        public int AtomCallCount { get; private set; }
        public int ApiCallCount { get; private set; }

        private FakeReleaseFeed(AtomFeedTag? atomResponse, ReleaseFetchResult apiResponse)
        {
            _atomResponse = atomResponse;
            _apiResponse = apiResponse;
        }

        /// <summary>
        /// 既存テスト（Atomのオーケストレーション導入前）と同じ書き味を保つための近道:
        /// Atomは常に「使えない（null）」を返し、APIだけで確認する（Atomの2段構えを
        /// 導入する前の、GitHub APIのみを叩くGitHubReleaseFeedと同じ経路をたどる）。
        /// </summary>
        public static FakeReleaseFeed WithApiOnly(GitHubReleaseInfo release)
            => new(atomResponse: null, ReleaseFetchResult.Ok(release));

        public static FakeReleaseFeed Failing(ReleaseFetchFailureReason reason)
            => new(atomResponse: null, ReleaseFetchResult.Fail(reason));

        public static FakeReleaseFeed WithAtomAndApi(AtomFeedTag? atomTag, ReleaseFetchResult apiResult)
            => new(atomTag, apiResult);

        public Task<AtomFeedTag?> TryGetLatestTagFromAtomAsync(string checkUrl, CancellationToken ct)
        {
            AtomCallCount++;
            return Task.FromResult(_atomResponse);
        }

        public Task<ReleaseFetchResult> GetLatestReleaseAsync(string checkUrl, string userAgent, CancellationToken ct)
        {
            ApiCallCount++;
            return Task.FromResult(_apiResponse);
        }
    }

    private sealed class ThrowingReleaseFeed : IReleaseFeed
    {
        public Task<AtomFeedTag?> TryGetLatestTagFromAtomAsync(string checkUrl, CancellationToken ct)
            => throw new InvalidOperationException("テスト用の想定外の例外。");

        public Task<ReleaseFetchResult> GetLatestReleaseAsync(string checkUrl, string userAgent, CancellationToken ct)
            => throw new InvalidOperationException("テスト用の想定外の例外。");
    }
}
