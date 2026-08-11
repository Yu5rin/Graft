using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Graft.Platform;
using Graft.Platform.Null;

namespace Graft.Themes;

/// <summary>
/// テーマ選択（9.3）。システム追従はライト/ダークどちらかへ解決される。
/// v2.0のWPF版の<c>Themes/ThemeManager.cs</c> の <c>AppTheme</c> と同一の選択肢を持つ。
/// </summary>
public enum AppTheme
{
    Dark,
    Light,
    System,
}

/// <summary>
/// テーマ辞書の切り替えを一元管理する。v2.0のWPF版の<c>Themes/ThemeManager.cs</c> の移植
/// （附録A・20章L2）。<see cref="Application.Resources"/> の MergedDictionaries内、
/// テーマ用の1枠（Dark.axaml / Light.axaml のいずれか）を差し替えることで即時反映し、
/// アプリの再起動を要求しない（9.3）。
///
/// 【v2.0のWPF版からの構成差分】
/// - システムテーマの判定は、v2.0のWPF版が行っていたレジストリの直接読み取り
///   （<c>Microsoft.Win32.SystemEvents</c> 経由）ではなく、仕様書2.4のとおり
///   <see cref="ISystemThemeWatcher"/> 抽象を経由する（OS固有APIを直接呼ばない）。
///   実装（<c>Platform/Windows</c>・<c>Platform/Linux</c>）はL4で追加され、それまでは
///   何もしない <see cref="NullSystemThemeWatcher"/> が使われる。判定できない場合は
///   ダークへフォールバックする（<see cref="ISystemThemeWatcher.TryReadIsLightTheme"/>
///   のドキュメントどおり）。
/// - ハイコントラストモードへのフォールバック（v2.0のWPF版の<c>BuildHighContrastDictionary</c>）
///   は持たない。<see cref="ISystemThemeWatcher"/> にハイコントラストの概念がなく、
///   検出手段が確立していないため（L4以降、Windows/Linux実装が追加された時点で
///   必要なら拡張する）。
/// - テーマ切り替え時のクロスフェード演出（v2.0のWPF版の<c>AnimateSwap</c>、
///   <c>RenderTargetBitmap</c>によるスナップショット合成）は持たない。即時反映という
///   要件（9.3・附録A）は満たすが、演出の移植はUIツリーが揃うフェーズL3以降で
///   検討する（本フェーズの担当はテーマ基盤そのものであり、Viewの装飾ではないため）。
/// - headless UIテスト（附録A.7）では1テストにつき新しい<see cref="Application"/>
///   インスタンスが生成される。監視の二重登録（<see cref="ISystemThemeWatcher"/>への
///   購読）は一度きりでよいが、テーマ辞書そのものは「今アクティブな
///   <see cref="Application.Current"/>」へ毎回適用し直す必要があるため、
///   両者のライフサイクルを分離している（<see cref="ApplyResolvedTheme"/>参照）。
/// - タイトルバー色連動（利用者からの要望）を追加した際、<see cref="Application.RequestedThemeVariant"/>
///   を<c>Default</c>のまま放置せずここで明示的に追従させるようにした（<see cref="ApplyResolvedTheme"/>
///   内のコメント参照）。Avalonia自身がWindows 11でDWMWA_USE_IMMERSIVE_DARK_MODEを
///   自動設定する既定動作と、<see cref="Graft.Platform.Windows.WindowsTitleBarTheme"/>側の
///   明示設定が競合しないようにするため。
/// </summary>
public static class ThemeManager
{
    private static ISystemThemeWatcher _themeWatcher = new NullSystemThemeWatcher();
    private static IResourceProvider? _currentThemeDictionary;
    private static Application? _lastAppliedApp;
    private static bool _watcherInitialized;
    private static bool _watcherAttached;
    private static AppTheme _selectedTheme = AppTheme.System;

    // OnSystemThemeChangedはISystemThemeWatcher.Changed経由でOS側の監視スレッド
    // （LinuxSystemThemeWatcherならgdbus監視プロセスの出力読み取りスレッド）から呼ばれる。
    // Dispatcher.UIThreadは遅延生成・スレッド非安全な静的プロパティのため、そこを別スレッドから
    // 直接読むと、headlessテストのテスト間リセットの窓と重なった場合に壊れたインスタンスが
    // キャッシュされてしまう（DocumentSessionクラス冒頭のコメント・CI調査結果を参照）。
    // 確実にUIスレッドで呼ばれる<see cref="Initialize"/>でのみ一度捕捉し、
    // <see cref="OnSystemThemeChanged"/>からはこのフィールド越しに使う。
    private static Dispatcher? _uiDispatcher;

    /// <summary>現在選択されているテーマ（解決前）。</summary>
    public static AppTheme SelectedTheme => _selectedTheme;

    /// <summary>
    /// <see cref="AppTheme.System"/>を含め、実際に適用されている側（true=ダーク）。
    /// タイトルバー配色（<see cref="Graft.Platform.TitleBarThemeSync"/>）等、
    /// 「今どちらの見た目になっているか」だけを必要とする呼び出し側向けに公開する
    /// （<see cref="ResolveIsDark"/>の判定ロジックを二重に持たせないため）。
    /// </summary>
    public static bool IsDarkResolved { get; private set; } = true;

