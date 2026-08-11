using System.Diagnostics;
using System.Text;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;
using Xunit.Abstractions;

namespace Graft.UiTests;

/// <summary>
/// 機能追加（差分の左右並列表示）の性能回帰テスト。
///
/// 要件（実装方針）: 並列表示への切り替えで全行を実体化するような実装にせず、既存の
/// 折りたたみ（8.13、変更が無い範囲を省略行にまとめる）と、DiffView.axamlのListBoxの
/// 仮想化（画面内の行だけを描画する）をそのまま活かす。ここでは画面描画までは検証できない
/// （headless環境のため）が、ViewModel側の行組み立て（RebuildRows）が行数に比例して
/// 極端に遅くならないこと・並列表示と統合表示とで所要時間に大きな差が無いことを、
/// PerformanceTests.csと同じ「相対比較」の考え方（絶対時間ではなく倍率で見る）で検証する。
/// </summary>
public class DiffSideBySidePerformanceTests
{
    private readonly ITestOutputHelper _output;

    public DiffSideBySidePerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [AvaloniaFact(DisplayName = "大きな差分（数万行）でも並列表示への切り替えは統合表示と同程度の速さで終わる")]
    public void 大きな差分での並列統合切り替え性能()
    {
        const int totalLines = 50_000;
        const int changeEvery = 40; // 40行おきに1行変更（折りたたみが効く現実的な密度）。

        var (before, after) = BuildLargeDiffSource(totalLines, changeEvery);

        var loadStopwatch = Stopwatch.StartNew();
        var diff = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        diff.Load(MakePlan(before, after));
        loadStopwatch.Stop();

        // 折りたたみにより実際にLinesへ積まれる行数は totalLines よりずっと少ないはずだが、
        // 変更行数ぶん（totalLines/changeEvery）＋前後コンテキスト＋省略マーカーは必ず積まれる。
        var initialRowCount = diff.Lines.Count;
        initialRowCount.Should().BeGreaterThan(0);
        initialRowCount.Should().BeLessThan(totalLines, "8.13の折りたたみにより変更の無い範囲は省略行1件にまとまるはず");

        // 並列→統合→並列と切り替えるたびの所要時間を計測する（RebuildRowsのみのコスト）。
        var toSideBySideMs = Measure(() => diff.IsSideBySide = true);
        var toUnifiedMs = Measure(() => diff.IsSideBySide = false);
        var backToSideBySideMs = Measure(() => diff.IsSideBySide = true);

        _output.WriteLine($"総行数: {totalLines:N0}行、変更行: {totalLines / changeEvery:N0}箇所");
        _output.WriteLine($"初回Load（並列表示、既定）: {loadStopwatch.Elapsed.TotalMilliseconds:F2} ms、実体化行数: {initialRowCount:N0}行");
        _output.WriteLine($"並列→統合への切り替え: {toUnifiedMs:F2} ms");
        _output.WriteLine($"統合→並列への切り替え（1回目）: {toSideBySideMs:F2} ms");
        _output.WriteLine($"統合→並列への切り替え（2回目）: {backToSideBySideMs:F2} ms");

        // 絶対時間の上限は「体感を大きく損なわない」水準に十分な余裕を持たせる
        // （PerformanceTests.csの考え方と同じ。共有ランナー等の遅さを考慮）。
        toSideBySideMs.Should().BeLessThan(2000, "5万行規模でも並列表示への切り替えは数秒未満で終わる必要がある");
        toUnifiedMs.Should().BeLessThan(2000);

        // 並列表示が統合表示より極端に（桁で）遅くなっていないこと（全行を余計に実体化する
        // ような実装退行が無いことの確認）。
        var ratio = toSideBySideMs / Math.Max(toUnifiedMs, 0.01);
        _output.WriteLine($"並列/統合の所要時間比: {ratio:F2}倍");
        ratio.Should().BeLessThan(5.0, "並列表示への切り替えが統合表示に対して桁違いに遅い場合、全行実体化などの性能退行を疑う");
    }

    private static double Measure(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>changeEvery行おきに1行だけ内容を変える、大量行のbefore/afterテキストを作る。</summary>
    private static (string Before, string After) BuildLargeDiffSource(int totalLines, int changeEvery)
    {
        var before = new StringBuilder();
        var after = new StringBuilder();
        for (var i = 0; i < totalLines; i++)
        {
            var isChanged = i % changeEvery == 0;
            before.Append("line ").Append(i).Append(" content unchanged-ish text here\n");
            after.Append(isChanged ? $"line {i} CHANGED content unchanged-ish text here\n" : $"line {i} content unchanged-ish text here\n");
        }
        return (before.ToString(), after.ToString());
    }

    private static BlockPlan MakePlan(string before, string after)
    {
        var diff = DiffBuilder.Build("large.txt", before, after, contextLines: 3);
        return new BlockPlan
        {
            Block = new DeleteBlock { Path = "large.txt" },
            Path = "large.txt",
            Operation = EntryOperation.Modify,
            CanApply = true,
            IsSelected = true,
            BeforeText = before,
            AfterText = after,
            Diff = diff,
        };
    }
}
