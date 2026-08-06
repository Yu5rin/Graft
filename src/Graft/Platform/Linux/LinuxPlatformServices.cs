using System.Text;

namespace Graft.Platform.Linux;

/// <summary>
/// <see cref="IPlatformServices"/> のLinux実装の集約（仕様書v2.1 19章 L4）。
/// トレイ常駐はAvalonia標準のTrayIcon（StatusNotifierItem）で、それ以外は
/// 本フォルダの <c>LinuxXxx</c> クラス群で実装する。
/// 利用できない機能（Wayland環境でのグローバルホットキー等）は例外を投げず、
/// <see cref="IPlatformService.IsSupported"/> で利用不可を表明して静かに縮退する。
/// </summary>
public sealed class LinuxPlatformServices : IPlatformServices
{
    public LinuxPlatformServices(IClipboardAccess clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);

        Tray = new AvaloniaTrayIcon(new LinuxDesktopNotifier());
        Hotkeys = new LinuxGlobalHotkeys();
        Clipboard = new LinuxClipboardMonitor(clipboard);
        Trash = new LinuxTrashService();
        FileManager = new LinuxFileManagerLauncher();
        Theme = new LinuxSystemThemeWatcher();
        SingleInstance = new LinuxSingleInstanceGuard();
    }

    public ITrayIcon Tray { get; }

    public IGlobalHotkeys Hotkeys { get; }

    public IClipboardMonitor Clipboard { get; }

    public ITrashService Trash { get; }

    public IFileManagerLauncher FileManager { get; }

    public ISystemThemeWatcher Theme { get; }

    public ISingleInstanceGuard SingleInstance { get; }

    /// <summary>16章: OS種別・バージョン・各サービスの利用可否を1行の日本語で記録する。</summary>
    public string DescribeEnvironment()
    {
        var builder = new StringBuilder();
        builder.Append("OS: Linux ").Append(Environment.OSVersion.Version);

        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (!string.IsNullOrEmpty(session)) builder.Append("（セッション: ").Append(session).Append('）');

        builder.Append("（トレイ: 対応, ホットキー: ").Append(Describe(Hotkeys));
        builder.Append(", クリップボード監視: ").Append(Describe(Clipboard));
        builder.Append(", ごみ箱: ").Append(Describe(Trash));
        builder.Append(", ファイルマネージャ連携: ").Append(Describe(FileManager));
        builder.Append(", テーマ自動追従: ").Append(Describe(Theme));
        builder.Append(", 多重起動防止: ").Append(Describe(SingleInstance)).Append('）');
        return builder.ToString();
    }

    private static string Describe(IPlatformService service) => service.IsSupported ? "対応" : "非対応";
}
