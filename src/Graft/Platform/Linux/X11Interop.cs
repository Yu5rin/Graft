using System.Runtime.InteropServices;

namespace Graft.Platform.Linux;

/// <summary>
/// グローバルホットキー（仕様書8.10）とクリップボード読み取り（<see cref="X11ClipboardReader"/>、
/// 9章・10章）に必要な最小限のXlib束縛。追加パッケージを増やさない方針（附録A.2）のため、
/// libX11 を直接P/Invokeする。ここで宣言するのはキーのつかみ取り（XGrabKey）・
/// セレクションの読み取り（XConvertSelection等）とイベント受信に必要なものだけで、
/// 描画やウィンドウ管理には一切関与しない。
/// </summary>
internal static class X11Interop
{
    private const string LibX11 = "libX11.so.6";
    private const string LibC = "libc.so.6";

    internal const int KeyPress = 2;
    internal const int GrabModeAsync = 1;

    // クリップボード読み取り（X11ClipboardReader）で使うイベント種別。
    internal const int SelectionNotify = 31;
    internal const int PropertyNotify = 19;

    // XPropertyEvent.state の値（PropertyNewValue = 0, PropertyDelete = 1）。
    internal const int PropertyNewValue = 0;

    // XSelectInputへ渡すイベントマスク。プロパティの変化（INCR転送の断片到着）を受け取るために使う。
    internal const long PropertyChangeMask = 1 << 22;

    // XGetWindowProperty の戻り値のうち成功を表す値（X.h の Success）。
    internal const int Success = 0;

    // poll(2) の events/revents に立つビット（データ読み取り可能）。
    internal const short PollIn = 0x0001;

    // XEventは共用体で最大サイズが24 long（=192バイト、64bit環境）。
    // 個々のメンバへはアクセスせず、種別（先頭のint）と鍵盤情報だけを取り出す。
    internal const int XEventSize = 192;

    [Flags]
    internal enum X11Modifiers : uint
    {
        None = 0,
        Shift = 1 << 0,
        Lock = 1 << 1,   // CapsLock
        Control = 1 << 2,
        Mod1 = 1 << 3,   // Alt
        Mod2 = 1 << 4,   // NumLock
        Mod4 = 1 << 6,   // Super（Windowsキー）
    }

