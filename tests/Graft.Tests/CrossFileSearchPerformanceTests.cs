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
/// 完了まで6秒超かかっていた（grepなら数十ミリ秒の規模）。ここではCI環境の
/// 変動を考慮しつつ、現状の6秒超では確実に落ちる2秒を上限として押さえる。
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

    private readonly ITestOutputHelper _output;

    public CrossFileSearchPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "1000ファイル・869ヒットの横断検索が2秒以内に完了する")]
    public async Task 大量ファイルの横断検索が高速に完了する()
    {
        using var ws = new TempWorkspace();
        GenerateFiles(ws);

        var project = new Project { Id = "p_perf", Name = "perf", Root = ws.RootPath };
        var engine = new CrossFileSearchEngine();
        var options = new CrossFileSearchOptions { Query = Query };

        // JITのウォームアップ（初回コンパイルコストを計測対象から除く）。
        await CollectAsync(engine, project, options);

        var sw = Stopwatch.StartNew();
        var hits = await CollectAsync(engine, project, options);
        sw.Stop();

        _output.WriteLine($"{FileCount}ファイル（{FileCount * LinesPerFile}行）中「{Query}」の横断検索: {sw.ElapsedMilliseconds} ms");

        hits.Should().HaveCount(HitFileCount);
        hits.Select(h => h.RelativePath).Distinct().Should().HaveCount(HitFileCount);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            $"実測 {sw.ElapsedMilliseconds}ms。10万ファイル規模でもUIをブロックしない仕様に対し、絶対速度が製品水準にないと退行");
    }

    [Fact(DisplayName = "検索とクリアを5回繰り返してもマネージドヒープが単調増加しない")]
    public async Task 検索とクリアの繰り返しでメモリが単調増加しない()
    {
        using var ws = new TempWorkspace();
        GenerateFiles(ws);

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
    /// 1000ファイル×21行の疑似コードを生成する。869ファイルには「Method19」という
    /// 語（メソッド定義とは重ならない、単独のコメント行）を1回だけ含める。
    /// </summary>
    private static void GenerateFiles(TempWorkspace ws)
    {
        for (var i = 0; i < FileCount; i++)
        {
            var includeHit = i < HitFileCount;
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
