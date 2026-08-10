using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Graft.Tests;

/// <summary>
/// 実機計測で確認された横断検索の性能・メモリ問題の回帰テスト（hardening-perf）。
///
/// 計測環境の再現: 1000ファイル（各21行、合計約2万行）のプロジェクトで
/// 「Method19」（869ファイルに各1ヒット）を検索する。実機の発行バイナリでは
/// 完了まで6秒超かかっていた（grepなら数十ミリ秒の規模）。
///
/// 壁時計時間を絶対値で判定すると共有ランナー等の遅さで無関係に落ちるため、ここでは
/// 相対比較で判定する。このエンジンはファイル数・行数にほぼ比例する設計（ディレクトリを
/// 列挙し、ファイルを順に読んで正規表現走査する）なので、「1000ファイルを1回で検索する
/// 場合」と「200ファイルずつ<see cref="ScaleFactor"/>回に分けて検索し合計する場合」は、
/// 処理するファイルの総数（1000件）が同じであれば所要時間もほぼ同じになるのが自然である。
/// 二乗のような破滅的な劣化（1回の呼び出し内でファイル数の2乗の処理をしてしまっている等）
/// があれば、分割したほうが「1回あたりの処理対象」が小さくなる分だけ合計時間が大きく
/// 短縮されるため、比が1から大きくずれて現れる。
///
/// 計測手法について（不定期失敗の調査結果を踏まえた設計）。以下の2段階で問題を発見し、
/// 順に対策した:
///
/// [第1段階] 素朴な相対比較（基準側だけをウォームアップし、基準・対象とも1回だけ計測して
/// 「対象は基準の何倍か」を見る）には欠陥があった。基準側だけウォームアップすると、対象
/// （1000ファイル）は初回読み取り＝OSページキャッシュが冷たいままで計測され、I/Oの遅い
/// 共有ランナーではこの非対称が増幅される。実際にCIでは基準11〜62ms（基準側だけが5.6倍も
/// ばらつく）・対象962msとなり、倍率が38.5倍（許容25倍）に達して不定期に失敗した。
/// 倍率のばらつきはほぼ全て分母（基準）のノイズが原因だった。
///
/// [第2段階] 上記を踏まえ「両方を必ずウォームアップする」「min-of-Nで最小値を採用する」
/// 「基準の規模を引き上げ小数精度で計測する」という3点を満たす、基準200ファイル・対象
/// 1000ファイルの単純な比率判定へ作り直した。ローカルでは安定したが、4コアを4本の
/// ビジーループで飽和させた負荷下で10回実行すると3回失敗した。原因は基準（1回あたり
/// 約30ms）と対象（1回あたり約120ms）とで1回の計測にかかる壁時計時間の長さが大きく違う
/// ことにあった。CPUが恒常的に取り合われている状況では、計測時間が短いほど「たまたま
/// 空いた一瞬に丸ごと収まってほぼ無傷で終わる」確率が高く、計測時間が長いほど「計測の
/// 途中でどこかは必ず横取りされる」確率が高くなる。min-of-Nを取っても、対象側は5回とも
/// 高確率で横取りされる一方、基準側は5回のうち1回でも運良く無傷で終われば異常に小さい
/// 最小値になり、倍率だけが跳ね上がる。つまり外乱の影響が基準・対象で対称ではなかった。
///
/// [第3段階] 第2段階では、基準側は「200ファイルの検索を<see cref="ScaleFactor"/>回連続で
/// 実行し、その合計時間」を1回分の計測値とした（課題文の対策候補「基準を複数回まわした
/// 合計時間で割る」に相当）。200ファイル×5回＝合計1000ファイル分の処理となり、対象
/// （1000ファイルを1回）と1回の計測にかかる壁時計時間がほぼ同じ長さになる。これでほとんどの
/// 場合は安定したが、同じ負荷試験（4本のビジーループ）を複数回繰り返すと、なお時折
/// 倍率が跳ねるケースが残った。原因は計測の「並び順」にあった。基準（<see cref="MeasurementRuns"/>回）
/// を先にまとめて全部計測し、そのあとで対象を<see cref="MeasurementRuns"/>回まとめて計測して
/// いたため、基準の計測区間と対象の計測区間は時間的に離れており、その間に負荷側の
/// スケジューリングの揺れが変化すると、基準は空いていた瞬間を丸ごと使えたのに対象は
/// 混んでいた瞬間に当たる、といった非対称が生じ得た。
///
/// これを解消するため、基準1回・対象1回を1組として交互に計測し、組ごとに倍率（対象÷基準）
/// を計算したうえで、<see cref="MeasurementRuns"/>組の倍率の中央値を採用する方式へ変更した。
/// 同じ組の基準・対象は直前直後に実行されるため、負荷側の状態がほぼ同じ条件を共有し、
/// 「片方だけ運良く空いた瞬間に当たる」非対称が起きにくい。そのうえで中央値を採る理由は、
/// 依然として1組だけがGCの一時停止等でまとめて外れ値になることはあり得るため、min（最小の
/// 倍率）ではなく中央値を使うことで、外れ値が1〜2組程度混ざっても最終判定が引きずられ
/// にくくするため（min-of-Nの「外乱は遅くする方向にのみ働く」という前提は、基準・対象が
/// 別々の独立した最小値を取り合う構成では成立しなくなるため、ここでは中央値を採用する）。
///
/// 最終的な計測手順:
///   1. 基準・対象の両方を計測前に必ず1回ウォームアップし、ページキャッシュ・JITの条件を揃える。
///   2. 基準1回（200ファイル×<see cref="ScaleFactor"/>回の合計）・対象1回（1000ファイル×1回）を
///      1組として、直前直後に交互に計測する。これにより組の中の基準・対象は、負荷の状態が
///      ほぼ同じ条件を共有する。
///   3. これを<see cref="MeasurementRuns"/>組行い、組ごとの倍率（対象÷基準）の中央値を採用する。
///      1〜2組が外れ値になっても中央値なら引きずられにくい。
///   4. 処理する総ファイル数を基準・対象で揃えて計測時間の長さを対称にすることに加え、
///      <see cref="Stopwatch.ElapsedMilliseconds"/>の整数丸めではなく
///      <see cref="Stopwatch.Elapsed"/>のTotalMillisecondsで小数精度を使う。
///
/// メモリについては、検索→クリアを5回繰り返してもマネージドヒープ
/// （<see cref="GC.GetTotalMemory(bool)"/>）が単調に増加し続けないことを検証する。
/// RSS（実プロセスメモリ）はテストに不向きなため対象外とし、実測値は別途手動計測による。
/// </summary>
public class CrossFileSearchPerformanceTests
{
    private const int FileCount = 1000;
    private const int LinesPerFile = 21;
    private const int HitFileCount = 869;
    private const string Query = "Method19";

