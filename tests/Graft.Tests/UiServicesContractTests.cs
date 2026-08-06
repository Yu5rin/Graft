using FluentAssertions;
using Graft.Platform;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書19章・20章（フェーズL3）: ViewModel層をWPF/Avalonia間で共有するための抽象
/// （<see cref="IUiServices"/>）のうち、実UIを起動せずに検証できる<see cref="UiRect"/>の
/// 幾何計算を扱う。<see cref="UiRect.IntersectsWith"/>は<c>WindowLayoutStore.ResolveWindowBounds</c>
/// （<c>ViewModels/WindowLayoutStore.cs</c>）のモニタ到達可能性判定が内部で使う中核ロジックであり、
/// 境界条件（辺のみ接する・角のみ接する・面積0）を誤ると保存済みウィンドウ位置の復元（仕様書8.11）
/// を壊すため、単体で厳密に検証する。
/// </summary>
public class UiServicesContractTests
{
    [Fact(DisplayName = "重なりのある矩形はIntersectsWithがtrueになる")]
    public void 重なりのある矩形はtrueになる()
    {
        var a = new UiRect(0, 0, 100, 100);
        var b = new UiRect(50, 50, 100, 100);

        a.IntersectsWith(b).Should().BeTrue();
        b.IntersectsWith(a).Should().BeTrue();
    }

    [Fact(DisplayName = "完全に離れた矩形はIntersectsWithがfalseになる")]
    public void 離れた矩形はfalseになる()
    {
        var a = new UiRect(0, 0, 100, 100);
        var b = new UiRect(200, 200, 100, 100);

        a.IntersectsWith(b).Should().BeFalse();
        b.IntersectsWith(a).Should().BeFalse();
    }

    [Theory(DisplayName = "辺だけが接する（面積の重なりが無い）矩形はIntersectsWithがfalseになる")]
    [InlineData(100, 0)] // 右辺と左辺が接する（水平方向に隣接）
    [InlineData(0, 100)] // 下辺と上辺が接する（垂直方向に隣接）
    public void 辺だけが接する矩形はfalseになる(double offsetX, double offsetY)
    {
        var a = new UiRect(0, 0, 100, 100);
        var b = new UiRect(offsetX, offsetY, 100, 100);

        a.IntersectsWith(b).Should().BeFalse();
        b.IntersectsWith(a).Should().BeFalse();
    }

    [Fact(DisplayName = "角だけが接する矩形はIntersectsWithがfalseになる")]
    public void 角だけが接する矩形はfalseになる()
    {
        var a = new UiRect(0, 0, 100, 100);
        var b = new UiRect(100, 100, 100, 100);

        a.IntersectsWith(b).Should().BeFalse();
        b.IntersectsWith(a).Should().BeFalse();
    }

    [Fact(DisplayName = "一方が他方を完全に包含する場合はIntersectsWithがtrueになる")]
    public void 完全包含はtrueになる()
    {
        var outer = new UiRect(0, 0, 100, 100);
        var inner = new UiRect(25, 25, 10, 10);

        outer.IntersectsWith(inner).Should().BeTrue();
        inner.IntersectsWith(outer).Should().BeTrue();
    }

    [Fact(DisplayName = "同一の矩形はIntersectsWithがtrueになる")]
    public void 同一矩形はtrueになる()
    {
        var a = new UiRect(10, 20, 30, 40);
        var b = new UiRect(10, 20, 30, 40);

        a.IntersectsWith(b).Should().BeTrue();
    }

    [Fact(DisplayName = "幅・高さが0の矩形は自分自身とも重ならない（面積が無いため）")]
    public void 幅高さ0の矩形は自分自身とも重ならない()
    {
        var zero = new UiRect(10, 10, 0, 0);

        zero.IntersectsWith(zero).Should().BeFalse();
    }

    [Theory(DisplayName = "外接するが重ならない矩形（境界ちょうど）はIntersectsWithがfalseになる")]
    [InlineData(-100, 0)]
    [InlineData(0, -100)]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void 四方向いずれで外接してもfalseになる(double offsetX, double offsetY)
    {
        var a = new UiRect(0, 0, 100, 100);
        var b = new UiRect(offsetX, offsetY, 100, 100);

        a.IntersectsWith(b).Should().BeFalse();
    }

    [Fact(DisplayName = "RightとBottomはLeft/Top+Width/Heightになる")]
    public void RightとBottomの計算()
    {
        var rect = new UiRect(10, 20, 30, 40);

        rect.Right.Should().Be(40);
        rect.Bottom.Should().Be(60);
    }

    [Fact(DisplayName = "UiRectは値の等しいインスタンス同士が等価になる（record struct）")]
    public void 値が等しければ等価になる()
    {
        var a = new UiRect(1, 2, 3, 4);
        var b = new UiRect(1, 2, 3, 4);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
    }
}
