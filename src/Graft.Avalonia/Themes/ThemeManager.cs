using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Graft.Platform;
using Graft.Platform.Null;

namespace Graft.Themes;

/// <summary>
/// テーマ選択（9.3）。システム追従はライト/ダークどちらかへ解決される。
/// WPF版 <c>Themes/ThemeManager.cs</c> の <c>AppTheme</c> と同一の選択肢を持つ。
/// </summary>
public enum AppTheme
{
    Dark,
    Light,
    System,
}

/// <summary>
/// テーマ辞書の切り替えを一元管理する。WPF版 <c>Themes/ThemeManager.cs</c> の移植
/// （附録A・20章L2）。<see cref="Application.Resources"/> の MergedDictionaries内、
/// テーマ用の1枠（Dark.axaml / Light.axaml のいずれか）を差し替えることで即時反映し、
/// アプリの再起動を要求しない（9.3）。
///
/// 【WPF版からの構成差分】
/// - システムテーマの判定は、WPF版が行っていたレジストリの直接読み取り
///   （<c>Microsoft.Win32.SystemEvents</c> 経由）ではなく、仕様書2.4のとおり
///   <see cref="ISystemThemeWatcher"/> 抽象を経由する（OS固有APIを直接呼ばない）。
///   実装（<c>Platform/Windows</c>・<c>Platform/Linux</c>）はL4で追加され、それまでは
///   何もしない <see cref="NullSystemThemeWatcher"/> が使われる。判定できない場合は
///   ダークへフォールバックする（<see cref="ISystemThemeWatcher.TryReadIsLightTheme"/>
///   のドキュメントどおり）。
/// - ハイコントラストモードへのフォールバック（WPF版の<c>BuildHighContrastDictionary</c>）
///   は持たない。<see cref="ISystemThemeWatcher"/> にハイコントラストの概念がなく、
///   検出手段が確立していないため（L4以降、Windows/Linux実装が追加された時点で
///   必要なら拡張する）。
/// - テーマ切り替え時のクロスフェード演出（WPF版の<c>AnimateSwap</c>、
///   <c>RenderTargetBitmap</c>によるスナップショット合成）は持たない。即時反映という
///   要件（9.3・附録A）は満たすが、演出の移植はUIツリーが揃うフェーズL3以降で
///   検討する（本フェーズの担当はテーマ基盤そのものであり、Viewの装飾ではないため）。
/// - headless UIテスト（附録A.7）では1テストにつき新しい<see cref="Application"/>
///   インスタンスが生成される。監視の二重登録（<see cref="ISystemThemeWatcher"/>への
///   購読）は一度きりでよいが、テーマ辞書そのものは「今アクティブな
///   <see cref="Application.Current"/>」へ毎回適用し直す必要があるため、
///   両者のライフサイクルを分離している（<see cref="ApplyResolvedTheme"/>参照）。
/// </summary>
public static class ThemeManager
{
    private static ISystemThemeWatcher _themeWatcher = new NullSystemThemeWatcher();
    private static IResourceProvider? _currentThemeDictionary;
    private static Application? _lastAppliedApp;
    private static bool _watcherInitialized;
    private static bool _watcherAttached;
    private static AppTheme _selectedTheme = AppTheme.System;

    /// <summary>現在選択されているテーマ（解決前）。</summary>
    public static AppTheme SelectedTheme => _selectedTheme;

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

    private static void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        if (_selectedTheme != AppTheme.System)
        {
            // システム追従を選んでいない間はシステム設定の変化を無視する（WPF版と同じ方針）。
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyResolvedTheme();
        }
        else
        {
            Dispatcher.UIThread.Post(ApplyResolvedTheme);
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
