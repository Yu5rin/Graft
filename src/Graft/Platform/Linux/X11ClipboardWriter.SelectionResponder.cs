namespace Graft.Platform.Linux;

/// <summary>
/// <see cref="X11ClipboardWriter"/>のうち、SelectionRequestイベントへの実応答
/// （TARGETS/UTF8_STRING/STRINGの返却、大きなデータのINCR転送）を担うpartial。
/// 1ファイル400行以内の方針のため<c>X11ClipboardWriter.cs</c>から分けている。
/// </summary>
public sealed partial class X11ClipboardWriter
{
    // INCR転送1件の全体締め切り。相手側が応答を止めた場合に無限に待ち続けないための安全弁。
    private static readonly TimeSpan IncrOverallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 読み取り要求への応答。TARGETS・UTF8_STRING・STRINGのみ対応する。MULTIPLEを含む
    /// それ以外のターゲットは、内容を解釈せずproperty=Noneで拒否する（対応不要かつ、
    /// 誤った解釈によるクラッシュを避けるため）。
    /// </summary>
    private void HandleSelectionRequest(byte[] buffer)
    {
        var (_, requestor, selection, target, property, time) = X11Interop.GetSelectionRequestEvent(buffer);

        if (selection != _clipboardAtom || _utf8Bytes is null || _latin1Bytes is null)
        {
            SendSelectionNotify(requestor, selection, target, IntPtr.Zero, time);
            return;
        }

        // ICCCMの後方互換: propertyがNone（X10方式の古いクライアント）の場合はtarget名を
        // プロパティ名として使う。
        var replyProperty = property == IntPtr.Zero ? target : property;

        if (target == _targetsAtom)
        {
            RespondTargets(requestor, replyProperty, selection, time);
        }
        else if (target == _utf8Atom || target == _stringAtom)
        {
            var bytes = target == _utf8Atom ? _utf8Bytes : _latin1Bytes;
            RespondText(requestor, replyProperty, selection, target, time, bytes);
        }
        else
        {
            SendSelectionNotify(requestor, selection, target, IntPtr.Zero, time);
        }
    }

    private void RespondTargets(IntPtr requestor, IntPtr property, IntPtr selection, IntPtr time)
    {
        var atoms = new[] { _targetsAtom, _utf8Atom, _stringAtom };
        var data = new byte[atoms.Length * 8]; // format=32の要素はXlib上8バイト（native long）単位。
        for (var i = 0; i < atoms.Length; i++)
        {
            BitConverter.GetBytes(atoms[i].ToInt64()).CopyTo(data, i * 8);
        }

        X11Interop.XChangeProperty(_display, requestor, property, _atomAtom, 32, X11Interop.PropModeReplace, data, atoms.Length);
        X11Interop.XFlush(_display);
        SendSelectionNotify(requestor, selection, _targetsAtom, property, time);
    }

    private void RespondText(IntPtr requestor, IntPtr property, IntPtr selection, IntPtr target, IntPtr time, byte[] bytes)
    {
        if (bytes.Length <= _chunkBytes)
        {
            X11Interop.XChangeProperty(_display, requestor, property, target, 8, X11Interop.PropModeReplace, bytes, bytes.Length);
            X11Interop.XFlush(_display);
            SendSelectionNotify(requestor, selection, target, property, time);
            return;
        }

        SendIncr(requestor, property, selection, target, time, bytes);
    }

    /// <summary>
    /// INCR転送（ICCCM）で送出する。まずtype=INCR・値=全体バイト数のプロパティを立てて
    /// SelectionNotifyを送り、以後は要求元がプロパティを削除する（次の断片を要求する）たびに
    /// 断片を書き込む。長さ0の断片を送って転送終了を伝える。
    /// </summary>
    private void SendIncr(IntPtr requestor, IntPtr property, IntPtr selection, IntPtr target, IntPtr time, byte[] bytes)
    {
        var deadline = DateTime.UtcNow + IncrOverallTimeout;

        // 要求元windowでのプロパティ削除（次の断片の催促）を検知できるようにする。
        // 要求元自身が別途選択しているイベントマスクとは独立に扱われるため、干渉しない。
        X11Interop.XSelectInput(_display, requestor, X11Interop.PropertyChangeMask);

        var sizeData = BitConverter.GetBytes((long)bytes.Length);
        X11Interop.XChangeProperty(_display, requestor, property, _incrAtom, 32, X11Interop.PropModeReplace, sizeData, 1);
        X11Interop.XFlush(_display);
        SendSelectionNotify(requestor, selection, target, property, time);

        var offset = 0;
        var buffer = new byte[X11Interop.XEventSize];

        while (true)
        {
            if (!WaitForPropertyDelete(requestor, property, deadline, buffer)) return; // タイムアウト・所有権喪失などで断念。

            var remaining = bytes.Length - offset;
            var chunkLength = Math.Min(remaining, _chunkBytes);
            var chunk = new byte[chunkLength];
            if (chunkLength > 0) Buffer.BlockCopy(bytes, offset, chunk, 0, chunkLength);
            offset += chunkLength;

            X11Interop.XChangeProperty(_display, requestor, property, target, 8, X11Interop.PropModeReplace, chunk, chunkLength);
            X11Interop.XFlush(_display);

            if (chunkLength == 0) return; // 長さ0の断片が転送終了の合図（ICCCM）。
        }
    }

    private bool WaitForPropertyDelete(IntPtr requestor, IntPtr property, DateTime deadline, byte[] buffer)
    {
        while (true)
        {
            if (_disposed) return false;
            if (!WaitForEvent(deadline, buffer)) return false;

            var type = X11Interop.GetEventType(buffer);
            if (type == X11Interop.SelectionClear)
            {
                HandleSelectionClear(buffer);
                continue; // このINCR転送自体は最後まで送り切る（相手が既に受信を始めているため）。
            }
            if (type != X11Interop.PropertyNotify) continue;

            var (atom, state) = X11Interop.GetPropertyEvent(buffer);
            var window = X11Interop.GetPropertyEventWindow(buffer);
            if (window != requestor || atom != property || state != X11Interop.PropertyDelete) continue;

            return true;
        }
    }
}
