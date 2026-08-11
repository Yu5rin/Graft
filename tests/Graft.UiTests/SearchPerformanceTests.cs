using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
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
///
/// 計測手法について: 「結果ツリーへバインドした状態でも横断検索が2秒以内に完了する」は
/// CIで実際に不定期失敗した（基準38ms・対象728msで倍率19.2倍、閾値15倍を突破）。原因は
/// tests/Graft.Tests/CrossFileSearchPerformanceTests.csが最初に踏んだ欠陥と同型で、
/// 「整数ミリ秒の小さい値を分母にする」「基準側しかウォームアップしない」「1回だけの
/// 計測で判定する」という3点が重なっていた。ここでは同クラスで確立した手法
/// （両側ウォームアップ・基準1回対象1回を1組とした交互計測・組ごとの倍率の中央値・
/// Elapsed.TotalMillisecondsの小数精度）へ揃える。詳細な設計の経緯はそちらのクラス
/// ドキュメントコメントを参照し、ここでは再発明しない。
///
/// 総処理量について: 基準（エンジン単体）・対象（画面バインド込み）は、同じ1000ファイルの
/// プロジェクトに対して毎回1回ずつ横断検索を行うため、CrossFileSearchPerformanceTestsの
/// ScaleFactorのような水増しをしなくても総処理ファイル数は既に揃っている
/// （見たいのは画面バインドが載せる追加コストの倍率であり、行数・ファイル数に対する
/// アルゴリズムの計算量ではないため）。
/// </summary>
public class SearchPerformanceTests : IDisposable
{
    private const int FileCount = 1000;
    private const int HitFileCount = 869;
    private const string Query = "Method19";

    private readonly ITestOutputHelper _output;

    // 各テストがSearchViewを載せたWindowをShow()するが、以前はどれもClose()もShownWindowTracker
    // への登録もしないまま終わっていた（閉じ忘れの実例）。他のシナリオテストと同じ後始末に揃える。
    private readonly ShownWindowTracker _windows = new();

    public SearchPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>基準1回・対象1回を1組として、この組数だけ交互に計測し、組ごとの倍率の中央値を採用する
    /// （CrossFileSearchPerformanceTestsで確立した手法に合わせる）。</summary>
    private const int MeasurementRuns = 7;

    /// <summary>
    /// 負荷なしで組ごとの倍率の中央値を3回計測した実測では1.76〜1.92倍の狭い範囲に収まった。
    /// 4本のビジーループで4コアを飽和させた負荷下では、正常時でも中央値が0.97〜5.04倍まで
    /// 広がることを確認した（本コミットの検証手順参照）。仮想化を無効化した場合は負荷なしでも
    /// 中央値34.2倍と明確に区別できる一方、バッチ反映のみを無効化した場合（仮想化は健在）は
    /// 負荷なしで中央値2.94〜3.05倍にとどまり、負荷下での正常時のばらつき（最大5.04倍）と
    /// 重なってしまうため、両者を安全に分離できる閾値は見つからなかった（検証4の詳細は
    /// WaitWhileSearchingAndRenderAsyncのコメントおよび本コミットの検証手順を参照）。
    /// そのため閾値は「負荷下でも正常時に誤って落ちない」ことを優先し、負荷下での正常時の
    /// 実測上限（5.04倍）に対して十分な余裕を持たせた10.0を採用する（旧来の15倍・1回だけの
    /// 計測・整数msから、中央値化と小数精度化により安定性は大きく改善したが、バッチ反映単体の
    /// 回帰に対する検出力は仮想化の回帰ほど強くない）。
    /// </summary>
    private const double RatioThreshold = 10.0;

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

            var vm = new SearchViewModel(engine, new NullDialogService())
            {
                Query = Query,
            };
            vm.SetContext(project, settings);

            var view = new SearchView { DataContext = vm };
            var window = _windows.Track(new Window { Width = 900, Height = 700, Content = view });
            window.Show();
            window.CaptureRenderedFrame();

