using FluentAssertions;
using Graft.Core.Vba;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>プロジェクトペインの「VBA」バッジの状態（仕様書_VBA連携 7.6）。入力の組み合わせを網羅して固定する。</summary>
public class VbaBadgeLogicTests
{
    [Fact(DisplayName = "この環境で使えない（Null ブリッジ・Linux）ときは、他の入力にかかわらずバッジを出さない")]
    public void 使えない環境では出ない()
    {
        foreach (var enabled in new[] { true, false })
        foreach (var open in new bool?[] { true, false, null })
        {
            VbaBadgeLogic.Compute(isSupported: false, enabled, open, 3, 2).IsVisible.Should().BeFalse();
        }
    }

    [Fact(DisplayName = "開いていて未反映なし: 「VBA」だけ。グレーでなく、補足も無い")]
    public void 通常の状態()
    {
        var badge = VbaBadgeLogic.Compute(true, true, workbookOpen: true, 0, 0);

        badge.Should().Match<VbaBadgeState>(b => b.IsVisible && b.Text == "VBA" && !b.IsGray && b.StatusText == string.Empty);
    }

    [Fact(DisplayName = "ブックが開いていない: グレー。ツールチップの先頭が「ブックが開いていません」")]
    public void 開いていないとグレー()
    {
        var badge = VbaBadgeLogic.Compute(true, true, workbookOpen: false, 0, 0);

        badge.IsGray.Should().BeTrue();
        badge.Text.Should().Be("VBA");
        badge.Tooltip.Should().Be("ブックが開いていません");
    }

    [Fact(DisplayName = "まだ調べていない（null）: 開いていないと決めつけず、グレーにしない")]
    public void 未確認ではグレーにしない()
    {
        var badge = VbaBadgeLogic.Compute(true, true, workbookOpen: null, 0, 0);

        badge.IsGray.Should().BeFalse();
        badge.Tooltip.Should().NotContain("ブックが開いていません");
    }

    [Theory(DisplayName = "件数: 未反映 N・削除保留 N を補足に出し、ツールチップにも件数を出す")]
    [InlineData(2, 0, "未反映 2")]
    [InlineData(0, 3, "削除保留 3")]
    [InlineData(2, 3, "未反映 2・削除保留 3")]
    public void 件数の補足(int unpushed, int pending, string expected)
    {
        var badge = VbaBadgeLogic.Compute(true, true, true, unpushed, pending);

        badge.StatusText.Should().Be(expected);
        if (unpushed > 0) badge.Tooltip.Should().Contain($"未反映のモジュール: {unpushed} 件");
        if (pending > 0) badge.Tooltip.Should().Contain($"削除保留のモジュール: {pending} 件");
    }

    [Fact(DisplayName = "閉じていて未反映もある: ツールチップは「ブックが開いていません」が先頭で、件数も続く")]
    public void 閉じていて未反映あり()
    {
        var badge = VbaBadgeLogic.Compute(true, true, false, 1, 1);

        badge.Tooltip.Split(Environment.NewLine).Should().HaveCount(3);
        badge.Tooltip.Should().StartWith("ブックが開いていません");
        badge.IsGray.Should().BeTrue();
        badge.StatusText.Should().Be("未反映 1・削除保留 1");
    }

    [Fact(DisplayName = "連携オフ: 「VBA 連携オフ」でグレー。開閉の状態は見ない。件数は出す（再び有効にしたときに送る必要があると分かるように）")]
    public void 連携オフ()
    {
        foreach (var open in new bool?[] { true, false, null })
        {
            var badge = VbaBadgeLogic.Compute(true, isEnabled: false, open, 2, 0);

            badge.Text.Should().Be("VBA 連携オフ");
            badge.IsGray.Should().BeTrue();
            badge.StatusText.Should().Be("未反映 2");
            badge.Tooltip.Should().NotContain("ブックが開いていません");
        }
    }

    [Fact(DisplayName = "負の件数は 0 として扱う")]
    public void 負の件数は0()
    {
        VbaBadgeLogic.Compute(true, true, true, -1, -5).StatusText.Should().BeEmpty();
    }
}
