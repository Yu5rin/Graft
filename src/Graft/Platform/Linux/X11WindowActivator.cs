using System.Runtime.InteropServices;
using System.Text;

namespace Graft.Platform.Linux;

/// <summary>
/// 多重起動時、既存ウィンドウの前面化（<see cref="LinuxSingleInstanceGuard.ActivateExistingInstance"/>）で
/// <c>wmctrl</c> コマンドが入っていない環境向けの縮退実装（実機調査で判明した追加不具合の修正）。
///
/// 背景: 従来の実装は <c>wmctrl -a</c> の実行に失敗すると（コマンド未導入・タイムアウト・
/// 対象ウィンドウ未検出のいずれでも）何も起きずに黙って終了していた。利用者からは
/// 「ダブルクリックしても何も起きない」ようにしか見えず、多重起動防止自体は機能していても
/// 気付きにくい。
///
/// 本クラスは、wmctrl自体が内部で使っているEWMH（Extended Window Manager Hints）の
/// <c>_NET_ACTIVE_WINDOW</c> クライアントメッセージを、自前のXlib接続（<see cref="X11Interop"/>、
/// クリップボード実装と同じ方針で追加パッケージを増やさない）で直接送ることで、
/// wmctrlが無い環境でも前面化できるようにする。GNOME/KDE/XFCE等、主要なウィンドウマネージャは
/// いずれもEWMHに対応しているため、これだけでほとんどの環境をカバーできる。
/// Waylandや非対応のウィンドウマネージャでは何も起きない
/// （多重起動の防止自体はMutexで成立しているため、これはあくまで前面化のみの縮退である）。
/// </summary>
internal static class X11WindowActivator
{
    // ルートウィンドウの_NET_CLIENT_LISTから読み取るウィンドウ数の上限（安全弁）。
    // 通常のデスクトップ環境でこれを超えることはあり得ない。
    private const int MaxWindows = 4096;

    // ウィンドウ名（_NET_WM_NAME）として読み取る最大バイト数（安全弁）。
    private const int MaxNameBytes = 4096;

    /// <summary>
    /// タイトルにマッチする既存ウィンドウへ _NET_ACTIVE_WINDOW を送る。成功したら true。
    /// X11に接続できない・libX11が無い・対応するウィンドウが見つからない・ウィンドウマネージャが
    /// EWMHに対応していない等、いずれの理由でも例外は投げず false を返す
    /// （呼び出し側はfalseの場合さらなる縮退やログ記録を行う）。
    /// </summary>
    internal static bool TryActivate(string windowTitle)
    {
        var display = IntPtr.Zero;
        try
        {
            display = X11Interop.XOpenDisplay(null);
            if (display == IntPtr.Zero) return false; // X11に接続できない環境（Waylandのみ等）。

            var root = X11Interop.XDefaultRootWindow(display);
            var clientListAtom = X11Interop.XInternAtom(display, "_NET_CLIENT_LIST", false);
            var activeWindowAtom = X11Interop.XInternAtom(display, "_NET_ACTIVE_WINDOW", false);
            var nameAtom = X11Interop.XInternAtom(display, "_NET_WM_NAME", false);

            var target = FindWindowByTitle(display, root, clientListAtom, nameAtom, windowTitle);
            if (target == IntPtr.Zero) return false; // 未検出（該当ウィンドウ無し、またはWM未対応）。

            return SendActivateMessage(display, root, target, activeWindowAtom);
        }
        catch (DllNotFoundException)
        {
            return false; // libX11が無い環境。
        }
        finally
        {
            if (display != IntPtr.Zero) X11Interop.XCloseDisplay(display);
        }
    }

    /// <summary>
    /// 不具合修正: 自分のプロセスが既に持っているウィンドウを、XID（Avaloniaの
    /// <c>Window.TryGetPlatformHandle()?.Handle</c>がX11環境で返す実際のウィンドウID）を
    /// 直接指定して前面化する。<see cref="TryActivate"/>（タイトルで<c>_NET_CLIENT_LIST</c>を
    /// 検索して見つけ出す経路）と異なり、ウィンドウ一覧の走査・タイトル一致判定を一切経由しない
    /// （<see cref="ISingleInstanceGuard.ActivateWindowHandle"/>のコメント参照:
    /// クリップボード監視は自分のウィンドウが対象であり、多重起動検出用のタイトル検索とは
    /// 前提が異なる）。
    ///
    /// <paramref name="windowHandle"/>が<see cref="IntPtr.Zero"/>、X11に接続できない
    /// （Waylandのみ等）、libX11が無い環境など、いずれの理由でも例外は投げず false を返す
    /// （呼び出し側<see cref="Platform.Linux.LinuxSingleInstanceGuard.ActivateWindowHandle"/>が
    /// タイトル検索の経路へ縮退する）。
    /// </summary>
    internal static bool TryActivateHandle(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero) return false;

