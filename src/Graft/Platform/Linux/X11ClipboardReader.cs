using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Graft.Infra;

namespace Graft.Platform.Linux;

/// <summary>
/// 自前のX11接続でCLIPBOARDセレクションを読み取るリーダー（実機バグ修正）。
///
/// 背景: AvaloniaのX11クリップボード実装は内部で要求を直列に処理しており、所有アプリが
/// 応答しない瞬間に読み取りを行うなどして一度でも要求が完了しないまま残ると、以後の
/// すべての読み取りが永久に完了しなくなる（呼び出し側でタイムアウトさせてawaitを
/// 放棄しても、Avalonia内部の詰まりは解消されない）。本クラスはAvaloniaを経由せず、
/// 専用のX11接続・専用スレッドのイベントループで読み取りを行うことで、1回の要求の
/// 失敗・タイムアウトが次回以降の要求に影響しないようにする。
///
/// 要求は<see cref="_requests"/>で1つずつ直列に処理する（同時に複数の変換要求を
/// X11へ投げない）。書き込み（コピー）は本クラスの対象外で、従来どおりAvalonia側が担う。
/// </summary>
public sealed class X11ClipboardReader : IDisposable
{
    // ICCCMで定義されたセレクション・ターゲットのアトム名。GRAFT_CLIPBOARD_READはこの
    // プロセス専用の受信用プロパティ名で、他アプリと衝突しないよう独自の名前にしている。
    private const string ClipboardAtomName = "CLIPBOARD";
    private const string Utf8StringAtomName = "UTF8_STRING";
    private const string StringAtomName = "STRING";
    private const string IncrAtomName = "INCR";
    private const string ReplyPropertyName = "GRAFT_CLIPBOARD_READ";

    // INCR転送（大きなデータの分割転送）1件あたりの上限。通常のクリップボード用途では
    // 十分すぎる大きさ（64MB）で、相手側の不具合で転送が終わらない場合の無制限メモリ確保・
    // 無限ループを防ぐ安全弁として設ける。
    private const int MaxIncrBytes = 64 * 1024 * 1024;

    private readonly BlockingCollection<ReadRequest> _requests = new();
    private readonly Thread _thread;

    private IntPtr _display;
    private IntPtr _window;
    private IntPtr _clipboardAtom;
    private IntPtr _utf8Atom;
    private IntPtr _stringAtom;
    private IntPtr _incrAtom;
    private IntPtr _replyAtom;

    private volatile bool _disposed;

