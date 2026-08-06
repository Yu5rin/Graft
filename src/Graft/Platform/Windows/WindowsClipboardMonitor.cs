using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Graft.Core;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="IClipboardMonitor"/> のWindows実装。v2.0での実装元は <c>Features/ClipboardWatcher.cs</c>。
/// <c>AddClipboardFormatListener</c> による変更検知のみを行い、ポーリングは一切行わない。
/// 取得したテキストがブロックヘッダのパターンを含む場合のみ <see cref="PatchDetected"/> を
/// 発火する。ロジックは移設元から変更していない。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardMonitor : IClipboardMonitor
{
    private const int RetryCount = 5;
    private const int RetryDelayMs = 50;

    private IntPtr _hwnd;
    private uint _excludeFormat;
    private bool _disposed;

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public bool IsEnabled { get; private set; }

    public event EventHandler<string>? PatchDetected;

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _excludeFormat = WindowsNativeMethods.RegisterClipboardFormat(
            "ExcludeClipboardContentFromMonitorProcessing");
    }

    public GraftResult<bool> Start()
    {
        if (IsEnabled)
        {
            return GraftResult<bool>.Ok(true);
        }

        if (!WindowsNativeMethods.AddClipboardFormatListener(_hwnd))
        {
            var win32Error = Marshal.GetLastWin32Error();
            return GraftResult<bool>.Fail(GraftIssue.Of(ErrorCode.E602,
                $"クリップボード監視リスナーの登録に失敗しました（Win32エラー: {win32Error}）。"));
        }

        IsEnabled = true;
        return GraftResult<bool>.Ok(true);
    }

    public void Stop()
    {
        if (!IsEnabled)
        {
            return;
        }

        WindowsNativeMethods.RemoveClipboardFormatListener(_hwnd);
        IsEnabled = false;
    }

    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (!IsEnabled || msg != WindowsNativeMethods.WmClipboardUpdate)
        {
            return false;
        }

        OnClipboardUpdated();
        return true;
    }

    /// <summary>
    /// クリップボード更新時の処理本体。読み取ったテキストはこのメソッドのローカル変数にのみ
    /// 存在し、パターン判定後はどこにも保持しない（フィールド・ログへの書き込みは一切行わない）。
    /// </summary>
    private void OnClipboardUpdated()
    {
        if (_excludeFormat != 0 && WindowsNativeMethods.IsClipboardFormatAvailable(_excludeFormat))
        {
            return;
        }

        var text = TryReadClipboardText();
        if (text is null)
        {
            return;
        }

        if (PatchTextDetector.LooksLikePatch(text))
        {
            PatchDetected?.Invoke(this, text);
        }
    }

    /// <summary>
    /// クリップボードから <c>CF_UNICODETEXT</c> を読み取る。<c>OpenClipboard</c> は他プロセスと
    /// 競合し得るため、失敗時は50ms間隔で最大5回リトライする。
    /// </summary>
    private string? TryReadClipboardText()
    {
        if (!WindowsNativeMethods.IsClipboardFormatAvailable(WindowsNativeMethods.CfUnicodeText))
        {
            return null;
        }

        for (var attempt = 0; attempt < RetryCount; attempt++)
        {
            if (WindowsNativeMethods.OpenClipboard(_hwnd))
            {
                try
                {
                    return ReadUnicodeText();
                }
                finally
                {
                    WindowsNativeMethods.CloseClipboard();
                }
            }

            Thread.Sleep(RetryDelayMs);
        }

        return null;
    }

    private static string? ReadUnicodeText()
    {
        var handle = WindowsNativeMethods.GetClipboardData(WindowsNativeMethods.CfUnicodeText);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var ptr = WindowsNativeMethods.GlobalLock(handle);
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
            WindowsNativeMethods.GlobalUnlock(handle);
        }
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
