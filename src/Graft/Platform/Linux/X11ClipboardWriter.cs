using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Graft.Platform.Linux;

/// <summary>
/// 自前のX11接続でCLIPBOARDセレクションの所有権を持ち、他アプリからの読み取り要求に応答する
/// ライタ（実機バグ修正）。
///
/// 背景: AvaloniaのX11クリップボード書き込み（<c>X11Clipboard.SetTextAsync</c>）は実機環境で
/// 例外なく完了するにもかかわらず、Xサーバー上でCLIPBOARDセレクションの所有者が一度も
/// 現れない不具合が確認された（原因はAvalonia内部の環境依存動作と推定されるが特定できていない）。
/// 本クラスはAvaloniaを経由せず、専用のX11接続・専用スレッドのイベントループで
/// XSetSelectionOwner・SelectionRequestへの応答を直接行うことで、この不具合を回避する。
///
/// 読み取り側の<see cref="X11ClipboardReader"/>とは意図的に接続・スレッドを分離している
/// （同一プロセス内で自分が所有者のときに自分で読み取っても、デッドロックせず正しく
/// 自己読み戻しできるようにするため）。
///
/// SelectionRequestイベントへの実応答（TARGETS/UTF8_STRING/STRING・INCR転送）は
/// <c>X11ClipboardWriter.SelectionResponder.cs</c>側（本クラスのpartial）に分けている
/// （1ファイル400行以内の方針のため）。
/// </summary>
public sealed partial class X11ClipboardWriter : IDisposable
{
    private const string ClipboardAtomName = "CLIPBOARD";
    private const string Utf8StringAtomName = "UTF8_STRING";
    private const string StringAtomName = "STRING";
    private const string TargetsAtomName = "TARGETS";
    private const string IncrAtomName = "INCR";
    private const string AtomAtomName = "ATOM";

    // XChangeProperty 1回で送出できるサイズの上限（超えたらINCR転送に切り替える）。
    // サーバーの実際の上限（XMaxRequestSizeより算出、SelectionResponder側で使用）と
    // この値の小さい方を実際の閾値として使う。
    private const int MaxChunkBytes = 256 * 1024;
    private const int MinChunkBytes = 16 * 1024;

    // コマンドキューの確認とXイベント待ちを同じスレッドで両立させるための巡回周期。
    private const int PollTimeoutMs = 50;

    private readonly BlockingCollection<SetTextCommand> _commands = new();
    private readonly Thread _thread;

    private IntPtr _display;
    private IntPtr _window;
    private IntPtr _clipboardAtom;
    private IntPtr _utf8Atom;
    private IntPtr _stringAtom;
    private IntPtr _targetsAtom;
    private IntPtr _incrAtom;
    private IntPtr _atomAtom;
    private int _chunkBytes = MaxChunkBytes;

    // 所有中のテキスト本文。STRINGターゲット用はLatin-1変換済み（非対応文字は'?'）。
    // どちらもnullなら「所有していない」とみなす。
    private byte[]? _utf8Bytes;
    private byte[]? _latin1Bytes;

    private volatile bool _disposed;

