namespace Graft.Platform.Null;

/// <summary>
/// <see cref="ITrayIcon"/> の何もしない実装。トレイ常駐に対応しない環境
/// （非Windows。Linux実装はフェーズL4）向け。呼び出し側は例外もnull判定も気にせず使える。
/// </summary>
public sealed class NullTrayIcon : ITrayIcon
{
    public bool IsSupported => false;

    public string? UnsupportedReason => "この環境ではトレイ常駐に対応していません。";

    public void Configure(TrayMenuDescriptor menu)
    {
        // 何もしない。
    }

    public void Show()
    {
        // 何もしない。
    }

    public void ShowBalloon(string title, string text)
    {
        // 何もしない。
    }

    public void Dispose()
    {
        // 何もしない。
    }
}
