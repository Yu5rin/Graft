using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.ViewModels;
using AppSettings = Graft.Infra.Settings;

namespace Graft.Views;

/// <summary>
/// 起動処理全体を統括する。DIコンテナは使わず（附録A.5）、依存の生成はすべてここで手動に行う
/// （附録A.3）。6.8 多重起動の防止、13.1/6.3/15章/4.10の起動時検証、MainWindow・トレイ・
/// グローバルホットキー・クリップボード監視の配線までを担う。
/// </summary>
public sealed class StartupCoordinator : IAsyncDisposable
{
    private const string MutexName = "Graft.SingleInstance.Mutex";
    private const string MainWindowTitle = "Graft";
    private const string PromptCopyHotkey = "Ctrl+Shift+C";

    private readonly AppPaths _appPaths = new();

    private SingleInstanceGuard? _guard;
    private Logger? _logger;
    private SettingsStore? _settingsStore;
    private PatchQueue? _patchQueue;
    private ClipboardWatcher? _clipboardWatcher;
    private HotkeyManager? _hotkeyManager;
    private TrayIconHost? _trayIcon;
    private AppSettings _settings = new();

    /// <summary>生成されたメインウィンドウ。<see cref="StartAsync"/> 完了後に設定される。</summary>
    public MainWindow? MainWindow { get; private set; }

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
            var vm = new SettingsViewModel(_appPaths, dialogService);
            new SettingsWindow(vm) { Owner = MainWindow }.ShowDialog();
        }

        var mainViewModel = new MainViewModel(
            applyEngine, projectStore, revisionStore, revisionRestorer,
            _settingsStore, new WindowLayoutStore(_appPaths), dialogService, patchQueue, OpenSettings);

        var window = new MainWindow(mainViewModel);
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

    // ------------------------------------------------------------------
    // 起動時検証（13.1/6.3/15章/4.10）。back/配下の走査を伴うため非同期・バックグラウンドで
    // 行い、UIの表示（1秒以内、17章）をブロックしない。結果が出てから通知する。
    // ------------------------------------------------------------------

    private async Task RunStartupValidationAsync(
        ProjectStore projectStore, RevisionStore revisionStore, DialogService dialogService,
        RevisionRestorer revisionRestorer, List<GraftIssue> issues, Dispatcher dispatcher)
    {
        var loaded = await projectStore.LoadAsync().ConfigureAwait(false);
        issues.AddRange(loaded.Issues);

        var validated = await projectStore.ValidateAsync(loaded.Value).ConfigureAwait(false);
        issues.AddRange(validated.Issues);

        var reconciled = await ReconcileRevisionsAsync(projectStore, revisionStore, validated.Value).ConfigureAwait(false);
        var inProgress = await CollectInProgressAsync(revisionStore, reconciled).ConfigureAwait(false);

        if (_logger is not null)
        {
            await _logger.CleanupOldLogsAsync().ConfigureAwait(false);
        }
        if (_patchQueue is not null)
        {
            issues.AddRange((await _patchQueue.LoadAsync().ConfigureAwait(false)).Issues);
        }

        var report = new StartupReport
        {
            Issues = issues,
            InProgressRevisions = inProgress,
            IsFirstLaunch = !OnboardingWindow.HasCompleted(_appPaths),
        };
        _logger?.Info("startup", "起動時検証を完了しました");

        await dispatcher.InvokeAsync(() => _ = PresentReportAsync(report, dialogService, revisionRestorer)).Task
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Project>> ReconcileRevisionsAsync(
        ProjectStore projectStore, RevisionStore revisionStore, IReadOnlyList<Project> projects)
    {
        var updated = new List<Project>(projects.Count);
        var changed = false;
        foreach (var project in projects)
        {
            var maxRevision = await revisionStore.DetectMaxRevisionAsync(project.Id).ConfigureAwait(false);
            var reconciled = maxRevision.IsSuccess ? ProjectStore.ReconcileRevision(project, maxRevision.Value) : project;
            changed |= reconciled.NextRevision != project.NextRevision;
            updated.Add(reconciled);
        }

        if (changed)
        {
            await projectStore.SaveAsync(updated).ConfigureAwait(false);
        }
        return updated;
    }

    private static async Task<IReadOnlyList<InProgressRevisionIssue>> CollectInProgressAsync(
        RevisionStore revisionStore, IReadOnlyList<Project> projects)
    {
        var result = new List<InProgressRevisionIssue>();
        foreach (var project in projects.Where(p => !p.IsDisconnected))
        {
            var found = await revisionStore.FindInProgressAsync(project.Id).ConfigureAwait(false);
            if (found.IsSuccess && found.Value.Count > 0)
            {
                result.Add(new InProgressRevisionIssue
                {
                    ProjectId = project.Id,
                    ProjectName = project.Name,
                    ProjectRoot = project.Root,
                    Revisions = found.Value,
                });
            }
        }
        return result;
    }

    // ------------------------------------------------------------------
    // 検証結果の通知（UIスレッド上で実行）
    // ------------------------------------------------------------------

    private async Task PresentReportAsync(StartupReport report, DialogService dialogService, RevisionRestorer revisionRestorer)
    {
        var summary = report.BuildIssuesSummaryText();
        if (!string.IsNullOrEmpty(summary))
        {
            await dialogService.ShowMessageAsync("起動時の確認事項", summary).ConfigureAwait(true);
        }

        foreach (var issue in report.InProgressRevisions)
        {
            await OfferRollbackAsync(issue, dialogService, revisionRestorer).ConfigureAwait(true);
        }
    }

    /// <summary>6.3/E403: 中途半端な適用状態を通知し、承諾された場合のみロールバックを実行する。</summary>
    private async Task OfferRollbackAsync(
        InProgressRevisionIssue issue, DialogService dialogService, RevisionRestorer revisionRestorer)
    {
        var confirmed = await dialogService
            .ConfirmAsync("未完了の適用を検出しました", StartupReport.BuildRollbackPrompt(issue))
            .ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        foreach (var revision in issue.Revisions)
        {
            var restored = await revisionRestorer
                .RestoreAsync(issue.ProjectId, issue.ProjectRoot, revision, force: true)
                .ConfigureAwait(true);
            if (restored.IsSuccess)
            {
                await MarkRolledBackAsync(revision).ConfigureAwait(true);
            }
        }
    }

    /// <summary>ロールバック後、manifest.jsonのstatusを更新し次回起動時に再度提案されないようにする。</summary>
    private static async Task MarkRolledBackAsync(RevisionSummary revision)
    {
        if (!revision.IsRestorable)
        {
            return;
        }
        var manifestPath = Path.Combine(revision.FolderPath, "manifest.json");
        var rolledBack = revision.Manifest with { Status = RevisionStatus.RolledBack };
        await new JsonFileStore().WriteAsync(manifestPath, rolledBack, JsonFileStore.DefaultOptions).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // 終了処理（4.10 パッチキューの保存を含む）
    // ------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        if (_patchQueue is not null)
        {
            await _patchQueue.SaveAsync().ConfigureAwait(true);
        }
        _hotkeyManager?.Dispose();
        _clipboardWatcher?.Dispose();
        _trayIcon?.Dispose();
        _guard?.Dispose();
        if (_logger is not null)
        {
            await _logger.DisposeAsync().ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------
    // Win32 P/Invoke（6.8 多重起動防止: 既存ウィンドウの前面表示）
    // ------------------------------------------------------------------

    private const int SwRestore = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
