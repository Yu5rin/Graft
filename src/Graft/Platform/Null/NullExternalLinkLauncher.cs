namespace Graft.Platform.Null;

/// <summary>
/// <see cref="IExternalLinkLauncher"/> の何もしない実装。ブラウザ起動に対応しない環境
/// （非Windows・非Linux）向け。
/// </summary>
public sealed class NullExternalLinkLauncher : IExternalLinkLauncher
{
    public bool IsSupported => false;

    public string? UnsupportedReason => "この環境では既定のブラウザを開く操作に対応していません。";

    public void Open(string url)
    {
        // 何もしない。
    }
}