    // 基準規模: 対象（1000ファイル）の1/5。基準は「この規模の検索をScaleFactor回連続で
    // 実行した合計時間」を対象の1回と比較するため、合計の処理ファイル数（200×5=1000）が
    // 対象と一致する。869件のヒット比率をなるべく保つため、ヒット数も1/5にする。
    private const int ScaleFactor = 5;
    private const int BaselineFileCount = FileCount / ScaleFactor;
    private const int BaselineHitFileCount = HitFileCount / ScaleFactor;

    // 基準1回・対象1回を1組として、この組数だけ交互に計測し、組ごとの倍率の中央値を採用する。
    private const int MeasurementRuns = 7;

    // 総ファイル数を揃えているため、線形なら比はおよそ1.0になるのが自然な範囲。定数コスト
    // （基準側はScaleFactor回に分割する分、プロジェクト単位の固定費用を余分に払う）を
    // 吸収する余裕を持たせつつ、二乗のような劣化（本来ScaleFactor=5倍規模になる。1回の
    // 呼び出し内がn^2なら、5分割で合計n^2/5になるため）だけを検出できる上限とする。
    private const double RatioThreshold = 3.0;

    private readonly ITestOutputHelper _output;

    public CrossFileSearchPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "1000ファイル・869ヒットの横断検索1回が、200ファイルを5回に分けた合計と同程度の時間で完了する")]
    public async Task 大量ファイルの横断検索が高速に完了する()
    {
        using var baselineWorkspace = new TempWorkspace();
        GenerateFiles(baselineWorkspace, BaselineFileCount, BaselineHitFileCount);

        using var ws = new TempWorkspace();
        GenerateFiles(ws, FileCount, HitFileCount);

        var baselineProject = new Project { Id = "p_perf_baseline", Name = "perf_baseline", Root = baselineWorkspace.RootPath };
        var project = new Project { Id = "p_perf", Name = "perf", Root = ws.RootPath };
        var engine = new CrossFileSearchEngine();
        var options = new CrossFileSearchOptions { Query = Query };

        // ウォームアップ（初回JITコンパイル・初回ファイルI/Oのゆらぎを計測対象から除く）。
        // 基準側だけをウォームアップすると「基準は温かいキャッシュ・対象は冷たいまま」の
        // 非対称が生じ、共有ランナーのI/Oの遅さでそれが増幅されて倍率が跳ねる
        // （詳細はクラスのドキュメントコメントを参照）。両方を必ずウォームアップする。
        await CollectAsync(engine, baselineProject, options);
        await CollectAsync(engine, project, options);

        // 基準1回（200ファイルの検索をScaleFactor回連続で実行した合計時間）・対象1回
        // （1000ファイルの検索1回）を1組として、直前直後に交互に計測する。同じ組の基準・
        // 対象は負荷側の状態がほぼ同じ条件を共有するため、「片方だけ運良く空いた瞬間に
        // 当たる」非対称が起きにくい（詳細はクラスのドキュメントコメントを参照）。
        var baselineTimes = new List<double>(MeasurementRuns);
        var targetTimes = new List<double>(MeasurementRuns);
        var ratios = new List<double>(MeasurementRuns);
        var targetLastHits = new List<SearchHit>();
        for (var i = 0; i < MeasurementRuns; i++)
        {
            var baselineMsOne = await MeasureBatchOnceAsync(
                engine, baselineProject, options, ScaleFactor, BaselineHitFileCount);
            var (targetMsOne, targetHits) = await MeasureOnceAsync(engine, project, options, HitFileCount);

            baselineTimes.Add(baselineMsOne);
            targetTimes.Add(targetMsOne);
            ratios.Add(targetMsOne / Math.Max(0.01, baselineMsOne));
            targetLastHits = targetHits;
        }

        targetLastHits.Select(h => h.RelativePath).Distinct().Should().HaveCount(HitFileCount);

        // 組ごとの倍率の中央値を採用する。理由はクラスのドキュメントコメントを参照。
        var ratio = Median(ratios);

        var baselineRawText = string.Join(", ", baselineTimes.Select(t => t.ToString("F3")));
        var targetRawText = string.Join(", ", targetTimes.Select(t => t.ToString("F3")));
        var ratiosRawText = string.Join(", ", ratios.Select(r => r.ToString("F3")));

        _output.WriteLine(
            $"基準（{BaselineFileCount}ファイル×{ScaleFactor}回の合計、{MeasurementRuns}組）: [{baselineRawText}] ms");
        _output.WriteLine(
            $"対象（{FileCount}ファイル×1回、{MeasurementRuns}組）: [{targetRawText}] ms");
        _output.WriteLine($"組ごとの倍率: [{ratiosRawText}] → 中央値 {ratio:F2}倍（総処理ファイル数は基準・対象とも{FileCount}件で同じ）");

        // 失敗時は基準・対象それぞれの全計測値（生の値）と組ごとの倍率をメッセージへ含める。
        // 次にCIで落ちたとき、たまたま1組だけ遅い値を引いたノイズなのか、全体的に遅い
        // 本物の劣化なのかを、この生データから判断できるようにするため。
        ratio.Should().BeLessThan(RatioThreshold,
            $"総処理ファイル数を揃えた基準（200ファイル×{ScaleFactor}回）に対し対象（1000ファイル×1回）が"
            + $"組ごとの倍率の中央値で{ratio:F2}倍の時間になっている"
            + $"（基準: 全{MeasurementRuns}組[{baselineRawText}]ms → 対象: 全{MeasurementRuns}組[{targetRawText}]ms → "
            + $"組ごとの倍率[{ratiosRawText}]）。"
            + "エンジンの計算量がファイル数・行数に対して線形から外れている可能性がある");
    }

    [Fact(DisplayName = "検索とクリアを5回繰り返してもマネージドヒープが単調増加しない")]
    public async Task 検索とクリアの繰り返しでメモリが単調増加しない()
    {
        using var ws = new TempWorkspace();
        GenerateFiles(ws, FileCount, HitFileCount);

        var project = new Project { Id = "p_perf_mem", Name = "perf_mem", Root = ws.RootPath };
        var engine = new CrossFileSearchEngine();
        var options = new CrossFileSearchOptions { Query = Query };

        // ウォームアップ（初回JIT・初回GC世代分けのゆらぎを除く）。
        await CollectAsync(engine, project, options);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(true);

        var samples = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var hits = await CollectAsync(engine, project, options);
            hits.Should().HaveCount(HitFileCount);
            hits = null; // 明示的に手放す（検索結果クリアに相当）。

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            samples.Add(GC.GetTotalMemory(true));
        }

        _output.WriteLine($"ベースライン: {baseline / 1024} KB");
        for (var i = 0; i < samples.Count; i++)
        {
            _output.WriteLine($"{i + 1}回目の検索後: {samples[i] / 1024} KB（ベースライン差: {(samples[i] - baseline) / 1024} KB）");
        }

        // 「単調増加しない」ことの検証: 最後の値が基準値を大きく超えて伸び続けていないこと。
        // 横断検索の性能テスト（本ファイル冒頭のドキュメントコメント）と同種の脆さがここにもある:
        // 基準をsamples[0]（1回目）にすると、GCタイミングのばらつきでたまたま異常に低い値
        // （実測575KB等、2回目以降の1/10程度）が出た場合に、本当は横ばいなのに「増加している」
        // と誤判定してしまう。ループ突入前に既にウォームアップを1回済ませているにもかかわらず
        // 1回目だけが不安定なため、1回目はさらなるウォームアップの延長とみなし基準から除外する。
        // 2回目（samples[1]）を基準に、それ以降の推移だけで判定する（閾値を緩めるのではなく、
        // 測定方法自体を「単調増加していないか」の検証に合わせる）。GCの世代管理・アロケータの
        // 断片化等でわずかな増減はありうるため、大きな倍数（4MB）を絶対的な余裕として許容し、
        // それを超える継続的な増加のみ失格とする。
        var growth = samples[^1] - samples[1];
        growth.Should().BeLessThan(4 * 1024 * 1024,
            $"5回の検索・クリア後もヒープが増え続けている（2回目: {samples[1] / 1024}KB → 5回目: {samples[^1] / 1024}KB）");
    }

    private static async Task<List<SearchHit>> CollectAsync(
        CrossFileSearchEngine engine, Project project, CrossFileSearchOptions options)
    {
        var state = new SearchRunState();
        var hits = new List<SearchHit>();
        await foreach (var hit in engine.SearchAsync(project, new Settings(), options, state))
        {
            hits.Add(hit);
        }
        return hits;
    }

    /// <summary>横断検索を1回計測し、所要時間（ms、小数精度）を返す。</summary>
    private static async Task<(double ElapsedMs, List<SearchHit> Hits)> MeasureOnceAsync(
        CrossFileSearchEngine engine, Project project, CrossFileSearchOptions options, int expectedHitCount)
    {
        var sw = Stopwatch.StartNew();
        var hits = await CollectAsync(engine, project, options);
        sw.Stop();
        hits.Should().HaveCount(expectedHitCount);
        return (sw.Elapsed.TotalMilliseconds, hits);
    }

    /// <summary>
    /// <paramref name="project"/>に対する横断検索を<paramref name="repeats"/>回連続で実行し、
    /// その合計時間（ms、小数精度）を返す。対象側（1回で完了）と1回分の計測にかかる
    /// 壁時計時間の長さを揃えるために、基準側をこの形で複数回に分けて合計する
    /// （詳細はクラスのドキュメントコメントを参照）。
    /// </summary>
    private static async Task<double> MeasureBatchOnceAsync(
        CrossFileSearchEngine engine, Project project, CrossFileSearchOptions options,
        int repeats, int expectedHitCountPerRun)
    {
        var totalMs = 0.0;
        for (var j = 0; j < repeats; j++)
        {
            var (elapsedMs, _) = await MeasureOnceAsync(engine, project, options, expectedHitCountPerRun);
            totalMs += elapsedMs;
        }
        return totalMs;
    }

    /// <summary>中央値を求める（偶数個なら中央2件の平均）。</summary>
    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>
    /// <paramref name="fileCount"/>ファイル×21行の疑似コードを生成する。先頭<paramref name="hitFileCount"/>件には
    /// 「Method19」という語（メソッド定義とは重ならない、単独のコメント行）を1回だけ含める。
    /// </summary>
    private static void GenerateFiles(TempWorkspace ws, int fileCount, int hitFileCount)
    {
        for (var i = 0; i < fileCount; i++)
        {
            var includeHit = i < hitFileCount;
            var text = string.Join("\n", BuildFileLines(i, includeHit)) + "\n";
            ws.WriteText($"Sample{i:D4}.cs", text);
        }
    }

    private static string[] BuildFileLines(int fileIndex, bool includeHit)
    {
        var lines = new List<string>(LinesPerFile)
        {
            $"// ファイル {fileIndex} 生成コード（性能検証用フィクスチャ）",
            "using System;",
            "",
            "namespace Sample.Generated;",
            "",
            $"public sealed class Sample{fileIndex}",
            "{",
        };
        for (var m = 0; m < 12; m++)
        {
            lines.Add($"    public void Method{m}(int value) => Console.WriteLine(value + {fileIndex});");
        }
        lines.Add(includeHit ? "    // 参照: Method19 はこのファイルでは未使用" : "    // 参照: 補助コメント");
        lines.Add("}");
        return lines.ToArray();
    }
}
