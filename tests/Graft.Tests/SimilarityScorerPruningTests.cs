using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Graft.Core;
using Xunit;
using Xunit.Abstractions;

namespace Graft.Tests;

/// <summary>
/// 段階5（類似度マッチ）の枝刈りが、結果を1件も変えていないことを固定する。
///
/// 背景: 段階5は素朴に全開始位置×窓長×編集距離DPを回しており、10万行のファイルでは
/// 実測281秒（打ち切り不能）と実用にならなかった。これを枝刈りで高速化したが、
/// 段階5の価値は「AIの出力が少しずれても拾う」ことにあるため、速くするために
/// 当たらなくなっては本末転倒である。そこで本テストでは、修正前のアルゴリズムを
/// <see cref="NaiveFindBestMatch"/> としてそのまま残し、無作為な入力に対して
/// 「採用される開始行・行数・類似度」がビット単位で一致することを総当たりで確認する。
///
/// 同点（同じ類似度の窓が複数ある）ときにどれが選ばれるかまで一致させる必要がある点に注意。
/// 修正前は「開始位置の昇順 → 窓長の昇順」に走査し、最初に現れた最大値を採用していた。
/// 枝刈り後もこの走査順序と採用条件を保っているため、同点の勝者まで変わらない。
/// 語彙を絞った（同じ行が何度も現れる）入力を意図的に多く混ぜているのは、同点を大量に
/// 発生させてこの性質を突くため。
/// </summary>
public class SimilarityScorerPruningTests
{
    private readonly ITestOutputHelper _output;

