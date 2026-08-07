using Graft.Platform.Linux;
using Graft.Platform.Null;
using Graft.Platform.Windows;

namespace Graft.Platform;

/// <summary>
/// 実行中のOSに応じて <see cref="IPlatformServices"/> の実装を選ぶファクトリ
/// （仕様書v2.1 19章・20章 L4）。
/// WindowsでもLinuxでもない環境ではNull実装（利用不可を表明し、何もしない実装）が選ばれ、
/// トレイ常駐・ホットキー・クリップボード監視などが無効な状態で静かに縮退する。
/// </summary>
public static class PlatformServices
{
    private static readonly Lazy<IPlatformServices> Instance = new(Create);

    /// <summary>プロセス内で共有する既定インスタンス。</summary>
    public static IPlatformServices Current => Instance.Value;

    /// <summary>実行中のOSを判定し、対応する実装を新規に生成する。</summary>
    public static IPlatformServices Create()
    {
        if (OperatingSystem.IsWindows()) return CreateWindows();
        if (OperatingSystem.IsLinux())
        {
            // AvaloniaUiServicesが対話的な読み取りに使うのと同じ共有インスタンス
            // （X11ClipboardReader.Shared）を渡す。詰まりの影響を一箇所の接続に閉じ込め、
            // クリップボード監視（LinuxClipboardMonitor）側もこの修正の恩恵を受けられるようにする。
            return new LinuxPlatformServices(new AvaloniaClipboardAccess(X11ClipboardReader.Shared));
        }
        return new NullPlatformServices();
    }

    // Windows専用の型に触れるためメソッドを分ける（呼び出し時まで型の読み込みを遅らせる）。
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IPlatformServices CreateWindows() => new WindowsPlatformServices();
}
