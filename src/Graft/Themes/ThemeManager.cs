using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Graft.Themes;

/// <summary>
/// テーマ選択（8.3）。システム追従はライト/ダークどちらかへ解決される。
/// </summary>
public enum AppTheme
{
    Dark,
    Light,
    System,
}

/// <summary>
/// テーマ辞書の切り替えを一元管理する。<see cref="Application.Resources"/> の
/// MergedDictionaries内、テーマ用の1枠を差し替えることで即時反映し、
/// アプリの再起動を要求しない（8.3）。
/// </summary>
public static class ThemeManager
{
    // MergedDictionaries内のどれが「テーマ用の枠」かを型やSourceに依存せず
    // 追跡するための目印。ハイコントラスト用辞書はコードで生成しSourceを
    // 持たないため、Sourceパスでの判定ではなくこのマーカーキーで統一する。
    private static readonly object ThemeSlotMarker = new();

    private static bool _initialized;
    private static AppTheme _selectedTheme = AppTheme.System;

    /// <summary>現在選択されているテーマ（解決前）。</summary>
    public static AppTheme SelectedTheme => _selectedTheme;

    /// <summary>テーマ辞書が差し替わるたびに発火する。</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// Windowsの「アニメーションを表示する」設定（8.9）。オフの場合はクロスフェードを
    /// 含む全てのモーションを無効化する。
    /// </summary>
    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    /// <summary>Windowsのハイコントラストモードが有効かどうか（8.3）。</summary>
    public static bool IsHighContrastActive => SystemParameters.HighContrast;

