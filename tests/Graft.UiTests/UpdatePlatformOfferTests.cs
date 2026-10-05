using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core.Update;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 不具合対応（2026-09-25）: Linuxでは自動更新が必ず失敗して巻き戻っていた件の回帰テスト。
///
/// 以前は新しい版が見つかると、OSに関わらず「今すぐ更新」ボタンのダイアログを出していた。
/// Linuxで押すとWindows版のzipをダウンロードし、入れ替えで<c>Graft.exe</c>が見つからず
/// 失敗して巻き戻り、「更新に失敗しました」を見せていた。判断そのものは
/// <see cref="UpdatePlatformPolicy"/>（Graft.Tests の UpdatePlatformPolicyTests で固定）にあり、
/// ここでは画面側がその判断どおりにダイアログを出し分けること（配線）を固定する。
/// </summary>
public class UpdatePlatformOfferTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-update-platform", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "Linuxでは新しい版があっても「今すぐ更新」を出さず、リリースページの案内だけを出す")]
    public async Task Linuxではリリースページの案内だけを出す()
    {
        var dialogs = new RecordingDialogService();
        var vm = await CreateViewModelAsync(dialogs, UpdatePlatform.Linux);

        await vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        dialogs.ActionLabels.Should().Equal("リリースページを開く");
        dialogs.ActionLabels.Should().NotContain("今すぐ更新");
        dialogs.Titles.Should().Contain("新しい版があります");
        // 失敗して巻き戻る動作（「更新に失敗しました」「更新できません」）を見せていないこと。
        dialogs.Titles.Should().NotContain("更新に失敗しました");
        dialogs.Titles.Should().NotContain("更新できません");
    }

    [AvaloniaFact(DisplayName = "Windowsではこれまでどおり「今すぐ更新」を出す")]
    public async Task Windowsでは今すぐ更新を出す()
    {
        var dialogs = new RecordingDialogService();
        var vm = await CreateViewModelAsync(dialogs, UpdatePlatform.Windows);

        await vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        // ×で閉じた扱い（false）にしているので、ダウンロードへは進まない。
        dialogs.ActionLabels.Should().Equal("今すぐ更新");
    }

    private async Task<SettingsViewModel> CreateViewModelAsync(RecordingDialogService dialogs, UpdatePlatform platform)
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        var release = new GitHubReleaseInfo
        {
            TagName = "v99.0.0",
            HtmlUrl = "https://github.com/Yu5rin/Graft/releases/tag/v99.0.0",
            Assets = new[]
            {
                new GitHubReleaseAsset { Name = "Graft-99.0.0-linux-x64.tar.gz", BrowserDownloadUrl = "https://example.invalid/linux" },
                new GitHubReleaseAsset { Name = "Graft-99.0.0-win-x64.zip", BrowserDownloadUrl = "https://example.invalid/win" },
            },
        };
        var vm = new SettingsViewModel(appPaths, dialogs, new AvaloniaUiServices(), releaseFeed: new FixedReleaseFeed(release))
        {
            UpdatePlatform = platform,
        };
        await vm.InitializeAsync();
        vm.UpdateCheckOnStartup = true;
        return vm;
    }

    /// <summary>Atomは使えない扱いにし、APIの応答として決まったリリースを返す。</summary>
    private sealed class FixedReleaseFeed(GitHubReleaseInfo release) : IReleaseFeed
    {
        public Task<AtomFeedTag?> TryGetLatestTagFromAtomAsync(string checkUrl, CancellationToken ct)
            => Task.FromResult<AtomFeedTag?>(null);

        public Task<ReleaseFetchResult> GetLatestReleaseAsync(string checkUrl, string userAgent, CancellationToken ct)
            => Task.FromResult(ReleaseFetchResult.Ok(release));
    }

    /// <summary>出したダイアログの題名とボタンの文言を記録する。アクションはすべて「しない」を選ぶ。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public List<string> Titles { get; } = new();
        public List<string> ActionLabels { get; } = new();

        public Task<bool> ShowActionMessageAsync(string title, string message, string actionLabel)
        {
            Titles.Add(title);
            ActionLabels.Add(actionLabel);
            return Task.FromResult(false);
        }

        public Task ShowMessageAsync(string title, string message)
        {
            Titles.Add(title);
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(false);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);
    }
}
