using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>保存時の自動反映の通知の間引きと文言（仕様書_ブックの情報と依頼の観点 8.5）。</summary>
public class VbaSaveNoticeThrottleTests
{
    private DateTimeOffset _now = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private VbaSaveNoticeThrottle Create() => new(() => _now);

    [Fact(DisplayName = "同じ理由は 60 秒に 1 回まで。60 秒経てば、また出す")]
    public void 同じコードは60秒に1回()
    {
        var throttle = Create();

        throttle.TryShow(ErrorCode.E813).Should().BeTrue();
        _now += TimeSpan.FromSeconds(59);
        throttle.TryShow(ErrorCode.E813).Should().BeFalse("59 秒後はまだ出さない");
        _now += TimeSpan.FromSeconds(1);
        throttle.TryShow(ErrorCode.E813).Should().BeTrue("最初から 60 秒で再び出す");
    }

    [Fact(DisplayName = "理由が違えば別々に数える")]
    public void コードごとに数える()
    {
        var throttle = Create();

        throttle.TryShow(ErrorCode.E813).Should().BeTrue();
        throttle.TryShow(ErrorCode.E805).Should().BeTrue();
        throttle.TryShow(ErrorCode.E813).Should().BeFalse();
    }

    [Fact(DisplayName = "E811 はモジュール名つきで 1 回に絞る（別のモジュールの E811 は別に知らせる）")]
    public void E811はモジュールごと()
    {
        var throttle = Create();

        throttle.TryShow(ErrorCode.E811, "Module1").Should().BeTrue();
        throttle.TryShow(ErrorCode.E811, "Module1").Should().BeFalse("直るまで保存のたびに起きるので、繰り返さない");
        throttle.TryShow(ErrorCode.E811, "Module2").Should().BeTrue();
    }

    [Fact(DisplayName = "文言: 「保存しました。Excel への自動反映は行いませんでした（コード: 見出し）」の形")]
    public void 文言()
    {
        VbaSaveNoticeThrottle.BuildText(ErrorCode.E813)
            .Should().Be("保存しました。Excel への自動反映は行いませんでした（E813: マクロの実行中です）");
        VbaSaveNoticeThrottle.BuildText(ErrorCode.E811, "Module1").Should().Contain("E811").And.EndWith(" — Module1）");
    }

    [Fact(DisplayName = "通知の理由は短い言い回し（ステータスバーの幅で省略されない）。短い言い回しの無いコードは見出しのまま")]
    public void 理由は短い言い回し()
    {
        foreach (var code in new[] { ErrorCode.E804, ErrorCode.E805, ErrorCode.E807, ErrorCode.E813 })
        {
            VbaSaveNoticeThrottle.ShortReason(code).Length.Should().BeLessThanOrEqualTo(14, $"{code} の理由は短い言い回し");
            VbaSaveNoticeThrottle.ShortReason(code).Length.Should().BeLessThan(ErrorCatalog.SummaryOf(code).Length + 1);
        }

        VbaSaveNoticeThrottle.ShortReason(ErrorCode.E817).Should().Be(ErrorCatalog.SummaryOf(ErrorCode.E817));
    }

    [Fact(DisplayName = "キーが溜まり続けない（古いものは捨てる）")]
    public void 古いキーを捨てる()
    {
        var throttle = Create();
        for (var i = 0; i < 100; i++) throttle.TryShow(ErrorCode.E811, $"M{i}");
        _now += TimeSpan.FromSeconds(61);

        throttle.TryShow(ErrorCode.E811, "M0").Should().BeTrue("60 秒経った古い通知は、また出せる");
        throttle.TryShow(ErrorCode.E811, "M1").Should().BeTrue();
    }
}
