using Avalonia;

namespace Graft;

/// <summary>
/// クロスプラットフォーム版のエントリポイント（仕様書v2.1 2.1）。
/// Windows / Linux のいずれでも同じ実行ファイル構成で起動する。
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>デザイナと headless テストの双方から参照する共通のアプリ構築。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
