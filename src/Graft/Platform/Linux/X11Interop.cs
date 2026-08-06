using System.Runtime.InteropServices;

namespace Graft.Platform.Linux;

/// <summary>
/// グローバルホットキー（仕様書8.10）に必要な最小限のXlib束縛。
/// 追加パッケージを増やさない方針（附録A.2）のため、libX11 を直接P/Invokeする。
/// ここで宣言するのはキーのつかみ取り（XGrabKey）とイベント受信に必要なものだけで、
/// 描画やウィンドウ管理には一切関与しない。
/// </summary>
internal static class X11Interop
{
    private const string LibX11 = "libX11.so.6";

    internal const int KeyPress = 2;
    internal const int GrabModeAsync = 1;

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
}
