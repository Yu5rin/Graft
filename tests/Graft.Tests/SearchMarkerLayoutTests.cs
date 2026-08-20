using System.Linq;
using FluentAssertions;
using Graft.Editor;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 検索ヒットの位置をスクロールバーのトラック上へ按分する<see cref="SearchMarkerLayout"/>の
/// テスト（利用者要望「検索ハイライト機能」B）。境界（0行・1行・総行数を超える行番号・
/// 重複の畳み込み・現在ヒットが必ず残ること）を網羅する。
/// </summary>
public class SearchMarkerLayoutTests
{
    // ========== 空・異常系 ==========

    [Fact(DisplayName = "ヒットが0件なら空の一覧を返す")]
    public void ヒットが0件なら空()
    {
        SearchMarkerLayout.Compute(matchLineNumbers: [], totalLines: 100, trackHeight: 200, currentMatchIndex: -1)
            .Should().BeEmpty();
    }

    [Fact(DisplayName = "総行数が0以下なら空の一覧を返す（0除算の防御）")]
    public void 総行数が0以下なら空()
    {
        SearchMarkerLayout.Compute([10], totalLines: 0, trackHeight: 200, currentMatchIndex: -1).Should().BeEmpty();
        SearchMarkerLayout.Compute([10], totalLines: -1, trackHeight: 200, currentMatchIndex: -1).Should().BeEmpty();
    }

    [Fact(DisplayName = "トラックの高さが0以下なら空の一覧を返す")]
    public void トラック高さが0以下なら空()
    {
        SearchMarkerLayout.Compute([10], totalLines: 100, trackHeight: 0, currentMatchIndex: -1).Should().BeEmpty();
        SearchMarkerLayout.Compute([10], totalLines: 100, trackHeight: -5, currentMatchIndex: -1).Should().BeEmpty();
    }

    // ========== 境界: 1行の文書 ==========

    [Fact(DisplayName = "総行数1行・ヒット1件でも目印が1本だけ描かれ、トラック内に収まる")]
    public void 総行数1行でも目印が収まる()
    {
        var result = SearchMarkerLayout.Compute([1], totalLines: 1, trackHeight: 100, currentMatchIndex: -1);

        result.Should().HaveCount(1);
        result[0].Y.Should().BeInRange(0, 100, "目印はトラックの範囲内に収まる必要がある");
        (result[0].Y + result[0].Height).Should().BeLessThanOrEqualTo(100.0001, "下端からはみ出してはいけない");
    }

    // ========== 位置の按分（行番号 ÷ 総行数） ==========

    [Fact(DisplayName = "先頭行の目印はトラック上端付近、末尾行の目印はトラック下端付近になる")]
    public void 先頭行と末尾行で位置が上下に分かれる()
    {
        var result = SearchMarkerLayout.Compute([1, 1000], totalLines: 1000, trackHeight: 1000, currentMatchIndex: -1);

        result.Should().HaveCount(2);
        var ordered = result.OrderBy(r => r.Y).ToList();
        ordered[0].Y.Should().BeLessThan(ordered[1].Y, "先頭行の目印は末尾行の目印より上にある必要がある");
        ordered[0].Y.Should().BeLessThan(50, "先頭行はトラック上端に近い位置になる");
        ordered[1].Y.Should().BeGreaterThan(900, "末尾行はトラック下端に近い位置になる");
    }

    [Fact(DisplayName = "文書中央付近の行はトラック中央付近に位置する")]
    public void 中央行はトラック中央付近になる()
    {
        var result = SearchMarkerLayout.Compute([500], totalLines: 1000, trackHeight: 1000, currentMatchIndex: -1);

        result.Should().HaveCount(1);
        result[0].Y.Should().BeInRange(400, 600, "500/1000行目はトラックのほぼ中央になる");
    }

    // ========== 総行数を超える行番号（クランプ） ==========

    [Fact(DisplayName = "総行数を超える行番号はトラック下端へクランプされる（例外にならない）")]
    public void 総行数を超える行番号はクランプされる()
    {
        var withinRange = SearchMarkerLayout.Compute([1000], totalLines: 1000, trackHeight: 1000, currentMatchIndex: -1);
        var beyondRange = SearchMarkerLayout.Compute([100000], totalLines: 1000, trackHeight: 1000, currentMatchIndex: -1);

        withinRange.Should().HaveCount(1);
        beyondRange.Should().HaveCount(1);
        // 総行数ちょうどの行番号と、大幅に超えた行番号は、どちらも「クランプ後は総行数」に
        // 揃うため同じY座標になる（クランプが効いている証拠）。
        beyondRange[0].Y.Should().Be(withinRange[0].Y);
    }

