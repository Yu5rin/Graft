using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Graft.UiTests.TestAppBuilder))]

namespace Graft.UiTests;

/// <summary>
/// headless UIテストのアプリ構築（仕様書v2.1 18章・附録A.7）。
/// UseHeadlessDrawing=false とすることで Skia が実際に描画し、
/// CaptureRenderedFrame で画面を画像として取得できる。画面のない環境で完結する。
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<Graft.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
