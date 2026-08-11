using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Media;
using Graft.Infra;

namespace Graft.Platform.Windows;

/// <summary>
/// タイトルバーの背景色・文字色・ダークモードフラグをDWM（Desktop Window Manager）経由で
/// ウィンドウへ適用する、1行で呼べる静的ヘルパー（利用者からの要望）。
///
/// 呼び出し方: <c>WindowsTitleBarTheme.Apply(window, isDarkMode, captionColor, textColor)</c>。
/// 色は決め打ちにせず引数で受け取る（依頼書3章）。どのリソースキー（<c>BgBaseColor</c>・
/// <c>TextPrimaryColor</c>、将来のアクセントカラー等）を渡すかは呼び出し側
/// （<see cref="TitleBarThemeSync"/>）が決める。ここではDWM呼び出しの正しさ（バイト順・
/// 対応バージョン判定・例外の握りつぶし）だけに責任を持つ。
///
/// 【Windows 11未満・Linuxで何もしない理由】(依頼書4章)
/// DWMWA_CAPTION_COLOR（35）・DWMWA_TEXT_COLOR（36）はWindows 11（ビルド22000）で
/// 追加されたAPIで、それ未満のOSへ渡すとDwmSetWindowAttributeがエラーを返すだけ
/// （例外にはならない）だが、依頼のとおり無意味な呼び出し自体を明示的に打ち切る。
/// LinuxにはDWMという概念自体が存在しないため、<see cref="OperatingSystem.IsWindows"/>の
/// 時点で即座に何もしない。呼び出し側の<see cref="TitleBarThemeSync"/>も同じ判定を
/// 先に行うが、このクラス単体で呼んでも安全なように二重にガードしている。
///
/// 【Avaloniaの既定動作との関係】(先に確認事項として調査)
/// Avalonia.Win32.dllの<c>WindowImpl.SetFrameThemeVariant</c>が、
/// <c>TopLevel.ActualThemeVariant</c>の変化に連動してDWMWA_USE_IMMERSIVE_DARK_MODE（20）を
/// 既に自動設定していることをilspycmdでの逆コンパイルで確認した（ビルド22000以上のみ、
/// Avalonia側もこのビルド番号を条件にしている）。何もしなければ、この自動設定は
/// <c>Application.RequestedThemeVariant</c>（既定値<c>Default</c>）を通じてOS実機の
/// 「実際の」ライト/ダーク設定に追従してしまい、Graftアプリ内で選んだテーマ
/// （<see cref="Graft.Themes.ThemeManager.SelectedTheme"/>）と食い違う余地があった
/// （例: OSはダーク・Graftはライトを選択、のケース）。この食い違いを断つため、
/// <see cref="Graft.Themes.ThemeManager"/>側で<c>Application.RequestedThemeVariant</c>を
/// Graftが解決した実際のテーマへ常に合わせるよう変更した（Themes/ThemeManager.csの
/// <c>ApplyResolvedTheme</c>参照）。これによりAvalonia自身の自動設定とここでの明示設定は
/// 常に同じ値になり、競合しない。20を自分でも設定しているのは依頼書1章の明示指示への対応と、
/// 上記の整合が崩れた場合の保険（フェイルセーフ）を兼ねる。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsTitleBarTheme
{
    // DWM呼び出しの失敗（対応していないOSビルド・セーフモード等）でログが毎回のテーマ切替で
    // 溢れないよう、プロセス内で1回だけ記録する（依頼書4章）。
    private static bool _dwmFailureLogged;

    /// <summary>
    /// タイトルバーへ配色を適用する。Windows 11未満・Linuxでは何もせずエラーも出さない。
    /// DWM呼び出しの失敗は例外を外へ投げず、<paramref name="logger"/>があれば1回だけ記録する
    /// （DWMの失敗でアプリが落ちてはならないため）。
    /// </summary>
    public static void Apply(Window window, bool isDarkMode, Color captionColor, Color textColor, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        // 判定そのものは環境非依存の純粋関数（WindowsTitleBarThemeSupport.ShouldApply）へ委ね、
        // ここではOSから読んだ値を渡すだけにする（依頼書のテスト方針・クラス冒頭コメント参照）。
        if (!WindowsTitleBarThemeSupport.ShouldApply(OperatingSystem.IsWindows(), Environment.OSVersion.Version.Build)) return;

        try
        {
            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return; // まだネイティブウィンドウが生成されていない。

            var darkFlag = isDarkMode ? 1 : 0;
            WindowsNativeMethods.DwmSetWindowAttribute(
                hwnd, WindowsNativeMethods.DwmwaUseImmersiveDarkMode, ref darkFlag, sizeof(int));

            var caption = WindowsTitleBarThemeSupport.ToColorRef(captionColor.R, captionColor.G, captionColor.B);
            WindowsNativeMethods.DwmSetWindowAttribute(
                hwnd, WindowsNativeMethods.DwmwaCaptionColor, ref caption, sizeof(uint));

            var text = WindowsTitleBarThemeSupport.ToColorRef(textColor.R, textColor.G, textColor.B);
            WindowsNativeMethods.DwmSetWindowAttribute(
                hwnd, WindowsNativeMethods.DwmwaTextColor, ref text, sizeof(uint));
        }
        catch (Exception ex)
        {
            LogFailureOnce(logger, ex);
        }
    }

    /// <summary>
    /// キャプション色・文字色の明示指定を取り消し、OS既定の配色（<c>DWMWA_COLOR_DEFAULT</c>）へ
    /// 戻す。現状のGraftは常にライト/ダークいずれかの色を持つため通常経路では使わないが、
    /// 依頼書1章の調査対象であり、色の解決に失敗した場合の呼び出し側（<see cref="TitleBarThemeSync"/>）
    /// のフォールバックとして使う（誤った色を決め打ちで塗るより、OS既定へ委ねる方が安全なため）。
    /// </summary>
    public static void ResetToSystemDefault(Window window, Logger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!WindowsTitleBarThemeSupport.ShouldApply(OperatingSystem.IsWindows(), Environment.OSVersion.Version.Build)) return;

        try
        {
            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;

            var defaultColor = WindowsNativeMethods.DwmwaColorDefault;
            WindowsNativeMethods.DwmSetWindowAttribute(
                hwnd, WindowsNativeMethods.DwmwaCaptionColor, ref defaultColor, sizeof(uint));
            defaultColor = WindowsNativeMethods.DwmwaColorDefault;
            WindowsNativeMethods.DwmSetWindowAttribute(
                hwnd, WindowsNativeMethods.DwmwaTextColor, ref defaultColor, sizeof(uint));
        }
        catch (Exception ex)
        {
            LogFailureOnce(logger, ex);
        }
    }

    private static void LogFailureOnce(Logger? logger, Exception ex)
    {
        if (_dwmFailureLogged) return;
        _dwmFailureLogged = true;
        // 握りつぶすが黙って捨てはしない。毎回のテーマ切替でログが溢れないよう1回だけ記録する
        // （依頼書4章）。
        logger?.Error("titlebar-theme", $"DWMでのタイトルバー配色適用に失敗しました（以後は再記録しません）: {ex}");
    }
}
