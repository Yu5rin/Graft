using System.ComponentModel;
using System.Diagnostics;
using CoreGuard = Graft.Core.SingleInstanceGuard;

namespace Graft.Platform.Linux;

/// <summary>
/// <see cref="ISingleInstanceGuard"/> のLinux実装（仕様書v2.1 19章 L4）。
/// ロックの取得・解放は <c>Core/SingleInstanceGuard.cs</c>（名前付きMutex）へ委譲する。
/// .NET の名前付きMutexはUnixでもプロセス間で機能するため、Windows版と同じ仕組みで足りる。
///
/// 既存ウィンドウの前面化は、X11環境で広く入っている <c>wmctrl</c> を試すだけに留める。
/// Waylandには「他プロセスのウィンドウを前面に出す」標準的な手段が無く、
/// 入っていない環境では前面化のみ行われない（多重起動の防止自体は機能する）。
/// </summary>
public sealed class LinuxSingleInstanceGuard : ISingleInstanceGuard
{
    private CoreGuard? _guard;
    private bool _disposed;

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public bool TryAcquire(string name)
    {
        _guard = CoreGuard.TryAcquire(name);
        return _guard is not null;
    }

    public void ActivateExistingInstance(string mainWindowTitle)
    {
        try
        {
            var info = new ProcessStartInfo("wmctrl")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("-a");
            info.ArgumentList.Add(mainWindowTitle);

            using var process = Process.Start(info);
            process?.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // wmctrl が無い環境では前面化を諦める（多重起動の防止自体は成立している）。
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _guard?.Dispose();
        _guard = null;
    }
}
