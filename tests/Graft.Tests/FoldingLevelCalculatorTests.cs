using FluentAssertions;
using Graft.Editor;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 折りたたみ範囲の入れ子レベル算出（<see cref="FoldingLevelCalculator"/>）のテスト。
/// 「レベル1〜5で折りたたむ」コマンドの土台となる純粋ロジック。
/// </summary>
public class FoldingLevelCalculatorTests
{
    [Fact(DisplayName = "範囲が無ければ空配列")]
    public void 範囲が無ければ空配列()
    {
        FoldingLevelCalculator.ComputeLevels(Array.Empty<(int, int)>()).Should().BeEmpty();
    }

    [Fact(DisplayName = "兄弟同士（入れ子でない）の範囲はどちらもレベル1")]
    public void 兄弟同士はレベル1()
    {
        var ranges = new (int, int)[] { (0, 10), (20, 30) };
        FoldingLevelCalculator.ComputeLevels(ranges).Should().Equal(1, 1);
    }

    [Fact(DisplayName = "2段の入れ子は外側1・内側2")]
    public void 入れ子2段()
    {
        // class Foo {        0..40  (level 1)
        //     void Bar() {   5..30  (level 2)
        //     }
        // }
        var ranges = new (int, int)[] { (0, 40), (5, 30) };
        FoldingLevelCalculator.ComputeLevels(ranges).Should().Equal(1, 2);
    }

    [Fact(DisplayName = "3段の入れ子は1・2・3の順になる")]
    public void 入れ子3段()
    {
        var ranges = new (int, int)[] { (0, 100), (10, 90), (20, 80) };
        FoldingLevelCalculator.ComputeLevels(ranges).Should().Equal(1, 2, 3);
    }

    [Fact(DisplayName = "同じ深さの兄弟が複数連続しても正しいレベルになる（1つ閉じたら2に戻らない）")]
    public void 兄弟が複数連続しても正しいレベル()
    {
        // class Foo {              0..100 level 1
        //     void A() { }         5..15  level 2
        //     void B() { }         20..30 level 2
        //     void C() {           35..90 level 2
        //         if (x) { }       40..60 level 3
        //     }
        // }
        var ranges = new (int, int)[] { (0, 100), (5, 15), (20, 30), (35, 90), (40, 60) };
        FoldingLevelCalculator.ComputeLevels(ranges).Should().Equal(1, 2, 2, 2, 3);
    }

    [Fact(DisplayName = "1〜5段まで正しく数えられる（レベル5の折りたたみコマンドの前提を満たす）")]
    public void 深さ5段まで数えられる()
    {
        var ranges = new (int, int)[] { (0, 100), (5, 95), (10, 90), (15, 85), (20, 80) };
        FoldingLevelCalculator.ComputeLevels(ranges).Should().Equal(1, 2, 3, 4, 5);
    }
}