    [Fact(DisplayName = "0や負の行番号はトラック上端へクランプされる")]
    public void ゼロや負の行番号はクランプされる()
    {
        var result = SearchMarkerLayout.Compute([0, -5], totalLines: 1000, trackHeight: 1000, currentMatchIndex: -1);

        result.Should().HaveCount(1, "0行目・負の行番号はどちらも上端へクランプされ、同じバケットへ畳み込まれる");
        result[0].Y.Should().Be(0);
    }

    // ========== 重複の畳み込み ==========

    [Fact(DisplayName = "近接する大量のヒットは1本の目印へ畳み込まれる")]
    public void 近接ヒットは畳み込まれる()
    {
        // 1〜10行目という狭い範囲（総行数10000行に対して0.1%）に1000件密集させる。
        // 畳み込みが効いていなければ最大1000本の矩形が生成されてしまう。
        var lines = Enumerable.Range(1, 1000).Select(i => 1 + i % 10).ToArray();
        var result = SearchMarkerLayout.Compute(lines, totalLines: 10000, trackHeight: 2000, currentMatchIndex: -1);

        result.Count.Should().BeLessThan(10, "同じ数ピクセルに収まるヒットは畳み込まれ、件数分の矩形にはならない");
    }

    [Fact(DisplayName = "離れた位置にあるヒットは別々の目印として残る")]
    public void 離れたヒットは畳み込まれない()
    {
        var result = SearchMarkerLayout.Compute([1, 500, 1000], totalLines: 1000, trackHeight: 1000, currentMatchIndex: -1);

        result.Should().HaveCount(3, "十分離れた位置のヒットは畳み込まれず、それぞれ別の目印になる");
    }

    // ========== 現在ヒットは必ず残る ==========

    [Fact(DisplayName = "現在ヒットが他の大量のヒットと同じ位置に重なっても、現在ヒットの目印は必ず残る")]
    public void 現在ヒットは密集地帯に埋もれても残る()
    {
        // 1〜5行目に999件密集させ、そのうちの1件（先頭）を現在ヒットに指定する。
        var lines = Enumerable.Range(1, 999).Select(i => 1 + i % 5).ToArray();
        var result = SearchMarkerLayout.Compute(lines, totalLines: 10000, trackHeight: 2000, currentMatchIndex: 0);

        result.Should().Contain(r => r.IsCurrent, "現在ヒットの目印が畳み込みで消えてはいけない");
        result.Count(r => r.IsCurrent).Should().Be(1, "現在ヒットの目印はちょうど1本だけ");
    }

    [Fact(DisplayName = "現在ヒットの目印は通常ヒットより大きい（色が判別できなくても位置が分かるように）")]
    public void 現在ヒットは通常より大きい()
    {
        var result = SearchMarkerLayout.Compute([1, 500], totalLines: 1000, trackHeight: 1000, currentMatchIndex: 1);

        var current = result.Single(r => r.IsCurrent);
        var normal = result.Single(r => !r.IsCurrent);
        current.Height.Should().BeGreaterThan(normal.Height);
    }

    [Fact(DisplayName = "現在ヒットの添字が範囲外（-1等）なら、どの目印もIsCurrentにならない")]
    public void 現在ヒット添字が範囲外ならIsCurrentは付かない()
    {
        var result = SearchMarkerLayout.Compute([1, 500, 1000], totalLines: 1000, trackHeight: 1000, currentMatchIndex: -1);

        result.Should().OnlyContain(r => !r.IsCurrent);
    }

    [Fact(DisplayName = "現在ヒットの添字が配列長以上でも例外にならず、IsCurrentは付かない")]
    public void 現在ヒット添字が配列長以上でも例外にならない()
    {
        var act = () => SearchMarkerLayout.Compute([1, 500], totalLines: 1000, trackHeight: 1000, currentMatchIndex: 99);

        act.Should().NotThrow();
        act().Should().OnlyContain(r => !r.IsCurrent);
    }

    [Fact(DisplayName = "大量のヒット（MaxMatches相当の20000件）でも例外にならず、妥当な件数まで畳み込まれる")]
    public void 大量ヒットでも畳み込まれる()
    {
        var lines = Enumerable.Range(0, 20000).Select(i => 1 + i % 5000).ToArray();

        var act = () => SearchMarkerLayout.Compute(lines, totalLines: 5000, trackHeight: 800, currentMatchIndex: 12345);

        act.Should().NotThrow();
        var result = act();
        result.Count.Should().BeLessThan(1000, "トラック800pxに対して20000件をそのまま描画するのは無駄なため、畳み込まれている必要がある");
        result.Should().Contain(r => r.IsCurrent);
    }
}
