using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Graft.Platform;

/// <summary>
/// クリップボード監視（9章）とグローバルホットキー（8.10）がウィンドウメッセージを
/// 受け取るための、表示されない専用ウィンドウ（メッセージ専用ウィンドウ）。
///
/// v2.0のWPF版はメインウィンドウのプロシージャへ <c>HwndSource.AddHook</c> で割り込んでいたが、
/// Avalonia 11.2.3 には同等の公開APIが無い（<c>WindowImpl.WndProcHookCallback</c> は
/// 内部実装への到達が必要）。<c>WM_CLIPBOARDUPDATE</c>・<c>WM_HOTKEY</c> はいずれも
/// 「登録したウィンドウ」に届けばよく、メインウィンドウである必要がないため、
/// 自前のメッセージ専用ウィンドウを作ってそこで受ける（仕様書v2.1 19章 L4）。
///
/// UIスレッド上で生成するため、メッセージはAvaloniaのメッセージループが配送する。
/// したがってコールバックもUIスレッドで動き、そのままViewModelを操作できる。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsMessageWindow : IDisposable
{
    private const string ClassName = "GraftMessageWindow";
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly WndProc _wndProc;
    private readonly Func<int, IntPtr, IntPtr, bool> _onMessage;
    private ushort _classAtom;
    private bool _disposed;

    private WindowsMessageWindow(Func<int, IntPtr, IntPtr, bool> onMessage)
    {
        _onMessage = onMessage;
        _wndProc = HandleMessage;
    }

    /// <summary>作成したウィンドウのハンドル。作成に失敗した場合は <see cref="IntPtr.Zero"/>。</summary>
    public IntPtr Handle { get; private set; }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// メッセージ専用ウィンドウを作る。作成できなかった場合も例外は投げず、
    /// <see cref="Handle"/> が <see cref="IntPtr.Zero"/> のインスタンスを返す
    /// （クリップボード監視・ホットキーがそれぞれ登録失敗として扱う）。
    /// </summary>
    public static WindowsMessageWindow Create(Func<int, IntPtr, IntPtr, bool> onMessage)
    {
        ArgumentNullException.ThrowIfNull(onMessage);

        var window = new WindowsMessageWindow(onMessage);
        var moduleHandle = GetModuleHandle(null);

        var wndClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(window._wndProc),
            hInstance = moduleHandle,
            lpszClassName = ClassName,
        };

        // 同名クラスが既に登録済み（複数回の生成）でも、CreateWindowExは成功する。
        window._classAtom = RegisterClassEx(ref wndClass);

        window.Handle = CreateWindowEx(
            0, ClassName, ClassName, 0, 0, 0, 0, 0,
            HwndMessage, IntPtr.Zero, moduleHandle, IntPtr.Zero);

        return window;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Handle != IntPtr.Zero)
        {
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
        if (_classAtom != 0)
        {
            UnregisterClass(ClassName, GetModuleHandle(null));
            _classAtom = 0;
        }
    }

    private IntPtr HandleMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!_disposed && _onMessage(unchecked((int)msg), wParam, lParam))
        {
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int width, int height,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