            // ウォームアップ（初回JIT・初回ファイルI/Oのゆらぎを計測対象から除く）。基準側
            // （エンジン単体）だけをウォームアップすると、対象（画面バインド）は初回描画・
            // 初回コントロール実体化のコストを負ったまま計測されてしまい非対称が生じる
            // （CrossFileSearchPerformanceTestsが最初に踏んだ欠陥と同型）。両方を必ず
            // ウォームアップする。
            await CollectAsync(engine, project, settings, options);
            vm.SearchCommand.Execute(null);
            await WaitWhileSearchingAndRenderAsync(vm, window).ConfigureAwait(true);
            vm.SetContext(project, settings); // 結果クリア（次の計測が「タブ切替」相当の別経路にならないよう毎回SearchCommandを実行し直す）

            // 基準1回（エンジン単体）・対象1回（画面バインド込み）を1組として、直前直後に
            // 交互に計測する。同じ組の基準・対象は負荷側の状態がほぼ同じ条件を共有するため、
            // 「片方だけ運良く空いた瞬間に当たる」非対称が起きにくい
            // （詳細はCrossFileSearchPerformanceTestsのクラスドキュメントコメントを参照）。
            var baselineTimes = new List<double>(MeasurementRuns);
            var targetTimes = new List<double>(MeasurementRuns);
            var ratios = new List<double>(MeasurementRuns);
            for (var i = 0; i < MeasurementRuns; i++)
            {
                var baselineSw = Stopwatch.StartNew();
                var baselineHits = await CollectAsync(engine, project, settings, options);
                baselineSw.Stop();
                baselineHits.Should().HaveCount(HitFileCount);

                vm.SetContext(project, settings);
                var sw = Stopwatch.StartNew();
                vm.SearchCommand.Execute(null);
                // WaitWhileSearchingAsync（単純ポーリング）ではなく、検索中も定期的に描画を
                // 挟むWaitWhileSearchingAndRenderAsyncを使う。実機の6秒超の遅さは「検索中、
                // UIスレッドが継続的に再描画し続ける」状況（ヒットのたびにレイアウト・描画が
                // 走る）で発生したものであり、検索完了後に1回だけ描画するのでは、バッチ反映
                // （BatchFlushIntervalMs）を無効化しても再現できない（本コミットの検証手順で
                // 実測・確認済み: 完了後1回描画だけの計測では、バッチ無効化時の倍率が2.6倍
                // 程度にしか上がらず、閾値10を超えず検出できなかった）。検索の進行中に
                // 実際に何度も描画させることで、ヒットのたびに再描画が走る劣化を検出できる
                // ようにする。
                await WaitWhileSearchingAndRenderAsync(vm, window).ConfigureAwait(true);
                sw.Stop();

                vm.IsSearching.Should().BeFalse("タイムアウトせず検索が完了していること");
                vm.Groups.Should().HaveCount(HitFileCount);

                var baselineMs = baselineSw.Elapsed.TotalMilliseconds;
                var targetMs = sw.Elapsed.TotalMilliseconds;
                baselineTimes.Add(baselineMs);
                targetTimes.Add(targetMs);
                ratios.Add(targetMs / Math.Max(0.01, baselineMs));
            }

            // 組ごとの倍率の中央値を採用する。理由はCrossFileSearchPerformanceTestsの
            // クラスドキュメントコメントを参照（1〜2組が外れ値になっても最終判定が引きずられにくい）。
            var ratio = Median(ratios);

            var baselineRawText = string.Join(", ", baselineTimes.Select(t => t.ToString("F3")));
            var targetRawText = string.Join(", ", targetTimes.Select(t => t.ToString("F3")));
            var ratiosRawText = string.Join(", ", ratios.Select(r => r.ToString("F3")));

            _output.WriteLine(
                $"{FileCount}ファイル中「{Query}」の横断検索: エンジン単体（{MeasurementRuns}組）=[{baselineRawText}] ms");
            _output.WriteLine($"画面バインド込み（{MeasurementRuns}組）=[{targetRawText}] ms");
            _output.WriteLine($"組ごとの倍率: [{ratiosRawText}] → 中央値 {ratio:F2}倍");

