namespace Graft.Platform;

/// <summary>
/// Windowsのウィンドウメッセージを、クリップボード監視（9章）とグローバルホットキー（8.10）へ
/// 中継する（仕様書v2.1 19章 L4）。
///
/// v2.0のWPF版はメインウィンドウのプロシージャへ <c>HwndSource.AddHook</c> で割り込んでいたが、
/// Avalonia 11.2.3 には同等の公開APIが無い。<c>WM_CLIPBOARDUPDATE</c>・<c>WM_HOTKEY</c> は
/// 登録先のウィンドウへ届けばよくメインウィンドウである必要がないため、
/// <see cref="WindowsMessageWindow"/>（表示されない専用ウィンドウ）を作ってそこで受ける。
/// Windows以外のOSではウィンドウメッセージという仕組み自体が無いため何もしない
/// （Linux側はX11のイベントループとタイマーで同じ役割を担う）。
///
/// 8.11メモ: PerMonitorV2下でモニタ間移動時にDPIが変わると、受け取る座標系は移動先モニタの
/// DPIで解釈される。ここで扱うホットキー・クリップボード通知はいずれも座標を用いないため
/// 影響しないが、将来ここに座標依存の処理を足す場合はDPI変更を考慮すること。
/// </summary>
internal sealed class WindowMessageBridge : IDisposable
{
    private readonly IDisposable? _messageWindow;

    private WindowMessageBridge(IDisposable? messageWindow)
    {
        _messageWindow = messageWindow;
    }

    /// <summary>
    /// メッセージの中継を開始し、各サービスへ受信用のウィンドウハンドルを割り当てる。
    /// Windows以外では何もせず、ハンドルは <see cref="IntPtr.Zero"/> のままになる。
    /// </summary>
    public static WindowMessageBridge Attach(IPlatformServices platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        if (!OperatingSystem.IsWindows()) return new WindowMessageBridge(null);

        return AttachWindows(platform);
    }

    public void Dispose() => _messageWindow?.Dispose();

    // Windows専用APIに触れるため、Windows以外で実行されないようメソッドを分ける。
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static WindowMessageBridge AttachWindows(IPlatformServices platform)
    {
        var window = WindowsMessageWindow.Create((msg, wParam, lParam) =>
        {
            var byClipboard = platform.Clipboard.HandleMessage(msg, wParam, lParam);
            var byHotkey = platform.Hotkeys.HandleMessage(msg, wParam, lParam);
            return byClipboard || byHotkey;
        });

        platform.Clipboard.Attach(window.Handle);
        platform.Hotkeys.Attach(window.Handle);
        return new WindowMessageBridge(window);
    }
}
