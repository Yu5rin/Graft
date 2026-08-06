using Avalonia.Threading;
using Graft.Core;

namespace Graft.Platform.Linux;

/// <summary>
/// <see cref="IGlobalHotkeys"/> のLinux実装（仕様書8.10、v2.1 19章 L4）。
/// X11 の <c>XGrabKey</c> でルートウィンドウ宛にキーをつかみ取り、専用スレッドの
/// イベントループで受け取る。Windows版がウィンドウメッセージ経由で受け取るのに対し、
/// X11には同等の仕組みが無いため <see cref="HandleMessage"/> は使わず、
/// コールバックはUIスレッドへ戻してから呼び出す。
///
/// Waylandでは他クライアントのキーをつかみ取れないため、この実装は動作しない
/// （XWayland経由でも同様）。その場合は<see cref="Register"/>がE601の失敗を返し、
/// 起動時の確認事項として利用者へ提示される。
/// </summary>
public sealed class LinuxGlobalHotkeys : IGlobalHotkeys
{
    // NumLock・CapsLockが有効なときも同じキーとして扱えるよう、
    // それらの組み合わせぶんも重ねてつかみ取る（X11での定番の対処）。
    private static readonly uint[] LockCombinations =
    {
        0,
        (uint)X11Interop.X11Modifiers.Lock,
        (uint)X11Interop.X11Modifiers.Mod2,
        (uint)(X11Interop.X11Modifiers.Lock | X11Interop.X11Modifiers.Mod2),
    };

    private readonly Dictionary<(uint Modifiers, uint KeyCode), Action> _callbacks = new();
    private readonly object _gate = new();

    private IntPtr _display;
    private IntPtr _root;
    private Thread? _loop;
    private volatile bool _running;
    private bool _disposed;

    public bool IsSupported => TryOpenDisplay();

    public string? UnsupportedReason
        => IsSupported ? null : "X11に接続できないため、グローバルホットキーを利用できません（Wayland環境では利用できません）。";

    /// <summary>X11ではウィンドウハンドルを使わないため何もしない（Windows版との互換のために存在する）。</summary>
    public void Attach(IntPtr hwnd)
    {
        // 何もしない。
    }

    public GraftResult<int> Register(string gesture, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (!TryOpenDisplay())
        {
            return GraftResult<int>.Fail(GraftIssue.Of(ErrorCode.E601,
                "X11に接続できないため、グローバルホットキーを登録できません（Wayland環境では利用できません）。"));
        }

        if (ParseGesture(gesture) is not var (modifiers, keySymName))
        {
            return GraftResult<int>.Fail(GraftIssue.Of(ErrorCode.E601,
                $"キー指定を解釈できません: '{gesture}'。修飾キー(Ctrl/Alt/Shift/Super)と英数字またはファンクションキーの組み合わせで指定してください。"));
        }

        var keySym = X11Interop.XStringToKeysym(keySymName);
        if (keySym == IntPtr.Zero)
        {
            return GraftResult<int>.Fail(GraftIssue.Of(ErrorCode.E601,
                $"キー '{keySymName}' をX11のキー名として解釈できませんでした。"));
        }

        var keyCode = X11Interop.XKeysymToKeycode(_display, keySym);
        if (keyCode == 0)
        {
            return GraftResult<int>.Fail(GraftIssue.Of(ErrorCode.E601,
                $"キー '{keySymName}' に対応するキーコードが現在のキーボード配列に見つかりません。"));
        }

        lock (_gate)
        {
            foreach (var lockBits in LockCombinations)
            {
                X11Interop.XGrabKey(_display, keyCode, modifiers | lockBits, _root,
                    ownerEvents: true, X11Interop.GrabModeAsync, X11Interop.GrabModeAsync);
            }
            X11Interop.XFlush(_display);
            _callbacks[(modifiers, keyCode)] = callback;
        }

        StartLoop();
        return GraftResult<int>.Ok(keyCode);
    }

