using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using Graft.Core;
using Xunit;
using Xunit.Abstractions;

namespace Graft.Tests;

/// <summary>
/// 段階5（類似度マッチ）の計算量が、ファイル行数・SEARCH行数のどちらに対しても
/// 破滅的に伸びないことを固定する回帰テスト。
///
/// 修正前の実測（4コアのLinuxコンテナ・Release、<see cref="SimilarityScorerPruningTests"/>と
/// 同じ疑似ソースコードで計測）:
///   2,000行×SEARCH 20行 0.24秒 / 50行 0.90秒 / 80行 2.91秒 / 120行 8.78秒
///   10,000行×SEARCH 40行 2.39秒、20,000行×SEARCH100行 52.24秒、
///   100,000行×SEARCH100行 281.35秒
/// 段階5は「段階1〜4が一致しなかったとき」にしか動かないため、
/// 「一致しなかったときこそ何分も待たされる」という最悪の性質になっていた。
///
/// 判定は壁時計時間の絶対値ではなく相対比較で行う（共有ランナーの遅さで無関係に落ちないため）。
/// 計測手法は<see cref="CrossFileSearchPerformanceTests"/>で確立したものへ揃える:
/// 基準・対象の両方をウォームアップし、基準1回・対象1回を1組として交互に
/// <see cref="MeasurementRuns"/>組計測し、組ごとの倍率の中央値を採用する。
/// しきい値もこのリポジトリの流儀に合わせて<see cref="RatioThreshold"/>=3.0とする。
///
/// 本テストで見ているのは次の2点。
///  ・SEARCH行数への依存（劣化の本命を捕まえるのはこちら）: 修正前の費用は概ね
///    SEARCH行数の二乗（窓長×DP）×窓長ゆらぎ幅に比例した。同じ10万行のファイルに対して
///    SEARCH 20行（修正前の実測から約12秒相当）と100行（同281秒）を比べると、修正前なら
///    20倍を超える。枝刈り後はどちらもファイルを一巡する費用（行の多重集合による事前枝刈り）が
///    支配的になり、ほぼ同じ時間で終わる。本コミット時の実測（負荷なし・3回繰り返し）は
///    中央値1.14〜1.24倍で、しきい値3.0に十分収まる。
///  ・ファイル行数への線形性: 1万行を10回と10万行を1回で総処理行数を揃える。
///    修正前もファイル行数に対しては線形だったため、これは劣化の検出というより
///    「枝刈りの前処理（語彙作り・スライド窓）が二乗になっていないこと」の保険。
///    本コミット時の実測は中央値0.30〜0.35倍（10万行を1回のほうが、1万行を10回よりも
///    1回あたりの固定費用を10分の1しか払わないため1未満になる）。
/// </summary>
public class SimilarityScorerPerformanceTests
{
    private const int LineCount = 100_000;
    private const int ScaleFactor = 10;
    private const int BaselineLineCount = LineCount / ScaleFactor;

    /// <summary>基準1回・対象1回を1組として、この組数だけ交互に計測し、組ごとの倍率の中央値を採用する。</summary>
    private const int MeasurementRuns = 7;

    /// <summary>組ごとの倍率の中央値がこの値未満であることを要求する（本リポジトリの他の性能テストと同じ値）。</summary>
    private const double RatioThreshold = 3.0;

    private const double SimilarityThreshold = 0.85;

    private readonly ITestOutputHelper _output;

