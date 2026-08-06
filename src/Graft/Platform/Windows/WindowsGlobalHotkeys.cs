using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Graft.Core;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="IGlobalHotkeys"/> のWindows実装。v2.0での実装元は <c>Features/HotkeyManager.cs</c>。
/// <c>RegisterHotKey</c> により "Ctrl+Alt+V" などの "修飾キー+キー" 形式の文字列を解釈して
/// 登録する。登録失敗（他アプリが使用中など）は例外を投げず <see cref="GraftResult{T}"/> の
/// 失敗として返す。ロジックは移設元から変更していない。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalHotkeys : IGlobalHotkeys
{
    private readonly Dictionary<int, Action> _callbacks = new();
    private IntPtr _hwnd;
    private int _nextId = 1;
    private bool _disposed;

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public void Attach(IntPtr hwnd) => _hwnd = hwnd;

    public GraftResult<int> Register(string gesture, Action callback)
    {
        var parsed = ParseGesture(gesture);
        if (parsed is null)
        {
            return GraftResult<int>.Fail(GraftIssue.Of(ErrorCode.E601,
                $"キー指定を解釈できません: '{gesture}'。修飾キー(Ctrl/Alt/Shift/Win)と英数字またはファンクションキーの組み合わせで指定してください。"));
        }

        var (modifiers, virtualKey) = parsed.Value;
        var id = _nextId++;

        if (!WindowsNativeMethods.RegisterHotKey(_hwnd, id, modifiers | WindowsNativeMethods.ModNoRepeat, virtualKey))
        {
            var win32Error = Marshal.GetLastWin32Error();
            return GraftResult<int>.Fail(GraftIssue.Of(ErrorCode.E601,
                $"ホットキー '{gesture}' の登録に失敗しました。他のアプリケーションが使用中の可能性があります（Win32エラー: {win32Error}）。"));
        }

        _callbacks[id] = callback;
        return GraftResult<int>.Ok(id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _callbacks.Keys)
        {
            WindowsNativeMethods.UnregisterHotKey(_hwnd, id);
        }
        _callbacks.Clear();
    }

    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WindowsNativeMethods.WmHotkey)
        {
            return false;
        }

        var id = wParam.ToInt32();
        if (_callbacks.TryGetValue(id, out var callback))
        {
            callback();
            return true;
        }

        return false;
    }

    /// <summary>
    /// "Ctrl+Alt+V" 形式の文字列を修飾子フラグと仮想キーコードに分解する。
    /// 修飾キーが1つ以上、末尾に英数字またはファンクションキーを1つだけ要求する。
    /// </summary>
    private static (uint Modifiers, uint VirtualKey)? ParseGesture(string gesture)
    {
        var tokens = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return null;
        }

        uint modifiers = 0;
        uint? virtualKey = null;

        foreach (var token in tokens)
        {
            var modifierFlag = ResolveModifier(token);
            if (modifierFlag is not null)
            {
                modifiers |= modifierFlag.Value;
                continue;
            }

            if (virtualKey is not null)
            {
                return null;
            }

            virtualKey = ResolveVirtualKey(token);
            if (virtualKey is null)
            {
                return null;
            }
        }

        if (virtualKey is null || modifiers == 0)
        {
            return null;
        }

        return (modifiers, virtualKey.Value);
    }

    private static uint? ResolveModifier(string token) => token.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => WindowsNativeMethods.ModControl,
        "ALT" => WindowsNativeMethods.ModAlt,
        "SHIFT" => WindowsNativeMethods.ModShift,
        "WIN" or "WINDOWS" => WindowsNativeMethods.ModWin,
        _ => null,
    };

    /// <summary>
    /// 英数字1文字またはファンクションキー（F1〜F24）を仮想キーコードへ変換する。
    /// 英字の仮想キーコードは 'A'〜'Z'、数字は '0'〜'9' の文字コードと一致する。
    /// </summary>
    private static uint? ResolveVirtualKey(string token)
    {
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return c;
            }

            return null;
        }

        if (token.Length is 2 or 3 && char.ToUpperInvariant(token[0]) == 'F'
            && int.TryParse(token.AsSpan(1), out var functionNumber)
            && functionNumber is >= 1 and <= 24)
        {
            const uint VkF1 = 0x70;
            return VkF1 + (uint)(functionNumber - 1);
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        UnregisterAll();
        _disposed = true;
    }
}
