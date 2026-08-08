using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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

    /// <summary>
    /// 実機検証で発見した不具合5: layout.jsonの極端な値からの復元でウィンドウが最小幅
    /// （960px）まで縮んだとき、コマンドバーのボタン列（幅Auto）がそのままだと右端の
    /// ボタン（設定など）が画面外へはみ出し、二度と押せなくなっていた。
    /// ScrollViewer（<see cref="ShellWindow.ToolbarButtonsScroll"/>）による横スクロール対応で、
    /// (1) ボタン列がウィンドウ幅を超えて描画されない（はみ出さない）こと、
    /// (2) 実際に横スクロールが必要な状態（Extent＞Viewport）になること、
    /// (3) 右端までスクロールすれば「設定」ボタンがウィンドウの表示範囲内に入る＝
    ///     実際に押せる位置に来ること、の3点を検証する。
    /// </summary>
    [AvaloniaFact(DisplayName = "最小幅960pxでもツールバーのボタンが画面外へはみ出さず、横スクロールで設定ボタンへ到達できる")]
    public void 最小幅でもツールバーが画面内に収まり設定ボタンへ到達できる()
    {
        var window = new ShellWindow { Width = 960, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scroll = window.GetControl<ScrollViewer>("ToolbarButtonsScroll");
        var settingsButton = window.GetControl<Button>("SettingsButton");

        // (1) ScrollViewer自身がウィンドウ幅を超えて配置されていないこと
        //     （不具合5そのもの: 以前はAuto幅の子がそのままウィンドウ外へあふれていた）。
        var scrollTopLeft = scroll.TranslatePoint(new Point(0, 0), window) ?? default;
        (scrollTopLeft.X + scroll.Bounds.Width).Should().BeLessThanOrEqualTo(
            window.Bounds.Width + 0.5, "ボタン列を包むScrollViewerがウィンドウ幅を超えてはならない");

        // (2) 実際に横スクロールが必要な状態になっていること（＝単に切り詰められて
        //     二度と辿り着けないのではなく、スクロールで到達できる状態であること）。
        scroll.Extent.Width.Should().BeGreaterThan(
            scroll.Viewport.Width, "最小幅では全ボタンが収まりきらず、横スクロールが有効になるはず");

        // (3) 右端までスクロールすれば「設定」ボタンがウィンドウの表示範囲内に入ること。
        scroll.Offset = new Vector(scroll.Extent.Width - scroll.Viewport.Width, 0);
        Dispatcher.UIThread.RunJobs();

        var settingsTopLeft = settingsButton.TranslatePoint(new Point(0, 0), window) ?? default;
        settingsTopLeft.X.Should().BeGreaterThanOrEqualTo(-0.5, "右端までスクロールすれば設定ボタンの左端がウィンドウ内に入るはず");
        (settingsTopLeft.X + settingsButton.Bounds.Width).Should().BeLessThanOrEqualTo(
            window.Bounds.Width + 0.5, "右端までスクロールすれば設定ボタンの右端もウィンドウ内に収まるはず");
    }
}
