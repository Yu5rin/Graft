using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 課題2・3で追加した「閉じたときの動作」「PC起動時に自動で起動する」の即時反映方式
/// （SetEditableProperty/ScheduleSave/CommitAndSaveAsync）の回帰テスト。保存ボタンが無く、
/// 変更した瞬間にプロパティが反映され、300msデバウンス後にsettings.jsonへ書き込まれる
/// ことを、実際にファイルの中身を読んで確認する。
///
/// 自動起動（LaunchAtStartup）は<see cref="Graft.Platform.PlatformServices.Current"/>という
/// プロセス全体で共有される実物のシングルトンを経由して実際のスタートアップフォルダへ
/// 触れてしまうため、ここでは検証しない（実環境への書き込みを伴うテストは安全でない）。
/// 登録・解除の実処理は<see cref="AutoStartServiceTests"/>で注入可能なパスを使い分離して
/// 検証済み。ここではCloseBehavior（外部リソースに触れない）のみを対象にする。
/// </summary>
public class ImmediateSettingsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-immediate-settings", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "課題2: CloseBehaviorを変更すると即座にプロパティへ反映され、保存ボタンを押さなくてもsettings.jsonへ書き込まれる")]
    public async Task CloseBehaviorの変更は即時にプロパティへ反映されデバウンス後に保存される()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        var vm = new SettingsViewModel(appPaths, new NoopDialogService(), new Graft.Platform.AvaloniaUiServices());
        await vm.InitializeAsync();
        vm.CloseBehavior.Should().Be("exit", "既定値は「終了する」");

        vm.CloseBehavior = "tray";

        // setter直後、保存ボタンを押さなくても値そのものは即座に反映されている（即時反映方式）。
        vm.CloseBehavior.Should().Be("tray");

        // ディスクへの反映は300msデバウンスされるため、書き込みが終わるまで待つ。
        var store = new SettingsStore(appPaths);
        await WaitUntilAsync(async () => (await store.LoadAsync()).Value.CloseBehavior == "tray");

        var reloaded = await store.LoadAsync();
        reloaded.Value.CloseBehavior.Should().Be("tray", "保存ボタンが無いため、変更が自動的にsettings.jsonへ書き込まれる必要がある");
    }

    [AvaloniaFact(DisplayName = "課題2: 短時間に連続して変更しても、最後の値だけがsettings.jsonへ保存される")]
    public async Task 連続変更では最後の値だけが保存される()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        var vm = new SettingsViewModel(appPaths, new NoopDialogService(), new Graft.Platform.AvaloniaUiServices());
        await vm.InitializeAsync();

        vm.CloseBehavior = "tray";
        vm.CloseBehavior = "exit";
        vm.CloseBehavior = "tray"; // 最後にこの値へ

        var store = new SettingsStore(appPaths);
        await WaitUntilAsync(async () => (await store.LoadAsync()).Value.CloseBehavior == "tray");

        var reloaded = await store.LoadAsync();
        reloaded.Value.CloseBehavior.Should().Be("tray");
    }

    [AvaloniaFact(DisplayName = "課題2: 設定画面での変更が、渡されたコールバック経由で実行中のウィンドウへ即時反映される")]
    public async Task ライブコールバックへ変更後の設定が渡される()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();

        Settings? received = null;
        var vm = new SettingsViewModel(
            appPaths, new NoopDialogService(), new Graft.Platform.AvaloniaUiServices(),
            onLiveSettingsChanged: s => received = s);
        await vm.InitializeAsync();

        vm.CloseBehavior = "tray";

        await WaitUntilAsync(() => Task.FromResult(received is { CloseBehavior: "tray" }));

        received.Should().NotBeNull();
        received!.CloseBehavior.Should().Be("tray", "StartupCoordinatorがShellWindow.CloseBehaviorへ即時反映するためのコールバック");
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

    private sealed class NoopDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(false);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
