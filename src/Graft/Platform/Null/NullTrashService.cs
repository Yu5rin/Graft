namespace Graft.Platform.Null;

/// <summary>
/// <see cref="ITrashService"/> の何もしない実装。ごみ箱への削除に対応しない環境
/// （非Windows。Linux実装はフェーズL4）向け。仕様書2.3のとおり、対応できない場合は
/// 無言で消さず、呼び出し側（<c>IsSupported</c> を見て確認ダイアログを出す）に判断を委ねる
/// ため、本実装は常に <c>false</c> を返すのみで実際の削除は一切行わない。
/// </summary>
public sealed class NullTrashService : ITrashService
{
    public bool IsSupported => false;

    public string? UnsupportedReason => "この環境ではごみ箱への削除に対応していません。";

    public bool Send(string path) => false;
}
