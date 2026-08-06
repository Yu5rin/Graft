namespace Graft.Platform.Null;

/// <summary>
/// <see cref="ISingleInstanceGuard"/> の何もしない実装。多重起動防止の前面化に対応しない環境
/// （非Windows。Linux実装はフェーズL4でロックファイル方式を追加する）向け。
/// <see cref="TryAcquire"/> は常に取得成功（true）を返し、多重起動を禁止しない
/// （縮退として「防止しない」を選ぶ。誤って弾いてユーザーの操作を妨げないため）。
/// </summary>
public sealed class NullSingleInstanceGuard : ISingleInstanceGuard
{
    public bool IsSupported => false;

    public string? UnsupportedReason => "この環境では多重起動の防止に対応していません。";

    public bool TryAcquire(string name) => true;

    public void ActivateExistingInstance(string mainWindowTitle)
    {
        // 何もしない。
    }

    public void Dispose()
    {
        // 何もしない。
    }
}