    private X11ClipboardReader(IntPtr display)
    {
        _display = display;
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "Graft X11 clipboard reader" };
        _thread.Start();
    }

    /// <summary>
    /// プロセス内で共有する既定インスタンス。対話的な貼り付け（<see cref="AvaloniaClipboardAccess"/>）と
    /// バックグラウンドのクリップボード監視（<see cref="LinuxClipboardMonitor"/>）が同じX11接続を
    /// 共有することで、詰まりの影響が一箇所（この接続）に閉じ込められ、双方が互いに影響されない。
    /// X11に接続できない環境（Waylandのみ等）ではnullで、呼び出し側は静かにAvalonia経由へ縮退する。
    /// </summary>
    public static X11ClipboardReader? Shared => LazyShared.Value;

    private static readonly Lazy<X11ClipboardReader?> LazyShared = new(TryCreate);

    /// <summary>
    /// X11に接続できる環境であれば専用接続を確立して返す。接続できない環境
    /// （Waylandのみ・libX11が見つからない等）ではnullを返す。
    /// </summary>
    public static X11ClipboardReader? TryCreate()
    {
        try
        {
            var display = X11Interop.XOpenDisplay(null);
            return display == IntPtr.Zero ? null : new X11ClipboardReader(display);
        }
        catch (DllNotFoundException)
        {
            return null; // libX11が無い環境（Wayland専用構成など）。
        }
    }

    /// <summary>CLIPBOARDセレクションのテキストを読み取る。取得できない・タイムアウトした場合はnull。</summary>
    public Task<string?> ReadTextAsync(TimeSpan timeout)
    {
        var request = new ReadRequest(timeout);
        try
        {
            if (_disposed || !_requests.TryAdd(request))
            {
                return Task.FromResult<string?>(null);
            }
        }
        catch (InvalidOperationException)
        {
            // Disposeとの競合でキューが締め切られた直後に呼ばれた場合。
            return Task.FromResult<string?>(null);
        }

        return request.Completion.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _requests.CompleteAdding();

        // よくある「アイドル中（要求待ち）にDisposeされる」場合は、CompleteAddingだけで
        // ループが速やかに終了する。稀に応答待ち中（poll/XNextEvent）にDisposeされた場合に
        // 備え、短時間で終わらなければ接続を閉じて待機を強制的に解く
        // （LinuxGlobalHotkeys.Disposeと同じ手法。接続が閉じるとXNextEvent/pollが異常終了し、
        // ループスレッドが終了する）。
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

        _requests.Dispose();
    }

    private sealed record ReadRequest(TimeSpan Timeout)
    {
        public TaskCompletionSource<string?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void RunLoop()
    {
        if (!InitializeWindow())
        {
            // ウィンドウ確保に失敗した場合、以後の要求はすべてnullで応答する
            // （静かにAvalonia経由へフォールバックさせるため、例外は投げない）。
            foreach (var request in _requests.GetConsumingEnumerable())
            {
                request.Completion.TrySetResult(null);
            }
            return;
        }

        foreach (var request in _requests.GetConsumingEnumerable())
        {
            string? result;
            try
            {
                result = ProcessRequest(request.Timeout);
            }
            catch (Exception ex)
            {
                // 1件の要求で予期しない例外が出ても、次の要求の処理は続ける。
                // これが本クラスの目的（1回の失敗・タイムアウトが以後に影響しないこと）そのもの。
                // v1.0.7: 種類ごとの発生回数だけを数える（詳細は出さない。高頻度経路のため）。
                // 終了時のshutdownログに集計される（SuppressedExceptionTracker参照）。
                SuppressedExceptionTracker.Shared.Record("clipboard-x11-read-request", ex);
                result = null;
            }
            request.Completion.TrySetResult(result);
        }
    }

    private bool InitializeWindow()
    {
        try
        {
            var root = X11Interop.XDefaultRootWindow(_display);
            _window = X11Interop.XCreateSimpleWindow(_display, root, 0, 0, 1, 1, 0, IntPtr.Zero, IntPtr.Zero);
            if (_window == IntPtr.Zero) return false;

            // INCR転送の断片到着（PropertyNotify）を受け取れるようにする。
            X11Interop.XSelectInput(_display, _window, X11Interop.PropertyChangeMask);

            _clipboardAtom = X11Interop.XInternAtom(_display, ClipboardAtomName, false);
            _utf8Atom = X11Interop.XInternAtom(_display, Utf8StringAtomName, false);
            _stringAtom = X11Interop.XInternAtom(_display, StringAtomName, false);
            _incrAtom = X11Interop.XInternAtom(_display, IncrAtomName, false);
            _replyAtom = X11Interop.XInternAtom(_display, ReplyPropertyName, false);

            X11Interop.XFlush(_display);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>1件の読み取り要求を処理する。UTF8_STRINGを試し、拒否されたらSTRINGで再試行する。</summary>
    private string? ProcessRequest(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        // 前回の要求が残した未処理イベント（タイムアウトで諦めた要求への遅延応答等）が
        // 今回の応答と誤認されないよう、開始前に読み捨てておく。
        DrainPendingEvents();

        return TryReadWithTarget(_utf8Atom, isUtf8: true, deadline)
            ?? TryReadWithTarget(_stringAtom, isUtf8: false, deadline);
    }

    /// <summary>
    /// 指定ターゲット（UTF8_STRING/STRING）でCLIPBOARDの変換を要求し、結果を読み取る。
    /// 変換自体が拒否された（所有者が無い・対応していない）場合やタイムアウトの場合はnull。
    /// </summary>
    private string? TryReadWithTarget(IntPtr target, bool isUtf8, DateTime deadline)
    {
        X11Interop.XDeleteProperty(_display, _window, _replyAtom);
        X11Interop.XConvertSelection(_display, _clipboardAtom, target, _replyAtom, _window, IntPtr.Zero);
        X11Interop.XFlush(_display);

        if (!WaitForSelectionNotify(target, deadline, out var property)) return null;
        if (property == IntPtr.Zero) return null; // 変換が拒否された（所有者が無い等、property=None）。

        return ReadProperty(isUtf8, deadline);
    }

    private bool WaitForSelectionNotify(IntPtr expectedTarget, DateTime deadline, out IntPtr property)
    {
        property = IntPtr.Zero;
        var buffer = new byte[X11Interop.XEventSize];

        while (true)
        {
            if (!WaitForEvent(deadline, buffer)) return false;
            if (X11Interop.GetEventType(buffer) != X11Interop.SelectionNotify) continue;

            var (selection, target, prop, requestor) = X11Interop.GetSelectionEvent(buffer);
            if (requestor != _window || selection != _clipboardAtom || target != expectedTarget) continue;

            property = prop;
            return true;
        }
    }

    private string? ReadProperty(bool isUtf8, DateTime deadline)
    {
        // long_lengthは常に「4バイト単位」で指定する（format=8でも同じ）ため、
        // 取得したい最大バイト数を4で割った値を渡す。
        var result = X11Interop.XGetWindowProperty(
            _display, _window, _replyAtom, 0, MaxIncrBytes / 4, false, IntPtr.Zero,
            out var actualType, out var actualFormat, out var nitems, out _, out var propPtr);

        try
        {
            if (result != X11Interop.Success) return null;
            if (actualType == _incrAtom) return ReceiveIncr(isUtf8, deadline);
            if (actualType == IntPtr.Zero || actualFormat != 8) return null;

            return DecodeText(CopyPropertyBytes(propPtr, nitems), isUtf8);
        }
        finally
        {
            if (propPtr != IntPtr.Zero) X11Interop.XFree(propPtr);
        }
    }

    /// <summary>
    /// INCR転送（ICCCM）を受信する。プロパティを削除して送信側に開始を促し、以後は
    /// PropertyNotify(NewValue)を合図に断片を読み進め、長さ0の断片で終端とみなす。
    /// </summary>
    private string? ReceiveIncr(bool isUtf8, DateTime deadline)
    {
        X11Interop.XDeleteProperty(_display, _window, _replyAtom);
        X11Interop.XFlush(_display);

        var chunks = new List<byte[]>();
        var totalBytes = 0;
        var buffer = new byte[X11Interop.XEventSize];

        while (true)
        {
            if (!WaitForPropertyNewValue(deadline, buffer)) return null;

            var result = X11Interop.XGetWindowProperty(
                _display, _window, _replyAtom, 0, MaxIncrBytes / 4, true, IntPtr.Zero,
                out _, out _, out var nitems, out _, out var propPtr);

            try
            {
                if (result != X11Interop.Success) return null;
                if (nitems == 0) break; // 長さ0の断片が転送終了の合図（ICCCM）。

                var chunk = CopyPropertyBytes(propPtr, nitems);
                totalBytes += chunk.Length;
                if (totalBytes > MaxIncrBytes) return null; // 安全弁超過（相手側の不具合とみなし諦める）。
                chunks.Add(chunk);
            }
            finally
            {
                if (propPtr != IntPtr.Zero) X11Interop.XFree(propPtr);
            }
        }

        return DecodeText(JoinChunks(chunks), isUtf8);
    }

    private bool WaitForPropertyNewValue(DateTime deadline, byte[] buffer)
    {
        while (true)
        {
            if (!WaitForEvent(deadline, buffer)) return false;
            if (X11Interop.GetEventType(buffer) != X11Interop.PropertyNotify) continue;

            var (atom, state) = X11Interop.GetPropertyEvent(buffer);
            if (atom != _replyAtom || state != X11Interop.PropertyNewValue) continue;

            return true;
        }
    }

    /// <summary>
    /// 次のXイベントを待つ。接続fdをpoll(2)で監視することで、スレッドを無期限に
    /// ブロックさせずタイムアウト時刻で確実に諦められるようにする。poll(2)がデータ到着を
    /// 示してもXPendingがまだ0（受信途中の断片等）ということがあり得るため、その場合は
    /// 失敗と決めつけず残り時間内でループし直す。
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

            var fd = X11Interop.XConnectionNumber(_display);
            var pollFds = new[] { new X11Interop.PollFd { Fd = fd, Events = X11Interop.PollIn } };
            var timeoutMs = (int)Math.Min(remainingMs, int.MaxValue);
            var ready = X11Interop.poll(pollFds, 1, timeoutMs);
            if (ready < 0) return false; // pollそのものの失敗（接続断など）。
            // ready==0（純粋なタイムアウト）・readyだがXPendingがまだ0（受信途中）のいずれも
            // ループへ戻り、残り時間があれば再試行する。
        }
    }

    private void DrainPendingEvents()
    {
        var buffer = new byte[X11Interop.XEventSize];
        while (X11Interop.XPending(_display) > 0)
        {
            X11Interop.XNextEvent(_display, buffer);
        }
    }

    private static byte[] CopyPropertyBytes(IntPtr propPtr, long nitems)
    {
        if (propPtr == IntPtr.Zero || nitems <= 0) return Array.Empty<byte>();

        var length = (int)Math.Min(nitems, int.MaxValue);
        var bytes = new byte[length];
        Marshal.Copy(propPtr, bytes, 0, length);
        return bytes;
    }

    /// <summary>
    /// INCR転送で分割受信した断片を1つに結合する。X11に依存しない純粋ロジックのため、
    /// 単体テスト（tests/Graft.Tests）で直接検証できる。
    /// </summary>
    internal static byte[] JoinChunks(IReadOnlyList<byte[]> chunks)
    {
        var total = 0;
        foreach (var chunk in chunks) total += chunk.Length;

        var result = new byte[total];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }
        return result;
    }

    /// <summary>
    /// X11から読み取ったバイト列をテキストへ変換する。UTF8_STRINGターゲットで得たデータはUTF-8、
    /// STRINGターゲットで得たデータはISO-8859-1（X11のSTRING型の定義）としてデコードする。
    /// X11に依存しない純粋ロジックのため、単体テスト（tests/Graft.Tests）で直接検証できる。
    /// </summary>
    internal static string DecodeText(byte[] bytes, bool isUtf8)
        => isUtf8 ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
}
