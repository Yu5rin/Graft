using Graft.Core;

namespace Graft.Platform.Null;

/// <summary>
/// <see cref="IGlobalHotkeys"/> の何もしない実装。グローバルホットキーに対応しない環境
/// （非Windows。Linux実装はフェーズL4。Wayland上のLinuxでは恒久的に利用不可）向け。
/// 仕様書2.3のとおり、利用不可でもアプリ内ホットキーのみで動作を継続できるよう
/// <see cref="Register"/> は例外を投げず <c>E601</c> 付きの失敗を返す。
/// </summary>
public sealed class NullGlobalHotkeys : IGlobalHotkeys
{
    public bool IsSupported => false;

    public string? UnsupportedReason => "この環境ではグローバルホットキーを利用できません。";

    public void Attach(IntPtr hwnd)
    {
        // 何もしない。
    }

    public GraftResult<int> Register(string gesture, Action callback)
        => GraftResult<int>.Fail(GraftIssue.Of(ErrorCode.E601,
            "この環境ではグローバルホットキーを利用できません。", severity: Severity.Warning));

    public void UnregisterAll()
    {
        // 何もしない。
    }

    public bool HandleMessage(int msg, IntPtr wParam, IntPtr lParam) => false;

    public void Dispose()
    {
        // 何もしない。
    }
}
