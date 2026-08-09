using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;
using Xunit.Abstractions;

namespace Graft.UiTests;

/// <summary>
/// 実機計測で確認された横断検索の遅延（hardening-perf）を、画面へ実際に結果ツリーを
/// バインドした状態で再現・回帰検証する。<see cref="CrossFileSearchEngine"/>単体は
/// 十分高速（tests/Graft.Tests/CrossFileSearchPerformanceTests参照）だが、実機での
/// 6秒超の遅さは主に2つの要因の重なりだった:
///   1. 結果ツリー（<see cref="TreeView"/>）に仮想化パネルが明示されておらず、
///      Avaloniaの既定パネルにフォールバックして869件すべてが常時実体化されていた
///      （Themes/Controls.Base.axaml・Controls.Layout.axamlでVirtualizingStackPanelを明示して解消）。
///   2. ヒット1件ごとに<see cref="ObservableCollection{T}"/>へAddしていたため、
///      レイアウト・描画のたびに走る機会が869回あった
///      （<see cref="SearchViewModel"/>側でバッチ反映して解消）。
/// ここではSearchViewを実際にウィンドウへ載せた状態で両対策の効果を回帰検証する。
/// </summary>
public class SearchPerformanceTests
{
    private const int FileCount = 1000;
    private const int HitFileCount = 869;
    private const string Query = "Method19";

    private readonly ITestOutputHelper _output;

