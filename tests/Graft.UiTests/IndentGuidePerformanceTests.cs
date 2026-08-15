using System.Diagnostics;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using FluentAssertions;
using Graft.Editor;
using Graft.UiTests.TestSupport;
using Xunit.Abstractions;

namespace Graft.UiTests;

/// <summary>
/// 検討書「インデントガイド（縦線）」の性能要件（10万行でも可視範囲だけを処理する）を検証する。
///
/// 計測手法・壁時計に依存しない相対比較の考え方は<see cref="PerformanceTests"/>と全く同じ
/// （基準・対象それぞれをウォームアップ→交互に<see cref="MeasurementRuns"/>組計測→組ごとの
/// 倍率の中央値、という<c>CrossFileSearchPerformanceTests</c>で確立した手法）。詳細な設計の
/// 経緯はそちらのクラスドキュメントコメントを参照し、ここでは再発明しない
/// （<see cref="PerformanceTests"/>自身も同じ理由でヘルパーを複製しており、本ファイルも
/// その既存の作法（各性能テストファイルが独立してヘルパーを持つ）にそのまま倣う）。
///
/// 「折りたたみできる範囲のみ」モードは<see cref="AvaloniaEdit.Folding.FoldingManager.
/// GetFoldingsContaining"/>・<c>GetNextFolding</c>（区間木への問い合わせ、
/// <see cref="AvaloniaEdit.Folding.FoldingMargin"/>自身が可視マーカー計算に使うのと同じ手法）、
/// 「すべてのインデント」モードは可視行の実インデントだけを見る設計（<see cref="IndentGuideRenderer"/>
/// クラスコメント参照）のため、どちらも初回描画のコストは可視範囲の行数だけで決まり、
/// 総行数（10万行）には依存しないはず、という仮説を検証する。
/// </summary>
public class IndentGuidePerformanceTests : IDisposable
{
    private const int LineCount = 100_000;
    private const int SmallLineCount = 2_000;
    private const int MeasurementRuns = 7;

    // 十分な余裕を残しつつ、可視範囲限定が壊れて全行を処理するようになった場合（本来なら
    // LineCount/SmallLineCount=50倍近くなるはず）だけを検出できる上限。PerformanceTests.
    // RelativeCostRatioThresholdと同じ値・同じ考え方を採用する。
    private const double RelativeCostRatioThreshold = 3.0;

    private readonly ITestOutputHelper _output;
    private readonly ShownWindowTracker _windows = new();