    private X11ClipboardWriter(IntPtr display)
    {
        _display = display;
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "Graft X11 clipboard writer" };
        _thread.Start();
    }

    /// <summary>プロセス内で共有する既定インスタンス。X11に接続できない環境ではnull。</summary>
    public static X11ClipboardWriter? Shared => LazyShared.Value;

    private static readonly Lazy<X11ClipboardWriter?> LazyShared = new(TryCreate);

    /// <summary>
    /// X11に接続できる環境であれば専用接続を確立して返す。接続できない環境
    /// （Waylandのみ・libX11が見つからない等）ではnullを返す。
    /// </summary>
    public static X11ClipboardWriter? TryCreate()
    {
        try
        {
            var display = X11Interop.XOpenDisplay(null);
            if (display == IntPtr.Zero) return null;

            X11Interop.EnsureSafeErrorHandlerInstalled();
            return new X11ClipboardWriter(display);
        }
        catch (DllNotFoundException)
        {
            return null; // libX11が無い環境（Wayland専用構成など）。
        }
    }

    /// <summary>
    /// CLIPBOARDセレクションの所有権を取得し、以後の読み取り要求にこのテキストで応答する。
    /// 所有権の取得に成功した場合のみtrue（他プロセスに即座に奪い返された等の場合はfalse）。
    /// </summary>
    public Task<bool> SetTextAsync(string text, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(text);

        var command = new SetTextCommand(text);
        try
        {
            if (_disposed || !_commands.TryAdd(command))
            {
                return Task.FromResult(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Disposeとの競合でキューが締め切られた直後に呼ばれた場合。
            return Task.FromResult(false);
        }

        return WaitWithTimeoutAsync(command.Completion.Task, timeout);
    }

    private static async Task<bool> WaitWithTimeoutAsync(Task<bool> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == task && await task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _commands.CompleteAdding();

        // Dispose時点で応答待ち・INCR転送中などで詰まっていても、接続を強制的に閉じることで
        // poll/XNextEventを異常終了させループスレッドを終了させる（X11ClipboardReader.Disposeと同じ手法）。
        if (!_thread.Join(TimeSpan.FromMilliseconds(200)) && _display != IntPtr.Zero)
        {
            var display = _display;
            _display = IntPtr.Zero;
            X11Interop.XCloseDisplay(display);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        if (_display != IntPtr.Zero)
        {
            var display = _display;
            _display = IntPtr.Zero;
            X11Interop.XCloseDisplay(display);
        }

        _commands.Dispose();
    }

    private sealed record SetTextCommand(string Text)
    {
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void RunLoop()
    {
        if (!InitializeWindow())
        {
            // ウィンドウ確保に失敗した場合、以後の要求はすべてfalseで応答する
            // （静かにAvalonia経由へフォールバックさせるため、例外は投げない）。
            _disposed = true;
            foreach (var command in _commands.GetConsumingEnumerable())
            {
                command.Completion.TrySetResult(false);
            }
            return;
        }

        while (!_commands.IsCompleted)
        {
            while (_commands.TryTake(out var command))
            {
                HandleSetText(command);
            }

            if (_commands.IsCompleted) break;

            try
            {
                ProcessAvailableEvents();
            }
            catch (Exception)
            {
                // 1件のイベント処理で予期しない例外が出ても、ループ自体は継続する。
            }

            if (!WaitForActivity()) break; // 接続エラー（Dispose時の強制closeを含む）。
        }

        _disposed = true;
        while (_commands.TryTake(out var command))
        {
            command.Completion.TrySetResult(false);
        }
    }

    private bool InitializeWindow()
    {
        try
        {
            var root = X11Interop.XDefaultRootWindow(_display);
            _window = X11Interop.XCreateSimpleWindow(_display, root, 0, 0, 1, 1, 0, IntPtr.Zero, IntPtr.Zero);
            if (_window == IntPtr.Zero) return false;

            _clipboardAtom = X11Interop.XInternAtom(_display, ClipboardAtomName, false);
            _utf8Atom = X11Interop.XInternAtom(_display, Utf8StringAtomName, false);
            _stringAtom = X11Interop.XInternAtom(_display, StringAtomName, false);
            _targetsAtom = X11Interop.XInternAtom(_display, TargetsAtomName, false);
            _incrAtom = X11Interop.XInternAtom(_display, IncrAtomName, false);
            _atomAtom = X11Interop.XInternAtom(_display, AtomAtomName, false);

            _chunkBytes = ComputeChunkBytes(_display);

            X11Interop.XFlush(_display);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 1回のXChangePropertyで安全に送れるバイト数を求める。サーバーの実際の上限
    /// （XMaxRequestSizeより算出）と<see cref="MaxChunkBytes"/>の小さい方を採用する。
    /// </summary>
    private static int ComputeChunkBytes(IntPtr display)
    {
        try
        {
            var maxUnits = X11Interop.XMaxRequestSize(display); // 4バイト単位
            if (maxUnits <= 0) return MaxChunkBytes;

            // リクエストヘッダ等のオーバーヘッド分を安全側に差し引く（100ワード=400バイト）。
            var safeUnits = Math.Max(maxUnits - 100, MinChunkBytes / 4);
            var bytes = safeUnits * 4L;
            return (int)Math.Clamp(bytes, MinChunkBytes, MaxChunkBytes);
        }
        catch (Exception)
        {
            return MaxChunkBytes;
        }
    }

    private void HandleSetText(SetTextCommand command)
    {
        try
        {
            var utf8 = Encoding.UTF8.GetBytes(command.Text);
            var latin1 = ToLatin1(command.Text);

            X11Interop.XSetSelectionOwner(_display, _clipboardAtom, _window, IntPtr.Zero); // time=CurrentTime
            X11Interop.XFlush(_display);

            var owner = X11Interop.XGetSelectionOwner(_display, _clipboardAtom);
            if (owner == _window)
            {
                _utf8Bytes = utf8;
                _latin1Bytes = latin1;
                command.Completion.TrySetResult(true);
            }
            else
            {
                command.Completion.TrySetResult(false);
            }
        }
        catch (Exception)
        {
            command.Completion.TrySetResult(false);
        }
    }

    private void ProcessAvailableEvents()
    {
        var buffer = new byte[X11Interop.XEventSize];
        while (X11Interop.XPending(_display) > 0)
        {
            X11Interop.XNextEvent(_display, buffer);
            HandleEvent(buffer);
        }
    }

    private void HandleEvent(byte[] buffer)
    {
        var type = X11Interop.GetEventType(buffer);
        if (type == X11Interop.SelectionRequest)
        {
            HandleSelectionRequest(buffer); // SelectionResponder側（partial）で実装。
        }
        else if (type == X11Interop.SelectionClear)
        {
            HandleSelectionClear(buffer);
        }
        // PropertyNotify等はINCR転送中の専用待ち（WaitForPropertyDelete）でのみ意味を持つため、
        // ここでは無視する（転送中でなければ読み捨てるだけでよい）。
    }

    private void HandleSelectionClear(byte[] buffer)
    {
        var (window, selection) = X11Interop.GetSelectionClearEvent(buffer);
        if (window == _window && selection == _clipboardAtom)
        {
            _utf8Bytes = null;
            _latin1Bytes = null;
        }
    }

    /// <summary>
    /// 次のXイベントを待つ。接続fdをpoll(2)で監視し、スレッドを無期限にブロックさせず
    /// タイムアウト時刻で確実に諦められるようにする（X11ClipboardReader.WaitForEventと同じ手法）。
    /// SelectionResponder側のINCR転送でも共用する。
    /// </summary>
    private bool WaitForEvent(DateTime deadline, byte[] buffer)
    {
        while (true)
        {
            if (X11Interop.XPending(_display) > 0)
            {
                X11Interop.XNextEvent(_display, buffer);
                return true;
            }

            var remainingMs = (deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remainingMs <= 0) return false;

            var timeoutMs = (int)Math.Min(remainingMs, int.MaxValue);
            if (PollConnection(timeoutMs) < 0) return false; // pollそのものの失敗（接続断など）。
        }
    }

    /// <summary>コマンドキューの巡回のために、Xイベントが来るかタイムアウトするまで短時間だけ待つ。</summary>
    private bool WaitForActivity() => PollConnection(PollTimeoutMs) >= 0;

    private int PollConnection(int timeoutMs)
    {
        var fd = X11Interop.XConnectionNumber(_display);
        var pollFds = new[] { new X11Interop.PollFd { Fd = fd, Events = X11Interop.PollIn } };
        return X11Interop.poll(pollFds, 1, timeoutMs);
    }

    private void SendSelectionNotify(IntPtr requestor, IntPtr selection, IntPtr target, IntPtr property, IntPtr time)
    {
        var eventBuffer = X11Interop.BuildSelectionNotifyEvent(requestor, selection, target, property, time);
        X11Interop.XSendEvent(_display, requestor, false, X11Interop.NoEventMask, eventBuffer);
        X11Interop.XFlush(_display);
    }

    /// <summary>
    /// ISO-8859-1（Latin-1）へ変換する。表現できない文字（0x00-0xFFの範囲外）は'?'に置き換える。
    /// X11に依存しない純粋ロジックのため、単体テスト（tests/Graft.Tests）で直接検証できる。
    /// </summary>
    internal static byte[] ToLatin1(string text)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            bytes[i] = c <= 0xFF ? (byte)c : (byte)'?';
        }
        return bytes;
    }
}
