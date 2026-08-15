using FluentAssertions;
using Graft.Editor;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 検討書「フォント設定」。等幅判定の幅比較ロジック（<see cref="MonospaceHeuristic"/>）の単体
/// テスト。実際のフォント列挙・グリフ計測（Avalonia依存）はtests/Graft.UiTests側で検証する
/// （SystemFontCatalog.csのクラスドキュメント参照）。
/// </summary>
public class MonospaceHeuristicTests
{
    [Fact(DisplayName = "幅が完全に一致すれば等幅と判定する")]
    public void 幅が完全に一致すれば等幅()
    {
        MonospaceHeuristic.IsMonospace(10, 10).Should().BeTrue();
    }

    [Fact(DisplayName = "幅の差が許容範囲内（6%以内）なら等幅と判定する")]
    public void 幅の差が許容範囲内なら等幅()
    {
        // 10に対して5%の差（0.5）は許容範囲内。
        MonospaceHeuristic.IsMonospace(10, 10.5).Should().BeTrue();
    }

    [Fact(DisplayName = "幅の差が許容範囲を超えれば等幅ではないと判定する（プロポーショナルフォント相当）")]
    public void 幅の差が大きければ等幅ではない()
    {
        // "i"(狭い)と"W"(広い)が大きく異なる、典型的なプロポーショナルフォントの例。
        MonospaceHeuristic.IsMonospace(4, 14).Should().BeFalse();
    }

    [Theory(DisplayName = "計測できなかった・異常な値は等幅ではない側へ倒す")]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    [InlineData(double.NaN, 10)]
    [InlineData(10, double.NaN)]
    public void 異常な計測値は等幅ではない(double narrow, double wide)
    {
        MonospaceHeuristic.IsMonospace(narrow, wide).Should().BeFalse();
    }

    [Fact(DisplayName = "許容比率のちょうど境界（6.0%）は等幅、それを僅かに超えると等幅ではない")]
    public void 許容比率の境界値()
    {
        MonospaceHeuristic.IsMonospace(100, 106).Should().BeTrue("6%ちょうどの差は許容範囲内");
        MonospaceHeuristic.IsMonospace(100, 106.1).Should().BeFalse("6%を僅かに超える差は許容範囲外");
    }
}
