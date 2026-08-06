using FluentAssertions;
using Graft.Platform;
using Graft.ViewModels;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書8.11・9.6 ウィンドウ位置・サイズの復元。画面構成が取得できない環境でも
/// 妥当なサイズで開けることを含めて検証する（実機で最小サイズまで縮む不具合が出たため）。
/// </summary>
public class WindowLayoutStoreTests
{
    private const double MinWidth = 960;
    private const double MinHeight = 600;

    [Fact(DisplayName = "保存された位置が画面内なら、その位置とサイズをそのまま使う")]
    public void 保存された位置が画面内ならそのまま使う()
    {
        var state = new WindowLayoutState { Left = 100, Top = 80, Width = 1280, Height = 800 };
        var screens = new FakeScreenInfo(new UiRect(0, 0, 1920, 1080), new UiRect(0, 0, 1920, 1040));

        var bounds = WindowLayoutStore.ResolveWindowBounds(state, MinWidth, MinHeight, screens);

        bounds.Should().Be(new UiRect(100, 80, 1280, 800));
    }

    [Fact(DisplayName = "保存された位置が画面外なら、プライマリモニタの中央へ補正する")]
    public void 画面外なら中央へ補正する()
    {
        var state = new WindowLayoutState { Left = -5000, Top = -5000, Width = 1280, Height = 800 };
        var screens = new FakeScreenInfo(new UiRect(0, 0, 1920, 1080), new UiRect(0, 0, 1920, 1040));

        var bounds = WindowLayoutStore.ResolveWindowBounds(state, MinWidth, MinHeight, screens);

        bounds.Left.Should().Be((1920 - 1280) / 2.0);
        bounds.Top.Should().Be((1040 - 800) / 2.0);
        bounds.Width.Should().Be(1280);
        bounds.Height.Should().Be(800);
    }

    [Fact(DisplayName = "画面構成が取得できない場合でも、要求されたサイズを維持する")]
    public void 画面構成が取得できなくてもサイズを維持する()
    {
        // ウィンドウマネージャの無い環境や、画面情報の準備が整う前の問い合わせでは
        // 作業領域が0になりうる。ここでサイズまで0へ潰すと、最小サイズまでしか戻らず
        // ウィンドウが常に最小サイズで開いてしまう。
        var state = new WindowLayoutState { Left = double.NaN, Top = double.NaN, Width = 1280, Height = 800 };
        var screens = new FakeScreenInfo(default, default);

        var bounds = WindowLayoutStore.ResolveWindowBounds(state, MinWidth, MinHeight, screens);

        bounds.Width.Should().Be(1280);
        bounds.Height.Should().Be(800);
    }

    [Fact(DisplayName = "保存されたサイズが最小サイズを下回る場合は最小サイズまで広げる")]
    public void 最小サイズを下回る場合は広げる()
    {
        var state = new WindowLayoutState { Left = 10, Top = 10, Width = 320, Height = 240 };
        var screens = new FakeScreenInfo(new UiRect(0, 0, 1920, 1080), new UiRect(0, 0, 1920, 1040));

        var bounds = WindowLayoutStore.ResolveWindowBounds(state, MinWidth, MinHeight, screens);

        bounds.Width.Should().Be(MinWidth);
        bounds.Height.Should().Be(MinHeight);
    }

    private sealed class FakeScreenInfo : IScreenInfo
    {
        public FakeScreenInfo(UiRect virtualScreen, UiRect primaryWorkArea)
        {
            VirtualScreen = virtualScreen;
            PrimaryWorkArea = primaryWorkArea;
        }

        public UiRect VirtualScreen { get; }

        public UiRect PrimaryWorkArea { get; }

        public bool IsAnimationEnabled => true;
    }
}
