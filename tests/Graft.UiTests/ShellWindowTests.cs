using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using FluentAssertions;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// シェルウィンドウの構築・描画テスト（仕様書v2.1 附録A.7）。
/// 「主要画面が例外なく構築・描画できること」を保証し、
/// リソース解決の失敗やレイアウト崩れをここで検出する。
/// </summary>
public class ShellWindowTests
{
    [AvaloniaFact(DisplayName = "シェルウィンドウを例外なく構築して表示できる")]
    public void シェルウィンドウを構築できる()
    {
        var window = new ShellWindow();
        window.Show();

        window.IsVisible.Should().BeTrue();
        window.Title.Should().Be("Graft");
    }

    [AvaloniaFact(DisplayName = "シェルウィンドウを実際に描画してフレームを取得できる")]
    public void シェルウィンドウを描画できる()
    {
        var window = new ShellWindow { Width = 1000, Height = 700 };
        window.Show();

        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");
        frame!.PixelSize.Width.Should().BeGreaterThan(0);
    }

    [AvaloniaFact(DisplayName = "最小サイズは仕様どおり960x600である")]
    public void 最小サイズが仕様どおりである()
    {
        var window = new ShellWindow();
        window.MinWidth.Should().Be(960);
        window.MinHeight.Should().Be(600);
    }
}
