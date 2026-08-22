using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;
using AppSettings = Graft.Infra.Settings;

namespace Graft.Views;

/// <summary>
/// 起動処理全体を統括する。DIコンテナは使わず（附録A.5）、依存の生成はすべてここで手動に行う
/// （附録A.3）。6.8 多重起動の防止、13.1/6.3/15章/4.10の起動時検証、メインウィンドウ・トレイ・
/// グローバルホットキー・クリップボード監視の配線までを担う。
///
/// v2.0のWPF版からの移植（19章 L3）。OS固有の機能は <see cref="IPlatformServices"/> 越しに扱い、
/// このクラス自体はWin32 P/Invokeを一切持たない（v2.0のWPF版は直接呼んでいた）。各OSの実装は
/// Platform/Windows・Platform/Linux が担い、未対応の環境では利用不可を表明する
/// Null実装が選ばれて静かに縮退する。
/// </summary>
public sealed partial class StartupCoordinator : IAsyncDisposable
{
    private const string MutexNamePrefix = "Graft.SingleInstance.";
    private const string MainWindowTitle = "Graft";
    private const string PromptCopyHotkey = "Ctrl+Shift+C";

    private readonly AppPaths _appPaths;

    // UIフレームワーク固有の機能（クリップボード・画面情報・タイマー）。ViewModelへ手動で配る。
    private readonly IUiServices _ui = new AvaloniaUiServices();

    // OS固有の機能（トレイ・ホットキー・クリップボード監視・ごみ箱・多重起動防止など）。
    private readonly IPlatformServices _platform = PlatformServices.Current;

    private Logger? _logger;
    private SettingsStore? _settingsStore;
    private PatchQueue? _patchQueue;
    private ShellViewModel? _shellViewModel;
    private WindowMessageBridge? _messageBridge;
    private AppSettings _settings = new();

    // 機能追加（v1.0.12・自己再起動では起動時の更新確認をしない）: TryAcquireSingleInstanceAsyncが
    // 受け取るisRestartLaunchを覚えておき、StartAsync内の起動時更新確認の呼び出しへそのまま
    // 使い回すためのフィールド。TryAcquireSingleInstanceAsyncとStartAsyncはApp.axaml.cs側で
    // 順番に（別々のawaitとして）呼ばれるため、引数で受け渡す経路が無く、フィールドに
    // 一旦覚えておく必要がある。既定値false（=自己再起動ではない）は、単体テスト等で
    // TryAcquireSingleInstanceAsyncを呼ばずに直接StartAsyncだけを呼ぶ経路（通常の起動として
    // 扱われる）にとって安全な既定値。
    private bool _isRestartLaunch;

    // 機能改善（Ctrl+マウスホイールでの文字サイズ変更の永続化）: 従来は設定画面を開くたびに
    // 使い捨てのSettingsViewModelを生成していたが（OpenSettings参照）、ホイール操作は設定画面を
    // 開いていない間にも起こりうる。ホイール操作からも設定画面の入力欄（EditorFontSizeText）と
    // 全く同じ保存経路（300msデバウンス→検証→保存）に乗せるには、アプリの起動中ずっと
    // 生きている単一のSettingsViewModelが要る。そのため常駐インスタンスとして起動時に1つだけ
    // 作り、設定画面を開くときもこれを使い回す（毎回作り直すのをやめる）。既存のInitializeAsync
    // は読み込み・インポート・既定値復元のたびに呼ばれ何度呼んでも安全（settings.jsonを読み直して
    // 画面へ反映するだけ）なため、使い回しても既存のSettingsWindow.Loaded経由の再読込動作は
    // 変わらない。
    private SettingsViewModel? _settingsViewModel;

    // 課題1: データ保存先（実行ファイルと同じ階層）へ書き込めるかどうか。既定はtrue
    // （StartAsync冒頭のCanWriteToBaseDirectory()確認より前に参照されることは無いが、
    // 万一に備え安全側の値にしておく）。
    private bool _isDataDirectoryWritable = true;

    // 機能3: exeと同じ階層（データ保存先を切り替えるポインタファイル datapath.txt を置く場所）。
    // 本番では常にAppContext.BaseDirectoryと一致する。baseDirectoryを明示的に渡すテストでは、
    // _appPaths.BaseDirectory（＝そのbaseDirectory自体、ポインタ解決を経由しない）と一致させ、
    // 「ポータブルで自己完結した一時ディレクトリ」をそのままシミュレートする
    // （SettingsViewModelのデータ保存先まわりのコメント参照）。
    private readonly string _exeDirectory;

    // 機能3の追加: 孤立したユーザーフォルダの復帰確認（DataDirectoryRecoveryクラスドキュメント
    // 参照）の結果。App.axaml.cs側でコンストラクタ呼び出しより前に確認を終えているため、
    // ここでは結果を受け取ってLogger生成後に記録するだけ（StartupCoordinator.
    // DataDirectoryRecovery.csのLogDataDirectoryRecoveryOutcome参照）。
    private readonly DataDirectoryRecoveryOutcome _dataDirectoryRecoveryOutcome;

    /// <param name="baseDirectory">
    /// settings.json 等の基準ディレクトリ。省略時は<see cref="AppPaths.ResolveBaseDirectory"/>
    /// （データ保存先の選択機能。既定は実行ファイルの場所、ポインタファイルがあればそちら）で決める。
    /// テストから一時ディレクトリを渡して、利用者の設定を汚さずに起動処理を検証できるようにする。
    /// </param>
    /// <param name="dataDirectoryRecoveryOutcome">
    /// <see cref="ResolveDataDirectoryRecoveryAsync"/>の結果（省略時は
    /// <see cref="DataDirectoryRecoveryOutcome.NotApplicable"/>）。App.axaml.cs側で
    /// このコンストラクタを呼ぶより前に確認を終え、その結果を渡す。テストが
    /// <paramref name="baseDirectory"/>を明示するときはこの確認自体を一切呼ばないため、
    /// 省略時の既定値のままになる。
    /// </param>
    public StartupCoordinator(string? baseDirectory = null, DataDirectoryRecoveryOutcome? dataDirectoryRecoveryOutcome = null)
    {
        _exeDirectory = baseDirectory ?? AppContext.BaseDirectory;
        _appPaths = new AppPaths(baseDirectory);
        _dataDirectoryRecoveryOutcome = dataDirectoryRecoveryOutcome ?? DataDirectoryRecoveryOutcome.NotApplicable;
    }

    /// <summary>生成されたメインウィンドウ（仕様書9.2の新シェルレイアウト）。<see cref="StartAsync"/> 完了後に設定される。</summary>
    public ShellWindow? MainWindow { get; private set; }

