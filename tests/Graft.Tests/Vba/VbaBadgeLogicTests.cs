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

    // ------------------------------------------------------------------
    // 保存時の自動反映をしなかった衝突（機能 D。仕様書_ブックの情報と依頼の観点 8.5）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "衝突 N: 補足行に「衝突 N」が出て、ツールチップに手動の反映への案内が出る")]
    public void 衝突の表示()
    {
        var badge = VbaBadgeLogic.Compute(true, true, workbookOpen: true, 0, 0, saveConflictCount: 2);

        badge.StatusText.Should().Be("衝突 2");
        badge.Tooltip.Should().Contain("衝突: 2 件").And.Contain("Excel へ反映");
        badge.Text.Should().Be("VBA");
        badge.IsGray.Should().BeFalse();
    }

    [Fact(DisplayName = "衝突 N: 未反映・削除保留と並べて出る（既存の表示は崩れない）")]
    public void 衝突は他の件数と並ぶ()
    {
        var badge = VbaBadgeLogic.Compute(true, true, workbookOpen: false, 3, 1, saveConflictCount: 2);

        badge.StatusText.Should().Be("未反映 3・削除保留 1・衝突 2");
        badge.IsGray.Should().BeTrue("ブックが開いていないグレーは変わらない");
        badge.Tooltip.Should().Contain("ブックが開いていません").And.Contain("未反映のモジュール: 3 件").And.Contain("削除保留のモジュール: 1 件");
    }

    [Fact(DisplayName = "衝突 0（既定）: 従来の表示とまったく同じ")]
    public void 衝突なしは従来どおり()
    {
        VbaBadgeLogic.Compute(true, true, true, 3, 1, saveConflictCount: 0)
            .Should().Be(VbaBadgeLogic.Compute(true, true, true, 3, 1));
        VbaBadgeLogic.Compute(true, true, true, 0, 0, saveConflictCount: -1).StatusText.Should().BeEmpty("負の値は 0 として扱う");
    }

    [Fact(DisplayName = "要確認: 件数が 0 を超えると『要確認 N』が補足に出て、ツールチップに説明が付く。0 なら従来の表示のまま")]
    public void 要確認の表示()
    {
        var badge = VbaBadgeLogic.Compute(true, true, true, 1, 0, 0, needsReviewCount: 2);

        badge.StatusText.Should().Be("未反映 1・要確認 2");
        badge.Tooltip.Should().Contain("要確認のモジュール: 2 件");

        VbaBadgeLogic.Compute(true, true, true, 1, 0).StatusText.Should().Be("未反映 1");
    }
}
