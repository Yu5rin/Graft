using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 「時間のかかる操作の最中もUIが応答し続ける」ことを実測で固定する回帰テスト。
///
/// 判定は<see cref="UiThreadStallProbe"/>（同クラスのコメント参照）で行う。UIスレッドが
/// 連続して塞がっていた最長時間を測り、しきい値<see cref="StallBudgetMs"/>を超えたら失敗させる。
///
/// 修正前の実測値（このテストを修正前のコードに対して実行したときの値。Xvfb不要のヘッドレス、
/// 同一マシン・Release構成）:
///   - コンテキスト収集のプレビュー（400ファイル・約1.6MB）: 最長停滞 約2.0秒
///     （<see cref="UiThreadStallProbe.TickCount"/>は0＝操作中に一度もUIスレッドが動けない）
///   - エクスプローラのフォルダ展開（直下4000ファイル）: 最長停滞 約0.5秒
/// 修正後は、いずれも数十ミリ秒（＝1フレーム前後）に収まる。
///
/// しきい値は「実機で押した操作が引っかかったと感じ始める境目」より少し緩い300msにしてある。
/// これは、CIの負荷やGCの一時停止で数十〜百数十ミリ秒の揺れが乗ることを見込んだ余裕であり、
/// 修正前の値（数百ミリ秒〜数秒）とは3〜7倍の開きがあるため、緩めても検出力は落ちない。
/// </summary>
public class UiResponsivenessTests : IDisposable
{
    /// <summary>UIスレッドが連続して塞がってよい上限（ミリ秒）。クラス本体のコメント参照。</summary>
    private const int StallBudgetMs = 300;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-ui-responsiveness", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "A-1: コンテキスト収集の「プレビュー」実行中もUIスレッドが応答し続ける")]
    public async Task コンテキスト収集のプレビュー中もUIが応答する()
    {
        var vm = await BuildContextCollectAsync(fileCount: 400, lineCount: 120).ConfigureAwait(true);

        var probe = UiThreadStallProbe.Start();
        await RunAsync(vm.PreviewCommand).ConfigureAwait(true);
        var longestStallMs = probe.StopAndMeasureLongestStallMs();

        vm.PreviewLines.Should().NotBeEmpty("プレビュー内容自体はこれまで通り作られる必要がある");
        probe.TickCount.Should().BeGreaterThan(0, "収集中に一度もUIスレッドが動けないのは「固まっている」ということ");
        longestStallMs.Should().BeLessThan(
            StallBudgetMs,
            "コンテキスト収集の本体（走査・テキスト組み立て・プレビューの字句解析）はUIスレッドで走らせてはならない");

        vm.Dispose();
    }

