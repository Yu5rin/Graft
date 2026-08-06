using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CoreGuard = Graft.Core.SingleInstanceGuard;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="ISingleInstanceGuard"/> のWindows実装。ロックの取得・解放そのものは
/// <c>Core/SingleInstanceGuard.cs</c>（名前付きMutex。プラットフォームを問わず動作するため
/// ロジックは変更せずそのまま利用する）に委譲する。既存ウィンドウの前面化
/// （<c>FindWindow</c>・<c>ShowWindow</c>・<c>SetForegroundWindow</c>）は移設元
/// <c>Views/StartupCoordinator.cs</c> のロジックをそのまま移す。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSingleInstanceGuard : ISingleInstanceGuard
{
    private const int SwRestore = 9;

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
        var hwnd = FindWindow(null, mainWindowTitle);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _guard?.Dispose();
        _guard = null;
        _disposed = true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