            // 実機で確認された遅さ（エンジン単体は高速なのに画面バインド込みで6秒超）を
            // 大きく下回ることを確認する回帰ガード。エンジン単体の時間との倍率で判定するため、
            // 絶対時間ではなくCI環境の遅さそのものには左右されない。失敗時は基準・対象それぞれの
            // 全計測値（生の値）と組ごとの倍率をメッセージへ含める。
            ratio.Should().BeLessThan(RatioThreshold,
                $"組ごとの倍率の中央値でエンジン単体に対し画面バインド込みが{ratio:F2}倍かかっている"
                + $"（基準: 全{MeasurementRuns}組[{baselineRawText}]ms → 対象: 全{MeasurementRuns}組[{targetRawText}]ms → "
                + $"組ごとの倍率[{ratiosRawText}]）。"
                + "結果ツリーの仮想化とバッチ反映が効いていない可能性がある");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>中央値を求める（偶数個なら中央2件の平均。CrossFileSearchPerformanceTestsと同じ実装）。</summary>
    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
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
            var window = _windows.Track(new Window { Width = 900, Height = 700, Content = view });
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
            var window = _windows.Track(new Window { Width = 900, Height = 700, Content = view });
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

    /// <summary>
    /// 実機で確認された6秒超の遅さを再現できる粒度で描画しながら完了を待つ。試した3案のうち
    /// 本コミットの検証手順で実際に劣化（バッチ反映の無効化）を検出できたのはこの案のみだった:
    ///   (1) 完了後に1回だけ描画（旧実装） → バッチ無効化時の倍率2.6倍で閾値10を超えず未検出。
    ///   (2) 5msごとに無条件でポーリング描画 → 倍率3.4倍で同じく未検出。
    ///   (3) <see cref="SearchViewModel.Groups"/>の<c>CollectionChanged</c>のたびに無条件で
    ///       描画 → 逆に正常時（バッチ反映が効いている状態）まで60倍を超えて誤検出した。
    ///       原因はバッチ反映が「一定間隔ごとにまとめて反映する」設計であって「反映のAdd呼び出し
    ///       自体の回数を減らす」設計ではないため（<c>SearchViewModel.FlushPending</c>参照。
    ///       1回のフラッシュでも新規ファイルの数だけ<c>Groups.Add</c>を個別に呼ぶ）、
    ///       バッチの有無に関わらずAdd呼び出し回数はほぼ869回のまま変わらない。
    /// 実機の「体感の遅さ」は、Add呼び出し回数そのものではなく、UIスレッドが一定の描画周期
    /// （60fpsなら約16ms間隔）でその時点までの変更をまとめて1フレームとして描画する頻度に
    /// 由来する。バッチ反映が効いていれば1回の検索（数百ms）あたり数回〜十数回のフレームで
    /// 済むが、無効化すると変更が869回に分散し、描画周期の粒度でも数十フレームに増える。
    /// これを模すため、CollectionChangedのたびに無条件で描画するのではなく、直近の描画から
    /// <see cref="SimulatedFrameIntervalMs"/>（60fps相当）以上経過していた場合だけ描画する
    /// （実際のコンポジタが変更を1フレームへ間引く動きに相当する）。
    /// </summary>
    private const double SimulatedFrameIntervalMs = 16.0;

    private static async Task WaitWhileSearchingAndRenderAsync(SearchViewModel vm, Window window)
    {
        var frameSw = Stopwatch.StartNew();
        void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (frameSw.Elapsed.TotalMilliseconds < SimulatedFrameIntervalMs) return;
            window.CaptureRenderedFrame();
            frameSw.Restart();
        }

        vm.Groups.CollectionChanged += OnGroupsChanged;
        try
        {
            for (var i = 0; i < 1000 && vm.IsSearching; i++)
            {
                await Task.Delay(10).ConfigureAwait(true);
            }
        }
        finally
        {
            vm.Groups.CollectionChanged -= OnGroupsChanged;
        }
        window.CaptureRenderedFrame();
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