    public IndentGuidePerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "10万行でもインデントガイド（折りたたみできる範囲のみ）が可視範囲に限定される")]
    public void 十万行でもインデントガイドが可視範囲に限定される_折りたたみ範囲のみ()
    {
        MeasureIndentGuideRender(SmallLineCount, IndentGuideMode.FoldableRangesOnly);
        MeasureIndentGuideRender(LineCount, IndentGuideMode.FoldableRangesOnly);

        var (ratio, baselineTimes, targetTimes, ratios) = MeasureAlternatingRatio(
            () => MeasureIndentGuideRender(SmallLineCount, IndentGuideMode.FoldableRangesOnly),
            () => MeasureIndentGuideRender(LineCount, IndentGuideMode.FoldableRangesOnly));

        WriteMeasurementLog(
            "インデントガイド初回描画（折りたたみできる範囲のみ）", $"{SmallLineCount}行", $"{LineCount}行",
            baselineTimes, targetTimes, ratios, ratio);

        ratio.Should().BeLessThan(RelativeCostRatioThreshold,
            $"総行数が{LineCount / SmallLineCount}倍（{SmallLineCount}→{LineCount}）なのに"
            + $"組ごとの倍率の中央値でインデントガイドの描画時間も{ratio:F2}倍になっている"
            + $"（基準: 全{MeasurementRuns}組[{string.Join(", ", baselineTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"対象: 全{MeasurementRuns}組[{string.Join(", ", targetTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"組ごとの倍率[{string.Join(", ", ratios.Select(r => r.ToString("F3")))}]）。"
            + "文書全体の折りたたみ範囲を毎回舐めている（可視範囲に限定できていない）可能性がある");
    }

    [AvaloniaFact(DisplayName = "10万行でもインデントガイド（すべてのインデント）が可視範囲に限定される")]
    public void 十万行でもインデントガイドが可視範囲に限定される_すべてのインデント()
    {
        MeasureIndentGuideRender(SmallLineCount, IndentGuideMode.AllIndentation);
        MeasureIndentGuideRender(LineCount, IndentGuideMode.AllIndentation);

        var (ratio, baselineTimes, targetTimes, ratios) = MeasureAlternatingRatio(
            () => MeasureIndentGuideRender(SmallLineCount, IndentGuideMode.AllIndentation),
            () => MeasureIndentGuideRender(LineCount, IndentGuideMode.AllIndentation));

        WriteMeasurementLog(
            "インデントガイド初回描画（すべてのインデント）", $"{SmallLineCount}行", $"{LineCount}行",
            baselineTimes, targetTimes, ratios, ratio);

        ratio.Should().BeLessThan(RelativeCostRatioThreshold,
            $"総行数が{LineCount / SmallLineCount}倍（{SmallLineCount}→{LineCount}）なのに"
            + $"組ごとの倍率の中央値でインデントガイドの描画時間も{ratio:F2}倍になっている"
            + $"（基準: 全{MeasurementRuns}組[{string.Join(", ", baselineTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"対象: 全{MeasurementRuns}組[{string.Join(", ", targetTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"組ごとの倍率[{string.Join(", ", ratios.Select(r => r.ToString("F3")))}]）。"
            + "空行の前後探索または可視行の走査が文書全体に及んでいる可能性がある");
    }

    /// <summary>指定行数・指定モードでインデントガイド付きの文書を開いて初回描画する時間（ms）を計測する。</summary>
    private double MeasureIndentGuideRender(int lines, IndentGuideMode mode)
    {
        var editor = new TextEditor { Document = new TextDocument(BuildNestedSource(lines)) };
        var window = _windows.Track(new Window { Width = 1200, Height = 800, Content = editor });

        using var folding = new FoldingSupport(editor);
        using var indentGuide = new IndentGuideRenderer(editor, folding);
        folding.Attach(editor.Document, ".cs"); // 括弧ベース戦略（C#）。Attach内でRecalculateNowが同期実行される。
        indentGuide.SetMode(mode);

        window.Show();

        var stopwatch = Stopwatch.StartNew();
        window.CaptureRenderedFrame().Should().NotBeNull();
        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// 基準1回・対象1回を1組として<see cref="MeasurementRuns"/>組だけ交互に計測し、組ごとの
    /// 倍率（対象÷基準）の中央値を返す（<see cref="PerformanceTests"/>と同じ手法の複製）。
    /// </summary>
    private static (double Ratio, List<double> BaselineTimes, List<double> TargetTimes, List<double> Ratios)
        MeasureAlternatingRatio(Func<double> measureBaselineOnce, Func<double> measureTargetOnce)
    {
        var baselineTimes = new List<double>(MeasurementRuns);
        var targetTimes = new List<double>(MeasurementRuns);
        var ratios = new List<double>(MeasurementRuns);
        for (var i = 0; i < MeasurementRuns; i++)
        {
            var baselineMs = measureBaselineOnce();
            var targetMs = measureTargetOnce();
            baselineTimes.Add(baselineMs);
            targetTimes.Add(targetMs);
            ratios.Add(targetMs / Math.Max(0.01, baselineMs));
        }
        return (Median(ratios), baselineTimes, targetTimes, ratios);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private void WriteMeasurementLog(
        string operationName, string baselineLabel, string targetLabel,
        List<double> baselineTimes, List<double> targetTimes, List<double> ratios, double ratio)
    {
        var baselineRawText = string.Join(", ", baselineTimes.Select(t => t.ToString("F3")));
        var targetRawText = string.Join(", ", targetTimes.Select(t => t.ToString("F3")));
        var ratiosRawText = string.Join(", ", ratios.Select(r => r.ToString("F3")));
        _output.WriteLine($"基準（{baselineLabel}での{operationName}、{MeasurementRuns}組）: [{baselineRawText}] ms");
        _output.WriteLine($"対象（{targetLabel}での{operationName}、{MeasurementRuns}組）: [{targetRawText}] ms");
        _output.WriteLine($"組ごとの倍率: [{ratiosRawText}] → 中央値 {ratio:F2}倍");
    }

    /// <summary>
    /// 4段までのネスト・空行を含む、インデントガイドの計算経路（折りたたみ範囲の祖先チェーン・
    /// 空行の前後探索の両方）を実際に働かせる内容を生成する。
    /// </summary>
    private static string BuildNestedSource(int lines)
    {
        var builder = new StringBuilder(lines * 24);
        for (var i = 0; i < lines; i++)
        {
            var kind = i % 12;
            builder.Append(kind switch
            {
                0 => $"public void Method{i}()\n",
                1 => "{\n",
                2 => "    if (x > 0)\n",
                3 => "    {\n",
                4 => "        for (var j = 0; j < 10; j++)\n",
                5 => "        {\n",
                6 => "\n", // 空行（前後最寄りの非空行へのフォールバックを働かせる）。
                7 => "            DoWork(j);\n",
                8 => "        }\n",
                9 => "    }\n",
                10 => "\n",
                _ => "}\n",
            });
        }
        return builder.ToString();
    }
}