    [AvaloniaFact(DisplayName = "A-1: 収集中は IsScanning が立ち、待機表示（プログレスバー）が出る")]
    public async Task 収集中はIsScanningが立つ()
    {
        var vm = await BuildContextCollectAsync(fileCount: 60, lineCount: 40).ConfigureAwait(true);

        // IsScanningはプログレスバーのIsVisibleへ直結している（ContextCollectWindow.axaml）。
        // 収集の開始から完了までの間に一度でもtrueになったかを、PropertyChangedで拾う。
        var sawScanning = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ContextCollectViewModel.IsScanning) && vm.IsScanning) sawScanning = true;
        };

        await RunAsync(vm.PreviewCommand).ConfigureAwait(true);

        sawScanning.Should().BeTrue("プレビュー・コピー・保存でも、再走査と同じく待機表示を出す必要がある");
        vm.IsScanning.Should().BeFalse("完了後は必ず下ろす");

        vm.Dispose();
    }

    [AvaloniaFact(DisplayName = "A-3: エクスプローラのフォルダ展開（直下に大量のファイル）でもUIスレッドが応答し続ける")]
    public async Task ファイルツリーの展開中もUIが応答する()
    {
        var explorer = await BuildExplorerAsync(childFileCount: 4000).ConfigureAwait(true);

        var big = explorer.RootNodes.Single(n => n.Name == "big");
        var probe = UiThreadStallProbe.Start();
        big.IsExpanded = true; // 展開要求 → ExplorerViewModel.ReconcileDirectoryAsync（列挙）
        // FileNodeViewModel.IsLoadedはExpandRequestedを発火する「前」に立つ目印であり、
        // 列挙の完了を表さない。実際に子が載り切るまで（＝プレースホルダが実体に
        // 置き換わるまで）待つ。
        await WaitForAsync(() => big.Children.Count == 4000).ConfigureAwait(true);
        var longestStallMs = probe.StopAndMeasureLongestStallMs();

        big.Children.Should().HaveCount(4000, "展開結果はこれまで通りすべて載る必要がある");
        longestStallMs.Should().BeLessThan(
            StallBudgetMs,
            "ディレクトリの列挙は同期処理でも、ExplorerFilterServiceと同じくスレッドプールへ逃がす必要がある");
    }

    // ---- 組み立てヘルパー ----

    private async Task<ContextCollectViewModel> BuildContextCollectAsync(int fileCount, int lineCount)
    {
        var appPaths = new AppPaths(Path.Combine(_root, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, "project");
        Directory.CreateDirectory(Path.Combine(projectDir, "src"));

        // 字句解析（SyntaxLexer）が実際に走る拡張子（.py）で、それなりの行数を作る。
        var body = string.Join(
            "\n",
            Enumerable.Range(0, lineCount).Select(i => $"def f{i}(a, b):  # 行{i}\n    return a + b * {i}"));
        for (var i = 0; i < fileCount; i++)
        {
            File.WriteAllText(Path.Combine(projectDir, "src", $"module{i}.py"), body);
        }

        var store = new ProjectStore(appPaths);
        var registered = (await store.RegisterAsync(projectDir, "responsiveness").ConfigureAwait(true)).Value;
        var vm = new ContextCollectViewModel(
            appPaths, store, registered, new Settings(), new AvaloniaUiServices(), new NullDialogService());
        await vm.InitializeAsync().ConfigureAwait(true);
        return vm;
    }

    private async Task<ExplorerViewModel> BuildExplorerAsync(int childFileCount)
    {
        var projectDir = Path.Combine(_root, "tree");
        var bigDir = Path.Combine(projectDir, "big");
        Directory.CreateDirectory(bigDir);
        for (var i = 0; i < childFileCount; i++)
        {
            File.WriteAllText(Path.Combine(bigDir, $"file{i:D5}.txt"), "x");
        }

        var dialogs = new NullDialogService();
        var ui = new AvaloniaUiServices();
        var editor = new EditorPaneViewModel(new Settings(), dialogs, ui);
        var appPaths = new AppPaths(Path.Combine(_root, "tree_app"));
        var explorer = new ExplorerViewModel(appPaths, editor, dialogs, new Settings(), ui);

        // 列挙1件ごとに走る除外判定（GitignoreFilter.Evaluate）の重さを、実プロジェクトの
        // .gitignore と同等まで持ち上げるための追加パターン。このコンテナのSSDは速く、
        // 4000件の列挙そのものは20ms程度で終わってしまい「固まる」現象を再現できない
        // （点検で報告された実機の0.4〜0.6秒は、ネットワーク越し・アンチウイルス常駐・
        // 大きな.gitignoreといった条件が重なった値）。パターン数で負荷を作ることで、
        // 「UIスレッドで回すか、スレッドプールへ逃がすか」の違いだけを安定して測れるようにする。
        var project = new Project
        {
            Id = "p_tree",
            Name = "ツリー",
            Root = projectDir,
            Overrides = new ProjectOverrides
            {
                Excludes = Enumerable.Range(0, 200).Select(i => $"*.no{i}match").ToArray(),
            },
        };
        await explorer.SetProjectAsync(project).ConfigureAwait(true);
        return explorer;
    }

    /// <summary>非同期コマンドを実行し、完了（IsExecutingが下りる）まで待つ。</summary>
    private static async Task RunAsync(AsyncRelayCommand command)
    {
        // AsyncRelayCommand.ExecuteはCanExecuteがfalseなら何もせず静かに戻る（多重起動の
        // 自己ガード）。実行されないまま「停滞0ms」で通ってしまうと計測の意味が無いため、
        // 呼ぶ前に実行可能であることを確かめる。
        command.CanExecute(null).Should().BeTrue("計測対象のコマンドが実行可能な状態である必要がある");
        command.Execute(null);
        while (command.IsExecuting)
        {
            await Task.Delay(5).ConfigureAwait(true);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1000; i++)
        {
            if (condition()) return;
            await Task.Delay(10).ConfigureAwait(true);
        }

        throw new TimeoutException("条件が10秒以内に成立しませんでした。");
    }
}
