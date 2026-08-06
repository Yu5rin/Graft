using System.Runtime.Versioning;
using System.Text;
using Graft.Platform.Linux;
using Graft.Platform.Null;
using Graft.Platform.Windows;

namespace Graft.Platform;

/// <summary>
/// 実行中のOSに応じて <see cref="IPlatformServices"/> の実装を選ぶファクトリ
/// （クロスプラットフォーム版。仕様書v2.1 19章・20章 L4）。
///
/// WPF版の <c>Platform/PlatformServices.cs</c> と役割は同じだが、トレイ常駐だけは
/// WPF版の実装（WPFのContextMenuとDrawingVisualに依存）を使えないため、
/// Avalonia標準のTrayIconを用いる <see cref="AvaloniaTrayIcon"/> へ差し替える。
/// それ以外のWindows実装（ホットキー・クリップボード監視・ごみ箱・ファイルマネージャ連携・
/// テーマ追従・多重起動防止）はWin32のP/Invokeのみで構成されているためWPF版と共有する。
/// WindowsでもLinuxでもない環境ではNull実装（何もしない）が選ばれる。
/// </summary>
public static class AvaloniaPlatformServices
{
    private static readonly Lazy<IPlatformServices> Instance = new(Create);

    /// <summary>プロセス内で共有する既定インスタンス。</summary>
    public static IPlatformServices Current => Instance.Value;

    /// <summary>実行中のOSを判定し、対応する実装を新規に生成する。</summary>
    public static IPlatformServices Create()
    {
        if (OperatingSystem.IsWindows()) return CreateWindows();
        if (OperatingSystem.IsLinux()) return new LinuxPlatformServices(new AvaloniaClipboardAccess());
        return new NullPlatformServices();
    }

    [SupportedOSPlatform("windows")]
    private static IPlatformServices CreateWindows() => new WindowsAvaloniaPlatformServices();
}

/// <summary>
/// Windows向けの実装一式。トレイ常駐のみ<see cref="AvaloniaTrayIcon"/>を使い、
/// それ以外はWPF版と同じ<c>Platform/Windows</c>の実装をそのまま使う。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAvaloniaPlatformServices : IPlatformServices
{
    public ITrayIcon Tray { get; } = new AvaloniaTrayIcon(new NullDesktopNotifier());

    public IGlobalHotkeys Hotkeys { get; } = new WindowsGlobalHotkeys();

    public IClipboardMonitor Clipboard { get; } = new WindowsClipboardMonitor();

    public ITrashService Trash { get; } = new WindowsTrashService();

    public IFileManagerLauncher FileManager { get; } = new WindowsFileManagerLauncher();

    public ISystemThemeWatcher Theme { get; } = new WindowsSystemThemeWatcher();

    public ISingleInstanceGuard SingleInstance { get; } = new WindowsSingleInstanceGuard();

    /// <summary>16章: OS種別・バージョン・各サービスの利用可否を1行の日本語で記録する。</summary>
    public string DescribeEnvironment()
    {
        var builder = new StringBuilder();
        builder.Append("OS: Windows ").Append(Environment.OSVersion.Version);
        builder.Append("（トレイ: 対応, ホットキー: 対応, クリップボード監視: 対応, ");
        builder.Append("ごみ箱: 対応, ファイルマネージャ連携: 対応, ");
        builder.Append("テーマ自動追従: 対応, 多重起動防止: 対応）");
        return builder.ToString();
    }
}
