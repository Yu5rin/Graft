using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="WindowsGlobalHotkeys"/>・<see cref="WindowsClipboardMonitor"/>・
/// <see cref="WindowsTitleBarTheme"/> で使用する Win32 API宣言と定数をまとめる。
/// v2.0での実装元は <c>Features/NativeMethods.cs</c>（クリップボード監視・
/// グローバルホットキー部分）。宣言内容は変更しない。タイトルバー配色（dwmapi.dll）は
/// 別系統のAPIだが、「Win32のP/Invoke宣言はこのファイルへ集約する」という本リポジトリの
/// 慣習に合わせてここへ追加する。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsNativeMethods
{
    internal const int WmClipboardUpdate = 0x031D;
    internal const int WmHotkey = 0x0312;
    internal const uint CfUnicodeText = 13;

    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;
    internal const uint ModNoRepeat = 0x4000;

    // --- クリップボード監視（9章） ---

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    // --- グローバルホットキー（8.10章） ---

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // --- タイトルバー配色（WindowsTitleBarTheme参照） ---
    // DWMWA_CAPTION_COLOR・DWMWA_TEXT_COLORはWindows 11（ビルド22000）で追加された値で、
    // それ未満のOSではDwmSetWindowAttributeがエラーを返すだけで例外にはならない
    // （呼び出し側のWindowsTitleBarThemeが事前にビルド番号を見て呼ばないようにする）。
    internal const int DwmwaUseImmersiveDarkMode = 20;
    internal const int DwmwaCaptionColor = 35;
    internal const int DwmwaTextColor = 36;

    // DWMWA_COLOR_DEFAULT。DWMWA_CAPTION_COLOR/DWMWA_TEXT_COLORへこの値を渡すと、
    // 明示指定を取り消してOS既定の配色（無指定状態）へ戻せる（MSDN準拠）。
    internal const uint DwmwaColorDefault = 0xFFFFFFFFu;

    // pvAttributeの実体はBOOL（4byte int）1つ、またはCOLORREF（4byte uint）1つのいずれか。
    // DwmSetWindowAttribute自体は引数の型を気にしないvoid*だが、C#側でoverloadを分けておくと
    // 呼び出し側の意図（bool値かCOLORREF値か）が型で強制され、取り違えを防げる。
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);
}
