using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Graft.Platform;
using Graft.Themes;
using Graft.ViewModels;

namespace Graft;

/// <summary>
/// アプリケーション本体。テーマ辞書の読み込みと起動処理の入り口を担う。
/// 起動処理の中身（多重起動防止・起動時検証・トレイ配線）はフェーズL3以降で移植する。
/// </summary>
public partial class App : Application
{
    private Views.StartupCoordinator? _coordinator;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    // 課題1（バグ）: ×で閉じてもプロセスが終了しない不具合の再発防止フラグ。
    // 後始末（DisposeAsync）が完了したあとの2回目のShutdownRequestedを
    // そのまま通す（＝キャンセルしない）ための目印。
    private bool _cleanupCompleted;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // 9.3・附録A.7: Dark.axaml/Light.axamlはApp.axaml側の静的なマージではなく、
        // ThemeManagerが実行時にMergedDictionariesへ追加する（テーマ切り替えの
        // 差し替え対象にするため）。Initialize()はheadlessテストを含め、Appが
        // 構築されるたびに必ず呼ばれるため、ここで初期化しておけばOnFrameworkInitialization
        // CompletedがCLIの起動経路以外で呼ばれない場合でもトークンが解決できる。
        // システムテーマの判定・変更通知は実行中のOSに応じた実装が担う（L4）。
        // 判定できない環境ではNull実装が選ばれ、AppTheme.Systemはダークへ解決される。
        ThemeManager.Initialize(PlatformServices.Current.Theme);
        EnableCommandRequery();
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _desktop = desktop;

        // 想定外の例外は最上位で記録する（附録A.4）。ユーザー操作起因の失敗は各所で
        // GraftResult として扱われ、ここには到達しない。
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _coordinator = new Views.StartupCoordinator();
        if (!_coordinator.TryAcquireSingleInstance())
        {
            // 既に起動中の場合は既存ウィンドウを前面へ表示済み（StartupCoordinator側）。
            // このプロセスはウィンドウを一切表示せずに終了する。
            desktop.Shutdown();
            return;
        }

        desktop.ShutdownRequested += OnShutdownRequested;

        await _coordinator.StartAsync().ConfigureAwait(true);
        desktop.MainWindow = _coordinator.MainWindow;
    }

    /// <summary>
    /// 課題1（バグ修正）: ×で閉じてもプロセスが終了しない不具合の真因と対処。
    ///
    /// 真因: 旧実装は <c>_coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult()</c> で
    /// UIスレッド上から後始末を同期的に待っていた。<see cref="StartupCoordinator.DisposeAsync"/>
    /// 内部の <c>await ... .ConfigureAwait(true)</c> は、awaitの継続をawait時点の同期コンテキスト
    /// （Avaloniaの UIスレッド用SynchronizationContext）へ戻そうとする。しかしその継続を
    /// 実行できるはずのUIスレッドは、まさにこの<c>GetResult()</c>呼び出しによって同期的に
    /// ブロックされていて、自分自身のディスパッチャキューを処理できない。結果として
    /// 継続が永久に実行されず、後始末が完了しないまま <c>Dispatcher.UIThread.MainLoop</c> も
    /// 終了せず、<c>Main</c>が返らずプロセスが残り続けていた（実機のXvfb環境で
    /// xdotool windowcloseを使い再現し、後始末が<c>DisposeAsync</c>の最初のawaitで
    /// 停止したまま戻らないことをログで確認した）。「TrayIconが生存している間は
    /// 終了しない」という当初の仮説は誤りで、トレイの有無に関係なく発生する。
    ///
    /// 対処: UIスレッドを同期的にブロックするのをやめる。最初のShutdownRequestedは
    /// いったん<c>e.Cancel = true</c>でキャンセルしてアプリの終了を保留し、後始末は
    /// 通常のasync/awaitで（UIスレッドをブロックせずに）実行する。UIスレッドは
    /// ブロックされていないので、ConfigureAwait(true)の継続も普通にディスパッチャ
    /// キュー経由で実行できる（＝WindowsのDestroyWindow等、生成スレッドでの実行が
    /// 必要なOS資源の後始末も安全に行える）。後始末が完了したら明示的に
    /// <c>desktop.Shutdown()</c>を呼び直す。この2回目の呼び出しは
    /// force引数がtrueになりShutdownRequestedを再度発火させない（Avalonia実装を
    /// 確認済み）ため、無限ループにはならない。
    /// </summary>
    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_cleanupCompleted) return; // 後始末済みの2回目の呼び出しはそのまま通す（キャンセルしない）。

        e.Cancel = true; // 後始末が終わるまで、いったん終了を保留する。

        ThemeManager.Shutdown();
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync().ConfigureAwait(true);
        }

        _cleanupCompleted = true;
        _desktop?.Shutdown();
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

    /// <summary>
    /// AvaloniaにはWPFの<c>CommandManager</c>に相当するアプリ全体の再評価機構が無いため、
    /// 同等のタイミング（ポインタ操作・キー入力・フォーカス移動の後）で
    /// <see cref="CommandRequery.Invalidate"/>を呼ぶよう配線する（仕様書v2.1 19章 L3）。
    /// トンネリング段階で購読するのは、各コントロールが処理を終えた直後ではなく
    /// 入力が届いた確実なタイミングで一度だけ拾うため。
    /// </summary>
    private static void EnableCommandRequery()
    {
        InputElement.PointerReleasedEvent.AddClassHandler<TopLevel>(
            (_, _) => CommandRequery.Invalidate(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        InputElement.KeyUpEvent.AddClassHandler<TopLevel>(
            (_, _) => CommandRequery.Invalidate(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        InputElement.GotFocusEvent.AddClassHandler<TopLevel>(
            (_, _) => CommandRequery.Invalidate(), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }
}
