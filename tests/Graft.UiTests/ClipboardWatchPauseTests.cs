using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 細かいユーザビリティ改善2: ステータスバーの「クリップボード監視中」表示をクリックした
/// ときの一時停止／再開（<c>ShellViewModel.ClipboardWatch.cs</c>）。
/// 実際にIClipboardMonitor.Start/Stopを呼ぶのはStartupCoordinator側の責務のため、ここでは
/// StartupCoordinatorが行う「クリックの中継」「結果をSetClipboardWatchPausedで折り返す」の
/// 役割をテスト側で肩代わりし、ShellViewModelの状態遷移が正しいことを検証する
/// （ClipboardWatchStatusBarTestsと同じ、ShellViewModel単体での検証方針）。
/// </summary>
public class ClipboardWatchPauseTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-clipboard-pause", Guid.NewGuid().ToString("N"));

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果と無関係のため無視する。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "クリックで一時停止要求が発火し、確定すると文言が変わる。設定（Settings.ClipboardWatch.Enabled）には触れない")]
    public async Task クリックで一時停止し文言が変わる()
    {
        var shell = await BuildShellAsync().ConfigureAwait(true);
        shell.SetClipboardWatchActive(true);
        shell.ClipboardWatchStatusText.Should().Contain("監視中");
        shell.IsClipboardWatchPaused.Should().BeFalse();

        bool? requestedPause = null;
        shell.ClipboardWatchPauseToggleRequested += (_, pause) => requestedPause = pause;

        shell.ToggleClipboardWatchPauseCommand.Execute(null);

        requestedPause.Should().BeTrue("クリックのたびに現在と逆の状態（この場合は一時停止）を要求する必要がある");
        // StartupCoordinator.OnClipboardWatchPauseToggleRequestedが実際にIClipboardMonitor.Stop()を
        // 呼んだ後、その結果をここで折り返す（本物の配線はStartupCoordinator.cs参照）。
        shell.SetClipboardWatchPaused(true);

        shell.IsClipboardWatchPaused.Should().BeTrue();
        shell.ClipboardWatchStatusText.Should().Contain("一時停止中", "停止中は表示を変えて分かるようにする必要がある");
    }

    [AvaloniaFact(DisplayName = "再クリックで再開要求が発火し、確定すると通常の文言に戻る")]
    public async Task 再クリックで再開できる()
    {
        var shell = await BuildShellAsync().ConfigureAwait(true);
        shell.SetClipboardWatchActive(true);
        shell.ToggleClipboardWatchPauseCommand.Execute(null);
        shell.SetClipboardWatchPaused(true);
        shell.IsClipboardWatchPaused.Should().BeTrue();

        bool? requestedPause = null;
        shell.ClipboardWatchPauseToggleRequested += (_, pause) => requestedPause = pause;
        shell.ToggleClipboardWatchPauseCommand.Execute(null);

        requestedPause.Should().BeFalse("一時停止中の再クリックは再開を要求する必要がある");
        shell.SetClipboardWatchPaused(false);

        shell.IsClipboardWatchPaused.Should().BeFalse();
        shell.ClipboardWatchStatusText.Should().Contain("監視中").And.NotContain("一時停止中");
    }

    [AvaloniaFact(DisplayName = "監視自体が無効な間はクリックしても一時停止要求は発火しない")]
    public async Task 監視無効中はクリックしても何も起きない()
    {
        var shell = await BuildShellAsync().ConfigureAwait(true);
        shell.SetClipboardWatchActive(false);

        var requested = false;
        shell.ClipboardWatchPauseToggleRequested += (_, _) => requested = true;

        shell.ToggleClipboardWatchPauseCommand.Execute(null);

        requested.Should().BeFalse("監視自体がオフの間は一時停止の概念が無い");
    }

    [AvaloniaFact(DisplayName = "設定・トレイ経由の明示的なオン/オフ（SetClipboardWatchActive）は一時停止状態をリセットする")]
    public async Task 設定経由のオンオフで一時停止状態がリセットされる()
    {
        var shell = await BuildShellAsync().ConfigureAwait(true);
        shell.SetClipboardWatchActive(true);
        shell.ToggleClipboardWatchPauseCommand.Execute(null);
        shell.SetClipboardWatchPaused(true);
        shell.IsClipboardWatchPaused.Should().BeTrue();

        // 設定画面やトレイメニューでの明示的なオフ→オンは、それまでの一時停止状態を上書きする
        // （一時停止はあくまで表示専用・一時的な状態であり、Settings.ClipboardWatch.Enabledとは
        // 独立している。ShellViewModel.ClipboardWatch.csのコメント参照）。
        shell.SetClipboardWatchActive(false);
        shell.SetClipboardWatchActive(true);

        shell.IsClipboardWatchPaused.Should().BeFalse("設定経由の明示的な操作の後は一時停止表示を引きずらない");
        shell.ClipboardWatchStatusText.Should().Contain("監視中").And.NotContain("一時停止中");
    }

    private async Task<ShellViewModel> BuildShellAsync()
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
            new Graft.Core.RevisionStore(appPaths),
            new Graft.Core.RevisionRestorer(appPaths),
            new Graft.Platform.Null.NullDialogService(),
            new AvaloniaUiServices(),
            openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window);

        return shell;
    }
}
