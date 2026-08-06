namespace Graft.Platform.Null;

/// <summary>
/// <see cref="IPlatformServices"/> のうち、いずれのプラットフォームでも成立しない場合の
/// 集約実装。すべてのサービスが <see cref="Platform.Null"/> 名前空間の何もしない実装となる。
/// <c>Platform/Linux</c>（フェーズL4）が実装されるまでの間、非Windows環境はこれを使う。
/// </summary>
public sealed class NullPlatformServices : IPlatformServices
{
    public ITrayIcon Tray { get; } = new NullTrayIcon();

    public IGlobalHotkeys Hotkeys { get; } = new NullGlobalHotkeys();

    public IClipboardMonitor Clipboard { get; } = new NullClipboardMonitor();

    public ITrashService Trash { get; } = new NullTrashService();

    public IFileManagerLauncher FileManager { get; } = new NullFileManagerLauncher();

    public ISystemThemeWatcher Theme { get; } = new NullSystemThemeWatcher();

    public ISingleInstanceGuard SingleInstance { get; } = new NullSingleInstanceGuard();

    public string DescribeEnvironment()
        => $"OS: {Environment.OSVersion.VersionString}"
           + "（未対応プラットフォーム。トレイ/ホットキー/クリップボード監視/ごみ箱/"
           + "ファイルマネージャ連携/テーマ自動追従/多重起動防止はいずれも無効）";
}
