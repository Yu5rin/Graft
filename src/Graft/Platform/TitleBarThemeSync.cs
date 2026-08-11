using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Graft.Infra;
using Graft.Platform.Windows;
using Graft.Themes;

namespace Graft.Platform;

/// <summary>
/// ウィンドウのタイトルバー色をアプリのテーマ（ライト/ダーク）へ連動させる配線
/// （利用者からの要望）。実際のDWM呼び出し（Windows 11・<see cref="WindowsTitleBarTheme"/>）
/// とは責任を分け、ここは「いつ・どのウィンドウへ・どの色で」適用するかだけを扱う。
///
/// 【なぜ10個のウィンドウを個別に配線しないか】
/// <see cref="Window.WindowOpenedEvent"/>・<see cref="Window.WindowClosedEvent"/>を
/// <c>AddClassHandler&lt;Window&gt;</c>で型全体に対するクラスハンドラとして購読すると、
/// 以後に生成されるあらゆる<see cref="Window"/>派生（現在10種・将来増える分も含め）の
/// 開閉を1箇所で漏れなく拾える。Avalonia.Controls.dllの
/// <c>ClassicDesktopStyleApplicationLifetime.SubscribeGlobalEvents</c>が
/// <c>Window.WindowOpenedEvent.AddClassHandler(typeof(Window), ...)</c>・
/// <c>Window.WindowClosedEvent.AddClassHandler(typeof(Window), ...)</c>という
/// 全く同じ手口でアプリ全体のウィンドウ開閉を捕捉していることをilspycmdでの逆コンパイルで
/// 確認済み（開いているウィンドウの一覧・トレイ格納時の判定等に使っている）。本体側にも
/// <c>App.EnableCommandRequery</c>という前例（<c>AddClassHandler&lt;TopLevel&gt;</c>で
/// 入力イベントを1箇所で拾う）があり、同じ作法に揃えた。
///
/// 【開いているウィンドウの一覧を自前で持つ理由】
/// テーマ切替時に「今開いている全ウィンドウ」へ再適用する必要がある。当初は
/// <c>Application.Current.ApplicationLifetime</c>を<c>IClassicDesktopStyleApplicationLifetime</c>へ
/// キャストして<c>.Windows</c>を使う案も検討したが、tests/Graft.UiTests/
/// MainWindowShutdownModeGuardTests.csのコメントに詳しいとおり、headlessテスト環境では
/// <c>ApplicationLifetime</c>がこの型に初期化されない（setterも一度きりで差し替え不可）ため
/// 単体テストで検証できなくなる。<see cref="Window.WindowOpenedEvent"/>と対になる
/// <see cref="Window.WindowClosedEvent"/>も同じ手口でクラスハンドラ購読し、自前の集合
/// （<see cref="_openWindows"/>）で開閉を追跡する方が、本番・テストの両方で同じ経路を
/// 通り、ApplicationLifetimeの実装や初期化タイミングに依存しない。
///
/// 【テーマ切替時】
/// <see cref="ThemeManager.ThemeChanged"/>を購読し、発火時点で開いている全ウィンドウへ
/// 再適用する（設定画面での即時反映に対応するため）。
/// </summary>
internal static class TitleBarThemeSync
{
    private static bool _initialized;
    private static Logger? _logger;
    private static readonly HashSet<Window> _openWindows = new();

    // 実際のDWM呼び出し先。既定はWindows実機でのみ何かをする本実装
    // （WindowsTitleBarTheme.Apply、Windows以外・Windows10以下では即座に何もしない）。
    // UIテスト（Graft.UiTests）では「ウィンドウを開いたとき・テーマ切替時に適用処理が
    // 呼ばれる経路が繋がっていること」を検証したいだけで、実機のDWMを叩く必要は無いため、
    // ここを差し替えて呼び出し回数を数えられるようにしている（依頼書のテスト方針章）。
    internal static Action<Window, bool, Color, Color, Logger?> ApplyAction { get; set; } = DefaultApply;

