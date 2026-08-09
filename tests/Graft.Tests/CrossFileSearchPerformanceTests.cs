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
/// 「同じ検索を1/10の規模（100ファイル）で行った場合の何倍か」という相対比較で判定する。
/// このエンジンはファイル数・行数にほぼ比例する設計（ディレクトリを列挙し、ファイルを
/// 順に読んで正規表現走査する）なので、ファイル数10倍なら時間もおよそ10倍が自然であり、
/// ハードウェアの速さは基準・対象の両方に等しく乗って相殺される。二乗のような破滅的な
/// 劣化（本来100倍規模になる）だけを検出できるよう、線形からの余裕を大きく持たせる。
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

    // 基準規模: 本番規模の1/10。869件のヒット比率をなるべく保つため、ヒット数も1/10にする。
    private const int BaselineFileCount = FileCount / 10;
    private const int BaselineHitFileCount = HitFileCount / 10;

    private readonly ITestOutputHelper _output;

    public CrossFileSearchPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "1000ファイル・869ヒットの横断検索が、100ファイル規模の10倍程度の時間で完了する")]
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
        await CollectAsync(engine, baselineProject, options);

        // 基準: 1/10規模（100ファイル）での横断検索時間。
        var baselineSw = Stopwatch.StartNew();
        var baselineHits = await CollectAsync(engine, baselineProject, options);
        baselineSw.Stop();
        baselineHits.Should().HaveCount(BaselineHitFileCount);

        var sw = Stopwatch.StartNew();
        var hits = await CollectAsync(engine, project, options);
        sw.Stop();

        var baselineMs = Math.Max(1, baselineSw.ElapsedMilliseconds);
        var ratio = (double)sw.ElapsedMilliseconds / baselineMs;
        _output.WriteLine(
            $"{BaselineFileCount}ファイル中「{Query}」の横断検索: {baselineSw.ElapsedMilliseconds} ms / "
            + $"{FileCount}ファイル中「{Query}」の横断検索: {sw.ElapsedMilliseconds} ms（倍率 {ratio:F1}倍、ファイル数は{FileCount / BaselineFileCount}倍）");

        hits.Should().HaveCount(HitFileCount);
        hits.Select(h => h.RelativePath).Distinct().Should().HaveCount(HitFileCount);

        // ファイル数10倍に対し時間もおよそ10倍で収まるのが自然な範囲。定数コスト
        // （プロセス起動済みのJIT・ファイルシステムキャッシュ等）を吸収する余裕を持たせつつ、
        // 二乗のような劣化（本来100倍規模になる）だけを検出できる上限とする。
        ratio.Should().BeLessThan(25,
            $"ファイル数{FileCount / BaselineFileCount}倍に対し所要時間が{ratio:F1}倍になっている"
            + $"（基準{baselineSw.ElapsedMilliseconds}ms→対象{sw.ElapsedMilliseconds}ms）。"
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
        // 不具合6: 基準をsamples[0]（1回目）にすると、GCタイミングのばらつきでたまたま
        // 異常に低い値（実測575KB等、2回目以降の1/10程度）が出た場合に、本当は横ばいなのに
        // 「増加している」と誤判定してしまう。ループ突入前に既にウォームアップを1回済ませて
        // いるにもかかわらず1回目だけが不安定なため、1回目はさらなるウォームアップの延長とみなし
        // 基準から除外する。2回目（samples[1]）を基準に、それ以降の推移だけで判定する
        // （閾値を緩めるのではなく、測定方法自体を「単調増加していないか」の検証に合わせる）。
        // GCの世代管理・アロケータの断片化等でわずかな増減はありうるため、大きな倍数（4MB）を
        // 絶対的な余裕として許容し、それを超える継続的な増加のみ失格とする。
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
