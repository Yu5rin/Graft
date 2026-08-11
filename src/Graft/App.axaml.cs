using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Graft.Core;
using Graft.Infra;
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

    // 不具合3: 設定画面「データ保存先の移行」完了ダイアログの「再起動」ボタン
    // （SettingsViewModel.RestartRequested→SettingsWindow.OnRestartRequested経由）で立てる。
    // trueの間にOnShutdownRequestedへ到達すると、後始末の完了（多重起動防止Mutexの解放を含む）を
    // 待ってから新プロセスを起動する（RestartSequencerのコメント参照）。
    private bool _restartRequested;

    // 不具合2: 再起動シーケンス専用の使い捨てロガー（TryStartNewProcessが組み立てる）。
    // CleanupAsync完了後（＝通常のLoggerが破棄済み）に実行ファイルのパス解決・Process.Startの
    // 成否・新プロセスのPIDを記録するために使う（OnShutdownRequestedのコメント参照）。
    private Logger? _restartLogger;

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

        // 不具合（データ保存先の復帰確認で「はい」「いいえ」を押すとそのまま終了する）:
        // ShutdownModeの既定値OnLastWindowCloseは、「開いているウィンドウが0枚になった瞬間」を
        // 終了条件として扱う。desktop.MainWindowを割り当てる（このメソッド末尾）より前に
        // 確認ダイアログ・起動時検証のダイアログを1枚でも開閉すると、その時点では
        // メインウィンドウがまだ1枚も無いため「最後のウィンドウが閉じた」と誤判定され、
        // メインウィンドウが立つより先にアプリごと終了してしまう（実機のXvfb環境で、
        // 孤立したユーザーフォルダの復帰確認ダイアログを閉じた直後に後始末ログだけが記録され
        // window.Show()まで到達しないことを確認した）。desktop.MainWindowを割り当てるまでの間は
        // OnExplicitShutdown（ウィンドウの開閉では終了しない）へ切り替え、割り当てた直後に
        // 元のOnLastWindowClose（×で閉じたら終了する、既存のOnShutdownRequested・トレイ格納の
        // 設定が前提とする挙動）へ必ず戻す。
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 想定外の例外は最上位で記録する（附録A.4）。ユーザー操作起因の失敗は各所で
        // GraftResult として扱われ、ここには到達しない。
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 不具合1: AvaloniaEdit内部（折りたたみの再描画等）から実行時に投げられる例外は
        // AppDomain.UnhandledExceptionまで抜けるとプロセスごと落ちてしまう（記録のみで
        // 継続はできない）。Dispatcher.UIThread.UnhandledExceptionはe.Handled=trueにすると
        // そのジョブ1回分の失敗として記録するだけでアプリを継続させられるため、AvaloniaEdit
        // 由来と判定できるものに限って握りつぶす（AvaloniaEditExceptionGuardのコメント参照）。
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;

        // 機能3の追加: 孤立したユーザーフォルダの復帰確認。AppPaths（＝StartupCoordinatorの
        // コンストラクタ）を組み立てるより前にdatapath.txtの内容を確定させる必要があるため、
        // new Views.StartupCoordinator()より前に行う（DataDirectoryRecovery・
        // StartupCoordinator.DataDirectoryRecovery.csの各クラスドキュメント参照。
        // AppPaths.EnsureCoreDirectoriesExistでback/・logs/を作るより前でなければならない点が
        // 特に重要）。この時点ではDispatcher.UIThread.MainLoopがまだ開始していないが、下の
        // TryAcquireSingleInstanceAsyncやStartAsync内のダイアログ表示と同じ理由
        // （async voidの最初のawaitでこのメソッドの残りが呼び出し元へ制御を返し、続けて
        // MainLoopが開始する。以後の継続はAvalonia側のSynchronizationContext経由で
        // 呼ばれる）で問題なく動作する。
        var recoveryOutcome = await Views.StartupCoordinator.ResolveDataDirectoryRecoveryAsync(
                new AvaloniaDialogService(), AppContext.BaseDirectory, AppPaths.DefaultUserDataDirectory())
            .ConfigureAwait(true);

        // 利用者からの明示的な要望への対応: 復帰確認ダイアログで「キャンセル」（タイトルバーの×を
        // 含む）を選んだ場合はGraftそのものを終了する（DataDirectoryRecoveryResult.Cancelledの
        // コメント参照。要点のみ再掲: 起動をそのまま続けるとexeフォルダにback/・logs/等が
        // 作られてしまい、次回以降ずっとこの確認自体が二度と出せなくなる不具合があったため）。
        // 「終了すべきか」の判定自体はDataDirectoryRecoveryOutcome.ShouldExitProcess（副作用の無い
        // 純粋なプロパティ）に委ね、ここでは実際の終了という副作用だけを行う。
        //
        // 終了方法にEnvironment.Exit(0)を選ぶ理由: この時点ではAppPathsもLoggerもウィンドウも
        // 一切作っておらず、後始末が必要な状態が無い（datapath.txtにも一切触れていない）ため、
        // Avaloniaのシャットダウン手順（desktop.Shutdown/TryShutdown、イベント発火、
        // ウィンドウの後始末等）を経由する必要が無い。実機報告の不具合修正（多重起動検出時、
        // 上のOnFrameworkInitializationCompletedコメント・TryAcquireSingleInstanceAsync呼び出し部の
        // コメント参照）でも同じ理由から同じAPIを使っており、単純な即時終了で十分かつ安全と
        // 判断した。desktop.Shutdown()も候補だったが、この経路はダイアログ表示という実際のawaitを
        // 経ておりDispatcher.UIThread.MainLoopは既に開始しているため理屈のうえでは動作しうるものの、
        // 何もしていないこの時点で使う積極的な理由が無く、Xvfb実機検証でEnvironment.Exit(0)が
        // 問題なく即座に終了しdatapath.txt・logs/・settings.jsonのいずれも作られないことを
        // 確認した（作業記録参照）ため、こちらを採用する。
        if (recoveryOutcome.ShouldExitProcess)
        {
            Environment.Exit(0);
            return;
        }

        _coordinator = new Views.StartupCoordinator(dataDirectoryRecoveryOutcome: recoveryOutcome);

        // 不具合2: 自己再起動で起動した新プロセスかどうかを起動引数から判定する
        // （AppRestart.BuildStartInfoが付与する。AppRestart.IsRestartLaunchのコメント参照）。
        // 再起動由来の場合に限り、多重起動防止Mutexの取得に失敗しても短時間リトライする。
        var isRestartLaunch = AppRestart.IsRestartLaunch(desktop.Args);
        if (!await _coordinator.TryAcquireSingleInstanceAsync(isRestartLaunch).ConfigureAwait(true))
        {
            // 既に起動中の場合は既存ウィンドウを前面へ表示済み（StartupCoordinator側）。
            // このプロセスはウィンドウを一切表示せずに終了する。
            //
            // 6.8のLinux版実機検証（Global\プレフィックス修正）で判明した追加の不具合の修正:
            // ここは OnFrameworkInitializationCompleted（AppBuilder.StartWithClassicDesktopLifetime内、
            // ClassicDesktopStyleApplicationLifetime.StartCoreがDispatcher.MainLoopを開始する
            // *前*）から同期的に呼ばれる。この時点で desktop.Shutdown() を呼ぶと、
            // まだ回り始めてすらいないDispatcherへ「シャットダウン済み」の状態を刻んでしまい、
            // 直後に開始されるMainLoop側のPushFrameが
            // 「Cannot perform requested operation because the Dispatcher shut down」という
            // InvalidOperationExceptionを投げて未処理のまま落ちる（実機のXvfb環境で、
            // 2つ目のGraftを起動して確認した）。以前はLinuxで多重起動検知そのものが機能して
            // いなかった（Global\プレフィックス欠落）ためこの経路を誰も通っておらず、
            // 気付かれていなかった。
            // このプロセスはまだ何も（設定読み込み・ウィンドウ生成・Mutex取得のいずれも）
            // 開始していないため後始末は不要で、Avaloniaのシャットダウン手順に頼らず
            // Environment.Exit(0)で即座にプロセスを終了させれば十分（かつ安全）。
            //
            // 課題1: この経路はStartAsyncを呼ばないため通常のロガーが存在せず、そのままでは
            // 終了ログが一切残らない（診断上の欠陥）。使い捨てのロガーで1行だけ記録するが、
            // Environment.Exit(0)は書き込み中のファイルI/Oも問答無用で打ち切るため、
            // 必ずログ書き込みの完了を待ってから呼ぶ順序にする。ログ書き込み自体が
            // （ディスク障害等で）ハングした場合にこのプロセスが二度と終了できなくなっては
            // 本末転倒なので、タイムアウトで諦める（多重起動検知・既存ウィンドウの前面化は
            // 既に完了しているため、ログが書けなくても実害は無い）。
            await LogSingleInstanceExitWithTimeoutAsync().ConfigureAwait(true);
            Environment.Exit(0);
            return;
        }

        desktop.ShutdownRequested += OnShutdownRequested;

        await _coordinator.StartAsync().ConfigureAwait(true);
        desktop.MainWindow = _coordinator.MainWindow;

        // 上のOnExplicitShutdownへの切り替えコメント参照。メインウィンドウを割り当てた
        // 直後に既定の挙動へ戻す。OnExplicitShutdownのままにしてしまうと、以後×ボタンで
        // ウィンドウを閉じてもプロセスが終了しなくなり（OnShutdownRequested・トレイ格納の
        // 設定と噛み合わなくなる）、この不具合とは別の不具合を生んでしまう。
        desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
    }

    /// <summary>
    /// 課題1: 多重起動検出時のログ記録を、一定時間で諦めるようにする。呼び出し元は
    /// この直後に<c>Environment.Exit(0)</c>を呼ぶため、ここでハングするとプロセスが
    /// 二度と終了できなくなってしまう（「終了できない」という課題1の症状そのものを、
    /// 直し方自身が生みかねない）。失敗・タイムアウトのいずれも黙って諦めてよい
    /// （多重起動検知・既存ウィンドウの前面化は呼び出し前に完了済みのため、
    /// ログが1行残らなくても機能上の実害は無い）。
    /// </summary>
    private async Task LogSingleInstanceExitWithTimeoutAsync()
    {
        if (_coordinator is null) return;

        try
        {
            var logTask = _coordinator.LogSingleInstanceExitAsync();
            var completed = await Task.WhenAny(logTask, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(true);
            if (completed == logTask)
            {
                await logTask.ConfigureAwait(true); // 例外を観測する（UnobservedTaskException化を防ぐ）。
            }
        }
        catch
        {
            // ログ記録の失敗でプロセスが終了できなくなってはならない（附録A.4と同じ方針）。
        }
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

        if (_restartRequested)
        {
            // 不具合2: 「終了はするが再起動しない」不具合の調査に必要な記録。実機ログに
            // 再起動を試みた記録が1行も無く、どこまで進んで何が失敗したのか切り分けられない
            // という診断上の欠陥があったため、この経路に入ったこと自体を必ず記録する。
            _coordinator?.Logger?.Info("restart", "再起動経路で終了処理を開始します（RestartSequencer経由）。");

            // CleanupAsync（＝StartupCoordinator.DisposeAsync）は通常のLoggerも一番最後に
            // 破棄するため、TryStartNewProcess（cleanupAndReleaseGuardの完了後に実行される）の
            // 時点では_coordinator.Loggerへ書いても黙って捨てられる。実行ファイルのパス解決・
            // Process.Startの成否・新プロセスのPIDという最も肝心な記録を残すため、
            // LogSingleInstanceExitAsyncと同じく使い捨てのロガー（_restartLogger）を使う。
            var appPaths = _coordinator?.AppPaths;

            // 不具合3: 新プロセスの起動は「後始末（多重起動防止Mutexの解放を含む）が完全に
            // 完了したあと」でなければならない（RestartSequencerのコメント参照）。
            // CleanupAsyncの完了を待ってからTryStartNewProcessを呼び、その後（起動の成否に
            // よらず）旧プロセスを終了させる、という順序をRestartSequencer側で保証する。
            var started = await RestartSequencer.RunAsync(
                cleanupAndReleaseGuard: CleanupAsync,
                startNewProcess: () => TryStartNewProcess(appPaths),
                shutdownCurrentProcess: () => _desktop?.Shutdown()).ConfigureAwait(true);

            _restartLogger?.Info("restart", started
                ? "新プロセスの起動に成功しました。旧プロセスを終了します。"
                : "新プロセスの起動に失敗しました。旧プロセスのみ終了します（利用者は手動で再起動する必要があります）。");
            if (_restartLogger is not null)
            {
                await _restartLogger.DisposeAsync().ConfigureAwait(true);
            }
            return;
        }

        await CleanupAsync().ConfigureAwait(true);
        _desktop?.Shutdown();
    }

    /// <summary>
    /// 課題1由来の通常の後始末一式（テーマ・<see cref="Views.StartupCoordinator.DisposeAsync"/>）。
    /// <see cref="Views.StartupCoordinator.DisposeAsync"/>の中で多重起動防止Mutexも解放される
    /// （<c>_platform.SingleInstance.Dispose()</c>）ため、不具合3の再起動でもこのメソッドの完了を
    /// 新プロセス起動の前提条件として使う。
    /// </summary>
    private async Task CleanupAsync()
    {
        ThemeManager.Shutdown();
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync().ConfigureAwait(true);
        }

        _cleanupCompleted = true;
    }

    /// <summary>
    /// 不具合3: 新プロセスの起動を試みる。実行ファイルパスの解決は<see cref="AppRestart"/>
    /// （単体テスト対象の純粋な部分）に委ね、ここでは実際の<see cref="Process.Start(ProcessStartInfo)"/>
    /// 呼び出しのみを行う。ここに到達する前に<see cref="ViewModels.SettingsViewModel"/>側で
    /// <see cref="AppRestart.CanRestart"/>による事前確認は済んでいる想定だが、実行ファイルが
    /// その後に削除された等の極端なケースに備え、失敗しても例外を外へ投げない
    /// （失敗時は新プロセスを起動せず、旧プロセスは通常どおり終了する。この時点ではもう
    /// ウィンドウが後始末で破棄済みのため、利用者への「手動で再起動してください」という通知は
    /// 事前確認の時点で済ませてあり、ここでは改めて出さない）。
    ///
    /// 不具合2: 実行ファイルのパスを解決した結果（成否とパス）・<see cref="Process.Start(ProcessStartInfo)"/>
    /// の成否・新プロセスのPIDを<see cref="_restartLogger"/>（使い捨てロガー。呼び出し元の
    /// コメント参照）へ記録する。失敗時は例外の内容も記録する。
    /// </summary>
    private bool TryStartNewProcess(AppPaths? appPaths)
    {
        if (appPaths is not null)
        {
            appPaths.EnsureCoreDirectoriesExist();
            _restartLogger = new Logger(appPaths, autoCleanupOnStart: false);
        }

        var startInfo = AppRestart.BuildStartInfo();
        if (startInfo is null)
        {
            _restartLogger?.Error("restart", "実行ファイルのパスを解決できませんでした。新プロセスを起動できません。");
            return false;
        }
        _restartLogger?.Info("restart", $"実行ファイルのパスを解決しました: {startInfo.FileName}");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _restartLogger?.Error("restart", "Process.Startがnullを返しました。新プロセスを起動できませんでした。");
                return false;
            }

            _restartLogger?.Info("restart", $"新プロセスの起動に成功しました（PID={process.Id}）。");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _restartLogger?.Error("restart", $"Process.Startで例外が発生しました: {ex}");
            return false;
        }
    }

    /// <summary>
    /// 不具合3: <see cref="ViewModels.SettingsViewModel.RestartRequested"/>
    /// （<see cref="Views.SettingsWindow"/>経由）から呼ばれる。実際の後始末・新プロセス起動・
    /// 旧プロセス終了は<see cref="OnShutdownRequested"/>（<see cref="RestartSequencer"/>経由）が
    /// 担う。ここでは「次にShutdownRequestedが来たら再起動として扱う」フラグを立て、
    /// 通常の×ボタンと同じ経路で終了処理を起動するだけに留める（後始末のロジックを二重化しないため）。
    ///
    /// 不具合2（真因・「終了はするが再起動しない」）: 以前はここで
    /// <see cref="IClassicDesktopStyleApplicationLifetime.Shutdown"/>を呼んでいたが、これは
    /// Avalonia側の実装で常に<c>force: true</c>として扱われ、<see cref="OnShutdownRequested"/>が
    /// 購読する<see cref="IClassicDesktopStyleApplicationLifetime.ShutdownRequested"/>イベントを
    /// 一切発火しない（Avalonia.Controls.dllを逆コンパイルして確認: <c>ClassicDesktopStyleApplication
    /// Lifetime.Shutdown</c>は<c>DoShutdown(..., force: true)</c>を呼び、<c>DoShutdown</c>は
    /// <c>if (!force) { ShutdownRequested?.Invoke(...); ... }</c>という作りのため、forceがtrueだと
    /// イベント購読側が一切呼ばれないまま各ウィンドウを強制クローズしてプロセスを終了させる）。
    /// このため実機ログに「再起動が要求されました」の1行までは記録されるのに、その先の
    /// 「再起動経路で終了処理を開始します」（<see cref="OnShutdownRequested"/>側のログ）が
    /// 一切残らず、<see cref="RestartSequencer.RunAsync"/>（延いては新プロセスの起動）へ
    /// 到達すらしていなかった。原因候補Cで疑っていた「終了経路がProcess.Startより先に走っている」
    /// はほぼ正しく、より正確には「再起動ボタンのハンドラ（RequestRestart）自身が、後始末を
    /// 挟める経路を一切通らない即時終了APIを呼んでいた」ことが真因だった。
    ///
    /// 対処: <see cref="IClassicDesktopStyleApplicationLifetime.TryShutdown"/>（<c>force: false</c>で
    /// <c>DoShutdown</c>を呼ぶ）を使う。これは<see cref="IClassicDesktopStyleApplicationLifetime.
    /// ShutdownRequested"/>を発火し、購読側が<c>e.Cancel = true</c>にすれば実際の終了を保留できる
    /// （<see cref="OnShutdownRequested"/>が最初の呼び出しで必ず行っている）。ウィンドウを×で
    /// 閉じたときの経路（Avalonia内部の<c>HandleWindowClosed</c>→<c>TryShutdown</c>）と同じAPIを
    /// 使うことになり、以後の後始末（<see cref="RestartSequencer"/>経由の新プロセス起動を含む）が
    /// 正しく<see cref="OnShutdownRequested"/>へ到達するようになる。
    /// tests/Graft.UiTests/DesktopShutdownSemanticsTests.csにAvaloniaのこの挙動差そのものを
    /// 固定する回帰テストがある。
    /// </summary>
    public void RequestRestart()
    {
        if (_restartRequested) return; // 多重クリック等での二重要求を防ぐ。
        _restartRequested = true;

        // 不具合2: 再起動が要求された時点そのものを記録する。実機ログにこの1行すら
        // 無かったことが「通常のウィンドウ終了経路を通っているだけではないか」（原因候補C）を
        // 疑うきっかけになった。この行が実際に記録されていれば、少なくともRequestRestartまでは
        // 到達していることが分かる。
        _coordinator?.Logger?.Info("restart", "再起動が要求されました（設定画面のデータ保存先移行完了ダイアログの「再起動」ボタン）。");
        _desktop?.TryShutdown();
    }

    /// <summary>UIスレッド外（バックグラウンドタスク・ファイナライザ等）の想定外の例外。記録のみ行う。</summary>
    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _coordinator?.Logger?.Error("unhandled", ex.ToString());
        }
    }

    /// <summary>
    /// 不具合1: UIスレッドのジョブ（レイアウト/描画パスを含む）から素通りしてきた想定外の例外。
    /// 必ず記録したうえで、AvaloniaEdit由来と判定できる場合に限り<c>e.Handled = true</c>にして
    /// アプリを継続させる（<see cref="AvaloniaEditExceptionGuard"/>のコメント参照。それ以外は
    /// 従来どおり<see cref="OnUnhandledException"/>経由でプロセスが終了する）。
    /// </summary>
    private void OnDispatcherUnhandledException(object? sender, Avalonia.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        _coordinator?.Logger?.Error("unhandled", $"UIスレッドの処理中に例外が発生しました: {e.Exception}");

        if (AvaloniaEditExceptionGuard.ShouldContinue(e.Exception))
        {
            e.Handled = true;
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
