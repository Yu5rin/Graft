using System.ComponentModel;
using System.Diagnostics;

namespace Graft.Platform.Linux;

/// <summary>
/// <see cref="ISystemThemeWatcher"/> のLinux実装（仕様書v2.1 19章 L4）。
/// デスクトップ環境をまたいで使える XDG デスクトップポータルの
/// <c>org.freedesktop.portal.Settings</c>（<c>color-scheme</c>）を <c>gdbus</c> 経由で参照し、
/// 同じくポータルの <c>SettingChanged</c> シグナルを購読して変更を検知する。
/// ポータルが無い環境では <c>gsettings</c> の <c>color-scheme</c> へフォールバックする。
/// いずれも読み取りのみで、設定の書き換えは一切行わない（附録A.5）。
/// </summary>
public sealed class LinuxSystemThemeWatcher : ISystemThemeWatcher
{
    private Process? _monitor;
    private bool _disposed;

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public event EventHandler? Changed;

    public bool? TryReadIsLightTheme()
    {
        // ポータルの color-scheme: 0=既定（指定なし） 1=ダーク 2=ライト。
        if (ReadPortalColorScheme() is { } scheme)
        {
            return scheme switch
            {
                1 => false,
                2 => true,
                _ => null,
            };
        }

        return ReadGSettingsColorScheme();
    }

    /// <summary>
    /// 依頼3（v2.1 仕様書9.3）。「対応するデスクトップ設定があれば追従し、なければ何もしない」
    /// との指示のとおり、GNOME系のアクセシビリティ設定
    /// （<c>org.gnome.desktop.a11y.interface high-contrast</c>、真偽値）のみを対象にする。
    /// XDGデスクトップポータルにはハイコントラストに相当する標準キーが無いため
    /// （<see cref="TryReadIsLightTheme"/>のcolor-schemeのようなポータル経由の代替が無い）、
    /// gsettingsのみを見る。KDE等gsettingsを持たない/このスキーマを持たない環境では
    /// <see cref="RunAndCapture"/>がnullを返し、そのままnull（判定不能）として返る
    /// （＝「なければ何もしない」を満たす）。
    /// </summary>
    public bool? TryReadIsHighContrast()
    {
        var output = RunAndCapture("gsettings", "get", "org.gnome.desktop.a11y.interface", "high-contrast");
        if (output is null) return null;

        var trimmed = output.Trim();
        if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return null; // 未知の出力形式。判定不能として呼び出し側の既定へ委ねる。
    }

    public void StartWatching()
    {
        if (_disposed || _monitor is not null) return;

        try
        {
            var info = new ProcessStartInfo("gdbus")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[]
                     {
                         "monitor", "--session",
                         "--dest", "org.freedesktop.portal.Desktop",
                         "--object-path", "/org/freedesktop/portal/desktop",
                     })
            {
                info.ArgumentList.Add(argument);
            }

            _monitor = Process.Start(info);
            if (_monitor is null) return;

            _monitor.OutputDataReceived += OnMonitorOutput;
            _monitor.BeginOutputReadLine();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // gdbus が無い環境ではテーマの自動追従を諦める（機能の縮退）。
            _monitor = null;
        }
    }

    public void StopWatching()
    {
        var monitor = _monitor;
        _monitor = null;
        if (monitor is null) return;

        try
        {
            monitor.OutputDataReceived -= OnMonitorOutput;
            if (!monitor.HasExited) monitor.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            // 監視プロセスの停止に失敗しても、アプリの終了は妨げない。
        }
        finally
        {
            monitor.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWatching();
    }

    private void OnMonitorOutput(object? sender, DataReceivedEventArgs e)
    {
        // 出力例:
        //   /org/freedesktop/portal/desktop: org.freedesktop.portal.Settings.SettingChanged
        //     ('org.freedesktop.appearance', 'color-scheme', <uint32 1>)
        if (e.Data is null || !e.Data.Contains("color-scheme", StringComparison.Ordinal)) return;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static int? ReadPortalColorScheme()
    {
        var output = RunAndCapture(
            "gdbus", "call", "--session",
            "--dest", "org.freedesktop.portal.Desktop",
            "--object-path", "/org/freedesktop/portal/desktop",
            "--method", "org.freedesktop.portal.Settings.Read",
            "org.freedesktop.appearance", "color-scheme");
        if (output is null) return null;

        // 出力例: (<<uint32 1>>,)
        var marker = output.IndexOf("uint32", StringComparison.Ordinal);
        if (marker < 0) return null;

        var digits = output.Skip(marker + "uint32".Length).SkipWhile(char.IsWhiteSpace).TakeWhile(char.IsAsciiDigit);
        return int.TryParse(new string(digits.ToArray()), out var value) ? value : null;
    }

    private static bool? ReadGSettingsColorScheme()
    {
        var output = RunAndCapture("gsettings", "get", "org.gnome.desktop.interface", "color-scheme");
        if (output is null) return null;

        if (output.Contains("prefer-dark", StringComparison.Ordinal)) return false;
        if (output.Contains("prefer-light", StringComparison.Ordinal)) return true;
        return null; // "default" は指定なし。呼び出し側の既定へ委ねる。
    }

    private static string? RunAndCapture(string fileName, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000)) return null;
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