    /// <summary>想定外の例外を記録するためのロガー。<see cref="StartAsync"/> 完了前は null。</summary>
    public Logger? Logger => _logger;

    /// <summary>
    /// 不具合2: 再起動シーケンス（<see cref="Core.RestartSequencer"/>）では、新プロセス起動の
    /// 試行結果を記録する時点で通常の<see cref="Logger"/>は既に<see cref="DisposeAsync"/>により
    /// 破棄済み（書き込みが黙って捨てられる）。<c>Graft.App</c>側が使い捨てのロガー
    /// （<see cref="LogSingleInstanceExitAsync"/>と同じ考え方）を組み立てられるよう、
    /// 基準ディレクトリを公開する。
    /// </summary>
    public AppPaths AppPaths => _appPaths;

    /// <summary>
    /// 多重起動を判定する（6.8）。既に起動中の場合は既存ウィンドウを前面へ表示しfalseを返す。
    /// 呼び出し側はfalseの場合アプリを即座に終了させること。
    ///
    /// 課題4: Mutex名は固定文字列ではなく発行フォルダ（<see cref="AppPaths.BaseDirectory"/>）を
    /// 混ぜ込んで作る（<see cref="SingleInstanceGuard.BuildInstanceScopedName"/>）。理由は
    /// そのメソッドのコメントを参照。
    ///
    /// 不具合2（「再起動」ボタンで終了はするが再起動しない）: 自己再起動で起動された新プロセス
    /// （<paramref name="isRestartLaunch"/>がtrue。<see cref="Infra.AppRestart.IsRestartLaunch"/>で
    /// 起動引数から判定する）に限り、Mutexの取得に1回失敗しても即座に諦めず短時間リトライする
    /// （<see cref="SingleInstanceAcquireRetry"/>のコメント参照）。通常の多重起動検知
    /// （利用者が2つ目を手動起動した場合、<paramref name="isRestartLaunch"/>がfalse）では
    /// リトライを一切行わず、これまでどおり即座に「既に起動中」と判定する。
    /// </summary>
    public async Task<bool> TryAcquireSingleInstanceAsync(bool isRestartLaunch)
    {
        // 機能追加（v1.0.12）: 起動時の更新確認をStartAsync内で自己再起動かどうかにより
        // 出し分けるため、判定結果をフィールドへ覚えておく（クラス冒頭のフィールドコメント参照）。
        _isRestartLaunch = isRestartLaunch;

        var mutexName = SingleInstanceGuard.BuildInstanceScopedName(MutexNamePrefix, _appPaths.BaseDirectory);
        var acquired = await SingleInstanceAcquireRetry
            .TryAcquireAsync(() => _platform.SingleInstance.TryAcquire(mutexName), isRestartLaunch)
            .ConfigureAwait(true);
        if (acquired) return true;

        _platform.SingleInstance.ActivateExistingInstance(MainWindowTitle);
        return false;
    }

    /// <summary>
    /// 課題1: <see cref="TryAcquireSingleInstance"/>がfalseを返し、多重起動検出により
    /// このプロセスを即座に終了させる経路専用のログ記録。この経路は<see cref="StartAsync"/>を
    /// 一切呼ばないため通常の<see cref="Logger"/>（<see cref="StartAsync"/>内で生成）が存在せず、
    /// そのままでは「終了処理が始まったことすら分からない」という課題1の欠陥が
    /// この経路にも当てはまってしまう。ここだけのために使い捨てのロガーを生成し、
    /// 1行記録してすぐに破棄する。
    /// </summary>
    public async Task LogSingleInstanceExitAsync()
    {
        _appPaths.EnsureCoreDirectoriesExist();
        await using var logger = new Logger(_appPaths, autoCleanupOnStart: false);
        logger.Info("shutdown", "多重起動を検出したため、既存ウィンドウを前面化してこのプロセスは終了します。");
    }