    /// <summary>テーマ辞書が差し替わるたびに発火する。</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// アプリ起動時に呼び出す。システム設定の監視開始は初回のみ行い、テーマ辞書の適用は
    /// 呼び出しのたびに「今の <see cref="Application.Current"/>」へ対して行う
    /// （<see cref="Application"/> が複数生成されるheadlessテストに対応するため）。
    /// <paramref name="themeWatcher"/> を省略した場合は何もしない実装が使われ、
    /// <see cref="AppTheme.System"/> は常にダークへ解決される。
    /// </summary>
    public static void Initialize(ISystemThemeWatcher? themeWatcher = null, AppTheme initialTheme = AppTheme.System)
    {
        if (!_watcherInitialized)
        {
            _watcherInitialized = true;
            _themeWatcher = themeWatcher ?? new NullSystemThemeWatcher();
            _selectedTheme = initialTheme;
            // Initializeは必ずUIスレッドから呼ばれる（App.axaml.cs参照）ため、
            // ここで一度だけ捕捉する（フィールドのコメント参照）。
            _uiDispatcher = Dispatcher.UIThread;

            if (_themeWatcher.IsSupported)
            {
                _themeWatcher.Changed += OnSystemThemeChanged;
                _themeWatcher.StartWatching();
                _watcherAttached = true;
            }
        }

        ApplyResolvedTheme();
    }

    /// <summary>監視を停止する。アプリ終了時に呼び出すことを想定する。</summary>
    public static void Shutdown()
    {
        if (!_watcherInitialized)
        {
            return;
        }

        if (_watcherAttached)
        {
            _themeWatcher.Changed -= OnSystemThemeChanged;
            _themeWatcher.StopWatching();
            _watcherAttached = false;
        }

        _watcherInitialized = false;
    }

    /// <summary>テーマを変更し、即時に反映する。</summary>
    public static void SetTheme(AppTheme theme)
    {
        _selectedTheme = theme;
        ApplyResolvedTheme();
    }

    /// <summary>
    /// settings.json の <c>theme</c>（"dark" / "light" / "system"）を選択肢へ読み替える。
    /// 起動時の反映と設定画面の双方から使い、対応表が二重に存在しないようにする。
    /// 未知の値はシステム追従として扱う。
    /// </summary>
    public static AppTheme ParseTheme(string? value) => value switch
    {
        "dark" => AppTheme.Dark,
        "light" => AppTheme.Light,
        _ => AppTheme.System,
    };

    private static void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        if (_selectedTheme != AppTheme.System)
        {
            // システム追従を選んでいない間はシステム設定の変化を無視する（v2.0のWPF版と同じ方針）。
            return;
        }

        // _uiDispatcherはInitializeで必ず先に捕捉済み（このハンドラは_watcherAttachedが
        // trueのとき、つまりInitializeが実行済みのときしか購読されない）。
        var ui = _uiDispatcher;
        if (ui is null || ui.CheckAccess())
        {
            ApplyResolvedTheme();
        }
        else
        {
            ui.Post(ApplyResolvedTheme);
        }
    }

    private static void ApplyResolvedTheme()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        if (!ReferenceEquals(app, _lastAppliedApp))
        {
            // Applicationインスタンスが前回と異なる（headlessテストでは1テストにつき
            // 新規生成される）。古い参照は別インスタンスのMergedDictionariesを
            // 指しているため、そのまま使うと何も削除できない。ここで破棄しておく。
            _currentThemeDictionary = null;
            _lastAppliedApp = app;
        }

        var isDark = ResolveIsDark(_selectedTheme);
        IsDarkResolved = isDark;
        var fileName = isDark ? "Dark.axaml" : "Light.axaml";
        var uri = new Uri($"avares://Graft/Themes/{fileName}");
        var newDictionary = new ResourceInclude(uri) { Source = uri };

        var dictionaries = app.Resources.MergedDictionaries;
        if (_currentThemeDictionary is not null)
        {
            dictionaries.Remove(_currentThemeDictionary);
        }

        dictionaries.Add(newDictionary);
        _currentThemeDictionary = newDictionary;

        // タイトルバー連動（Platform/TitleBarThemeSync.cs）の調査で判明した点への対応:
        // Avalonia.Win32.dll（WindowImpl.SetFrameThemeVariant）は、TopLevel.ActualThemeVariant
        // の変化に連動してDWMWA_USE_IMMERSIVE_DARK_MODEを自動設定済みだった（ilspycmdでの
        // 逆コンパイルで確認）。App.axamlのRequestedThemeVariant="Default"のままだと、
        // ActualThemeVariantはGraftが選んだテーマではなくOS実機の「実際の」ライト/ダーク設定へ
        // 追従してしまい、Graftアプリ内のテーマ選択と食い違う余地があった（例:
        // OSはダーク・Graftはライトを選択、のケース）。ここでApplication.RequestedThemeVariant
        // をGraftが解決した実際のテーマへ毎回合わせることで、Avalonia自身の自動設定と
        // WindowsTitleBarTheme側の明示設定が常に同じ値になり、競合を断つ。
        app.RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// <see cref="AppTheme.System"/> の解決。<see cref="ISystemThemeWatcher"/> が利用不可、
    /// または判定できない場合はダークへフォールバックする（仕様書2.3・
    /// <see cref="ISystemThemeWatcher.TryReadIsLightTheme"/> の既定方針）。
    /// </summary>
    private static bool ResolveIsDark(AppTheme requested) => requested switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => _themeWatcher.TryReadIsLightTheme() != true,
    };
}
