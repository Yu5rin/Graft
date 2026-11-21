using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Graft.Platform.Windows;

/// <summary>
/// 別プロセス（Excel／VBE）のウィンドウを前面へ出す（仕様書_VBA連携 7.6）。
/// <para>
/// 【<see cref="ForegroundActivationDecision"/> を直接使えない理由】 あちらは Graft 自身のウィンドウ向けで、
/// 重複起動の判定に結びついている。ここは相手が別プロセスなので、ハンドルを受けて前面化するだけの小さな実装にし、
/// Windows が前面化を許さない場合の回避策（<c>AttachThreadInput</c> の考え方）と、紐づけるかどうかの純ロジック
/// （<see cref="ForegroundActivationDecision.ShouldAttachThreadInput"/>）だけを流用する。
/// 実機で確かめる項目（10.3-19）。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsForeground
{
    private const int SwRestore = 9;

    /// <summary>最小化されていれば元に戻し、前面へ出す。出せたら true。</summary>
    public static bool BringToFront(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return false;

        if (IsIconic(handle)) ShowWindow(handle, SwRestore);
        if (SetForegroundWindow(handle) && GetForegroundWindow() == handle) return true;

        // OS のフォーカス窃取防止に拒否された場合の回避策。いま前面にあるウィンドウの入力スレッドと、
        // 自分の入力スレッドを一時的に結び付けると、前面化の要求元が「ユーザーが操作している側」とみなされる。
        var foreground = GetForegroundWindow();
        var myThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foreground, IntPtr.Zero);
        if (!ForegroundActivationDecision.ShouldAttachThreadInput(foreground, handle, foregroundThread, myThread))
        {
            return false;
        }

        var attached = AttachThreadInput(myThread, foregroundThread, true);
        try
        {
            if (attached)
            {
                SetForegroundWindow(handle);
                BringWindowToTop(handle);
            }
        }
        finally
        {
            // 解除漏れは、結び付けたままの相手のスレッドの入力キュー（IME 変換など）を道連れにするので必ず外す。
            if (attached) AttachThreadInput(myThread, foregroundThread, false);
        }

        return GetForegroundWindow() == handle;
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);
}
