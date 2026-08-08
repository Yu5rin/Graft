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
    private const string MutexName = "Graft.SingleInstance.Mutex";
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

    /// <param name="baseDirectory">
    /// settings.json 等の基準ディレクトリ。省略時は実行ファイルの場所を使う。
    /// テストから一時ディレクトリを渡して、利用者の設定を汚さずに起動処理を検証できるようにする。
    /// </param>
    public StartupCoordinator(string? baseDirectory = null)
    {
        _appPaths = new AppPaths(baseDirectory);
    }

    /// <summary>生成されたメインウィンドウ（仕様書9.2の新シェルレイアウト）。<see cref="StartAsync"/> 完了後に設定される。</summary>
    public ShellWindow? MainWindow { get; private set; }

    /// <summary>想定外の例外を記録するためのロガー。<see cref="StartAsync"/> 完了前は null。</summary>
    public Logger? Logger => _logger;

    /// <summary>
    /// 多重起動を判定する（6.8）。既に起動中の場合は既存ウィンドウを前面へ表示しfalseを返す。
    /// 呼び出し側はfalseの場合アプリを即座に終了させること。
    /// </summary>
    public bool TryAcquireSingleInstance()
    {
        if (_platform.SingleInstance.TryAcquire(MutexName)) return true;

        _platform.SingleInstance.ActivateExistingInstance(MainWindowTitle);
        return false;
    }

    /// <summary>依存の生成・メインウィンドウの表示・各種配線・初回起動ガイドまでを行う。</summary>
    public async Task StartAsync()
    {
        // 18章「起動から操作可能まで1秒以内」を実機で確認できるよう、ウィンドウが
        // 表示されるまでの所要時間を記録する。プロセス開始からの計測とするため、
        // 起点は現在のプロセスの開始時刻を使う。
        var startedAt = Process.GetCurrentProcess().StartTime;

        _appPaths.EnsureCoreDirectoriesExist();
        _logger = new Logger(_appPaths);
        _logger.Info("startup", _platform.DescribeEnvironment());

        var dialogService = new AvaloniaDialogService();

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

        // 9.3: 保存しておいたテーマを反映する。App起動時点では設定をまだ読めていないため、
        // ここで当て直さないと、選んだテーマが再起動のたびにシステム追従へ戻ってしまう。
        Themes.ThemeManager.SetTheme(Themes.ThemeManager.ParseTheme(_settings.Theme));

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
        var revisionStore = new RevisionStore(_appPaths);
        var revisionRestorer = new RevisionRestorer(_appPaths);

        void OpenSettings()
        {
            // 課題2: 「閉じたときの動作」は即時反映のため、設定画面での変更を
            // 実行中のShellWindowへその場で反映するコールバックを渡す。
            var vm = new SettingsViewModel(_appPaths, dialogService, _ui, ApplyLiveSettingsChange);
            var window = new SettingsWindow(vm);
            if (MainWindow is not null) _ = window.ShowDialog(MainWindow);
            else window.Show();
        }

        var shellViewModel = BuildShellViewModel(
            _appPaths, _settings, _settingsStore, patchQueue, projectStore, revisionStore, revisionRestorer,
            dialogService, _ui, OpenSettings);
        var mainViewModel = shellViewModel.Graft;
        _shellViewModel = shellViewModel;

        var window = new ShellWindow(shellViewModel);
        MainWindow = window;

        // 課題2: 「閉じたときの動作」設定と、トレイの実際の利用可否をウィンドウへ渡す。
        // IsTraySupportedはプロセス起動中に変わらないため、ここで一度だけ設定する。
        window.CloseBehavior = _settings.CloseBehavior;
        window.IsTraySupported = _platform.Tray.IsSupported;

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

        // 9章: 最小化でトレイへ格納する（トレイが使えない環境では通常の最小化のまま）。
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty) return;
            if (_platform.Tray.IsSupported && window.WindowState == WindowState.Minimized) window.Hide();
        };

        window.Show();
        _logger.Info("startup",
            $"操作可能まで {(int)(DateTime.Now - startedAt).TotalMilliseconds} ms");

        if (!OnboardingWindow.HasCompleted(_appPaths))
        {
            // シェルの左ペイン・上部ドロップダウンが参照しているProjectPaneViewModelと同じ
            // インスタンスを渡す（バグ修正: チュートリアルで登録したプロジェクトが一覧に
            // 反映されない不具合。詳細はOnboardingWindowのコンストラクタのコメントを参照）。
            await new OnboardingWindow(_appPaths, mainViewModel.ProjectPane).ShowDialog(window).ConfigureAwait(true);
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
    public static ShellViewModel BuildShellViewModel(
        AppPaths appPaths, AppSettings settings, SettingsStore settingsStore, PatchQueue patchQueue,
        ProjectStore projectStore, RevisionStore revisionStore, RevisionRestorer revisionRestorer,
        IDialogService dialogService, IUiServices ui, Action openSettings)
    {
        var applyEngine = BuildApplyEngine(appPaths, settings);
        var mainViewModel = new MainViewModel(
            applyEngine, projectStore, revisionStore, revisionRestorer,
            settingsStore, new WindowLayoutStore(appPaths), dialogService, patchQueue, openSettings, ui);
        var editorViewModel = new EditorPaneViewModel(settings, dialogService, ui);
        return new ShellViewModel(mainViewModel, editorViewModel, dialogService, settings, ui);
    }

    private static ApplyEngine BuildApplyEngine(AppPaths appPaths, AppSettings settings)
    {
        var matchEngine = new MatchEngine(new MatchOptions
        {
            SimilarityThreshold = settings.Matching.SimilarityThreshold,
            AllowSimilarityMatch = settings.Matching.AllowSimilarityMatch,
            RangeWarningLines = settings.Matching.RangeWarningLines,
        });
        return new ApplyEngine(new BackupManager(appPaths), new RevisionStore(appPaths), matchEngine);
    }

    private static void RestoreWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    // ------------------------------------------------------------------
    // OS固有機能の配線（9章 クリップボード監視・8.10 グローバルホットキー・トレイ常駐）
    // ------------------------------------------------------------------

    private void WirePlatformServices(ShellWindow window, MainViewModel mainViewModel, List<GraftIssue> issues)
    {
        // クリップボード監視とホットキーが受信するウィンドウハンドルの割り当ては
        // WindowMessageBridge が行う（Windowsは専用のメッセージ受信ウィンドウ、
        // Linuxはハンドルを使わない実装）。
        _messageBridge = WindowMessageBridge.Attach(_platform);

        if (_settings.ClipboardWatch.Enabled)
        {
            issues.AddRange(_platform.Clipboard.Start().Issues);
        }
        _platform.Clipboard.PatchDetected += (_, _) => OnClipboardPatchDetected(window);

        issues.AddRange(_platform.Hotkeys
            .Register(_settings.Hotkey, () => OnPasteHotkey(window, mainViewModel)).Issues);
        issues.AddRange(_platform.Hotkeys
            .Register(PromptCopyHotkey, () => OnCopyPromptHotkey(mainViewModel)).Issues);

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

    /// <summary>トレイメニューからのクリップボード監視ON/OFF。設定にも反映して次回起動へ引き継ぐ。</summary>
    private void SetClipboardWatchEnabled(bool enabled)
    {
        if (enabled) _platform.Clipboard.Start();
        else _platform.Clipboard.Stop();

        _settings = _settings with { ClipboardWatch = _settings.ClipboardWatch with { Enabled = enabled } };
        if (_settingsStore is not null) _ = _settingsStore.SaveAsync(_settings);
    }

    /// <summary>
    /// 課題2: 設定画面での「閉じたときの動作」変更を、実行中のウィンドウへ即時反映する。
    /// SettingsViewModelの即時反映（300msデバウンス後の保存）が成功した直後に呼ばれる。
    /// </summary>
    private void ApplyLiveSettingsChange(AppSettings updated)
    {
        _settings = updated;
        if (MainWindow is not null) MainWindow.CloseBehavior = updated.CloseBehavior;
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

    /// <summary>9章: 反応時の挙動（トレイ通知のみ／非アクティブ表示／アクティブ表示）。</summary>
    private void OnClipboardPatchDetected(Window window)
    {
        switch (_settings.ClipboardWatch.Action)
        {
            case "active":
                RestoreWindow(window);
                break;
            case "passive":
                window.Show();
                break;
            default:
                _platform.Tray.ShowBalloon("Graft", "パッチ形式のテキストを検知しました。");
                break;
        }
    }
}
