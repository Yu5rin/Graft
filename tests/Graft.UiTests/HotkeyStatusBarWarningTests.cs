using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 10件目の不具合修正の回帰テスト: グローバルホットキーの再登録失敗を、既存のステータスバー
/// 警告スロット（<c>ShellViewModel.StatusBarWarning.cs</c>）で利用者に伝える経路
/// （<see cref="ShellViewModel.SetHotkeyRegistrationWarning"/>）の検証。
///
/// 実際の再登録処理（<see cref="StartupCoordinator.ReapplyHotkey"/>）自体は
/// HotkeyReapplyTests.csで検証済みのため、ここでは「StartupCoordinatorが呼ぶ入口から、
/// 実際に利用者へ見える警告表示までが正しくつながっていること」だけをClipboardWatchStatusBarTests
/// と同じ考え方で確認する。
/// </summary>
public class HotkeyStatusBarWarningTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-hotkey-statusbar", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "SetHotkeyRegistrationWarning(文言)でステータスバーに警告が出て、nullで消える")]
    public async Task 警告表示のオンオフ()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);

        shell.HasStatusBarWarning.Should().BeFalse("既定では登録に失敗していないので警告は出ていないはず");

        shell.SetHotkeyRegistrationWarning("ホットキー 'Ctrl+Alt+B' への変更に失敗したため、以前の組み合わせ 'Ctrl+Alt+V' のまま維持します。");
        shell.HasStatusBarWarning.Should().BeTrue("再登録失敗は握り潰さずステータスバーへ出す必要がある");
        shell.StatusBarWarningText.Should().Be("グローバルホットキーの登録に失敗しました");
        shell.StatusBarWarningTooltip.Should().Contain("Ctrl+Alt+B").And.Contain("Ctrl+Alt+V");

        shell.SetHotkeyRegistrationWarning(null);
        shell.HasStatusBarWarning.Should().BeFalse("再登録が成功した（またはnullを渡された）ら警告は消える必要がある");
    }

    [AvaloniaFact(DisplayName = "書き込み不可の警告と同時に成立している場合、優先度の高い書き込み不可のほうが表示され「ほか1件」が付く")]
    public async Task 書き込み不可警告と同時発生時は優先度どおり集約される()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);

        shell.Graft.MarkDataDirectoryReadOnly();
        shell.SetHotkeyRegistrationWarning("ホットキーの登録に失敗しました。");

        shell.StatusBarWarningText.Should().Be("書き込み不可のため設定・履歴・バックアップは保存されません　ほか1件",
            "優先順位（データ喪失に関わるものが最優先）どおり、書き込み不可のほうが前面に出るはず");
        shell.StatusBarWarningTooltip.Should().Contain("書き込む権限がありません").And.Contain("ホットキーの登録に失敗しました。",
            "省略された側の警告も、ToolTipの全文には必ず残っているはず");
    }

    private async Task<ShellViewModel> OpenShellAsync()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(new Settings { ShowPreview = false }).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings { ShowPreview = false },
            settingsStore,
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            new AutoConfirmDialogService(),
            new FakeUiServices(),
            openSettings: () => { });

        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (shell.Graft.ProjectPane.State == ProjectPaneState.Loading)
        {
            await Task.Delay(10, cts.Token).ConfigureAwait(true);
        }

        return shell;
    }

    private sealed class AutoConfirmDialogService : IDialogService
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

        public IClipboardAccess Clipboard { get; } = new FakeClipboard();

        public IScreenInfo Screens => _inner.Screens;

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }
}