    [DllImport(LibX11)]
    internal static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport(LibX11)]
    internal static extern int XCloseDisplay(IntPtr display);

    [DllImport(LibX11)]
    internal static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(LibX11)]
    internal static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);

    [DllImport(LibX11)]
    internal static extern IntPtr XStringToKeysym(string name);

    [DllImport(LibX11)]
    internal static extern int XGrabKey(
        IntPtr display, int keycode, uint modifiers, IntPtr grabWindow,
        bool ownerEvents, int pointerMode, int keyboardMode);

    [DllImport(LibX11)]
    internal static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);

    [DllImport(LibX11)]
    internal static extern int XSelectInput(IntPtr display, IntPtr window, long eventMask);

    [DllImport(LibX11)]
    internal static extern int XNextEvent(IntPtr display, byte[] eventBuffer);

    [DllImport(LibX11)]
    internal static extern int XPending(IntPtr display);

    [DllImport(LibX11)]
    internal static extern int XFlush(IntPtr display);

    [DllImport(LibX11)]
    internal static extern int XSetErrorHandler(IntPtr handler);

    [DllImport(LibX11)]
    internal static extern int XConnectionNumber(IntPtr display);

    // ---- クリップボード読み取り（X11ClipboardReader）用の追加束縛 ----
    // グローバルホットキーと同じ方針で、必要な最小限のみを宣言する。

    [DllImport(LibX11, CharSet = CharSet.Ansi)]
    internal static extern IntPtr XInternAtom(IntPtr display, string atomName, bool onlyIfExists);

    [DllImport(LibX11)]
    internal static extern int XConvertSelection(
        IntPtr display, IntPtr selection, IntPtr target, IntPtr property, IntPtr requestor, IntPtr time);

    /// <summary>
    /// プロパティを読み取る。unsigned long（nitems_return・bytes_after_return）は
    /// 64bit環境では8バイトのため <see langword="long"/> で受ける。
    /// </summary>
    [DllImport(LibX11)]
    internal static extern int XGetWindowProperty(
        IntPtr display, IntPtr window, IntPtr property, long longOffset, long longLength, bool delete,
        IntPtr reqType, out IntPtr actualTypeReturn, out int actualFormatReturn,
        out long nitemsReturn, out long bytesAfterReturn, out IntPtr propReturn);

    [DllImport(LibX11)]
    internal static extern int XDeleteProperty(IntPtr display, IntPtr window, IntPtr property);

    [DllImport(LibX11)]
    internal static extern IntPtr XCreateSimpleWindow(
        IntPtr display, IntPtr parent, int x, int y, uint width, uint height,
        uint borderWidth, IntPtr border, IntPtr background);

    [DllImport(LibX11)]
    internal static extern int XDestroyWindow(IntPtr display, IntPtr window);

    [DllImport(LibX11)]
    internal static extern int XFree(IntPtr data);

    /// <summary>poll(2)で監視する1件分（利用するのはfdとevents/revents=POLLINのみ）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    /// <summary>
    /// クリップボード読み取りの応答待ちで使う。X11接続のfdをブロッキングで監視し、
    /// スレッドを無期限に眠らせず指定ミリ秒でタイムアウトさせる（nfds_tは64bit環境で8バイト）。
    /// </summary>
    [DllImport(LibC, SetLastError = true)]
    internal static extern int poll(PollFd[] fds, ulong nfds, int timeoutMs);

    /// <summary>受信したXEventの種別（先頭のint）を返す。</summary>
    internal static int GetEventType(byte[] buffer) => BitConverter.ToInt32(buffer, 0);

    /// <summary>
    /// XKeyEvent のキーコードと修飾子を取り出す。64bit環境のXKeyEventのレイアウトは
    /// type(4)+pad(4)+serial(8)+send_event(4)+pad(4)+display(8)+window(8)+root(8)+
    /// subwindow(8)+time(8)+x(4)+y(4)+x_root(4)+y_root(4)+state(4)+keycode(4) で、
    /// state は先頭から80バイト、keycode は84バイトの位置になる。
    /// </summary>
    internal static (uint State, uint KeyCode) GetKeyEvent(byte[] buffer)
        => (BitConverter.ToUInt32(buffer, 80), BitConverter.ToUInt32(buffer, 84));

    /// <summary>
    /// XSelectionEvent から selection・target・property・requestor を取り出す。
    /// 64bit環境でのレイアウトは type(4)+pad(4)+serial(8)+send_event(4)+pad(4)+display(8)+
    /// requestor(8)+selection(8)+target(8)+property(8)+time(8)で、requestorは32バイト、
    /// selectionは40バイト、targetは48バイト、propertyは56バイトの位置になる。
    /// </summary>
    internal static (IntPtr Selection, IntPtr Target, IntPtr Property, IntPtr Requestor) GetSelectionEvent(byte[] buffer)
        => (
            (IntPtr)BitConverter.ToInt64(buffer, 40),
            (IntPtr)BitConverter.ToInt64(buffer, 48),
            (IntPtr)BitConverter.ToInt64(buffer, 56),
            (IntPtr)BitConverter.ToInt64(buffer, 32));

    /// <summary>
    /// XPropertyEvent から atom・state を取り出す。64bit環境でのレイアウトは
    /// type(4)+pad(4)+serial(8)+send_event(4)+pad(4)+display(8)+window(8)+atom(8)+time(8)+state(4)で、
    /// atomは40バイト、stateは56バイトの位置になる。
    /// </summary>
    internal static (IntPtr Atom, int State) GetPropertyEvent(byte[] buffer)
        => ((IntPtr)BitConverter.ToInt64(buffer, 40), BitConverter.ToInt32(buffer, 56));
}
