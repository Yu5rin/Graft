using System.Runtime.InteropServices;

namespace Graft.Features;

/// <summary>
/// クリップボード監視とグローバルホットキーで使用する Win32 API の宣言をまとめる。
/// 9章・8.10章の実装のための最小限の P/Invoke のみを含む。
/// 本クラス自体は非Windows環境でも問題なくコンパイルできるが、実行時に呼び出す側
/// （<see cref="ClipboardWatcher"/> / <see cref="HotkeyManager"/>）で
/// <c>OperatingSystem.IsWindows()</c> による分岐を必ず行うこと。
/// </summary>
/// <remarks>
/// 本来は <c>[LibraryImport]</c>（ソース生成）を優先する方針だが、生成コードが
/// <c>unsafe</c> を要求し、本プロジェクトは <c>AllowUnsafeBlocks</c> を有効化していない
/// （<c>.csproj</c> は本タスクの担当外のため変更しない）。そのため本ファイルに限り
/// 従来の <c>[DllImport]</c> を使用する。
/// </remarks>
internal static class NativeMethods
{
    /// <summary>クリップボードの内容が変化したことを通知するウィンドウメッセージ。</summary>
    internal const int WM_CLIPBOARDUPDATE = 0x031D;

    /// <summary>グローバルホットキーが押されたことを通知するウィンドウメッセージ。</summary>
    internal const int WM_HOTKEY = 0x0312;

    /// <summary>Unicodeテキスト形式のクリップボードフォーマット識別子。</summary>
    internal const uint CF_UNICODETEXT = 13;

    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;

    /// <summary>キーリピートによる連続発火を抑止する修飾子（Windows 7以降）。</summary>
    internal const uint MOD_NOREPEAT = 0x4000;

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

    /// <summary>
    /// パスワードマネージャ等が使用する除外用フォーマット
    /// （"ExcludeClipboardContentFromMonitorProcessing"）を含め、任意の名前付き
    /// クリップボードフォーマットを登録する。既に登録済みの名前は同じ値を返す。
    /// </summary>
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
}