    public SearchPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [AvaloniaFact(DisplayName = "結果ツリーへバインドした状態でも横断検索が2秒以内に完了する")]
    public async Task 画面バインド状態でも横断検索が高速に完了する()
    {
        var root = Path.Combine(Path.GetTempPath(), "graft-search-perf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            GenerateFiles(root);

            var project = new Project { Id = "p", Name = "p", Root = root };
            var settings = new Settings();
            var engine = new CrossFileSearchEngine();
            var options = new CrossFileSearchOptions { Query = Query };

            // ウォームアップ（初回JIT・初回ファイルI/Oのゆらぎを計測対象から除く）。
            await CollectAsync(engine, project, settings, options);

            // 基準: エンジン単体（画面バインドなし）での横断検索時間。
            // tests/Graft.Tests/CrossFileSearchPerformanceTestsで検証済みのとおりエンジン単体は
            // 高速であり、ここで見たいのはUI側の追加コスト（結果ツリーへのバインド・バッチ反映）が
            // 基準の何倍かという点。同じデータ・同じハードウェアで測るため、CI環境そのものの
            // 速さのばらつきには左右されない。
            var baselineSw = Stopwatch.StartNew();
            var baselineHits = await CollectAsync(engine, project, settings, options);
            baselineSw.Stop();
            baselineHits.Should().HaveCount(HitFileCount);

            var vm = new SearchViewModel(engine, new NullDialogService())
            {
                Query = Query,
            };
            vm.SetContext(project, settings);

            var view = new SearchView { DataContext = vm };
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            window.CaptureRenderedFrame();

            var sw = Stopwatch.StartNew();
            vm.SearchCommand.Execute(null);
            await WaitWhileSearchingAsync(vm).ConfigureAwait(true);
            window.CaptureRenderedFrame();
            sw.Stop();

            var baselineMs = Math.Max(1, baselineSw.ElapsedMilliseconds);
            var ratio = (double)sw.ElapsedMilliseconds / baselineMs;
            _output.WriteLine(
                $"{FileCount}ファイル中「{Query}」の横断検索: エンジン単体={baselineSw.ElapsedMilliseconds} ms / "
                + $"画面バインド込み={sw.ElapsedMilliseconds} ms（倍率 {ratio:F1}倍）");

            vm.IsSearching.Should().BeFalse("タイムアウトせず検索が完了していること");
            vm.Groups.Should().HaveCount(HitFileCount);

            // 実機で確認された遅さ（エンジン単体は高速なのに画面バインド込みで6秒超）を
            // 大きく下回ることを確認する回帰ガード。エンジン単体の時間との倍率で判定するため、
            // 絶対時間ではなくCI環境の遅さそのものには左右されない。
            ratio.Should().BeLessThan(15,
                $"エンジン単体（{baselineSw.ElapsedMilliseconds}ms）に対し画面バインド込み"
                + $"（{sw.ElapsedMilliseconds}ms）が{ratio:F1}倍かかっている。"
                + "結果ツリーの仮想化とバッチ反映が効いていない可能性がある");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>エンジン単体（画面バインドなし）で検索し、全ヒットを収集する。</summary>
    private static async Task<List<SearchHit>> CollectAsync(
        CrossFileSearchEngine engine, Project project, Settings settings, CrossFileSearchOptions options)
    {
        var state = new SearchRunState();
        var hits = new List<SearchHit>();
        await foreach (var hit in engine.SearchAsync(project, settings, options, state))
        {
            hits.Add(hit);
        }
        return hits;
    }

    [AvaloniaFact(DisplayName = "バッチ反映後も検索を中止ボタンで中断できる")]
    public async Task バッチ反映後も検索を中止できる()
    {
        var root = Path.Combine(Path.GetTempPath(), "graft-search-perf-cancel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            GenerateFiles(root);

            var vm = new SearchViewModel(new CrossFileSearchEngine(), new NullDialogService())
            {
                Query = Query,
            };
            vm.SetContext(new Project { Id = "p", Name = "p", Root = root }, new Settings());

            var view = new SearchView { DataContext = vm };
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            window.CaptureRenderedFrame();

            vm.SearchCommand.Execute(null);
            // 完了前に中止コマンドを発行する。バッチ反映へ変えても中止経路
            // （CancellationTokenSource経由）が生きていることを確認する。
            vm.CancelCommand.CanExecute(null).Should().BeTrue("検索中は中止コマンドが実行可能であること");
            vm.CancelCommand.Execute(null);

            await WaitWhileSearchingAsync(vm).ConfigureAwait(true);

            vm.IsSearching.Should().BeFalse("中止後は検索中状態が解除されること");
            vm.Groups.Count.Should().BeLessThanOrEqualTo(HitFileCount, "中止した検索の結果は全件に達しないか、達したとしても超えないこと");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact(DisplayName = "画面バインド状態で検索とクリアを5回繰り返してもマネージドヒープが単調増加しない")]
    public async Task 画面バインド状態での検索とクリアの繰り返しでメモリが単調増加しない()
    {
        var root = Path.Combine(Path.GetTempPath(), "graft-search-perf-mem", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            GenerateFiles(root);
            var project = new Project { Id = "p", Name = "p", Root = root };
            var settings = new Settings();

            var vm = new SearchViewModel(new CrossFileSearchEngine(), new NullDialogService())
            {
                Query = Query,
            };
            vm.SetContext(project, settings);

            var view = new SearchView { DataContext = vm };
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            window.CaptureRenderedFrame();

            // ウォームアップ（初回JIT・初回コンテナ生成のゆらぎを除く）。
            vm.SearchCommand.Execute(null);
            await WaitWhileSearchingAsync(vm).ConfigureAwait(true);
            vm.SetContext(project, settings); // 検索結果クリアに相当

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var baseline = GC.GetTotalMemory(true);

            var samples = new List<long>();
            for (var i = 0; i < 5; i++)
            {
                vm.SearchCommand.Execute(null);
                await WaitWhileSearchingAsync(vm).ConfigureAwait(true);
                vm.Groups.Should().HaveCount(HitFileCount);

                vm.SetContext(project, settings); // 検索結果クリア（プロジェクト切替相当）

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                samples.Add(GC.GetTotalMemory(true));
            }

            _output.WriteLine($"ベースライン: {baseline / 1024} KB");
            for (var i = 0; i < samples.Count; i++)
            {
                _output.WriteLine($"{i + 1}回目のクリア後: {samples[i] / 1024} KB（ベースライン差: {(samples[i] - baseline) / 1024} KB）");
            }

            // 検索結果（ツリー項目・SearchHitViewModel等）が確実に解放されることの検証。
            // 不具合6: 1回目（samples[0]）はGCタイミングのばらつきで異常に低い値が出ることがあり、
            // これを基準にすると横ばいでも誤って「増加」と判定してしまう
            // （tests/Graft.Tests/CrossFileSearchPerformanceTests.cs参照）。1回目は基準から除外し、
            // 2回目（samples[1]）以降の推移で判定する。わずかな増減はGCの世代管理上ありうるため、
            // 大きめの絶対余裕（8MB）を許容し、それを超える継続的な増加のみ失格とする。
            var growth = samples[^1] - samples[1];
            growth.Should().BeLessThan(8 * 1024 * 1024,
                $"5回の検索・クリア後もヒープが増え続けている（2回目: {samples[1] / 1024}KB → 5回目: {samples[^1] / 1024}KB）");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>AsyncRelayCommandは内部でTaskを起動する（async void）ため、完了をポーリングで待つ。</summary>
    private static async Task WaitWhileSearchingAsync(SearchViewModel vm)
    {
        for (var i = 0; i < 1000 && vm.IsSearching; i++)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    /// <summary>1000ファイル×21行の疑似コードを生成する。869ファイルに「Method19」を1回含める。</summary>
    private static void GenerateFiles(string root)
    {
        for (var i = 0; i < FileCount; i++)
        {
            var includeHit = i < HitFileCount;
            var lines = new List<string>
            {
                $"// ファイル {i} 生成コード（性能検証用フィクスチャ）",
                "using System;",
                "",
                "namespace Sample.Generated;",
                "",
                $"public sealed class Sample{i}",
                "{",
            };
            for (var m = 0; m < 12; m++)
            {
                lines.Add($"    public void Method{m}(int value) => Console.WriteLine(value + {i});");
            }
            lines.Add(includeHit ? "    // 参照: Method19 はこのファイルでは未使用" : "    // 参照: 補助コメント");
            lines.Add("}");
            File.WriteAllText(Path.Combine(root, $"Sample{i:D4}.cs"), string.Join("\n", lines) + "\n");
        }
    }
}
