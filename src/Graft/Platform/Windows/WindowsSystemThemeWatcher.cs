using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="ISystemThemeWatcher"/> のWindows実装。v2.0での実装元は <c>Themes/ThemeManager.cs</c> の
/// <c>TryReadAppsUseLightTheme</c> と、それに付随する <c>SystemEvents.UserPreferenceChanged</c>
/// の監視。レジストリの読み取りロジックは変更していない。テーマ辞書の差し替えなどUIに関わる
/// 処理は行わず、変更の可能性があったことを <see cref="Changed"/> で通知するのみとする
/// （UIスレッドへのディスパッチや実際の反映は呼び出し側の責務）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemThemeWatcher : ISystemThemeWatcher
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ValueName = "AppsUseLightTheme";

    private bool _watching;
    private bool _disposed;

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public event EventHandler? Changed;

    /// <summary>
    /// HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize の
    /// AppsUseLightTheme（DWORD、0=ダーク / 1=ライト）を読み取る。取得できない場合はnullを
    /// 返し、呼び出し側で既定のダークへフォールバックさせる。
    /// </summary>
    public bool? TryReadIsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue(ValueName) is int value)
            {
                return value != 0;
            }
        }
        catch (Exception)
        {
            // レジストリキーが存在しない、アクセス権がない等、読み取りに失敗するケースは
            // 環境依存で網羅できないため、ここでは判定不能として扱い呼び出し側の既定
            // （ダーク）へフォールバックさせる。読み取り専用の最善努力の参照であるため、
            // 例外種別を問わずここで吸収する。
        }

        return null;
    }

    /// <summary>
    /// 依頼3（v2.1 仕様書9.3）。<c>SystemParametersInfo(SPI_GETHIGHCONTRAST)</c>で
    /// <see cref="HighContrastInfo.dwFlags"/>の<c>HCF_HIGHCONTRASTON</c>ビットを見る。
    /// これはWindowsが「設定 &gt; アクセシビリティ &gt; コントラストテーマ」の状態を取得する際の
    /// 標準API（レジストリを直接読む方式は非公式で将来のOSバージョンで壊れる恐れがあるため
    /// 採らない）。<see cref="TryReadIsLightTheme"/>と同じく読み取り専用の最善努力の参照であり、
    /// 失敗時は種別を問わず吸収してnull（判定不能）を返す。
    /// </summary>
    public bool? TryReadIsHighContrast()
    {
        try
        {
            var info = new HighContrastInfo { cbSize = (uint)Marshal.SizeOf<HighContrastInfo>() };
            if (SystemParametersInfo(SpiGetHighContrast, info.cbSize, ref info, 0))
            {
                return (info.dwFlags & HcfHighContrastOn) != 0;
            }
        }
        catch (Exception)
        {
            // TryReadIsLightThemeと同じ方針: 読み取り専用の最善努力の参照のため、
            // 例外種別を問わずここで吸収し判定不能として扱う。
        }

        return null;
    }

    // --- ハイコントラスト検出用のWin32 P/Invoke宣言 ---
    // このファイル固有のAPIのため、共有宣言（WindowsNativeMethods.cs、クリップボード監視・
    // ホットキー・タイトルバー配色向け）へは加えず、使用箇所に閉じて宣言する
    // （WindowsTrashService.csが独自にshell32.dllの宣言を持つのと同じ方針）。

    private const uint SpiGetHighContrast = 0x0042;
    private const uint HcfHighContrastOn = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr lpszDefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref HighContrastInfo pvParam, uint fWinIni);

    public void StartWatching()
    {
        if (_watching)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _watching = true;
    }

    public void StopWatching()
    {
        if (!_watching)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _watching = false;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General
            or UserPreferenceCategory.Color
            or UserPreferenceCategory.Accessibility)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopWatching();
        _disposed = true;
    }
}
