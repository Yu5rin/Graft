using Graft.Platform.Null;
using Graft.Platform.Windows;

namespace Graft.Platform;

/// <summary>
/// 実行中のOSに応じて <see cref="IPlatformServices"/> の実装を選ぶファクトリ。
/// Windowsでは <see cref="WindowsPlatformServices"/>、それ以外では
/// <see cref="NullPlatformServices"/>（何もしない実装）を返す。Linux実装
/// （<c>Platform/Linux/</c>）はフェーズL4の担当であり、それまでの間、非Windows環境では
/// 常にNull実装が使われる。
/// </summary>
public static class PlatformServices
{
    private static readonly Lazy<IPlatformServices> Instance = new(Create);

    /// <summary>プロセス内で共有する既定インスタンス。</summary>
    public static IPlatformServices Current => Instance.Value;

    /// <summary>実行中のOSを判定し、対応する実装を新規に生成する。</summary>
    public static IPlatformServices Create()
        => OperatingSystem.IsWindows() ? new WindowsPlatformServices() : new NullPlatformServices();
}
