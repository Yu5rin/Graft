using FluentAssertions;
using Graft.Core.Update;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="UpdateChecker"/>の要件を固定する:
/// - 起動時チェックは前回確認から24時間未満なら通信しない、24時間以上なら確認する（1日1回の絞り込み）。
/// - 通信の失敗は例外を投げず「確認できなかった」で済ませる（起動を妨げない）。
/// - バージョンの数値比較（UpdateVersionTests側でも別途固定）。
/// HTTP通信は一切行わず、<see cref="IReleaseFeed"/>をフェイクに差し替える。
/// </summary>
public class UpdateCheckerTests
{
    private const string CheckUrl = "https://api.github.com/repos/Yu5rin/Graft/releases/latest";
    private const string UserAgent = "Graft/1.0.7";

    [Fact(DisplayName = "前回確認から24時間未満なら通信せずNotDueを返す")]
    public async Task 二十四時間未満なら確認しない()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        await stateStore.SaveAsync(new UpdateCheckState { LastCheckedAt = now.AddHours(-23) });

        var feed = new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v9.9.9" });
        var checker = new UpdateChecker(feed, stateStore, () => now);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.NotDue);
        feed.CallCount.Should().Be(0, "24時間未満のため通信そのものが発生してはいけない");
    }

    [Fact(DisplayName = "前回確認から24時間以上経っていれば確認する")]
    public async Task 二十四時間以上経っていれば確認する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        await stateStore.SaveAsync(new UpdateCheckState { LastCheckedAt = now.AddHours(-24) });

        var feed = new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v1.0.7" });
        var checker = new UpdateChecker(feed, stateStore, () => now);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpToDate);
        feed.CallCount.Should().Be(1);
    }

    [Fact(DisplayName = "一度も確認していない（状態ファイルが無い）場合は起動時チェックが通信する")]
    public async Task 未確認なら起動時チェックが通信する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var feed = new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v1.0.7" });
        var checker = new UpdateChecker(feed, stateStore);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpToDate);
        feed.CallCount.Should().Be(1);
    }

    [Fact(DisplayName = "確認のたびに前回確認日時が更新され、以後24時間は再確認しない")]
    public async Task 確認のたびに前回確認日時が更新される()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var current = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var feed = new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v1.0.7" });
        var checker = new UpdateChecker(feed, stateStore, () => current);

        (await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent)).Status.Should().Be(UpdateCheckStatus.UpToDate);
        feed.CallCount.Should().Be(1);

        // 1分後: まだ24時間経っていないので確認しない。
        current = current.AddMinutes(1);
        (await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent)).Status.Should().Be(UpdateCheckStatus.NotDue);
        feed.CallCount.Should().Be(1, "追加の通信は発生しないはず");

        var state = await stateStore.LoadAsync();
        state.LastCheckedAt.Should().Be(new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact(DisplayName = "手動確認（CheckNowAsync）は絞り込みを無視して必ず通信する")]
    public async Task 手動確認は絞り込みを無視する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        await stateStore.SaveAsync(new UpdateCheckState { LastCheckedAt = now });

        var feed = new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v1.0.7" });
        var checker = new UpdateChecker(feed, stateStore, () => now);

        var result = await checker.CheckNowAsync(CheckUrl, "1.0.7", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpToDate);
        feed.CallCount.Should().Be(1);
    }

    [Fact(DisplayName = "通信に失敗しても例外を投げず、確認できなかった扱いになる（起動を妨げない）")]
    public async Task 通信失敗は例外を投げず確認できなかった扱いになる()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var stateStore = new UpdateCheckStateStore(appPaths);
        var feed = new FakeReleaseFeed(null); // 通信失敗を模したnull応答。
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
        var feed = new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "v1.0.10" });
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
        var feed = new FakeReleaseFeed(new GitHubReleaseInfo { TagName = "release-notes" });
        var checker = new UpdateChecker(feed, stateStore);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.7", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.Failed);
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

    private sealed class ThrowingReleaseFeed : IReleaseFeed
    {
        public Task<GitHubReleaseInfo?> GetLatestReleaseAsync(string checkUrl, string userAgent, CancellationToken ct)
            => throw new InvalidOperationException("テスト用の想定外の例外。");
    }
}
