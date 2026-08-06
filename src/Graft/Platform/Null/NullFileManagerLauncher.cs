namespace Graft.Platform.Null;

/// <summary>
/// <see cref="IFileManagerLauncher"/> の何もしない実装。ファイルマネージャでの表示に対応しない
/// 環境（非Windows。Linux実装はフェーズL4）向け。
/// </summary>
public sealed class NullFileManagerLauncher : IFileManagerLauncher
{
    public bool IsSupported => false;

    public string? UnsupportedReason => "この環境ではファイルマネージャでの表示に対応していません。";

    public void Reveal(string fullPath)
    {
        // 何もしない。
    }
}
