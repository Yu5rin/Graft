using System.IO;
using FluentAssertions;
using Graft.Core.Update;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 「確認せずに自動で更新する」（<see cref="UpdateSettings.AutoInstall"/>、1.0.25）の設定まわり:
/// 既定がオフであること、この項目が無い既存のsettings.json（1.0.24以前から引き継いだもの）を
/// 既定値のまま読めること、保存して読み直せること。さらに、失敗の通知を版ごとに1回に絞る
/// 記録（<see cref="UpdateCheckState.AutoInstallFailedTag"/>）が、更新確認のたびに消えないこと。
/// </summary>
public class UpdateAutoInstallSettingsTests
{
    private static AppPaths MakePaths(TempWorkspace ws) => new(ws.CreateDirectory("app"));

    [Fact(DisplayName = "AutoInstallの既定はオフ（確認ダイアログを省くのは、利用者が明示的にオンにした場合だけ）")]
    public void 既定はオフ()
    {
        new UpdateSettings().AutoInstall.Should().BeFalse();
        new Settings().Update.AutoInstall.Should().BeFalse();
        new Settings().Update.CheckOnStartup.Should().BeTrue("既存の既定は変えない");
    }

    [Fact(DisplayName = "settings.jsonが無いときも、AutoInstallは既定のオフで返る")]
    public async Task 設定ファイルが無ければ既定のオフ()
    {
        using var ws = new TempWorkspace();
        var result = await new SettingsStore(MakePaths(ws)).LoadAsync();

        result.Value.Update.AutoInstall.Should().BeFalse();
    }

    [Fact(DisplayName = "autoInstallの項目が無い既存のsettings.json（1.0.24以前）は、他の値を保ったままオフで読める")]
    public async Task 既存のsettingsは項目が無くても読める()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        WriteRawSettings(paths, """
            { "update": { "checkOnStartup": false, "checkUrl": "https://api.github.com/repos/Yu5rin/Graft/releases/latest" } }
            """);

        var result = await new SettingsStore(paths).LoadAsync();

        result.Value.Update.AutoInstall.Should().BeFalse("項目が無ければ既定のオフ。更新しただけで挙動が変わってはならない");
        result.Value.Update.CheckOnStartup.Should().BeFalse("既存の設定値は保たれる");
        result.Issues.Should().BeEmpty();
    }

    [Fact(DisplayName = "updateセクション自体が無い既存のsettings.jsonでも、AutoInstallはオフで読める")]
    public async Task updateセクションが無くても読める()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        WriteRawSettings(paths, """{ "theme": "dark" }""");

        var result = await new SettingsStore(paths).LoadAsync();

        result.Value.Update.AutoInstall.Should().BeFalse();
        result.Value.Update.CheckOnStartup.Should().BeTrue();
    }

    [Fact(DisplayName = "autoInstall=trueを保存して読み直すと、trueのまま戻り、確認先URLの検証にも影響しない")]
    public async Task オンにした値は保存して読み直せる()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        var store = new SettingsStore(paths);

        await store.SaveAsync(new Settings { Update = new UpdateSettings { AutoInstall = true } });
        var result = await store.LoadAsync();

        result.Value.Update.AutoInstall.Should().BeTrue();
        result.Value.Update.CheckUrl.Should().Be(UpdateHostPolicy.DefaultCheckUrl);
        result.Issues.Should().BeEmpty();
    }

    [Fact(DisplayName = "更新の確認（UpdateChecker）が状態を書き直しても、自動更新の失敗の通知済みの記録は消えない")]
    public async Task 更新確認は失敗の記録を消さない()
    {
        // 以前の保存は「確認日時と成否だけの新しい状態」で丸ごと上書きしていた。項目を増やした
        // あと、確認のたびに記録が消えると、失敗の通知を1回に絞れず毎回出てしまう。
        using var ws = new TempWorkspace();
        var stateStore = new UpdateCheckStateStore(MakePaths(ws));
        await stateStore.SaveAsync(new UpdateCheckState { AutoInstallFailedTag = "v1.0.25" });
        var feed = new FixedFeed(new GitHubReleaseInfo { TagName = "v1.0.24" });
        var checker = new UpdateChecker(feed, stateStore);

        var result = await checker.CheckNowAsync(UpdateHostPolicy.DefaultCheckUrl, "1.0.24.0", "Graft/1.0.24");

        result.Status.Should().Be(UpdateCheckStatus.UpToDate);
        var state = await stateStore.LoadAsync();
        state.AutoInstallFailedTag.Should().Be("v1.0.25", "確認のたびに消えてはならない");
        state.LastCheckedAt.Should().NotBeNull();
        state.LastCheckSucceeded.Should().BeTrue();
    }

    [Fact(DisplayName = "状態ストアのUpdateAsyncは、他の項目を保ったまま変更した項目だけ書き換える")]
    public async Task UpdateAsyncは他の項目を保つ()
    {
        using var ws = new TempWorkspace();
        var stateStore = new UpdateCheckStateStore(MakePaths(ws));
        var checkedAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        await stateStore.SaveAsync(new UpdateCheckState { LastCheckedAt = checkedAt, LastCheckSucceeded = true });

        await stateStore.UpdateAsync(s => s with { AutoInstallFailedTag = "v1.0.25" });

        var state = await stateStore.LoadAsync();
        state.AutoInstallFailedTag.Should().Be("v1.0.25");
        state.LastCheckedAt.Should().Be(checkedAt);
        state.LastCheckSucceeded.Should().BeTrue();
    }

    [Fact(DisplayName = "autoInstallFailedTagの項目が無い従来のupdate-check.jsonは、そのまま読める")]
    public async Task 従来の状態ファイルは項目が無くても読める()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.UpdateCheckStateFilePath)!);
        File.WriteAllText(paths.UpdateCheckStateFilePath, """{ "lastCheckedAt": "2026-10-01T09:00:00+00:00", "lastCheckSucceeded": false }""");

        var state = await new UpdateCheckStateStore(paths).LoadAsync();

        state.AutoInstallFailedTag.Should().BeNull();
        state.LastCheckSucceeded.Should().BeFalse();
    }

    private static void WriteRawSettings(AppPaths paths, string json)
    {
        var directory = Path.GetDirectoryName(paths.SettingsFilePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(paths.SettingsFilePath, json);
    }

    private sealed class FixedFeed(GitHubReleaseInfo release) : IReleaseFeed
    {
        public Task<AtomFeedTag?> TryGetLatestTagFromAtomAsync(string checkUrl, CancellationToken ct)
            => Task.FromResult<AtomFeedTag?>(null);

        public Task<ReleaseFetchResult> GetLatestReleaseAsync(string checkUrl, string userAgent, CancellationToken ct)
            => Task.FromResult(ReleaseFetchResult.Ok(release));
    }
}
