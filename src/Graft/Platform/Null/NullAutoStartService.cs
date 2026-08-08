namespace Graft.Platform.Null;

/// <summary>
/// <see cref="IAutoStartService"/> の何もしない実装。Windows/Linux以外の環境向け
/// （仕様書2.3のとおり、対応できない場合は無言で失敗させず、呼び出し側が
/// <see cref="IsSupported"/> を見て選択肢自体を隠すか、選んでも失敗を明示する）。
/// </summary>
public sealed class NullAutoStartService : IAutoStartService
{
    public bool IsSupported => false;

    public string? UnsupportedReason => "この環境では自動起動に対応していません。";

    public bool IsRegistered => false;

    public AutoStartResult Enable() => AutoStartResult.Fail(UnsupportedReason!);

    public AutoStartResult Disable() => AutoStartResult.Ok(); // 登録され得ないため解除は常に成功扱い。
}
