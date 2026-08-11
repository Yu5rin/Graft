using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 不具合3の回帰テスト: 「最小化するとタスクトレイに隠れてしまい、タスクバーからも消える」
/// （利用者からの指摘: 「最小化でトレイに入るのが既定ですか？通常はタスクバーには表示される
/// ものですが。」）。
///
/// 原因は<see cref="StartupCoordinator.StartAsync"/>のwindow.PropertyChangedハンドラが、
/// 設定に関わらずトレイが使える環境では常に<c>window.Hide()</c>していたこと。修正は
/// <see cref="Infra.Settings.MinimizeToTray"/>（既定オフ）を追加し、設定で選べるようにした。
///
/// ShutdownLoggingTests.cs・CloseBehaviorTests.csと同じ既存のテスト方針
/// （<c>StartupCoordinator.StartAsync</c>は実際のOS資源（トレイ・ホットキー等）に触れるため
/// 単体テストからは呼ばず、実機・Xvfbでの確認に委ねる）に従い、ここでは
/// <see cref="StartupCoordinator.ShouldHideOnMinimize"/>（判定ロジックだけを切り出した
/// static純粋関数）を対象にする。
/// </summary>
public class MinimizeToTrayTests
{
    [AvaloniaFact(DisplayName = "不具合3: 設定オフ（既定）なら、トレイが使える環境でも最小化でHideすべきと判定されない")]
    public void 設定オフならHideすべきと判定されない()
    {
        StartupCoordinator.ShouldHideOnMinimize(trayIsSupported: true, minimizeToTraySetting: false, WindowState.Minimized)
            .Should().BeFalse("既定（オフ）ではWindowsの通常の慣習どおり最小化してもタスクバーに残るべき");
    }

    [AvaloniaFact(DisplayName = "設定オンかつトレイが使える環境なら、最小化でHideすべきと判定される")]
    public void 設定オンかつトレイ対応環境ならHideすべきと判定される()
    {
        StartupCoordinator.ShouldHideOnMinimize(trayIsSupported: true, minimizeToTraySetting: true, WindowState.Minimized)
            .Should().BeTrue("オンにした場合は最小化と同時にタスクトレイへ格納される必要がある");
    }

    [AvaloniaFact(DisplayName = "設定オンでも、トレイが使えない環境ではHideすべきと判定されない（縮退）")]
    public void トレイ非対応環境では設定オンでもHideすべきと判定されない()
    {
        StartupCoordinator.ShouldHideOnMinimize(trayIsSupported: false, minimizeToTraySetting: true, WindowState.Minimized)
            .Should().BeFalse("トレイが使えない環境では、設定がオンでも従来どおり通常の最小化のままにする必要がある（縮退）");
    }

    [AvaloniaFact(DisplayName = "最小化以外の状態変化ではHideすべきと判定されない")]
    public void 最小化以外ではHideすべきと判定されない()
    {
        StartupCoordinator.ShouldHideOnMinimize(trayIsSupported: true, minimizeToTraySetting: true, WindowState.Normal)
            .Should().BeFalse();
        StartupCoordinator.ShouldHideOnMinimize(trayIsSupported: true, minimizeToTraySetting: true, WindowState.Maximized)
            .Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "即時反映: 設定オフ→オンへ変えた直後の最小化でHideが呼ばれ、オン→オフへ戻すと呼ばれなくなる（再起動不要）")]
    public void 設定変更が再起動なしで最小化ハンドラへ反映される()
    {
        // StartupCoordinator.StartAsyncのwindow.PropertyChangedハンドラと全く同じ書き方
        // （ローカル変数へ一度だけ読み出さず、可変な保持先を毎回参照する）を再現する。
        // _settingsは差し替わるインスタンスフィールドだが、ここではミュータブルなラッパーで
        // 同じ性質（クロージャが毎回最新値を読む）を再現する。
        var settingsHolder = new MutableSettingsHolder(new Settings()); // MinimizeToTray既定オフ。
        var window = new Window();
        var hideRequestedCount = 0;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty) return;
            if (StartupCoordinator.ShouldHideOnMinimize(
                    trayIsSupported: true, settingsHolder.Current.MinimizeToTray, window.WindowState))
            {
                hideRequestedCount++;
            }
        };
        window.Show();

        // オフ（既定）の間は最小化してもHideすべきと判定されない。
        window.WindowState = WindowState.Minimized;
        hideRequestedCount.Should().Be(0, "既定のオフではタスクバーに残るはず");

        // 設定画面でオンへ変更した想定（アプリを再起動していない）。
        window.WindowState = WindowState.Normal;
        settingsHolder.Current = settingsHolder.Current with { MinimizeToTray = true };
        window.WindowState = WindowState.Minimized;
        hideRequestedCount.Should().Be(1, "オンへ変更した直後の最小化から、再起動なしでHide判定される必要がある");

        // 再びオフへ戻した想定。
        window.WindowState = WindowState.Normal;
        settingsHolder.Current = settingsHolder.Current with { MinimizeToTray = false };
        window.WindowState = WindowState.Minimized;
        hideRequestedCount.Should().Be(1, "オフへ戻した直後の最小化では、再度Hide判定されてはならない（カウントが増えない）");
    }

    [AvaloniaFact(DisplayName = "SettingsViewModel.MinimizeToTrayは即時にプロパティへ反映され、保存ボタンを押さなくてもsettings.jsonへ書き込まれる")]
    public async Task 設定画面での変更が即座にプロパティへ反映されデバウンス後に保存される()
    {
        var root = Path.Combine(Path.GetTempPath(), "graft-minimize-to-tray-tests", Guid.NewGuid().ToString("N"));
        var appPaths = new AppPaths(root);
        appPaths.EnsureCoreDirectoriesExist();
        try
        {
            var vm = new SettingsViewModel(appPaths, new NullDialogService(), new Graft.Platform.AvaloniaUiServices());
            await vm.InitializeAsync();
            vm.MinimizeToTray.Should().BeFalse("既定はオフ（Windowsの通常の慣習どおりタスクバーに残る）");

            vm.MinimizeToTray = true;

            // setter直後、保存ボタンを押さなくても値そのものは即座に反映されている（即時反映方式）。
            vm.MinimizeToTray.Should().BeTrue();

            // ディスクへの反映は300msデバウンスされるため、書き込みが終わるまで待つ。
            var store = new SettingsStore(appPaths);
            await WaitUntilAsync(async () => (await store.LoadAsync()).Value.MinimizeToTray);

            var reloaded = await store.LoadAsync();
            reloaded.Value.MinimizeToTray.Should().BeTrue("保存ボタンが無いため、変更が自動的にsettings.jsonへ書き込まれる必要がある");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 後始末の失敗は検証結果に影響しない。
            }
        }
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

    /// <summary>
    /// StartupCoordinatorの<c>_settings</c>インスタンスフィールドの性質（ApplyLiveSettingsChangeで
    /// 随時差し替わり、クロージャはフィールド越しに毎回最新値を読む）を、テストの中で
    /// 再現するための最小限のラッパー。
    /// </summary>
    private sealed class MutableSettingsHolder(Settings initial)
    {
        public Settings Current { get; set; } = initial;
    }
}
