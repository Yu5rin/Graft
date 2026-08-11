using System.Diagnostics;
using System.Text;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Views;
using Xunit.Abstractions;

namespace Graft.UiTests;

/// <summary>
/// Markdownプレビュー機能（利用者指示の追加要件6: 大きなファイルへの備え）の性能回帰ガード。
///
/// <see cref="ManualMarkdownRenderer.Render"/>はMarkdownの全ブロックをAvalonia標準コントロールへ
/// 丸ごと展開する（AvaloniaEditの可視行のみの仮想化とは異なる方式）ため、ブロック数にほぼ
/// 比例したコストがかかる。<see cref="Graft.Core.MarkdownPreviewSizeGuard"/>の上限（文字数・行数）は
/// この比例関係を前提に実測して決めており（同クラスのコメント参照）、本テストはその前提
/// （2乗的な劣化が無いこと）が壊れていないかを継続的に確認する回帰ガードである。
///
/// 計測手法は<c>CrossFileSearchPerformanceTests</c>と同じ作法に揃えた: 基準・対象の両方を
/// ウォームアップしたうえで、基準1回（小規模ブロック数をScaleFactor回連続で実行した合計時間）・
/// 対象1回（大規模ブロック数を1回でレンダリングした時間）を1組として交互に計測し、組ごとの
/// 倍率（対象÷基準）の中央値で判定する（詳細な理由は同テストのクラスコメント参照。ここでは
/// 重複を避けるため要点のみ記す）。
/// </summary>
public class MarkdownPreviewPerformanceTests
{
    // 対象: 20,000ブロック（10,000見出し+段落ペア）。基準の5倍規模。
    private const int TargetPairs = 10_000;
    private const int ScaleFactor = 5;
    private const int BaselinePairs = TargetPairs / ScaleFactor;

    private const int MeasurementRuns = 7;

    // 総処理ブロック数を基準・対象で揃えているため、線形なら比はおよそ1.0になるのが自然な範囲。
    // 定数コスト（基準側はScaleFactor回に分割する分、呼び出しごとの固定費用を余分に払う）を
    // 吸収する余裕を持たせつつ、2乗的な劣化（1回の呼び出し内でブロック数の2乗の処理をして
    // しまっている等。本来ScaleFactor=5倍規模になるはずが、5分割の基準よりずっと遅くなる）
    // だけを検出できる上限とする。
    private const double RatioThreshold = 3.0;

    private readonly ITestOutputHelper _output;

    public MarkdownPreviewPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [AvaloniaFact(DisplayName = "20,000ブロックのMarkdownレンダリング1回が、4,000ブロックを5回に分けた合計と同程度の時間で完了する")]
    public void 大きなMarkdownのレンダリングが線形に近い時間で完了する()
    {
        var baselineMarkdown = BuildMarkdown(BaselinePairs);
        var targetMarkdown = BuildMarkdown(TargetPairs);

        // ウォームアップ（初回JITのゆらぎを計測対象から除く。両方とも行う理由は
        // CrossFileSearchPerformanceTestsのクラスコメント参照）。
        RenderOnce(baselineMarkdown);
        RenderOnce(targetMarkdown);

        var baselineTimes = new List<double>(MeasurementRuns);
        var targetTimes = new List<double>(MeasurementRuns);
        var ratios = new List<double>(MeasurementRuns);

        for (var i = 0; i < MeasurementRuns; i++)
        {
            var baselineMs = MeasureBatchOnce(baselineMarkdown, ScaleFactor);
            var targetMs = MeasureOnce(targetMarkdown);

            baselineTimes.Add(baselineMs);
            targetTimes.Add(targetMs);
            ratios.Add(targetMs / Math.Max(0.01, baselineMs));
        }

        var ratio = Median(ratios);

        var baselineRawText = string.Join(", ", baselineTimes.Select(t => t.ToString("F3")));
        var targetRawText = string.Join(", ", targetTimes.Select(t => t.ToString("F3")));
        var ratiosRawText = string.Join(", ", ratios.Select(r => r.ToString("F3")));

        _output.WriteLine($"基準（{BaselinePairs * 2}ブロック×{ScaleFactor}回の合計、{MeasurementRuns}組）: [{baselineRawText}] ms");
        _output.WriteLine($"対象（{TargetPairs * 2}ブロック×1回、{MeasurementRuns}組）: [{targetRawText}] ms");
        _output.WriteLine($"組ごとの倍率: [{ratiosRawText}] → 中央値 {ratio:F2}倍（総処理ブロック数は基準・対象とも{TargetPairs * 2}件で同じ）");

        ratio.Should().BeLessThan(RatioThreshold,
            $"総処理ブロック数を揃えた基準（{BaselinePairs * 2}ブロック×{ScaleFactor}回）に対し対象（{TargetPairs * 2}ブロック×1回）が"
            + $"組ごとの倍率の中央値で{ratio:F2}倍の時間になっている"
            + $"（基準: 全{MeasurementRuns}組[{baselineRawText}]ms → 対象: 全{MeasurementRuns}組[{targetRawText}]ms → "
            + $"組ごとの倍率[{ratiosRawText}]）。"
            + "レンダリングの計算量がブロック数に対して線形から外れている可能性がある");
    }

    private static void RenderOnce(string markdown) => ManualMarkdownRenderer.Render(markdown, _ => { });

    private static double MeasureOnce(string markdown)
    {
        var sw = Stopwatch.StartNew();
        RenderOnce(markdown);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static double MeasureBatchOnce(string markdown, int repeats)
    {
        var totalMs = 0.0;
        for (var i = 0; i < repeats; i++)
        {
            totalMs += MeasureOnce(markdown);
        }
        return totalMs;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>見出し・段落を交互に並べたMarkdownを組み立てる（1ペアで2行・2ブロック）。</summary>
    private static string BuildMarkdown(int pairs)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pairs; i++)
        {
            sb.Append("## 見出し").Append(i).Append('\n');
            sb.Append("これは段落です。**強調**や`コード`、[リンク](https://example.com/").Append(i).Append(")も含みます。\n\n");
        }
        return sb.ToString();
    }
}