    public SimilarityScorerPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "段階5の所要時間がSEARCH行数に対して爆発しない（10万行×20行 対 10万行×100行）")]
    public void SEARCH行数を増やしても段階5が爆発しない()
    {
        var fileLines = SimilarityScorerPruningTests.GenerateSourceLike(LineCount);
        var shortSearch = MakeSearch(fileLines, 50_000, 20);
        var longSearch = MakeSearch(fileLines, 50_000, 100);

        var (ratio, baselineTimes, targetTimes, ratios) = MeasureAlternatingRatio(
            () => MeasureOnce(fileLines, shortSearch, 50_000, 20),
            () => MeasureOnce(fileLines, longSearch, 50_000, 100));

        Report("基準（10万行×SEARCH20行）", "対象（10万行×SEARCH100行）", ratio, baselineTimes, targetTimes, ratios);

        ratio.Should().BeLessThan(RatioThreshold,
            "SEARCH行数を5倍にしただけで段階5の所要時間が跳ね上がるなら、事前枝刈りが効かなくなっている"
            + $"（倍率の中央値 {ratio:F2}倍）");
    }

    [Fact(DisplayName = "段階5の所要時間がファイル行数に対して線形（1万行×10回 対 10万行×1回）")]
    public void ファイル行数に対して線形に収まる()
    {
        var baselineLines = SimilarityScorerPruningTests.GenerateSourceLike(BaselineLineCount);
        var baselineSearch = MakeSearch(baselineLines, BaselineLineCount / 2, 100);
        var targetLines = SimilarityScorerPruningTests.GenerateSourceLike(LineCount);
        var targetSearch = MakeSearch(targetLines, LineCount / 2, 100);

        var (ratio, baselineTimes, targetTimes, ratios) = MeasureAlternatingRatio(
            () =>
            {
                var total = 0.0;
                for (var i = 0; i < ScaleFactor; i++)
                {
                    total += MeasureOnce(baselineLines, baselineSearch, BaselineLineCount / 2, 100);
                }

                return total;
            },
            () => MeasureOnce(targetLines, targetSearch, LineCount / 2, 100));

        Report($"基準（{BaselineLineCount}行×{ScaleFactor}回の合計）", $"対象（{LineCount}行×1回）",
            ratio, baselineTimes, targetTimes, ratios);

        ratio.Should().BeLessThan(RatioThreshold,
            "総処理行数を揃えているため線形なら1倍前後になるはず"
            + $"（倍率の中央値 {ratio:F2}倍）");
    }

    /// <summary>1回の段階5探索の所要時間（ミリ秒）。期待どおりの箇所を拾えたことも毎回確かめる。</summary>
    private static double MeasureOnce(IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines,
        int expectedStart, int expectedLength)
    {
        var sw = Stopwatch.StartNew();
        var scan = SimilarityScorer.Scan(fileLines, searchLines, SimilarityThreshold);
        sw.Stop();

        // 「速いが当たらない」ことを性能テストが見逃さないよう、毎回の計測で結果も確認する。
        scan.Aborted.Should().BeFalse();
        scan.Match.Should().NotBeNull();
        scan.Match!.StartLine.Should().Be(expectedStart);
        scan.Match.LineCount.Should().Be(expectedLength);

        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>SEARCH部を作る。1行だけ書き換え、段階1〜4では一致せず段階5へ落ちるようにする。</summary>
    private static string[] MakeSearch(IReadOnlyList<string> fileLines, int start, int length)
    {
        var search = new string[length];
        for (var i = 0; i < length; i++) search[i] = fileLines[start + i];
        search[length / 2] = "        // AIが書き換えてしまった1行";
        return search;
    }

    private (double Ratio, List<double> BaselineTimes, List<double> TargetTimes, List<double> Ratios)
        MeasureAlternatingRatio(Func<double> baseline, Func<double> target)
    {
        // ウォームアップ（初回JITの費用を計測対象から除く）。基準・対象の両方を必ず行う。
        baseline();
        target();

        var baselineTimes = new List<double>(MeasurementRuns);
        var targetTimes = new List<double>(MeasurementRuns);
        var ratios = new List<double>(MeasurementRuns);
        for (var i = 0; i < MeasurementRuns; i++)
        {
            var baselineMs = baseline();
            var targetMs = target();
            baselineTimes.Add(baselineMs);
            targetTimes.Add(targetMs);
            ratios.Add(targetMs / Math.Max(0.01, baselineMs));
        }

        return (Median(ratios), baselineTimes, targetTimes, ratios);
    }

    private void Report(string baselineName, string targetName, double ratio,
        List<double> baselineTimes, List<double> targetTimes, List<double> ratios)
    {
        _output.WriteLine($"{baselineName}: [{string.Join(", ", baselineTimes.Select(t => t.ToString("F3")))}] ms");
        _output.WriteLine($"{targetName}: [{string.Join(", ", targetTimes.Select(t => t.ToString("F3")))}] ms");
        _output.WriteLine($"組ごとの倍率: [{string.Join(", ", ratios.Select(r => r.ToString("F3")))}] → 中央値 {ratio:F2}倍");
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;
    }
}
