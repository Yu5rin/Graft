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

    [Fact(DisplayName = "文言: 「コード 理由（モジュール）: 保存のみ。Excel へは未反映」の形（理由が先頭）")]
    public void 文言()
    {
        VbaSaveNoticeThrottle.BuildText(ErrorCode.E813)
            .Should().Be("E813 マクロの実行中: 保存のみ。Excel へは未反映");
        VbaSaveNoticeThrottle.BuildText(ErrorCode.E811, "Module1").Should().StartWith("E811").And.Contain("（Module1）");
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

    private static readonly ErrorCode[] NotifiableCodes =
    {
        ErrorCode.E801, ErrorCode.E802, ErrorCode.E803, ErrorCode.E804, ErrorCode.E805, ErrorCode.E807,
        ErrorCode.E808, ErrorCode.E809, ErrorCode.E811, ErrorCode.E813, ErrorCode.E816,
    };

    [Fact(DisplayName = "不具合 6: 通知は理由を文の先頭に置く（「E813 マクロの実行中: 保存のみ。Excel へは未反映」）。省略されても理由が残る")]
    public void 不具合6_理由が先頭()
    {
        VbaSaveNoticeThrottle.BuildText(ErrorCode.E813).Should().Be("E813 マクロの実行中: 保存のみ。Excel へは未反映");
        VbaSaveNoticeThrottle.BuildText(ErrorCode.E811, "Module1").Should().Be("E811 Shift_JIS で表せない文字（Module1）: 保存のみ。Excel へは未反映");

        foreach (var code in NotifiableCodes)
        {
            var text = VbaSaveNoticeThrottle.BuildText(code, "Module1");
            text.Should().StartWith(code.ToString(), "理由のコードが先頭");
            text.IndexOf(VbaSaveNoticeThrottle.ShortReason(code), StringComparison.Ordinal).Should().BeLessThan(10, $"{code} の理由は先頭の近く");
            text.IndexOf("（Module1）", StringComparison.Ordinal).Should().BeLessThan(
                text.IndexOf(VbaSaveNoticeThrottle.ResultText, StringComparison.Ordinal), "モジュール名は定型の結果より前");
            text.Should().EndWith(VbaSaveNoticeThrottle.ResultText);
        }
    }

    [Fact(DisplayName = "不具合 10（E809）: E809 の理由は、控えを残せず上書きしなかったときと、書き戻した内容が違うときで言い分ける")]
    public void 不具合10_E809の理由を言い分ける()
    {
        VbaSaveNoticeThrottle.ShortReason(ErrorCode.E809, $"Modules/Module1.bas: Excel 側の本文を控えに{VbaSaveNoticeThrottle.NotOverwrittenPhrase}。")
            .Should().Be("控えを残せず上書きせず");
        VbaSaveNoticeThrottle.ShortReason(ErrorCode.E809, "Modules/Module1.bas: 書き戻した内容が一致しません")
            .Should().Be("反映後の内容が不一致");
        VbaSaveNoticeThrottle.ShortReason(ErrorCode.E809).Should().Be("反映後の内容が不一致");
        VbaSaveNoticeThrottle.BuildText(ErrorCode.E809, "Mod", $"控えを{VbaSaveNoticeThrottle.NotOverwrittenPhrase}。")
            .Should().Contain("控えを残せず上書きせず");
    }
}