    /// <summary>
    /// <see cref="ApplyAction"/>を既定（実際にDWMを呼ぶ経路）へ戻す。UIテストが
    /// <see cref="ApplyAction"/>を差し替えて呼び出し回数を数えたあと、他のテストへ
    /// 影響しないよう後始末で呼ぶ（<see cref="_initialized"/>と同じくプロセス全体で
    /// 共有される静的状態のため）。本体（App.axaml.cs）からは呼ばない。
    /// </summary>
    internal static void ResetApplyActionToDefault() => ApplyAction = DefaultApply;

    /// <summary>
    /// アプリ起動時に一度だけ呼び出す。二重に呼んでも安全（2回目以降は無視する）。
    /// headless UIテストでは1テストにつき新しい<see cref="Application"/>が生成されるが、
    /// <see cref="Window.WindowOpenedEvent"/>のクラスハンドラ購読はCLR上の
    /// <see cref="Avalonia.Interactivity.RoutedEvent"/>登録に対して行われ特定の
    /// <see cref="Application"/>インスタンスに紐づかないため、プロセス内で一度購読すれば
    /// 以降生成される全ウィンドウに対して有効であり続ける（<see cref="ThemeManager"/>の
    /// システムテーマ監視が一度きりの初期化で足りるのと同じ理由）。
    /// </summary>
    public static void Initialize(Logger? logger = null)
    {
        _logger = logger;
        if (_initialized) return;
        _initialized = true;

        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            _openWindows.Add(window);
            ApplyTo(window);
        });
        Window.WindowClosedEvent.AddClassHandler<Window>((window, _) => _openWindows.Remove(window));
        ThemeManager.ThemeChanged += (_, _) => ApplyToAllOpenWindows();
    }

    /// <summary>
    /// Loggerを後から差し替える。App起動シーケンス上、通常のLoggerが生成されるのは
    /// <see cref="Initialize"/>より後（<c>StartupCoordinator.StartAsync</c>内）のため、
    /// 生成され次第これで渡す（App.axaml.cs参照）。
    /// </summary>
    public static void SetLogger(Logger? logger) => _logger = logger;

    private static void ApplyToAllOpenWindows()
    {
        // ToArrayでコピーする: ApplyAction内でウィンドウが閉じる等してもコレクションの
        // 変更中列挙で壊れないようにするための保険。
        foreach (var window in _openWindows.ToArray())
        {
            ApplyTo(window);
        }
    }

    private static void ApplyTo(Window window)
    {
        var isDark = ThemeManager.IsDarkResolved;
        var captionColor = ResolveColor(window, "BgBaseColor");
        var textColor = ResolveColor(window, "TextPrimaryColor");

        if (captionColor is null || textColor is null)
        {
            // リソースが見つからない異常系。誤った色を決め打ちで塗るよりOS既定へ委ねる方が
            // 安全（WindowsTitleBarTheme.ResetToSystemDefaultのコメント参照）。
            if (OperatingSystem.IsWindows())
            {
                WindowsTitleBarTheme.ResetToSystemDefault(window, _logger);
            }
            return;
        }

        ApplyAction(window, isDark, captionColor.Value, textColor.Value, _logger);
    }

    private static Color? ResolveColor(Window window, string resourceKey)
        => window.TryFindResource(resourceKey, out var value) && value is Color color ? color : null;

    // 既定の適用処理。Windows以外・Windows 10以下ではWindowsTitleBarTheme側が即座に
    // 何もしないため、ここでもOperatingSystem.IsWindows()で先に打ち切り、Windows専用の型
    // （WindowsTitleBarTheme、[SupportedOSPlatform("windows")]）に他OSから触れないようにする
    // （Platform/PlatformServices.csのCreateWindows等、本リポジトリの既存の作法と同じ）。
    private static void DefaultApply(Window window, bool isDarkMode, Color captionColor, Color textColor, Logger? logger)
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsTitleBarTheme.Apply(window, isDarkMode, captionColor, textColor, logger);
    }
}
