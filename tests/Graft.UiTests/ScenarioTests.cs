using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 主要シナリオを画面ありで通しで動かす（仕様書v2.1 18章「UIの自動検証」・20章 L5）。
///
/// ViewTestsが「画面が壊れずに描けること」、StartupTestsが「本番と同じ依存で組めること」を
/// 見るのに対し、ここでは利用者の操作の流れ（プロジェクト登録 → 解析 → ブロック選択 →
/// 差分表示 → 適用）をViewModel経由で実行し、UIが追随することまで確認する。
/// </summary>
public class ScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-scenario", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();

    public ScenarioTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
    }

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

    [AvaloniaFact(DisplayName = "プロジェクト登録から解析・差分表示・適用まで通しで動く")]
    public async Task 解析から適用まで通しで動く()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);

        // 1. プロジェクトを登録すると一覧に現れ、選択される。
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        shell.Graft.ProjectPane.Items.Should().ContainSingle();
        shell.Graft.ProjectPane.SelectedItem.Should().NotBeNull();
        window.CaptureRenderedFrame().Should().NotBeNull();

        // 2. クリップボードのパッチを解析すると、ブロック一覧に反映される。
        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Should().ContainSingle("パッチ1件がブロックとして並ぶ必要がある");
        shell.Graft.Blocks[0].IsOk.Should().BeTrue("マッチできる内容なので適用可になる必要がある");
        shell.IsGraftPanelOpen.Should().BeTrue("解析すると接ぎ木パネルが自動展開される（9.2）");
        window.CaptureRenderedFrame().Should().NotBeNull();

        // 3. ブロックを選ぶと、差分がエディタ領域のタブとして開く（9.2/4.8）。
        shell.Graft.SelectedBlock = shell.Graft.Blocks[0];
        shell.Editor.ActiveTab.Should().NotBeNull();
        shell.Editor.ActiveTab!.Kind.Should().Be(EditorTabKind.Diff);
        window.CaptureRenderedFrame().Should().NotBeNull();

        // 4. 適用するとファイルが書き換わり、履歴が1件増える。
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        var applied = await File.ReadAllTextAsync(targetPath).ConfigureAwait(true);
        applied.Should().Contain("2行目（変更後）");
        applied.Should().NotContain("2行目\n", "元の行は置き換わっている必要がある");
        window.CaptureRenderedFrame().Should().NotBeNull();
    }

    // 適用後フック（仕様書6.5）の通しシナリオはHookScenarioTests.cs（1ファイル400行上限のため分割）。
    // 4.1 ファイルからのパッチ解析の通しシナリオはFileParseScenarioTests.cs（同じく400行上限のため分割）。

    [AvaloniaFact(DisplayName = "エクスプローラからファイルを開くとエディタのタブになる")]
    public async Task エクスプローラからファイルを開ける()
    {
        var targetPath = Path.Combine(_projectDirectory, "open-me.txt");
        await File.WriteAllTextAsync(targetPath, "開いた内容\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        shell.SelectSideView(SideViewKind.Explorer);
        window.CaptureRenderedFrame().Should().NotBeNull();

        var opened = await shell.Editor.OpenFileAsync(targetPath, preview: false).ConfigureAwait(true);
        opened.IsSuccess.Should().BeTrue();

        shell.Editor.Tabs.Should().ContainSingle();
        shell.Editor.ActiveTab!.Session.Document.Text.Should().Contain("開いた内容");
        window.CaptureRenderedFrame().Should().NotBeNull();
    }

    [AvaloniaFact(DisplayName = "解析に失敗したパッチはエラーとして表示される")]
    public async Task 解析に失敗したパッチはエラーになる()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        // ファイル内に存在しない内容をSEARCH部に置くとマッチに失敗する（E101）。
        _clipboard.Text = BuildPatch("sample.txt", "存在しない行", "置換後");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Should().ContainSingle();
        shell.Graft.Blocks[0].IsError.Should().BeTrue("マッチできないブロックは失敗として示される必要がある");
        shell.Graft.Blocks[0].IssueText.Should().Contain("E101");
        window.CaptureRenderedFrame().Should().NotBeNull();
    }

    /// <summary>
    /// 不具合1の回帰テスト（実機のスクリーンショットで見つかった「1ファイルに複数のエラーが
    /// あるとき、エラー行が同じ位置に重なって描画される」現象）。
    /// ピクセル単位の重なり自体をheadlessテストで安定して検証するのは難しい
    /// （実際にheadlessで幅・折り返し・スクロール・仮想化・実機同等のX11環境まで幅広く
    /// 再現を試みたが、いずれも重ならずに描画された）ため、ここでは検証可能な形へ
    /// 目的を落とし込む: 同一ファイルに対する複数のSEARCH失敗が、1つに丸められたり
    /// 互いを上書きしたりせず、それぞれ独立したブロック（BlockItemViewModel）として
    /// 保持されることを確認する。取り違えて重ね描きされていれば、ここでBlocksの件数や
    /// 行番号の対応がずれて検出できる。
    /// </summary>
    [AvaloniaFact(DisplayName = "不具合1: 同一ファイルの複数のSEARCH失敗が個別のブロックとして保持される")]
    public async Task 同一ファイルの複数のSEARCH失敗が個別のブロックとして保持される()
    {
        var targetPath = Path.Combine(_projectDirectory, "utf8bom-sample.txt");
        await File.WriteAllTextAsync(targetPath, string.Concat(Enumerable.Range(1, 15).Select(n => $"{n}行目\n")))
            .ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        // 同一FILEセクションに、ファイル内に存在しないSEARCH部を2つ含める。
        _clipboard.Text = """
            <<<< FILE: utf8bom-sample.txt
            summary: 不具合1回帰テスト
            <<<<<<< SEARCH
            存在しない行A
            =======
            置換後A
            >>>>>>> REPLACE
            <<<<<<< SEARCH
            存在しない行B
            =======
            置換後B
            >>>>>>> REPLACE
            >>>> END

            """;
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        // 2件とも取り違え・重ね描きされず、別々のブロックとして残っていること。
        shell.Graft.Blocks.Should().HaveCount(2, "2つのSEARCH失敗はそれぞれ独立したブロックになる必要がある");
        shell.Graft.Blocks.Should().OnlyContain(b => b.IsError, "SEARCH不一致のブロックはすべて失敗として示される必要がある");
        shell.Graft.Blocks.Select(b => b.PathText).Should().AllBeEquivalentTo("utf8bom-sample.txt");

        // 各ブロックのエラー文が互いを上書きしていないこと（同一の文字列に潰れていないこと）。
        var issueTexts = shell.Graft.Blocks.Select(b => b.IssueText).ToList();
        issueTexts.Should().OnlyHaveUniqueItems("2件のエラー文が同じ内容に潰れていれば重ね描きと見分けが付かない");
        issueTexts.Should().AllSatisfy(t => t.Should().Contain("E101"));

        window.CaptureRenderedFrame().Should().NotBeNull();
    }

    [AvaloniaFact(DisplayName = "検索結果の選択変更（単一クリック・キーボード移動）で該当行へジャンプする")]
    public void 検索結果を選択するとジャンプ要求が発火する()
    {
        var vm = new SearchViewModel(new CrossFileSearchEngine(), new AutoConfirmDialogService());
        var group = new SearchFileGroupViewModel("/proj/a.txt", "a.txt");
        var hit = new SearchHitViewModel(new SearchHit
        {
            FullPath = "/proj/a.txt",
            RelativePath = "a.txt",
            LineNumber = 3,
            LineText = "hello world",
            ColumnStart = 0,
            MatchLength = 5,
        });
        group.Hits.Add(hit);
        vm.Groups.Add(group);

        var view = new SearchView { DataContext = vm };
        var window = new Window { Width = 400, Height = 400, Content = view };
        window.Show();

        (string FullPath, int Line)? requested = null;
        vm.JumpRequested += (_, target) => requested = target;

        var tree = view.GetControl<TreeView>("ResultTree");
        tree.SelectedItem = hit; // 単一クリック・キーボードの上下移動と同じ「選択変更」を模擬する。

        requested.Should().Be(("/proj/a.txt", 3), "ヒット行の選択でジャンプが要求される必要がある");

        requested = null;
        tree.SelectedItem = group; // ファイル見出し（ヒット行以外）の選択では何もしない。
        requested.Should().BeNull("ファイル見出しの選択ではジャンプしてはならない");
    }

    [AvaloniaFact(DisplayName = "タブ見出し右クリックの「他のタブを閉じる」で対象以外が閉じる")]
    public async Task 他のタブを閉じるで対象以外が閉じる()
    {
        var pathA = Path.Combine(_projectDirectory, "a.txt");
        var pathB = Path.Combine(_projectDirectory, "b.txt");
        var pathC = Path.Combine(_projectDirectory, "c.txt");
        await File.WriteAllTextAsync(pathA, "A\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathB, "B\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathC, "C\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await shell.Editor.OpenFileAsync(pathA).ConfigureAwait(true);
        var keep = (await shell.Editor.OpenFileAsync(pathB).ConfigureAwait(true)).Value;
        await shell.Editor.OpenFileAsync(pathC).ConfigureAwait(true);
        shell.Editor.Tabs.Should().HaveCount(3);

        var closed = await shell.Editor.CloseOthersAsync(keep).ConfigureAwait(true);

        closed.Should().BeTrue();
        shell.Editor.Tabs.Should().ContainSingle().Which.Should().BeSameAs(keep);
    }

    [AvaloniaFact(DisplayName = "タブ見出し右クリックの「すべてのタブを閉じる」で開いているタブが空になる")]
    public async Task すべてのタブを閉じるで空になる()
    {
        var pathA = Path.Combine(_projectDirectory, "a.txt");
        var pathB = Path.Combine(_projectDirectory, "b.txt");
        await File.WriteAllTextAsync(pathA, "A\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathB, "B\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await shell.Editor.OpenFileAsync(pathA).ConfigureAwait(true);
        await shell.Editor.OpenFileAsync(pathB).ConfigureAwait(true);
        shell.Editor.Tabs.Should().HaveCount(2);

        var closed = await shell.Editor.CloseAllAsync().ConfigureAwait(true);

        closed.Should().BeTrue();
        shell.Editor.Tabs.Should().BeEmpty();
    }

    [AvaloniaFact(DisplayName = "「他のタブを閉じる」は保存確認でキャンセルされると中断する")]
    public async Task 他のタブを閉じるは保存確認でキャンセルされると中断する()
    {
        var pathA = Path.Combine(_projectDirectory, "a.txt");
        var pathB = Path.Combine(_projectDirectory, "b.txt");
        await File.WriteAllTextAsync(pathA, "A\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathB, "B\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync(new CancelSaveDialogService()).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        var tabA = (await shell.Editor.OpenFileAsync(pathA).ConfigureAwait(true)).Value;
        var tabB = (await shell.Editor.OpenFileAsync(pathB).ConfigureAwait(true)).Value;

        // aを未保存の変更ありにしておく。保存確認で「キャンセル」を選んだ想定。
        tabA.Session.Document.Insert(0, "編集");

        var closed = await shell.Editor.CloseOthersAsync(tabB).ConfigureAwait(true);

        closed.Should().BeFalse("保存確認でキャンセルされたら中断する必要がある");
        shell.Editor.Tabs.Should().Contain(tabA, "キャンセルされたタブは閉じずに残す必要がある");
    }

    /// <summary>
    /// このファイルの各テストは <c>window.CaptureRenderedFrame()</c> でシナリオの各段階の描画結果を
    /// 確認する（クラス概要コメントのとおり「UIが追随することまで確認する」のが目的）ため、
    /// 本物のShellWindowを実体化する必要がある。
    ///
    /// そのためwindow.Show()は必須だが、window.Show()はShellWindow.OnLoadedを介して非同期に
    /// MainViewModel.InitializeAsyncを呼ぶ。ここでさらに明示的にInitializeAsyncを呼んでしまうと、
    /// 2つの初期化が実行順序不定のまま同時に走り、settings.json/projects.jsonの読み直しが
    /// 競合する（LiveSettingsPropagationTests.OpenShellAsync参照。実機で5割前後の確率での
    /// 失敗を確認した事故と同じ種類の競合状態）。そこでこのメソッドはInitializeAsyncを
    /// 自分では呼ばず、OnLoaded経由の初期化が完了するのを待つだけにする。ProjectPane.Stateは
    /// InitializeAsyncの最後に呼ばれるProjectPane.LoadAsyncの完了時点で必ずLoading以外へ
    /// 変わるため、これを初期化完了の合図として使う（初期化が1回しか走らないので安全に待てる）。
    /// </summary>
    private async Task<(ShellViewModel Shell, Avalonia.Controls.Window Window)> OpenShellAsync(IDialogService? dialogs = null)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        // ShowPreview（課題1）はこのファイルの各シナリオの対象外なので明示的にfalseにし、
        // ApplyCommandが素通りする従来どおりの挙動のまま検証できるようにする
        // （ApplyPreviewWindowTests.csで専用の回帰テストを別に用意する）。
        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(new Settings { ShowPreview = false }).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings { ShowPreview = false },
            settingsStore,
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            dialogs ?? new AutoConfirmDialogService(),
            new FakeUiServices(_clipboard),
            openSettings: () => { });

        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();
        await WaitForShellInitializedAsync(shell).ConfigureAwait(true);
        return (shell, window);
    }

    /// <summary>
    /// window.Show()（ShellWindow.OnLoaded経由）が裏で走らせているMainViewModel.InitializeAsyncの
    /// 完了を、それ自身を呼び直すことなく待つ。ProjectPaneViewModelはLoading状態で構築され、
    /// InitializeAsyncの最後で呼ぶProjectPane.LoadAsyncが完了するまでLoadingのまま変わらないため、
    /// これが変わったことをもって初期化完了とみなせる。
    /// </summary>
    private static async Task WaitForShellInitializedAsync(ShellViewModel shell)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (shell.Graft.ProjectPane.State == ProjectPaneState.Loading)
            {
                await Task.Delay(10, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException(
                "ShellWindow.OnLoaded経由の初期化が30秒以内に完了しませんでした（ProjectPane.StateがLoadingのまま）。", ex);
        }
    }

    /// <summary>SEARCH/REPLACE形式のパッチ本文を組み立てる（仕様書4.1）。</summary>
    private static string BuildPatch(string relativePath, string search, string replace)
        => $"""
            <<<< FILE: {relativePath}
            summary: テスト用の変更
            <<<<<<< SEARCH
            {search}
            =======
            {replace}
            >>>>>>> REPLACE
            >>>> END

            """;

    /// <summary>非同期コマンドを実行し、完了するまで待つ。</summary>
    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);

        // AsyncRelayCommand は ICommand.Execute が void のため、実行中かどうかで完了を待つ。
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting)
            {
                await Task.Delay(10).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// 確認をすべて承諾するダイアログ。実際の操作では利用者が「はい」を押す場面にあたる
    /// （Null実装は常に否定を返すため、適用まで進めない）。
    /// </summary>
    private sealed class AutoConfirmDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    /// <summary>
    /// 未保存変更の保存確認で常に「キャンセル」を選ぶダイアログ（<see cref="CloseOthersAsync"/>等の
    /// 中断挙動の検証用）。それ以外の確認は承諾する。
    /// </summary>
    private sealed class CancelSaveDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(null);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    /// <summary>テストから内容を差し替えられるクリップボード。</summary>
    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public void SetText(string text) => Text = text;

        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    /// <summary>クリップボードだけ差し替えたUI機能一式。画面情報とタイマーは本物を使う。</summary>
    private sealed class FakeUiServices : IUiServices
    {
        private readonly AvaloniaUiServices _inner = new();

        public FakeUiServices(IClipboardAccess clipboard)
        {
            Clipboard = clipboard;
        }

        public IClipboardAccess Clipboard { get; }

        public IScreenInfo Screens => _inner.Screens;

        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }
}
