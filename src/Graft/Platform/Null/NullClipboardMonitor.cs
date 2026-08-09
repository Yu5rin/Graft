using Graft.Core;

namespace Graft.Platform.Null;

/// <summary>
/// <see cref="IClipboardMonitor"/> の何もしない実装。クリップボード監視に対応しない環境
/// （非Windows。Linux実装はフェーズL4。Wayland上のLinuxでは恒久的に利用不可）向け。
/// 仕様書2.3のとおり、利用不可でも手動での取り込み（Ctrl+Shift+V）は常に使えるため、
/// <see cref="Start"/> は例外を投げず <c>E602</c> 付きの失敗を返すのみとする。
/// </summary>
public sealed class NullClipboardMonitor : IClipboardMonitor
{
    public bool IsSupported => false;

    public string? UnsupportedReason => "この環境ではクリップボード監視を利用できません。";

    public bool IsEnabled => false;

    public event EventHandler<string>? PatchDetected
    {
        add { }
        remove { }
    }

    public event EventHandler? NonPatchTextChanged
    {
        add { }
        remove { }
    }

    public void Attach(IntPtr hwnd)
    {
        // 何もしない。
    }

    public GraftResult<bool> Start()
        => GraftResult<bool>.Ok(false, new[]
        {
            GraftIssue.Of(ErrorCode.E602,
                "この環境ではクリップボード監視を利用できません。", severity: Severity.Warning),
        });

    public void Stop()
    {
        // 何もしない。
    }

    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam) => false;

    public void Dispose()
    {
        // 何もしない。
    }
}
