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
/// 検討書「コード中のカラープレビュー」の性能要件（10万行でも可視範囲だけを処理する）を検証する。
/// 計測手法は<see cref="IndentGuidePerformanceTests"/>と全く同じ（基準・対象を交互に計測し、
/// 組ごとの倍率の中央値を見る。クラスコメントの経緯もそちらを参照し、ここでは再発明しない）。
///
/// <see cref="ColorPreviewElementGenerator"/>は<see cref="AvaloniaEdit.Rendering.
/// VisualLineElementGenerator"/>として実装しており、AvaloniaEdit自身が可視行の構築時にしか
/// 呼ばないため、初回描画のコストは可視行数だけで決まり総行数（10万行）には依存しないはず、
/// という仮説を検証する。
/// </summary>
public class ColorPreviewPerformanceTests : IDisposable
{
    private const int LineCount = 100_000;
    private const int SmallLineCount = 2_000;
    private const int MeasurementRuns = 7;
    private const double RelativeCostRatioThreshold = 3.0;

    private readonly ITestOutputHelper _output;
    private readonly ShownWindowTracker _windows = new();

    public ColorPreviewPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "10万行でもカラープレビューの初回描画が可視範囲に限定される")]
    public void 十万行でもカラープレビューが可視範囲に限定される()
    {
        MeasureColorPreviewRender(SmallLineCount);
        MeasureColorPreviewRender(LineCount);

        var (ratio, baselineTimes, targetTimes, ratios) = MeasureAlternatingRatio(
            () => MeasureColorPreviewRender(SmallLineCount),
            () => MeasureColorPreviewRender(LineCount));

        WriteMeasurementLog(
            "カラープレビュー初回描画", $"{SmallLineCount}行", $"{LineCount}行",
            baselineTimes, targetTimes, ratios, ratio);

        ratio.Should().BeLessThan(RelativeCostRatioThreshold,
            $"総行数が{LineCount / SmallLineCount}倍（{SmallLineCount}→{LineCount}）なのに"
            + $"組ごとの倍率の中央値でカラープレビューの描画時間も{ratio:F2}倍になっている"
            + $"（基準: 全{MeasurementRuns}組[{string.Join(", ", baselineTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"対象: 全{MeasurementRuns}組[{string.Join(", ", targetTimes.Select(t => t.ToString("F3")))}]ms → "
            + $"組ごとの倍率[{string.Join(", ", ratios.Select(r => r.ToString("F3")))}]）。"
            + "文書全体の色リテラルを毎回舐めている（可視範囲に限定できていない）可能性がある");
    }

    /// <summary>指定行数の文書（各行に色リテラルを1つ含む）を開いて初回描画する時間（ms）を計測する。</summary>
    private double MeasureColorPreviewRender(int lines)
    {
        var editor = new TextEditor { Document = new TextDocument(BuildColorfulSource(lines)) };
        var window = _windows.Track(new Window { Width = 1200, Height = 800, Content = editor });

        var colorPreview = new ColorPreviewElementGenerator();
        editor.TextArea.TextView.ElementGenerators.Add(colorPreview);

        window.Show();

        var stopwatch = Stopwatch.StartNew();
        window.CaptureRenderedFrame().Should().NotBeNull();
        stopwatch.Stop();

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>基準1回・対象1回を1組として<see cref="MeasurementRuns"/>組だけ交互に計測し、
    /// 組ごとの倍率（対象÷基準）の中央値を返す（<see cref="IndentGuidePerformanceTests"/>と同じ手法）。</summary>
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

    /// <summary>各行に#RRGGBB・rgb()・hsl()を織り交ぜて含む文書を生成する
    /// （スウォッチ生成の実処理を実際に働かせるため）。</summary>
    private static string BuildColorfulSource(int lines)
    {
        var builder = new StringBuilder(lines * 32);
        for (var i = 0; i < lines; i++)
        {
            builder.Append((i % 3) switch
            {
                0 => $"color{i}: #ff6600;\n",
                1 => $"background{i}: rgb({i % 255}, 102, 0);\n",
                _ => $"border{i}: hsl(24, 100%, 50%);\n",
            });
        }
        return builder.ToString();
    }
}
