using System.Runtime.Versioning;
using System.Text;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="IPlatformServices"/> のWindows実装の集約。各サービスの実体は本フォルダの
/// <c>WindowsXxx</c> クラス群であり、いずれも移設元（<c>Views/</c>・<c>Features/</c>・
/// <c>Core/</c>・<c>Themes/</c>）のロジックをそのまま移したものである。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformServices : IPlatformServices
{
    public ITrayIcon Tray { get; } = new WindowsTrayIcon();

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