    /// <summary>依存の生成・メインウィンドウの表示・各種配線・初回起動ガイドまでを行う。</summary>
    public async Task StartAsync()
    {
        // 18章「起動から操作可能まで1秒以内」を実機で確認できるよう、ウィンドウが
        // 表示されるまでの所要時間を記録する。プロセス開始からの計測とするため、
        // 起点は現在のプロセスの開始時刻を使う。
        var startedAt = Process.GetCurrentProcess().StartTime;

        var dialogService = new AvaloniaDialogService();

        // 機能3: 保存先切り替えの「後始末待ち」（前回、設定画面でユーザーフォルダ⇔ポータブルの
        // 切り替えを行い、まだ削除されていない旧保存先があれば、ここで取り込み直してから削除する）。
        // なぜ移行のその場ではなくここ（次回起動時）で削除するのかは
        // DataDirectoryMigratorクラスドキュメントの【なぜ即時削除ではなく次回起動時なのか】参照。
        // 【実行順序が重要】必ずLogger生成（直後のInitializeDataDirectoryAsync）より前に行うこと。
        // 後回しにすると、取り込み直し（Migrate）がlogs/配下を上書きコピーする際、今回の起動で
        // Loggerが既に書き込んだログ（起動直後の1行目）を旧保存先の内容で上書きしてしまう
        // （旧保存先の同日付ログファイルには今回のログ行が含まれていないため）。
        var pendingCleanupOutcome = DataDirectoryMigrator.RunPendingCleanup(_appPaths.BaseDirectory);

        // 課題1（バグ）: データ保存先へ書き込めるかを確認し、書き込めなければ日本語で警告する。
        // ログ経由の通知に頼れない状況を想定しているため、Loggerより先に行う
        // （詳細はStartupCoordinator.WriteCheck.csのコメント参照）。
        _logger = await InitializeDataDirectoryAsync(dialogService).ConfigureAwait(true);

        // 機能3: 孤立したユーザーフォルダの復帰確認（App.axaml.cs側でこのコンストラクタより前に
        // 完了済み）・上の後始末、いずれもLogger生成より前に完了させる必要があったため、
        // ここでまとめて結果を記録する（DataDirectoryRecovery.cs・DataDirectory.csの
        // 各クラスドキュメント参照）。
        LogDataDirectoryRecoveryOutcome(_dataDirectoryRecoveryOutcome);
        LogPendingCleanupOutcome(pendingCleanupOutcome);

        // 機能追加（自動更新）: 前回の自動更新で残った*.old（旧ファイルの退避、
        // Core.Update.SelfUpdateInstaller参照）を掃除する。指示書の設計どおり
        // 「次回起動時に.oldを削除する」。対象はUpdateFiles.RequiredFileNamesに列挙された
        // 配布物ファイルの.old版のみで、settings.json等の利用者データには一切触れない。
        // 削除に失敗しても起動は継続する（次回起動時に再試行される）ため、ここも他の
        // 起動時後始末と同じくLogger生成直後・軽量な同期処理として行う。
        //
        // 【不具合修正（v1.0.11・実機報告「フォルダの中が.oldで溢れる」）】以前はここで
        // _appPaths.BaseDirectory（settings.json等の"データ保存先"）を渡していたが、.oldは
        // 実行ファイルの隣（Core.Update.SelfUpdateInstallerがGraft.exe等をリネームする場所）に
        // できる。「設定画面からデータ保存先をユーザーフォルダへ移動」した環境ではこの2つが
        // 食い違い、.oldが永久に掃除されない不具合を引き起こしていた。これはPR #41で修正した
        // 自動更新のインストール先の取り違え（Infra.AppRestart.TryResolveExecutableDirectoryの
        // XMLコメント参照）と全く同じ種類の誤りで、この呼び出し箇所だけがPR #41の修正から
        // 漏れていた。SettingsViewModel.Update.csのRunUpdateAsyncと同じ解決経路
        // （AppRestart.TryResolveExecutableDirectory）に必ず揃える。
        // 万一実行ファイルの場所を解決できなければ（Environment.ProcessPathが取得できない
        // 異常時のみ）、_exeDirectory（本番ではAppContext.BaseDirectoryと一致する。コンストラクタの
        // コメント参照）へ安全側でフォールバックする。
        var oldFileCleanupDirectory = Infra.AppRestart.TryResolveExecutableDirectory() ?? _exeDirectory;
        var removedOldFiles = Core.Update.PendingUpdateCleanup.Run(oldFileCleanupDirectory);
        if (removedOldFiles.Count > 0)
        {
            _logger.Info("update", $"前回の更新で残っていた退避ファイルを削除しました: {string.Join(", ", removedOldFiles)}");
        }

        // 機能追加（v1.0.12・利用者からの指摘「ダウンロードした一時ファイルは削除されているか」）:
        // 自動更新のダウンロード中にGraftが強制終了・クラッシュすると、一時作業フォルダ
        // （%TEMP%\GraftUpdate\<GUID>\。50MB超のZIPを含みうる）が掃除されずに残り続ける
        // （UpdateInstallPipeline.RunAsyncのfinallyはプロセスが生きている間しか働かないため）。
        // 上の.old掃除と同じ「次回起動時に掃除する」方針で、ここでまとめて後始末する。
        // 実行中の更新（別プロセスが今まさに使っている作業フォルダ）を誤って消さないための
        // 安全策はPendingUpdateWorkDirCleanupのクラスコメント参照（既定24時間以上前に
        // 作成されたフォルダだけを対象にする）。
        var removedWorkDirs = Core.Update.PendingUpdateWorkDirCleanup.Run();
        if (removedWorkDirs > 0)
        {
            _logger.Info("update", $"自動更新の古い一時作業フォルダを{removedWorkDirs}件削除しました。");
        }

        // 設計目標5（製品相当の完成度）: UIハンドラ内の想定外の例外でアプリを終わらせない。
        // 記録したうえで日本語の通知だけ出し、操作を続けられるようにする。
        SafeHandler.OnUnexpected = (context, ex) =>
        {
            _logger?.Error("handler", $"{context}: {ex}");
            // 想定外の例外（附録A.4: 握り潰さずログへ記録しつつ日本語で通知する）。
            // ex.Messageの生文言をそのまま出さず、よくある原因はExceptionMessagesで日本語化する。
            _ = dialogService.ShowMessageAsync("Graft", $"{context}に失敗しました。{Environment.NewLine}{ExceptionMessages.Describe(ex)}");
        };

        _settingsStore = new SettingsStore(_appPaths);
        var patchQueue = new PatchQueue(_appPaths);
        _patchQueue = patchQueue;

        var settingsResult = await _settingsStore.LoadAsync().ConfigureAwait(true);
        _settings = settingsResult.Value;

        // v1.0.7実機不具合対応: 起動時の環境要約をログへ残す（EnvironmentSummaryLoggerの
        // クラスコメント参照）。この時点ではまだプロジェクトが自動選択される前
        // （ProjectPane.LoadAsyncはMainViewModel.InitializeAsync、ShellWindow.OnLoadedより後）
        // のため、プロジェクトは「未選択」として記録される。実際に選ばれたプロジェクトの
        // 要約は、選択のたびにShellViewModel.OnProjectSelectedから別途記録する。
        EnvironmentSummaryLogger.Log(_logger, _appPaths, _exeDirectory, _settings, projectRoot: null);

        // 9.3: 保存しておいたテーマを反映する。App起動時点では設定をまだ読めていないため、
        // ここで当て直さないと、選んだテーマが再起動のたびにシステム追従へ戻ってしまう。
        Themes.ThemeManager.SetTheme(Themes.ThemeManager.ParseTheme(_settings.Theme));

        // 依頼3（E709）: OSのハイコントラストモードの検出結果（App.axaml.csのThemeManager.
        // Initializeで起動時に1回読み取り済み）をログへ残す。9.3のとおり配色は切り替えないため、
        // ここではダイアログを出さずログのみに記録する（PlatformDiagnosticsLoggingのコメント参照）。
        PlatformDiagnosticsLogging.LogHighContrastIfDetected(_logger, Themes.ThemeManager.IsHighContrastActive);

        // 検討書「フォント設定」。テーマと同じ理由（この後SettingsViewModelを生成するまでの
        // 間に表示されうるダイアログ・ShellWindow自体の初回描画にも正しいフォントを効かせる
        // ため）で、ここでも早期に反映しておく。SettingsViewModel.InitializeAsync側の
        // PopulateEditorFields経由でも同じ値がSelectedFontFamily/SelectedMonospaceFontFamilyへ
        // 反映され、そちらのsetterが再度AppFontManagerを呼ぶが、同じ値を2回適用するだけで
        // 副作用は無い。
        Themes.AppFontManager.SetBodyFontFamily(_settings.Editor.FontFamily);
        Themes.AppFontManager.SetCodeFontFamily(_settings.Editor.MonospaceFontFamily);

        // 課題3: 自動起動が有効なら、毎起動時に登録し直して現在の実行ファイルパスへ追従させる。
        // アプリを別の場所へ移動した後でも（設定画面でオン・オフし直さなくても）、次回起動時に
        // 古いパスの登録が自然に正しいパスへ上書きされるようにするための保険。失敗しても
        // 起動そのものは継続し、ログにのみ記録する（利用者を起動のたびに煩わせないため。
        // 明示的にオン/オフを切り替えたときの失敗は設定画面で都度通知する）。
        if (_settings.LaunchAtStartup)
        {
            var result = _platform.AutoStart.Enable();
            if (!result.Success)
            {
                _logger.Warn("startup", $"自動起動の登録し直しに失敗しました: {result.ErrorMessage}");
            }
        }

        var projectStore = new ProjectStore(_appPaths);
        var revisionStore = new RevisionStore(_appPaths, _platform.Trash);
        var revisionRestorer = new RevisionRestorer(_appPaths);

        // 課題2: 「閉じたときの動作」は即時反映のため、設定画面での変更を実行中のShellWindowへ
        // その場で反映するコールバックを渡す。常駐インスタンスにする理由は_settingsViewModel
        // フィールドのコメント参照。設定画面を一度も開かないままCtrl+マウスホイールが使われても
        // 正しい既存の設定内容を土台に保存できるよう、ここで既に読み込み済みのsettings.jsonを
        // 読み直しておく。
        // データ保存先の切り替え（datapath.txt）はexeと同じ階層を基準に判断するため、
        // _exeDirectoryも渡す（SettingsViewModel.DataDirectory.cs参照）。
        _settingsViewModel = new SettingsViewModel(
            _appPaths, dialogService, _ui, ApplyLiveSettingsChange, _exeDirectory, externalLinks: _platform.ExternalLinks);
        // 機能追加（v1.0.11・起動時の更新確認の可観測性）: 起動時・手動いずれの更新確認の結果も
        // logs/<日付>.logへ記録できるよう、他の常駐ViewModel（mainViewModel.Logger等、下記参照）と
        // 同じ作法でロガーを渡す。InitializeAsync（RefreshUpdateLastCheckedAsyncを含む）より
        // 前に設定しておく。
        _settingsViewModel.Logger = _logger;
        await _settingsViewModel.InitializeAsync().ConfigureAwait(true);

        // 機能追加（自動更新）: 「更新の準備ができました」ダイアログの「今すぐ再起動」から
        // 発火するRestartRequestedを、データ保存先移行の「再起動」ボタンと同じ経路
        // （Graft.App.RequestRestart）へつなぐ。SettingsWindow.axaml.cs側にも同じ配線が
        // あるが、それはウィンドウを開いている間しか有効でない購読であり、起動時の自動確認
        // （設定画面を一度も開いていない状態でも発火しうる）を取りこぼさないよう、常駐する
        // SettingsViewModelの生存期間ぶんここで購読しておく。App.RequestRestart自体は
        // 二重要求を防ぐガードを持つため、SettingsWindowを開いていたときの重複購読と鉢合わせても
        // 実害はない。
        _settingsViewModel.RestartRequested += (_, _) =>
        {
            if (Avalonia.Application.Current is App app) app.RequestRestart();
        };

        void OpenSettings()
        {
            var window = new SettingsWindow(_settingsViewModel!);
            if (MainWindow is not null) _ = window.ShowDialog(MainWindow);
            else window.Show();
        }

        var shellViewModel = BuildShellViewModel(
            _appPaths, _settings, _settingsStore, patchQueue, projectStore, revisionStore, revisionRestorer,
            dialogService, _ui, OpenSettings, _platform.Trash);
        var mainViewModel = shellViewModel.Graft;
        _shellViewModel = shellViewModel;

        // 機能追加（自動更新）: 更新の再起動前に未保存の編集を確認する差し替え口
        // （SettingsViewModel.Update.csのConfirmUnsavedDocumentsAsyncコメント参照）。
        // SettingsViewModelはShellViewModelより先に生成されるためコンストラクタでは渡せず、
        // ここで生成後に設定する。既存のプロジェクト切り替え時の未保存確認
        // （ShellViewModel.cs参照）と同じEditor.CloseAllAsyncをそのまま流用する。
        _settingsViewModel.ConfirmUnsavedDocumentsAsync = () => shellViewModel.Editor.CloseAllAsync();

        // 機能改善: エディタ・差分表示でのCtrl+マウスホイールでの確定を、常駐の
        // SettingsViewModelへ橋渡しする（SettingsViewModel.SetEditorFontSizeLiveのコメント参照）。
        shellViewModel.EditorFontSizeChangeRequested += (_, size) => _settingsViewModel!.SetEditorFontSizeLive(size);
        // 機能改善（差分の左右並列表示）: diff表示ヘッダーでの並列／統合表示の切り替えを、
        // 同じ経路で常駐のSettingsViewModelへ橋渡しする（SettingsViewModel.SetSideBySideLive参照）。
        shellViewModel.DiffSideBySideChangeRequested += (_, v) => _settingsViewModel!.SetSideBySideLive(v);

        // 課題3: Git自動コミットの失敗理由をlogs/<日付>.logへ記録できるよう、window.Loggerと
        // 同じ流儀（生成後に設定するnullableプロパティ）でロガーを渡す。
        mainViewModel.Logger = _logger;
        // 「ここまで戻す」の成否をログへ残すため、History側にも同じロガーを渡す。
        mainViewModel.History.Logger = _logger;

        // 課題1: 起動時ダイアログは1回きりで、その後は画面から見えなくなってしまう。
        // 「保存されない」状態が続いている間はステータスバーに常時表示し続けることで、
        // 黙って失敗し続けることを防ぐ（MainViewModel.DataWritability.cs参照）。
        if (!_isDataDirectoryWritable)
        {
            mainViewModel.MarkDataDirectoryReadOnly();
        }

        var window = new ShellWindow(shellViewModel);
        MainWindow = window;

        // 課題2: 「閉じたときの動作」設定と、トレイの実際の利用可否をウィンドウへ渡す。
        // IsTraySupportedはプロセス起動中に変わらないため、ここで一度だけ設定する。
        window.CloseBehavior = _settings.CloseBehavior;
        window.IsTraySupported = _platform.Tray.IsSupported;

        // 課題1: 終了処理（ShellWindow.OnClosing）が経路・レイアウト保存の成否を記録できるよう、
        // 起動時に生成済みのロガーを渡す。
        window.Logger = _logger;

        // 画面情報（IScreenInfo）はデスクトップライフタイムのウィンドウ経由で解決するため、
        // レイアウト復元より前にMainWindowを割り当てておく。割り当てが遅れると画面構成が
        // 取得できず、復元サイズが最小サイズまで縮む。
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = window;
        }

