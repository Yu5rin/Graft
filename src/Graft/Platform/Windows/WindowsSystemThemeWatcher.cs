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