        var display = IntPtr.Zero;
        try
        {
            display = X11Interop.XOpenDisplay(null);
            if (display == IntPtr.Zero) return false; // X11に接続できない環境（Waylandのみ等）。

            var root = X11Interop.XDefaultRootWindow(display);
            var activeWindowAtom = X11Interop.XInternAtom(display, "_NET_ACTIVE_WINDOW", false);

            return SendActivateMessage(display, root, windowHandle, activeWindowAtom);
        }
        catch (DllNotFoundException)
        {
            return false; // libX11が無い環境。
        }
        finally
        {
            if (display != IntPtr.Zero) X11Interop.XCloseDisplay(display);
        }
    }

    private static IntPtr FindWindowByTitle(IntPtr display, IntPtr root, IntPtr clientListAtom, IntPtr nameAtom, string windowTitle)
    {
        foreach (var window in GetClientList(display, root, clientListAtom))
        {
            var name = GetWindowName(display, window, nameAtom);
            // wmctrl -a と同様、部分一致で判定する（完全一致に限定すると将来タイトルへ
            // サフィックスを足しただけで前面化できなくなる事故を避けたいため）。
            if (name is not null && name.Contains(windowTitle, StringComparison.Ordinal)) return window;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// ルートウィンドウの _NET_CLIENT_LIST（トップレベルウィンドウの一覧、EWMH）を読み取る。
    /// ウィンドウマネージャが起動していない・EWMHに対応していない環境ではプロパティ自体が
    /// 存在せず、空を返す。
    ///
    /// 各要素はプロトコル上format=32（4バイト単位）だが、XlibはXGetWindowPropertyで
    /// クライアント側メモリへ展開する際、各要素をCの<c>long</c>型（64bit環境では8バイト）として
    /// 格納する仕様のため、8バイト刻みで読む（<see cref="X11ClipboardReader"/>のformat=8
    /// プロパティ読み取り＝1バイト刻みとは異なる点に注意）。
    /// </summary>
    private static IEnumerable<IntPtr> GetClientList(IntPtr display, IntPtr root, IntPtr clientListAtom)
    {
        var result = X11Interop.XGetWindowProperty(
            display, root, clientListAtom, 0, MaxWindows, false, IntPtr.Zero,
            out var actualType, out var format, out var nitems, out _, out var propPtr);

        try
        {
            if (result != X11Interop.Success || propPtr == IntPtr.Zero) yield break;
            if (actualType == IntPtr.Zero || format != 32) yield break; // プロパティ未設定（WM未対応/未起動）。

            var count = (int)Math.Min(nitems, MaxWindows);
            for (var i = 0; i < count; i++)
            {
                yield return (IntPtr)Marshal.ReadInt64(propPtr, i * 8);
            }
        }
        finally
        {
            if (propPtr != IntPtr.Zero) X11Interop.XFree(propPtr);
        }
    }

    /// <summary>指定ウィンドウの _NET_WM_NAME（UTF8_STRING）を読み取る。存在しなければnull。</summary>
    private static string? GetWindowName(IntPtr display, IntPtr window, IntPtr nameAtom)
    {
        var result = X11Interop.XGetWindowProperty(
            display, window, nameAtom, 0, MaxNameBytes / 4, false, IntPtr.Zero,
            out var actualType, out var format, out var nitems, out _, out var propPtr);

        try
        {
            if (result != X11Interop.Success || propPtr == IntPtr.Zero) return null;
            if (actualType == IntPtr.Zero || format != 8 || nitems <= 0) return null;

            var length = (int)Math.Min(nitems, MaxNameBytes);
            var bytes = new byte[length];
            Marshal.Copy(propPtr, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            if (propPtr != IntPtr.Zero) X11Interop.XFree(propPtr);
        }
    }

    /// <summary>
    /// EWMHの _NET_ACTIVE_WINDOW クライアントメッセージをルートウィンドウへ送る。
    /// data.l[0]=送信元種別（1=通常アプリケーション）、data.l[1]=タイムスタンプ
    /// （0=不明。多重起動時の縮退用途であり、フォーカス窃取防止の厳密な制御までは求めない）、
    /// data.l[2]=現在アクティブなウィンドウ（0=不明で構わない、EWMH仕様上省略可）。
    /// </summary>
    private static bool SendActivateMessage(IntPtr display, IntPtr root, IntPtr target, IntPtr activeWindowAtom)
    {
        var eventBuffer = X11Interop.BuildClientMessageEvent(
            target, activeWindowAtom, 32, new long[] { 1, 0, 0, 0, 0 });

        var mask = X11Interop.SubstructureNotifyMask | X11Interop.SubstructureRedirectMask;
        var sent = X11Interop.XSendEvent(display, root, false, mask, eventBuffer);
        X11Interop.XFlush(display);
        return sent != 0;
    }
}
