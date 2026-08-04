using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Graft.Themes;
using Graft.Views;

namespace Graft;

/// <summary>
/// アプリケーションのエントリポイント。テーマ基盤の初期化に続けて、
/// 多重起動防止（6.8）・依存の手動構築・起動時検証・トレイ/ホットキー/クリップボード監視の
/// 配線を <see cref="StartupCoordinator"/> に委譲する。
/// 附録A.4: 想定外の例外のみ最上位（Dispatcher/AppDomain/TaskScheduler）でキャッチしログに
/// 記録する。ユーザー操作起因の失敗は各所で <see cref="Core.GraftResult{T}"/> として扱われ、
/// ここには到達しない。
/// </summary>
public partial class App : Application
{
    private StartupCoordinator? _coordinator;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        ThemeManager.Initialize();

        _coordinator = new StartupCoordinator();
        if (!_coordinator.TryAcquireSingleInstance())
        {
            // 既に起動中の場合は既存ウィンドウを前面へ表示済み（StartupCoordinator側）。
            // このプロセスはウィンドウを一切表示せずに終了する。
            Shutdown();
            return;
        }

        await _coordinator.StartAsync().ConfigureAwait(true);
        MainWindow = _coordinator.MainWindow;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _coordinator?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        ThemeManager.Shutdown();
        base.OnExit(e);
    }

    /// <summary>UIスレッド上の想定外の例外。ログに記録し、日本語メッセージを表示して終了する。</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _coordinator?.Logger?.Error("unhandled", e.Exception.ToString());
        MessageBox.Show(
            "予期しないエラーが発生したため、Graftを終了します。" + Environment.NewLine + e.Exception.Message,
            "Graft", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown();
    }

    /// <summary>UIスレッド外（バックグラウンドタスク・ファイナライザ等）の想定外の例外。記録のみ行う。</summary>
    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _coordinator?.Logger?.Error("unhandled", ex.ToString());
        }
    }

    /// <summary>await されなかった Task 内の想定外の例外。記録のうえ観測済みとしてプロセス終了を防ぐ。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _coordinator?.Logger?.Error("unhandled", e.Exception.ToString());
        e.SetObserved();
    }
}