    public SimilarityScorerPruningTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "枝刈りの有無で段階5の結果（開始行・行数・類似度）が完全に一致する")]
    [InlineData(2024, 40, 12, 600)]
    [InlineData(7, 300, 60, 60)]
    [InlineData(99, 120, 90, 60)]
    public void 枝刈りをしても素朴実装と同じ結果になる(int seed, int maxFileLines, int maxSearchLines, int cases)
    {
        var random = new Random(seed);
        // 語彙が小さいほど同点が増える。1文字種（全行同じ）から12文字種までを混ぜる。
        var alphabets = new[] { 1, 2, 3, 5, 12 };
        var thresholds = new[] { 0.0, 0.3, 0.5, 0.7, 0.85, 0.95, 1.0 };

        for (var i = 0; i < cases; i++)
        {
            var alphabet = alphabets[random.Next(alphabets.Length)];
            var fileLines = RandomLines(random, random.Next(1, maxFileLines), alphabet);
            var searchLines = RandomLines(random, random.Next(1, maxSearchLines), alphabet);
            var threshold = thresholds[random.Next(thresholds.Length)];

            var expected = NaiveFindBestMatch(fileLines, searchLines, threshold);
            // 予算は無制限（0以下＝無制限）にする。ここで検証したいのは枝刈りの等価性であり、
            // 安全網の打ち切りが混ざると比較にならないため。
            var actual = SimilarityScorer.Scan(fileLines, searchLines, threshold, cellBudget: 0);

            actual.Aborted.Should().BeFalse();
            var context = $"threshold={threshold} file=[{string.Join("|", fileLines)}] search=[{string.Join("|", searchLines)}]";
            if (expected is null)
            {
                actual.Match.Should().BeNull(context);
                continue;
            }

            actual.Match.Should().NotBeNull(context);
            actual.Match!.StartLine.Should().Be(expected.StartLine, context);
            actual.Match.LineCount.Should().Be(expected.LineCount, context);
            actual.Match.Similarity.Should().Be(expected.Similarity, context);
        }

        _output.WriteLine($"無作為{cases}件（seed={seed}、最大{maxFileLines}行×SEARCH最大{maxSearchLines}行）で素朴実装と完全一致");
    }

    [Fact(DisplayName = "空のSEARCH・空のファイルは枝刈り後もnullを返す")]
    public void 空入力はnullになる()
    {
        SimilarityScorer.FindBestMatch(Array.Empty<string>(), new[] { "a" }, 0.85).Should().BeNull();
        SimilarityScorer.FindBestMatch(new[] { "a" }, Array.Empty<string>(), 0.85).Should().BeNull();
    }

    [Fact(DisplayName = "1行だけ書き換えられた100行のSEARCHを、10万行のファイルの中から正しく拾う")]
    public void 十万行でも正しい位置を拾える()
    {
        var fileLines = GenerateSourceLike(100_000);
        var searchLines = fileLines.Skip(50_000).Take(100).ToArray();
        searchLines[50] = "        // AIが書き換えてしまった1行";

        var match = SimilarityScorer.FindBestMatch(fileLines, searchLines, 0.85);

        match.Should().NotBeNull();
        match!.StartLine.Should().Be(50_000);
        match.LineCount.Should().Be(100);
        match.Similarity.Should().BeApproximately(0.99, 0.0001);
    }

    [Fact(DisplayName = "予算を使い切ると段階5を打ち切り、打ち切ったことを呼び出し元へ伝える")]
    public void 予算切れで打ち切られる()
    {
        // 「どの窓も検索行の並べ替えになっている」入力は、行の多重集合による上限が常に1.0となり
        // 枝刈りが効かない（本質的に病的なケース）。安全網としての予算切れが働くことを確認する。
        var searchLines = Enumerable.Range(0, 100).Select(i => $"line {i}").ToArray();
        var shuffled = Shuffle(searchLines, new Random(1));
        var fileLines = Enumerable.Range(0, 20_000).Select(i => shuffled[i % shuffled.Length]).ToArray();

        var scan = SimilarityScorer.Scan(fileLines, searchLines, 0.85);

        scan.Aborted.Should().BeTrue("枝刈りが効かない病的な入力では、何分も待たせるより打ち切るべき");
        scan.Match.Should().BeNull();
    }

    [Fact(DisplayName = "通常の入力では予算切れの打ち切りは起きない")]
    public void 通常入力では打ち切られない()
    {
        var fileLines = GenerateSourceLike(100_000);
        var searchLines = fileLines.Skip(70_000).Take(100).ToArray();
        searchLines[10] = "        // AIが書き換えてしまった1行";

        var scan = SimilarityScorer.Scan(fileLines, searchLines, 0.85);

        scan.Aborted.Should().BeFalse();
        scan.Match!.StartLine.Should().Be(70_000);
    }

    [Fact(DisplayName = "予算を極端に絞って打ち切っても、返す候補は本物（類似度が再計算と一致する）")]
    public void 打ち切っても返す候補は本物である()
    {
        // 予算を絞ると事前パス（Floorの底上げ）の途中で打ち切られる。そのとき窓の数え上げの
        // 巻き戻し範囲を取り違えると内部状態が壊れるため、その回帰も兼ねる。
        var fileLines = GenerateSourceLike(20_000);
        var searchLines = fileLines.Skip(9_000).Take(60).ToArray();
        searchLines[5] = "        // AIが書き換えてしまった1行";

        var scan = SimilarityScorer.Scan(fileLines, searchLines, 0.85, cellBudget: 500);

        scan.Aborted.Should().BeTrue();
        if (scan.Match is not null)
        {
            var window = fileLines.Skip(scan.Match.StartLine).Take(scan.Match.LineCount).ToArray();
            SimilarityScorer.Similarity(searchLines, window).Should().Be(scan.Match.Similarity);
            scan.Match.Similarity.Should().BeGreaterThanOrEqualTo(0.85);
        }
    }

    /// <summary>10万行の疑似ソースコード。行の内容は無作為だが、実行ごとに変わらないよう種を固定する。</summary>
    internal static string[] GenerateSourceLike(int lineCount)
    {
        var random = new Random(12345);
        string[] shapes =
        {
            "    var x{0} = Compute({1});",
            "    if (flag{0}) {{ Run({1}); }}",
            "    public void Method{0}(int a{1})",
            "        // 注記 {0} / {1}",
            "    return result{0} + {1};",
        };

        var lines = new string[lineCount];
        for (var i = 0; i < lineCount; i++)
        {
            lines[i] = string.Format(shapes[random.Next(shapes.Length)], i, random.Next(1000));
        }

        return lines;
    }

    private static string[] Shuffle(string[] source, Random random)
    {
        var copy = source.ToArray();
        for (var i = copy.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy;
    }

    private static string[] RandomLines(Random random, int count, int alphabet)
        => Enumerable.Range(0, count).Select(_ => "L" + random.Next(alphabet)).ToArray();

    /// <summary>
    /// 修正前の段階5の実装（枝刈り無し）。比較の基準としてそのまま残している。
    /// 変更しないこと（これが変わると「同じ結果になる」という保証の意味が失われる）。
    /// </summary>
    private static SimilarityScorer.SimilarityMatch? NaiveFindBestMatch(
        IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines, double threshold)
    {
        if (searchLines.Count == 0 || fileLines.Count == 0) return null;

        var searchLen = searchLines.Count;
        var maxDelta = Math.Min(20, Math.Max(1, (int)Math.Ceiling(searchLen * (1 - threshold)) + 1));

        SimilarityScorer.SimilarityMatch? best = null;
        for (var start = 0; start < fileLines.Count; start++)
        {
            for (var delta = -maxDelta; delta <= maxDelta; delta++)
            {
                var len = searchLen + delta;
                if (len <= 0 || start + len > fileLines.Count) continue;

                var window = new string[len];
                for (var i = 0; i < len; i++) window[i] = fileLines[start + i];

                var similarity = SimilarityScorer.Similarity(searchLines, window);
                if (similarity < threshold) continue;
                if (best is null || similarity > best.Similarity)
                {
                    best = new SimilarityScorer.SimilarityMatch
                    {
                        StartLine = start,
                        LineCount = len,
                        Similarity = similarity,
                    };
                }
            }
        }

        return best;
    }
}