        var startupIssues = new List<GraftIssue>(settingsResult.Issues);
        WirePlatformServices(window, mainViewModel, startupIssues);

        // 不具合4対応: OnLoaded経由の起動直後プロジェクト自動選択（MainViewModel.InitializeAsync）が
        // ファイル監視の開始に失敗すると、ExplorerViewModelは既定で即座に自分のダイアログを出す。
        // これがRunStartupValidationAsync（背景検証、完了後に1枚のダイアログへ集約する）と
        // 別々に表示され、2枚重なって出ていた（実機で確認）。ここでハンドラを差し込み、
        // 起動時レポートが提示されるまでの間はこのリストへ集約する。
        //
        // 単に「差し込んで完了後にnullへ戻す」だけだと、初回のSetProjectAsync（監視開始の
        // 試行）が完了するより先にRunStartupValidationAsyncが完了・レポート確定してしまう
        // 順序のときに、監視失敗の警告が一切表示されないまま失われる（実機検証で実際に発生を
        // 確認したレース）。そのため、initialWatchSignalで「初回の監視開始試行が
        // 完了（成功・失敗問わず）」を通知させ、RunStartupValidationAsync側はプロジェクトが
        // 1件以上あるときに限りこの通知（またはタイムアウト）を待ってからレポートを確定する
        // （StartupCoordinator.Validation.cs参照）。issues一覧への追加はスレッドをまたぐため
        // startupIssuesをロックオブジェクトとして使う。
        var initialWatchSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shellViewModel.Explorer.WatchStartCompletedHandler = issue =>
        {
            if (issue is not null)
            {
                lock (startupIssues) startupIssues.Add(issue);
            }
            initialWatchSignal.TrySetResult();
        };