    /// <summary>
    /// アプリ起動時に1度だけ呼び出す。システム設定変更の監視を開始し、
    /// 初期テーマを適用する。
    /// </summary>
    public static void Initialize(AppTheme initialTheme = AppTheme.System)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SetTheme(initialTheme);
    }

    /// <summary>監視を停止する。アプリ終了時に呼び出すことを想定する。</summary>
    public static void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _initialized = false;
    }

    /// <summary>テーマを変更し、即時に反映する。</summary>
    public static void SetTheme(AppTheme theme)
    {
        _selectedTheme = theme;
        ApplyResolvedTheme(animate: _initialized);
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // ハイコントラストの切り替えやアニメーション設定の変更はGeneral/Color/
        // Accessibilityカテゴリで通知される。関係のない変更まで毎回再評価しない。
        if (e.Category is UserPreferenceCategory.General
            or UserPreferenceCategory.Color
            or UserPreferenceCategory.Accessibility)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => ApplyResolvedTheme(animate: true)));
        }
    }

    private static void ApplyResolvedTheme(bool animate)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var newDictionary = BuildDictionary(ResolveKind(_selectedTheme));
        newDictionary[ThemeSlotMarker] = true;

        var dictionaries = app.Resources.MergedDictionaries;
        var existingIndex = FindThemeDictionaryIndex(dictionaries);

        if (existingIndex < 0)
        {
            dictionaries.Add(newDictionary);
        }
        else if (animate && AnimationsEnabled)
        {
            AnimateSwap(dictionaries, existingIndex, newDictionary);
        }
        else
        {
            dictionaries[existingIndex] = newDictionary;
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private enum ResolvedThemeKind
    {
        Dark,
        Light,
        HighContrast,
    }

    private static ResolvedThemeKind ResolveKind(AppTheme requested)
    {
        if (IsHighContrastActive)
        {
            return ResolvedThemeKind.HighContrast;
        }

        return requested switch
        {
            AppTheme.Dark => ResolvedThemeKind.Dark,
            AppTheme.Light => ResolvedThemeKind.Light,
            // AppTheme.System: WPFの SystemParameters にはWindows 10/11の
            // 「アプリのモード」（ライト/ダーク）を判定するAPIが存在しない。
            // 値はレジストリ（HKCU\...\Personalize\AppsUseLightTheme）にしか
            // 存在せず、附録A.5によりレジストリ読み取りは避ける方針のため、
            // 判定不能として既定のダークへフォールバックする。
            _ => ResolvedThemeKind.Dark,
        };
    }

    private static ResourceDictionary BuildDictionary(ResolvedThemeKind kind)
    {
        if (kind == ResolvedThemeKind.HighContrast)
        {
            return BuildHighContrastDictionary();
        }

        var fileName = kind == ResolvedThemeKind.Dark ? "Dark.xaml" : "Light.xaml";
        return new ResourceDictionary { Source = new Uri($"Themes/{fileName}", UriKind.Relative) };
    }

    /// <summary>
    /// 8.3: ハイコントラストモード検出時はシステムカラーへフォールバックする。
    /// レジストリは読まず、SystemColorsが提供する静的ブラシのみを使う。
    /// 状態別の色（追加/削除/警告/失敗）はハイコントラストの限られた配色に
    /// 収束するため区別が弱まるが、8.1の設計原則どおり形状（アイコン・バー）で
    /// 状態を判別できることを前提に許容する。
    /// </summary>
    private static ResourceDictionary BuildHighContrastDictionary()
    {
        var d = new ResourceDictionary();

        SetBrushAndColor(d, "BgBase", SystemColors.WindowBrush);
        SetBrushAndColor(d, "BgSurface", SystemColors.WindowBrush);
        SetBrushAndColor(d, "BgElevated", SystemColors.WindowBrush);
        SetBrushAndColor(d, "BgHover", SystemColors.HighlightBrush);
        SetBrushAndColor(d, "BgSelected", SystemColors.HighlightBrush);
        SetBrushAndColor(d, "TextPrimary", SystemColors.WindowTextBrush);
        SetBrushAndColor(d, "TextSecondary", SystemColors.GrayTextBrush);
        SetBrushAndColor(d, "TextDisabled", SystemColors.GrayTextBrush);
        SetBrushAndColor(d, "Accent", SystemColors.HighlightBrush);
        SetBrushAndColor(d, "BorderSubtle", SystemColors.ActiveBorderBrush);
        SetBrushAndColor(d, "DiffAddBg", SystemColors.WindowBrush);
        SetBrushAndColor(d, "DiffAddBar", SystemColors.HighlightBrush);
        SetBrushAndColor(d, "DiffDelBg", SystemColors.WindowBrush);
        SetBrushAndColor(d, "DiffDelBar", SystemColors.HotTrackBrush);
        SetBrushAndColor(d, "StateWarn", SystemColors.HotTrackBrush);
        SetBrushAndColor(d, "StateError", SystemColors.WindowTextBrush);
        SetBrushAndColor(d, "StateOk", SystemColors.HighlightBrush);

        SetBrushAndColor(d, "SyntaxKeyword", SystemColors.WindowTextBrush);
        SetBrushAndColor(d, "SyntaxString", SystemColors.WindowTextBrush);
        SetBrushAndColor(d, "SyntaxNumber", SystemColors.WindowTextBrush);
        SetBrushAndColor(d, "SyntaxComment", SystemColors.GrayTextBrush);
        SetBrushAndColor(d, "SyntaxFunction", SystemColors.WindowTextBrush);
        SetBrushAndColor(d, "SyntaxType", SystemColors.WindowTextBrush);
        SetBrushAndColor(d, "SyntaxOperator", SystemColors.WindowTextBrush);
        SetBrushAndColor(d, "SyntaxPlain", SystemColors.WindowTextBrush);

        return d;
    }

    private static void SetBrushAndColor(ResourceDictionary d, string key, SolidColorBrush brush)
    {
        d[key] = brush;
        d[key + "Color"] = brush.Color;
    }

    private static int FindThemeDictionaryIndex(Collection<ResourceDictionary> dictionaries)
    {
        for (var i = 0; i < dictionaries.Count; i++)
        {
            if (dictionaries[i].Contains(ThemeSlotMarker))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 8.9: テーマ切り替えのクロスフェード（120ms / ease-out）。
    /// 切り替え前の見た目をビットマップとして捕捉し、新テーマ適用後に
    /// 重ねてフェードアウトさせることでクロスフェードに見せる。
    /// メインウィンドウが未生成・非表示の場合は即時切り替えにフォールバックする。
    /// </summary>
    private static void AnimateSwap(
        Collection<ResourceDictionary> dictionaries, int index, ResourceDictionary newDictionary)
    {
        var window = Application.Current?.MainWindow;
        var root = window?.Content as UIElement;

        if (window is null || root is null || !window.IsVisible
            || root.RenderSize.Width <= 0 || root.RenderSize.Height <= 0)
        {
            dictionaries[index] = newDictionary;
            return;
        }

        var layer = AdornerLayer.GetAdornerLayer(root);
        if (layer is null)
        {
            dictionaries[index] = newDictionary;
            return;
        }

        var snapshot = new RenderTargetBitmap(
            (int)Math.Ceiling(root.RenderSize.Width),
            (int)Math.Ceiling(root.RenderSize.Height),
            96, 96, PixelFormats.Pbgra32);
        snapshot.Render(root);
        snapshot.Freeze();

        dictionaries[index] = newDictionary;

        var adorner = new ThemeSnapshotAdorner(root, snapshot);
        layer.Add(adorner);

        var duration = window.TryFindResource("MotionDuration") is Duration d ? d : new Duration(TimeSpan.FromMilliseconds(120));
        var fadeOut = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = duration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        fadeOut.Completed += (_, _) => layer.Remove(adorner);
        adorner.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    /// <summary>旧テーマの見た目のスナップショットを表示するだけの読み取り専用アドーナ。</summary>
    private sealed class ThemeSnapshotAdorner : Adorner
    {
        private readonly ImageSource _snapshot;

        public ThemeSnapshotAdorner(UIElement adornedElement, ImageSource snapshot)
            : base(adornedElement)
        {
            _snapshot = snapshot;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            drawingContext.DrawImage(_snapshot, new Rect(0, 0, AdornedElement.RenderSize.Width, AdornedElement.RenderSize.Height));
        }
    }
}