    public void UnregisterAll()
    {
        lock (_gate)
        {
            if (_display == IntPtr.Zero) return;

            foreach (var (modifiers, keyCode) in _callbacks.Keys)
            {
                foreach (var lockBits in LockCombinations)
                {
                    X11Interop.XUngrabKey(_display, (int)keyCode, modifiers | lockBits, _root);
                }
            }
            X11Interop.XFlush(_display);
            _callbacks.Clear();
        }
    }

    /// <summary>X11ではウィンドウメッセージを使わないため常にfalseを返す。</summary>
    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam) => false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();
        _running = false;

        // イベントループはXNextEventで待機しているため、次のイベントが来るまで抜けない。
        // 接続を閉じることでXNextEventが失敗し、ループが終了する。
        var display = _display;
        _display = IntPtr.Zero;
        if (display != IntPtr.Zero) X11Interop.XCloseDisplay(display);
    }

    private bool TryOpenDisplay()
    {
        if (_display != IntPtr.Zero) return true;
        if (_disposed) return false;

        try
        {
            var display = X11Interop.XOpenDisplay(null);
            if (display == IntPtr.Zero) return false;

            _display = display;
            _root = X11Interop.XDefaultRootWindow(display);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false; // libX11 が無い環境（Wayland専用構成など）。
        }
    }

    private void StartLoop()
    {
        if (_loop is not null) return;

        _running = true;
        _loop = new Thread(RunEventLoop)
        {
            IsBackground = true,
            Name = "Graft global hotkeys (X11)",
        };
        _loop.Start();
    }

    private void RunEventLoop()
    {
        var buffer = new byte[X11Interop.XEventSize];
        while (_running && _display != IntPtr.Zero)
        {
            try
            {
                X11Interop.XNextEvent(_display, buffer);
            }
            catch (Exception)
            {
                return; // 接続が閉じられた（Dispose）。
            }

            if (X11Interop.GetEventType(buffer) != X11Interop.KeyPress) continue;

            var (state, keyCode) = X11Interop.GetKeyEvent(buffer);
            var normalized = state & ~(uint)(X11Interop.X11Modifiers.Lock | X11Interop.X11Modifiers.Mod2);

            Action? callback;
            lock (_gate)
            {
                _callbacks.TryGetValue((normalized, keyCode), out callback);
            }

            // コールバックはViewModelの操作を伴うため、必ずUIスレッドへ戻してから呼ぶ。
            if (callback is not null) Dispatcher.UIThread.Post(callback);
        }
    }

    /// <summary>
    /// "Ctrl+Alt+V" 形式の文字列を、X11の修飾子ビットとキー名（keysym名）へ分解する。
    /// 修飾キーが1つ以上、末尾に英数字またはファンクションキーを1つだけ要求する
    /// （Windows版 <c>WindowsGlobalHotkeys.ParseGesture</c> と同じ規則）。
    /// </summary>
    private static (uint Modifiers, string KeySym)? ParseGesture(string gesture)
    {
        var tokens = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return null;

        uint modifiers = 0;
        string? keySym = null;

        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= (uint)X11Interop.X11Modifiers.Control;
                    break;
                case "alt":
                    modifiers |= (uint)X11Interop.X11Modifiers.Mod1;
                    break;
                case "shift":
                    modifiers |= (uint)X11Interop.X11Modifiers.Shift;
                    break;
                case "win" or "super" or "meta":
                    modifiers |= (uint)X11Interop.X11Modifiers.Mod4;
                    break;
                default:
                    if (keySym is not null) return null; // キーは1つだけ
                    keySym = TranslateKeyName(token);
                    if (keySym is null) return null;
                    break;
            }
        }

        return modifiers != 0 && keySym is not null ? (modifiers, keySym) : null;
    }

    /// <summary>"V" や "F5" をX11のkeysym名（"v" / "F5"）へ変換する。</summary>
    private static string? TranslateKeyName(string token)
    {
        if (token.Length == 1 && char.IsAsciiLetterOrDigit(token[0]))
        {
            return token.ToLowerInvariant();
        }

        if (token.Length is 2 or 3
            && (token[0] is 'F' or 'f')
            && int.TryParse(token.AsSpan(1), out var number)
            && number is >= 1 and <= 24)
        {
            return $"F{number}";
        }

        return null;
    }
}