        // 不具合修正: 最小化時にトレイへ格納するかどうかは設定
        // （AppSettings.MinimizeToTray、既定オフ）で選べるようにする。以前は設定に関わらず
        // トレイが使える環境では常にHide()しており、タスクバーからもウィンドウが消えてしまう
        // （Windowsの通常の慣習＝最小化時はタスクバーに残る、から外れる）不具合だった。
        // トレイが使えない環境では、設定がオンでも従来どおり通常の最小化のまま（縮退）。
        //
        // 即時反映の注意: このハンドラは<see cref="StartAsync"/>実行時に1度だけ登録し、
        // 以後は毎回発火のたびに実行される。ここで<c>_settings.MinimizeToTray</c>を直接
        // 参照しているのは重要で、ローカル変数へ一度だけ読み出してクロージャに焼き付けると、
        // 設定画面で変更してもこのハンドラには反映されなくなってしまう
        // （StartupCoordinator.Hotkey.csのReapplyHotkeyIfChangedと同じ「即時反映」の作法。
        // <c>_settings</c>はApplyLiveSettingsChangeで随時差し替わるインスタンスフィールドの
        // ため、フィールド越しに毎回読むこのままの書き方であれば自動的に最新値を拾える）。
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty) return;
            if (ShouldHideOnMinimize(_platform.Tray.IsSupported, _settings.MinimizeToTray, window.WindowState)) window.Hide();
        };

        window.Show();
        _logger.Info("startup",
            $"操作可能まで {(int)(DateTime.Now - startedAt).TotalMilliseconds} ms");

        // 機能追加（自動更新）: 起動時の更新確認。ウィンドウ表示後にfire-and-forgetで開始し、
        // 起動そのものを一切ブロックしない（要件: 通信は非同期・起動をブロックしないこと。
        // 上の「操作可能まで」計測より後ろに置いているのはそのため）。設定がオフなら通信自体を
        // 行わない（SettingsViewModel.Update.cs・Core.Update.UpdateChecker参照）。通信の失敗は
        // UpdateChecker側で「確認できなかった」に丸められ例外は投げない契約だが、
        // 附録A.4（握り潰さない）に倣い、万一の想定外の例外は観測してログへ落とす。
        //
        // 仕様変更（v1.0.12・利用者からの追加要望）: 「起動するたびに確認する」の対象は
        // 利用者が自分で起動したときであり、Graft自身がテーマ変更・データ保存先の移動・
        // 自動更新の適用後に自己再起動したとき（AppRestart.BuildStartInfo経由の再起動は
        // すべて該当。<see cref="_isRestartLaunch"/>参照）は対象外とする。短時間に何度も
        // 再起動が起きる状況（例: 起動直後にクラッシュを繰り返す）でGitHub APIの未認証時
        // 上限（IPごと1時間60回）を無駄に消費しない、という副次的な効果もあるが、それを
        // 主目的とした時間ベースのガード（等）は入れていない。自己再起動は「利用者による
        // 起動」ではないという設計上の理由だけで十分に説明できるため。
        _ = _settingsViewModel.CheckForUpdateOnStartupAsync(_isRestartLaunch)
            .ContinueWith(
                task => _logger?.Error("update", $"起動時の更新確認に失敗しました: {task.Exception!.GetBaseException()}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

        if (!OnboardingWindow.HasCompleted(_appPaths))
        {
            // シェルの左ペイン・上部ドロップダウンが参照しているProjectPaneViewModelと同じ
            // インスタンスを渡す（バグ修正: 初回起動ガイドで登録したプロジェクトが一覧に
            // 反映されない不具合。詳細はOnboardingWindowのコンストラクタのコメントを参照）。
            var onboarding = new OnboardingWindow(_appPaths, mainViewModel.ProjectPane);
            await onboarding.ShowDialog(window).ConfigureAwait(true);

            // 最終画面「使い方を学ぶ」が選ばれていれば、ガイドを閉じた直後にシェル側の
            // 画面上チュートリアル（ShellWindow.Tutorial.cs）を開始する。OnboardingWindow自体は
            // シェルの実際のコントロールを一切知らないため、開始そのものはここ（両方を知る
            // StartupCoordinator）が橋渡しする。
            if (onboarding.StartTutorialRequested)
            {
                window.StartTutorial();
            }
        }

        // 起動を待たせたくないので完了を待たない。ただし投げっぱなしにすると失敗が
        // ファイナライザ経由の未観測例外として遅れて表面化し、原因を追いにくい。
        // 例外は必ず観測してログへ落とす（附録A.4: 握り潰さない）。
        _ = RunStartupValidationAsync(
                projectStore, revisionStore, dialogService, revisionRestorer, startupIssues, initialWatchSignal.Task)
            .ContinueWith(
                task => _logger?.Error("startup", $"起動時検証に失敗しました: {task.Exception!.GetBaseException()}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }

    /// <summary>
    /// ShellViewModel以下の依存グラフを組み立てる（附録A.3: DIコンテナを使わない手動構築）。
    /// 起動処理本体とUIテストの双方から使い、実際の起動と同じ組み合わせを検証できるようにする。
    /// </summary>
    /// <param name="trash">
    /// ごみ箱への削除。省略時（UIテスト等）はごみ箱を使わない（10件目の不具合修正）。
    /// 実際の起動（<see cref="StartAsync"/>）は <c>PlatformServices.Current.Trash</c> を渡す。
    /// </param>
    public static ShellViewModel BuildShellViewModel(
        AppPaths appPaths, AppSettings settings, SettingsStore settingsStore, PatchQueue patchQueue,
        ProjectStore projectStore, RevisionStore revisionStore, RevisionRestorer revisionRestorer,
        IDialogService dialogService, IUiServices ui, Action openSettings, ITrashService? trash = null)
    {
        var applyEngine = BuildApplyEngine(appPaths, settings, trash);
        var mainViewModel = new MainViewModel(
            applyEngine, projectStore, revisionStore, revisionRestorer,
            settingsStore, new WindowLayoutStore(appPaths), dialogService, patchQueue, openSettings, ui);
        var editorViewModel = new EditorPaneViewModel(settings, dialogService, ui);
        return new ShellViewModel(appPaths, mainViewModel, editorViewModel, dialogService, settings, ui);
    }

    private static ApplyEngine BuildApplyEngine(AppPaths appPaths, AppSettings settings, ITrashService? trash)
    {
        var matchEngine = new MatchEngine(new MatchOptions
        {
            SimilarityThreshold = settings.Matching.SimilarityThreshold,
            AllowSimilarityMatch = settings.Matching.AllowSimilarityMatch,
            RangeWarningLines = settings.Matching.RangeWarningLines,
        });
        return new ApplyEngine(new BackupManager(appPaths), new RevisionStore(appPaths, trash), matchEngine);
    }

    private static void RestoreWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// 不具合修正: 最小化時にタスクトレイへ格納すべきかどうかの純粋な判定。
    /// window.PropertyChangedハンドラから毎回呼ぶ（<see cref="StartAsync"/>参照）ことで、
    /// 「トレイが使える」「設定がオンである」「実際に最小化された」の3条件をすべて満たす
    /// 場合にのみ格納する。トレイが使えない環境では、設定がオンでも常にfalse（縮退。
    /// 従来どおりの通常の最小化のまま）。
    ///
    /// テスト容易性: <see cref="ActivateWindowOnPatchDetected"/>・<see cref="ReapplyHotkey"/>と
    /// 同じ理由で、実際のOS資源（Window・トレイ）に触れずに判定ロジックだけを単体テストできる
    /// よう、staticな純粋関数として切り出している（MinimizeToTrayTests.cs参照）。
    /// </summary>
    public static bool ShouldHideOnMinimize(bool trayIsSupported, bool minimizeToTraySetting, WindowState currentState)
        => trayIsSupported && minimizeToTraySetting && currentState == WindowState.Minimized;

    // ------------------------------------------------------------------
    // OS固有機能の配線（9章 クリップボード監視・8.10 グローバルホットキー・トレイ常駐）
    // ------------------------------------------------------------------

    private void WirePlatformServices(ShellWindow window, MainViewModel mainViewModel, List<GraftIssue> issues)
    {
        // 依頼2（E706）: 個別のエラーコードを持たない機能（トレイ常駐・自動起動）がこの環境で
        // 使えない場合、その事実をログへ残す。設定画面（SettingsViewModel.
        // IsTraySupported/IsAutoStartSupported）は既にUnsupportedReasonで理由付きの無効表示を
        // 行っているため機能としては満たしているが、E706というエラーコードとしてのトレース
        // （このコードが実際に何のために使われたか、ログから追える状態）が欠けていた。
        // ホットキー（E601）・クリップボード監視（E602）は専用コードを既に持つためここでは
        // 対象にしない（重複記録を避ける。ErrorCodes.csのE706コメント参照）。
        PlatformDiagnosticsLogging.LogUnsupportedFeature(_logger, "タスクトレイ常駐", _platform.Tray);
        PlatformDiagnosticsLogging.LogUnsupportedFeature(_logger, "自動起動", _platform.AutoStart);

        // クリップボード監視とホットキーが受信するウィンドウハンドルの割り当ては
        // WindowMessageBridge が行う（Windowsは専用のメッセージ受信ウィンドウ、
        // Linuxはハンドルを使わない実装）。
        _messageBridge = WindowMessageBridge.Attach(_platform);

        if (_settings.ClipboardWatch.Enabled)
        {
            issues.AddRange(_platform.Clipboard.Start().Issues);
        }
        // ステータスバーの「クリップボード監視中」表示（ShellViewModel.ClipboardWatch.cs）を
        // 起動直後の状態に合わせる。以降はToggleClipboardWatchが開始・停止のたびに更新する。
        _shellViewModel?.SetClipboardWatchActive(_platform.Clipboard.IsEnabled);
        _platform.Clipboard.PatchDetected += (_, _) => OnClipboardPatchDetected(window);
        // 11件目の不具合修正: パッチ検知の通知は出したままだと、その後に非パッチのテキストを
        // コピーしても消える経路が無かった。NonPatchTextChanged（本タスクで追加）を購読し、
        // ShellViewModel.ClearClipboardPatchNoticeへ橋渡しする。
        _platform.Clipboard.NonPatchTextChanged += (_, _) => _shellViewModel?.ClearClipboardPatchNotice();
        // 細かいユーザビリティ改善2: ステータスバーのインジケータクリックによる一時停止／再開。
        if (_shellViewModel is not null) _shellViewModel.ClipboardWatchPauseToggleRequested += OnClipboardWatchPauseToggleRequested;

        // 10件目の不具合修正: 実際の登録処理はStartupCoordinator.Hotkey.csへ切り出した
        // （設定画面での変更を再起動なしで反映する再登録処理と、登録ロジックを共有するため）。
        RegisterInitialHotkeys(window, mainViewModel, issues);

        _platform.Tray.Configure(new TrayMenuDescriptor
        {
            ClipboardWatchEnabled = _platform.Clipboard.IsEnabled,
            OnToggleClipboardWatch = SetClipboardWatchEnabled,
            RecentProjects = mainViewModel.ProjectPane.Items
                .Take(9)
                .Select(p => new TrayRecentProjectItem(p.DisplayName, () => mainViewModel.ProjectPane.SelectedItem = p))
                .ToList(),
            OnRestoreMainWindow = () => RestoreWindow(window),
            OnOpenSettings = () => mainViewModel.OpenSettingsCommand.Execute(null),
            OnExit = () => ForceExit(window),
        });
        _platform.Tray.Show();
    }

    /// <summary>
    /// クリップボード監視を実際に開始・停止し、ステータスバー表示（ShellViewModel.
    /// ClipboardWatch.cs）を最新の状態へ合わせる共通処理。トレイメニューからのトグル
    /// （<see cref="SetClipboardWatchEnabled"/>、設定の保存を伴う）と、設定画面での変更の伝播
    /// （<see cref="ApplyLiveSettingsChange"/>、保存は既にSettingsViewModel側で完了済み）の
    /// 両方から呼ぶ。開始・停止はApplyEngine・MatchEngineのいずれも参照せず、書き込み中の
    /// ファイルへは一切影響しないため、MainViewModel.UpdateSettingsが行っている
    /// 「適用処理中は反映を保留する」（_isApplyInProgress）とは無関係にその場で切り替えてよい。
    /// </summary>
    private void ToggleClipboardWatch(bool enabled)
    {
        if (enabled) _platform.Clipboard.Start();
        else _platform.Clipboard.Stop();

        _shellViewModel?.SetClipboardWatchActive(_platform.Clipboard.IsEnabled);
    }

    /// <summary>
    /// 細かいユーザビリティ改善2: ステータスバーの「クリップボード監視中」表示をクリックしたときの
    /// 一時停止・再開。<see cref="ToggleClipboardWatch"/>（設定・トレイ経由、Settings.ClipboardWatch.
    /// Enabledの保存を伴う）とはあえて別経路にしている。ここでは<c>_settings</c>を一切書き換えず
    /// （<see cref="ShellViewModel.ClipboardWatch.IsClipboardWatchPaused"/>のコメント参照）、実際に
    /// <c>IClipboardMonitor.Stop/Start</c>だけを呼ぶ。パスワードをコピーする間だけ止めたい、という
    /// ような一時的な用途を想定しており、アプリを再起動すれば設定どおりの状態に戻る。
    ///
    /// 再開（<paramref name="pause"/>がfalse）でStart()が失敗した場合（環境側の制約等、起動時と
    /// 同じ理由で稀に起こりうる）は、<c>_platform.Clipboard.IsEnabled</c>がfalseのままになるため、
    /// 一時停止表示を維持し（!IsEnabledをそのまま渡す）利用者が再度クリックして再試行できるように
    /// している。
    /// </summary>
    private void OnClipboardWatchPauseToggleRequested(object? sender, bool pause)
    {
        if (pause) _platform.Clipboard.Stop();
        else _platform.Clipboard.Start();

        _shellViewModel?.SetClipboardWatchPaused(!_platform.Clipboard.IsEnabled);
    }

    /// <summary>トレイメニューからのクリップボード監視ON/OFF。設定にも反映して次回起動へ引き継ぐ。</summary>
    private void SetClipboardWatchEnabled(bool enabled)
    {
        ToggleClipboardWatch(enabled);

        _settings = _settings with { ClipboardWatch = _settings.ClipboardWatch with { Enabled = enabled } };

        // 課題1関連: 以前は`_ = _settingsStore.SaveAsync(_settings);`と投げっぱなしにしており、
        // 書き込みに失敗した場合の例外はTaskScheduler.UnobservedTaskException（App.axaml.cs）に
        // しか届かず、ログに記録されるだけで利用者には一切通知されなかった（設定の保存は
        // 即時反映方式のため、これは「保存できたと思い込んだまま実は消えている」実害の大きい
        // 抜け道だった）。SettingsViewModel.CommitAndSaveAsync と同じSafeHandler経由に揃え、
        // 失敗時は他の保存失敗と同じダイアログ通知（+ロガーが書ければログにも記録）を行う。
        if (_settingsStore is not null)
        {
            _ = SafeHandler.RunAsync("設定の保存", () => _settingsStore.SaveAsync(_settings));
        }
    }

    /// <summary>
    /// 課題2: 設定画面での「閉じたときの動作」変更を、実行中のウィンドウへ即時反映する。
    /// SettingsViewModelの即時反映（300msデバウンス後の保存）が成功した直後に呼ばれる。
    ///
    /// 課題1: 適用の挙動を決める設定（安全機構・マッチング・バックアップ・Git連携・
    /// 適用後フックのタイムアウト・差分表示の折り返し/空白表示等）は、MainViewModelが
    /// 起動時に読み込んだ<c>_settings</c>を以後一切更新していなかったため、設定画面で
    /// 変更しても再起動するまで動作へ反映されなかった（実機確認: 適用中に「適用後に
    /// 自動コミットする」をオンにしても、その場の適用ではコミットされない）。ここから
    /// MainViewModel.UpdateSettingsへ伝播させる。反映のタイミング（適用処理の実行中は
    /// 完了まで保留する等）はMainViewModel側の責務とする（詳細はUpdateSettingsのコメント）。
    ///
    /// 9件目の不具合修正: クリップボード監視の有効/無効（15章）は、以前はここで
    /// <c>_settings</c>を差し替えるだけで、実際に<c>IClipboardMonitor</c>のStart/Stopを
    /// 呼んでいなかった。設定画面のトグルを保存できても監視は次回起動まで一切反応せず、
    /// 実機で「オンにしても反応しない」不具合として報告された。トレイメニューからのトグル
    /// （<see cref="SetClipboardWatchEnabled"/>）は元々動いていたため、その中身
    /// （<see cref="ToggleClipboardWatch"/>）を切り出して共有し、設定画面での変更もここから
    /// 同じ経路で反映する。
    ///
    /// 10件目の不具合修正: グローバルホットキー（8.10章）も同じ「設定の前後比較→変化があれば
    /// 実際の資源へ反映」の流儀へ揃える。クリップボード監視との違い（失敗しうる操作であり、
    /// 失敗時は握り潰さず古い組み合わせへ戻したうえで警告する必要がある）は
    /// <see cref="ReapplyHotkeyIfChanged"/>（StartupCoordinator.Hotkey.cs）側の責務とする。
    /// </summary>
    private void ApplyLiveSettingsChange(AppSettings updated)
    {
        var previousClipboardWatchEnabled = _settings.ClipboardWatch.Enabled;

        _settings = updated;
        if (MainWindow is not null) MainWindow.CloseBehavior = updated.CloseBehavior;
        _shellViewModel?.Graft.UpdateSettings(updated);
        // 機能改善: エディタ本文のフォントサイズ（Settings.Editor.FontSize）も、設定画面での
        // 変更や他画面でのCtrl+マウスホイールでの変更から再起動なしでその場に反映する
        // （EditorPaneViewModel.UpdateSettings参照）。
        _shellViewModel?.Editor.UpdateSettings(updated);

        if (updated.ClipboardWatch.Enabled != previousClipboardWatchEnabled)
        {
            ToggleClipboardWatch(updated.ClipboardWatch.Enabled);
        }

        // MainWindow・_shellViewModelはStartAsync完了後（＝設定画面を開けている時点）なら
        // 必ず両方揃っているはずだが、念のため両方揃っている場合のみ再登録を試みる。
        if (MainWindow is not null && _shellViewModel is not null)
        {
            ReapplyHotkeyIfChanged(updated.Hotkey, MainWindow, _shellViewModel.Graft);
        }
    }

    /// <summary>
    /// トレイメニューの「終了」。CloseBehaviorが「タスクトレイに常駐する」であっても
    /// ここからは必ず終了させたいため、IsForceClosingを立ててからWindow.Close()を呼ぶ。
    /// window.Close()はOnClosing→（最後の1枚なら）ApplicationLifetimeの自動シャットダウン判定
    /// →App.OnShutdownRequestedという、×で閉じた場合と全く同じ経路を通る（課題1の修正が
    /// そのまま効く）。以前はここで直接desktop.Shutdown()を呼んでおり、Avalonia実装上
    /// force=trueとなってShutdownRequestedそのものが発火せず、後始末
    /// （DisposeAsync＝パッチキューの保存やトレイアイコンの破棄等）が一切行われないまま
    /// プロセスが終了してしまう不具合があった（本タスクの調査で判明）。
    /// </summary>
    private static void ForceExit(ShellWindow window)
    {
        window.IsForceClosing = true;
        window.Close();
    }

    private static void OnPasteHotkey(Window window, MainViewModel mainViewModel)
    {
        RestoreWindow(window);
        mainViewModel.PasteAndParseCommand.Execute(null);
    }

    /// <summary>
    /// Ctrl+Shift+C（4.8.4）。コマンドバーの「プロンプト」ボタンと同一の
    /// <see cref="MainViewModel.CopyPromptCommand"/> を実行し、コンテキスト収集（10章）と
    /// 同じ出力パイプラインで形式指示・前提・コードを展開してコピーする。
    /// </summary>
    private static void OnCopyPromptHotkey(MainViewModel mainViewModel)
    {
        if (mainViewModel.CopyPromptCommand.CanExecute(null))
        {
            mainViewModel.CopyPromptCommand.Execute(null);
        }
    }

    /// <summary>
    /// 9章: 反応時の挙動（トレイ通知のみ／非アクティブ表示／アクティブ表示）。
    /// 設定の挙動に加え、ステータスバーの通知（ShellViewModel.ClipboardWatch.cs）は
    /// 挙動の設定に関わらず常に立てる。トレイ通知はタスクトレイが使えない環境
    /// （D-Bus非対応・Wayland等）では見た目上の変化が起きず気付けないため、その保険となる。
    /// クリックするまではクリップボードを読み直さない（確認なしに解析・適用しない）。
    ///
    /// 機能追加: 「検知したら自動で解析する」（既定オン）。自動解析するかどうかの判断
    /// （設定オン、かつ未処理の解析結果・キューが残っていない）自体は
    /// <see cref="ShellViewModel.HandleClipboardPatchDetected"/>に集約している。
    ///
    /// 機能追加: 「検知したら前面に表示する」（既定オン、StartupCoordinator.
    /// ClipboardActivation.cs参照）。この設定がオンで前面化に成功した場合
    /// （<see cref="ClipboardActivationOutcome.Activated"/>）・既に前面にあった場合
    /// （<see cref="ClipboardActivationOutcome.AlreadyForeground"/>）は、自動解析の有無に
    /// 関わらず必ずここで前面化を完結させる（要件4: 検知したことを伝えるのが目的で、解析の
    /// 有無は別軸のため）。それ以外の場合（設定オフ＝<see cref="ClipboardActivationOutcome.
    /// Disabled"/>、またはOS側の制約で前面化が拒否された縮退＝<see cref="
    /// ClipboardActivationOutcome.Degraded"/>）は、自動解析の有無に関わらず「反応時の挙動」
    /// 設定（トレイ通知のみ／非アクティブ表示／アクティブ表示）へ厳密に従う。
    ///
    /// 不具合修正: 以前は「検知したら前面に表示する」がオフでも、自動解析した場合には
    /// 「反応時の挙動」設定を無視して無条件にウィンドウを前面化する特例があった
    /// （その場で解析した接ぎ木パネルの結果を見せる意図だったが、自動解析は既定オンのため、
    /// 実機では「検知したら前面に表示する」をオフにしていてもほぼ常にこの特例へ入ってしまい、
    /// 「反応時の挙動＝トレイ通知のみ」を選んでいるのにウィンドウが前面に出てしまう不具合に
    /// なっていた）。この特例は廃止した。「検知したら前面に表示する」がオフの間は、自動解析の
    /// 有無に関わらず常に「反応時の挙動」設定だけに従う。
    ///
    /// 回帰修正: Degradedのときも以前はここで早期returnしていたが、前面化が拒否されている
    /// 以上、利用者へ検知を伝える手段は上記の通知経路しか残っていない。「反応時の挙動＝
    /// トレイ通知のみ」を選んでいる利用者にとっては、前面化にも通知にも失敗する
    /// 「何も起きない」状態になってしまうため、Degradedのときも必ずこのフォールバックへ
    /// 合流させる（詳細はClipboardActivationOutcome.Degradedのコメント参照）。
    /// </summary>
    private void OnClipboardPatchDetected(Window window)
    {
        // 戻り値（自動解析が実際に行われたか）は、以前は前面化の特例判定に使っていたが
        // その特例は廃止した（このメソッドの不具合修正コメント参照）。自動解析そのものの
        // 実行（副作用）はこの呼び出しで行われるため、呼び出し自体は引き続き必要。
        _shellViewModel?.HandleClipboardPatchDetected(_settings.ClipboardWatch.AutoParse);

        var isAlreadyForeground = window.IsVisible && window.WindowState != WindowState.Minimized && window.IsActive;

        // 分岐（前面化できた／既に前面だった場合のみ通知経路を省略し、それ以外は必ず従来の
        // 通知経路へ合流させる）自体を含めてActivateOrFallBackOnPatchDetectedへ切り出している
        // （回帰修正: 詳細はClipboardActivationOutcome.Degradedのコメントおよび
        // ClipboardActivationTests.csのテスト参照）。
        var activation = ActivateOrFallBackOnPatchDetected(
            _platform.SingleInstance, _platform.Tray, window, MainWindowTitle,
            _settings.ClipboardWatch.ActivateOnDetect, isAlreadyForeground, _settings.ClipboardWatch.Action);

        if (activation == ClipboardActivationOutcome.Degraded)
        {
            // 要件6: OS側の制約（Windowsのフォーカス窃取防止等）による縮退はエラー扱いにせず、
            // ログにのみ記録する（利用者へダイアログ等は出さない）。通知経路への合流は上の
            // ActivateOrFallBackOnPatchDetected側で既に行っている。
            _logger?.Warn("clipboard",
                "クリップボード監視での前面化がOS側の制約により縮退しました（タスクバー通知等に切り替わった可能性があります）。" +
                "多重起動検出時の前面化と同じ挙動です。");
        }
    }
}
