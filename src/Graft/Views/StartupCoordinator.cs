using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform; using Graft.Platform.Windows;
using Graft.ViewModels;
using AppSettings = Graft.Infra.Settings;

namespace Graft.Views;

/// <summary>
/// 起動処理全体を統括する。DIコンテナは使わず（附録A.5）、依存の生成はすべてここで手動に行う
/// （附録A.3）。6.8 多重起動の防止、13.1/6.3/15章/4.10の起動時検証、MainWindow・トレイ・
/// グローバルホットキー・クリップボード監視の配線までを担う。
/// </summary>
public sealed partial class StartupCoordinator : IAsyncDisposable
{
    private const string MutexName = "Graft.SingleInstance.Mutex";
    private const string MainWindowTitle = "Graft";
    private const string PromptCopyHotkey = "Ctrl+Shift+C";

    private readonly AppPaths _appPaths = new();

    // UIフレームワーク固有の機能（クリップボード・画面情報・タイマー）。ViewModelへ手動で配る。
    private readonly IUiServices _ui = new WpfUiServices();

    private SingleInstanceGuard? _guard; private Logger? _logger;
    private SettingsStore? _settingsStore;
    private PatchQueue? _patchQueue;
    private ShellViewModel? _shellViewModel;
    private ClipboardWatcher? _clipboardWatcher;
    private HotkeyManager? _hotkeyManager;
    private TrayIconHost? _trayIcon;
    private AppSettings _settings = new();

    /// <summary>生成されたメインウィンドウ（仕様書9.2の新シェルレイアウト）。<see cref="StartAsync"/> 完了後に設定される。</summary>
    public ShellWindow? MainWindow { get; private set; }

    /// <summary>想定外の例外を記録するためのロガー。<see cref="StartAsync"/> 完了前は null。</summary>
    public Logger? Logger => _logger;

    /// <summary>
    /// 名前付きMutexで多重起動を判定する（6.8）。既に起動中の場合は既存ウィンドウを前面へ表示し
    /// falseを返す。呼び出し側（App.xaml.cs）はfalseの場合アプリを即座に終了させること。
    /// </summary>
    public bool TryAcquireSingleInstance()
    {
        _guard = SingleInstanceGuard.TryAcquire(MutexName);
        if (_guard is not null)
        {
            return true;
        }

        ActivateExistingInstance();
        return false;
    }

    /// <summary>依存の生成・MainWindowの表示・各種配線・初回起動ガイドまでを行う。</summary>
    public async Task StartAsync()
    {
        _appPaths.EnsureCoreDirectoriesExist();
        _logger = new Logger(_appPaths);

        // 設計目標5（製品相当の完成度）: UIハンドラ内の想定外の例外でアプリを終わらせない。
        // 記録したうえで日本語の通知だけ出し、操作を続けられるようにする。
        Graft.ViewModels.SafeHandler.OnUnexpected = (context, ex) =>
        {
            _logger?.Error("handler", $"{context}: {ex}");
            System.Windows.MessageBox.Show(
                $"{context}に失敗しました。" + Environment.NewLine + ex.Message,
                "Graft", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        };
        _settingsStore = new SettingsStore(_appPaths);
        var patchQueue = new PatchQueue(_appPaths);
        _patchQueue = patchQueue;

        var settingsResult = await _settingsStore.LoadAsync().ConfigureAwait(true);
        _settings = settingsResult.Value;

        var projectStore = new ProjectStore(_appPaths);
        var revisionStore = new RevisionStore(_appPaths);
        var revisionRestorer = new RevisionRestorer(_appPaths);
        var dialogService = new DialogService();
        var applyEngine = BuildApplyEngine(_appPaths, _settings);

        void OpenSettings()
        {
            var vm = new SettingsViewModel(_appPaths, dialogService, _ui);
            new SettingsWindow(vm) { Owner = MainWindow }.ShowDialog();
        }

        var mainViewModel = new MainViewModel(
            applyEngine, projectStore, revisionStore, revisionRestorer,
            _settingsStore, new WindowLayoutStore(_appPaths), dialogService, patchQueue, OpenSettings, _ui);
        var editorViewModel = new EditorPaneViewModel(_settings, dialogService, _ui);
        var shellViewModel = new ShellViewModel(mainViewModel, editorViewModel, dialogService, _settings, _ui);
        _shellViewModel = shellViewModel;

        var window = new ShellWindow(shellViewModel);
        MainWindow = window;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        var startupIssues = new List<GraftIssue>(settingsResult.Issues);
        _clipboardWatcher = new ClipboardWatcher(hwnd);
        _hotkeyManager = new HotkeyManager(hwnd);
        WireWindowMessaging(hwnd, window, mainViewModel, startupIssues);

        _trayIcon = new TrayIconHost(
            mainViewModel, _settingsStore, _clipboardWatcher, _settings,
            () => RestoreWindow(window), OpenSettings, () => Application.Current?.Shutdown());
        _trayIcon.Show();
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.Hide();
            }
        };

        window.Show();

        if (!OnboardingWindow.HasCompleted(_appPaths))
        {
            new OnboardingWindow { Owner = window }.ShowDialog();
        }

        _ = RunStartupValidationAsync(
            projectStore, revisionStore, dialogService, revisionRestorer, startupIssues, window.Dispatcher);
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
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetForegroundWindow(hwnd);
        }
    }

    private static void ActivateExistingInstance()
    {
        var hwnd = FindWindow(null, MainWindowTitle);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    // ------------------------------------------------------------------
    // ウィンドウメッセージ配線（9章 クリップボード監視・8.10 グローバルホットキー）
    // 8.11メモ: PerMonitorV2下でモニタ間移動時にDPIが変わると、AddHookで受け取る座標系は
    // 移動先モニタのDPIで解釈される。本クラスが扱うホットキー・クリップボード通知はいずれも
    // 座標を用いないため影響しないが、将来ここに座標依存の処理を足す場合は
    // WM_DPICHANGED（WPF側は SizeChanged 等に変換される）を考慮すること。
    // ------------------------------------------------------------------

    private void WireWindowMessaging(IntPtr hwnd, Window window, MainViewModel mainViewModel, List<GraftIssue> issues)
    {
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook((IntPtr _, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            var byClipboard = _clipboardWatcher!.HandleMessage(msg, w, l);
            var byHotkey = _hotkeyManager!.HandleMessage(msg, w, l);
            handled = byClipboard || byHotkey;
            return IntPtr.Zero;
        });

        if (_settings.ClipboardWatch.Enabled)
        {
            issues.AddRange(_clipboardWatcher!.Start().Issues);
        }
        _clipboardWatcher!.PatchDetected += (_, _) => OnClipboardPatchDetected(window);

        issues.AddRange(_hotkeyManager!.Register(_settings.Hotkey, () => OnPasteHotkey(window, mainViewModel)).Issues);
        issues.AddRange(_hotkeyManager!.Register(PromptCopyHotkey, () => OnCopyPromptHotkey(mainViewModel)).Issues);
    }

    private void OnPasteHotkey(Window window, MainViewModel mainViewModel)
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
                _trayIcon?.ShowBalloon("Graft", "パッチ形式のテキストを検知しました。");
                break;
        }
    }
}
