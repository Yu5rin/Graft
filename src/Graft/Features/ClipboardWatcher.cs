using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Graft.Core;

namespace Graft.Features;

/// <summary>
/// クリップボード監視（9章）。<c>AddClipboardFormatListener</c> による変更検知のみを行い、
/// ポーリングは一切行わない。取得したテキストがブロックヘッダのパターンを含む場合のみ
/// <see cref="PatchDetected"/> を発火する。反応時にトレイ通知・ウィンドウ表示のどれを
/// 行うかはUI層の責務であり、本クラスはイベント発火のみを担う。
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    /// <summary>OpenClipboard 失敗時の最大リトライ回数。</summary>
    private const int RetryCount = 5;

    /// <summary>OpenClipboard 失敗時のリトライ間隔（ミリ秒）。</summary>
    private const int RetryDelayMs = 50;

    /// <summary>
    /// ブロックヘッダとして認識する行頭パターン（4章）。
    /// これ以外のコピー内容には一切反応しない。
    /// </summary>
    private static readonly string[] PatchHeaderPrefixes =
    {
        "<<<< FILE:",
        "<<<< PATCH",
        "<<<< DELETE:",
        "<<<< RENAME:",
        "<<<< MKDIR:",
        "<<<< APPEND:",
        "<<<< PREPEND:",
        "<<<<<<< SEARCH",
    };

    private readonly IntPtr _hwnd;

    /// <summary>
    /// パスワードマネージャ等が設定する除外用フォーマットの識別子。
    /// 非Windows環境では使用しないため 0 のままとする。
    /// </summary>
    private readonly uint _excludeFormat;

    private bool _disposed;

    public ClipboardWatcher(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _excludeFormat = OperatingSystem.IsWindows()
            ? NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing")
            : 0;
    }

    /// <summary>監視が現在有効かどうか。</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>
    /// ブロックヘッダのパターンを含むテキストがクリップボードに現れたときに発火する。
    /// それ以外のコピー操作では一切発火しない。
    /// </summary>
    public event EventHandler<string>? PatchDetected;

    /// <summary>
    /// クリップボード変更通知の受信を開始する。非Windows環境では何もせず、
    /// 利用できない旨の警告付きで成功を返す（設定でOFFにできる要件・テスト容易性のため）。
    /// </summary>
    public GraftResult<bool> Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            IsEnabled = false;
            return GraftResult<bool>.Ok(false, new[]
            {
                GraftIssue.Of(ErrorCode.E602,
                    "この環境（非Windows）ではクリップボード監視を利用できません。",
                    severity: Severity.Warning),
            });
        }

        if (IsEnabled)
        {
            return GraftResult<bool>.Ok(true);
        }

        if (!NativeMethods.AddClipboardFormatListener(_hwnd))
        {
            var win32Error = Marshal.GetLastWin32Error();
            return GraftResult<bool>.Fail(GraftIssue.Of(ErrorCode.E602,
                $"クリップボード監視リスナーの登録に失敗しました（Win32エラー: {win32Error}）。"));
        }

        IsEnabled = true;
        return GraftResult<bool>.Ok(true);
    }

    /// <summary>クリップボード変更通知の受信を停止する。</summary>
    public void Stop()
    {
        if (!OperatingSystem.IsWindows() || !IsEnabled)
        {
            return;
        }

        NativeMethods.RemoveClipboardFormatListener(_hwnd);
        IsEnabled = false;
    }

    /// <summary>
    /// ウィンドウプロシージャから転送されたメッセージを処理する。
    /// <c>WM_CLIPBOARDUPDATE</c> であれば処理して true を返す。
    /// </summary>
    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (!IsEnabled || msg != NativeMethods.WM_CLIPBOARDUPDATE)
        {
            return false;
        }

        OnClipboardUpdated();
        return true;
    }

    /// <summary>
    /// クリップボード更新時の処理本体。読み取ったテキストはこのメソッドのローカル変数
    /// にのみ存在し、パターン判定後はどこにも保持しない（フィールド・ログへの書き込みは
    /// 一切行わない）。ブロックヘッダを含まない場合はメソッドを抜けた時点でGC対象となり
    /// 内容は残らない。
    /// </summary>
    private void OnClipboardUpdated()
    {
        // パスワードマネージャ等が「監視除外」を要求している場合は読み取り自体を行わない。
        if (_excludeFormat != 0 && NativeMethods.IsClipboardFormatAvailable(_excludeFormat))
        {
            return;
        }

        var text = TryReadClipboardText();
        if (text is null)
        {
            return;
        }

        if (LooksLikePatch(text))
        {
            PatchDetected?.Invoke(this, text);
        }
        // text はここで参照を失う。フィールドやキャッシュへの代入は行っていない。
    }

    /// <summary>
    /// クリップボードから <c>CF_UNICODETEXT</c> を読み取る。<c>OpenClipboard</c> は
    /// 他プロセスと競合し得るため、失敗時は50ms間隔で最大5回リトライする。
    /// </summary>
    private string? TryReadClipboardText()
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_UNICODETEXT))
        {
            return null;
        }

        for (var attempt = 0; attempt < RetryCount; attempt++)
        {
            if (NativeMethods.OpenClipboard(_hwnd))
            {
                try
                {
                    return ReadUnicodeText();
                }
                finally
                {
                    NativeMethods.CloseClipboard();
                }
            }

            Thread.Sleep(RetryDelayMs);
        }

        return null;
    }

    private static string? ReadUnicodeText()
    {
        var handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var ptr = NativeMethods.GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// テキストがブロックヘッダのパターンを行頭に含むかどうかを判定する。
    /// パッチらしいと判定できない通常のコピー内容はここで弾かれ、以降一切処理しない。
    /// </summary>
    public static bool LooksLikePatch(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            foreach (var prefix in PatchHeaderPrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }
}
