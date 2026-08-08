using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 課題1（×で閉じてもプロセスが終了しないバグ）・課題2（「閉じたときの動作」設定）の
/// 回帰テスト。ShellWindow.OnClosingの分岐（終了する／タスクトレイに常駐する／
/// トレイ非対応環境での縮退／トレイメニューからの強制終了）を、実際にCloseを呼んで検証する。
/// </summary>
public class CloseBehaviorTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-ui-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_baseDirectory)) Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "課題1: CloseBehaviorが\"exit\"（既定）のとき、Close()で実際にウィンドウが閉じる")]
    public void 既定のexitでは実際に閉じる()
    {
        var window = BuildWindow();
        window.CloseBehavior = "exit";
        window.IsTraySupported = true; // トレイの有無に関係なくexitなら閉じる。
        var closed = 0;
        window.Closed += (_, _) => closed++;
        window.Show();

        window.Close();

        closed.Should().Be(1, "\"exit\"設定では×で閉じたとき実際にウィンドウが閉じる（＝プロセスが終了できる）必要がある");
    }

    [AvaloniaFact(DisplayName = "課題2: CloseBehaviorが\"tray\"かつトレイ対応環境では、Close()はキャンセルされウィンドウは隠れるだけになる")]
    public void trayかつ対応環境では閉じずに隠れる()
    {
        var window = BuildWindow();
        window.CloseBehavior = "tray";
        window.IsTraySupported = true;
        var closed = 0;
        window.Closed += (_, _) => closed++;
        window.Show();

        window.Close();

        closed.Should().Be(0, "トレイに常駐する設定では、×で閉じてもウィンドウを破棄してはならない");
        window.IsVisible.Should().BeFalse("閉じるボタンを押したら見た目上は隠れる必要がある");
    }

    [AvaloniaFact(DisplayName = "課題2: CloseBehaviorが\"tray\"でもトレイが使えない環境では実際に終了する（縮退）")]
    public void trayでもトレイ非対応環境では実際に閉じる()
    {
        var window = BuildWindow();
        window.CloseBehavior = "tray";
        window.IsTraySupported = false; // 仕様書2.3: 未対応環境は必ず縮退させる。
        var closed = 0;
        window.Closed += (_, _) => closed++;
        window.Show();

        window.Close();

        closed.Should().Be(1, "トレイが使えない環境では\"tray\"設定であっても×で閉じたら終了しなければならない");
    }

    [AvaloniaFact(DisplayName = "課題2: トレイメニューの「終了」等、IsForceClosingを立てた場合はCloseBehaviorが\"tray\"でも実際に閉じる")]
    public void 強制終了フラグがあればtray設定でも実際に閉じる()
    {
        var window = BuildWindow();
        window.CloseBehavior = "tray";
        window.IsTraySupported = true;
        window.IsForceClosing = true; // トレイメニュー「終了」相当。
        var closed = 0;
        window.Closed += (_, _) => closed++;
        window.Show();

        window.Close();

        closed.Should().Be(1, "トレイメニューからの「終了」は常駐設定に関わらず実際に終了できなければならない");
    }

    private ShellWindow BuildWindow()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        IDialogService dialogs = new NullDialogService();
        IUiServices ui = new AvaloniaUiServices();

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            dialogs,
            ui,
            openSettings: () => { });

        return new ShellWindow(shell) { Width = 1280, Height = 800 };
    }
}
