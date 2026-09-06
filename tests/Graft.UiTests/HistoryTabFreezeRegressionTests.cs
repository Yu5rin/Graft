using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 不具合1（実機点検）の回帰テスト: 履歴の差分タブを開いた状態で新しいパッチを解析すると、
/// 接ぎ木パネルが真っ白になったまま操作を受け付けなくなる（UIスレッドが無限ループに
/// 入り込んで戻ってこない）不具合。
///
/// 【原因の特定方法（推論ではなく実測）】 このテストは元々、点検報告どおり「適用可能ブロックが
/// 0件のパッチ」だけを対象に書いた。ところがヘッドレスのViewModelだけの再現（実ウィンドウ無し）
/// では再現せず、<see cref="Views.ShellWindow"/>を実際に開いてレイアウト・バインディングまで
/// 走らせて初めてハングした。これは原因がViewModelの相互呼び出しではなく、View側の
/// データバインディング（AvaloniaのRadioButtonGroupManagerによる自動排他）にあることを示す。
/// <c>dotnet test --blame-hang-dump-type full --blame-hang-timeout 30s</c>でハングダンプを採取し、
/// <c>dotnet-dump analyze</c>でハング中のUIスレッドのコールスタックを確認したところ、
/// <see cref="DiffViewModel"/>.set_IsSideBySide → 複数のAvalonia.PropertyStore層 →
/// Avalonia.Controls.RadioButtonGroupManager.OnCheckedChanged → 再びDiffViewModel.set_IsSideBySide
/// という無限再入を実際に確認した（詳細はdocs/変更履歴.mdの「修正（操作不能）」節、および
/// DiffView.axaml.csのコンストラクタのコメント参照）。
///
/// 原因（RadioButtonのGroupName衝突）はブロックが適用可能かどうかに関係なく成立するため、
/// 「適用可能ブロック0件」（点検報告どおりのケース）と「適用可能ブロックあり」（点検では
/// 正常と報告されていたケース）の両方を検証する。後者について、点検報告と異なり実際には
/// 同じ原因でハングすることを実測で確認したため、両方を回帰対象に含めている。
/// </summary>
public class HistoryTabFreezeRegressionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-history-tab-freeze", Guid.NewGuid().ToString("N"));
    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly ShownWindowTracker _windows = new();

    public HistoryTabFreezeRegressionTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
    }

    public void Dispose()
    {
        _windows.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "不具合1回帰: 履歴差分タブを開いた状態で0件適用可のパッチを解析してもフリーズしない")]
    public async Task 履歴タブ表示中に適用不可パッチを解析してもフリーズしない()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        // r1を作り、履歴差分タブを開く（差分:r1）。
        _clipboard.Text = BuildFullPatch("a.txt", "a1");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
        await SelectHistoryRevisionAsync(shell, "r1").ConfigureAwait(true);
        shell.Editor.Tabs.Should().ContainSingle(t => t.IsHistoryDiffTab, "履歴差分タブが開いているはず");
        window.CaptureRenderedFrame().Should().NotBeNull();

        // 適用可能ブロックが0件のパッチ（SEARCH部が現在のファイルと一致しない）を解析する。
        //
        // 【安全にハングを検出する方法についての注記】 この不具合は実ウィンドウ・実バインディング
        // （AvaloniaのRadioButtonGroupManager）の中で起こる同期的な無限再入であり、UIスレッド
        // そのものが戻ってこなくなる。バックグラウンドスレッド（Task.Run）からAvaloniaの
        // コントロールを操作するとスレッド検証（Dispatcher.VerifyAccess）に弾かれて別の
        // 例外になり検出を偽装してしまうため使えない。また`SafeHandler.OnUnexpected`
        // （静的フィールド）を使った再入回数カウントも検討したが、xUnitはデフォルトで
        // テストクラスを並行実行するため、他のテストと静的状態を奪い合ってしまう危険がある。
        // そのため、このテストが属するGraft.UiTestsプロジェクト全体に既に用意されている
        // 安全網（tests/Graft.UiTests/test.runsettingsのTestSessionTimeout、および
        // このテストクラス自身に指定する--blame-hang-timeout。過去の同種の事故
        // （課題3: 応答者のいないモーダルダイアログでのハング）で採用されたのと同じ方式）に
        // 委ねる。すなわちこの呼び出し自体はタイムアウトせず、フリーズしていれば
        // dotnet testプロセスが「test run timeout」または「blame-hang」で打ち切られ、
        // 非0の終了コードとハングダンプで検知できる。実際、修正前のコードではこの直後の
        // ExecuteAsyncが返ってこず、--blame-hang-timeout（30秒）でテストホストが強制終了
        // されることを確認済み（詳細はdocs/変更履歴.mdの「修正（操作不能）」節参照）。
        _clipboard.Text = BuildMismatchPatch("a.txt", "この内容はa.txtに存在しない");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        window.CaptureRenderedFrame().Should().NotBeNull();

        shell.Graft.Blocks.Should().NotBeEmpty("解析自体は完了しているはず（ブロックは作られる）");
        shell.Graft.State.Should().Be(CenterPaneState.Content);
        shell.Graft.StatusSummaryText.Should().Contain("0件適用可");
    }

    [AvaloniaFact(DisplayName = "不具合1回帰（点検報告との相違点）: 履歴差分タブを開いた状態で適用可能なパッチを解析してもフリーズしない")]
    public async Task 履歴タブ表示中に適用可能パッチを解析してもフリーズしない()
    {
        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildFullPatch("a.txt", "a1");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
        await SelectHistoryRevisionAsync(shell, "r1").ConfigureAwait(true);
        shell.Editor.Tabs.Should().ContainSingle(t => t.IsHistoryDiffTab);
        window.CaptureRenderedFrame().Should().NotBeNull();

        // 別のファイル（b.txt）を新規作成する、常に適用可能なパッチ。ハング検出の考え方は
        // 上のテスト（履歴タブ表示中に適用不可パッチを解析してもフリーズしない）のコメント参照。
        _clipboard.Text = BuildFullPatch("b.txt", "new content");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        window.CaptureRenderedFrame().Should().NotBeNull();

        shell.Editor.Tabs.Should().Contain(t => t.IsHistoryDiffTab, "履歴差分タブは残ったままのはず");
        shell.Editor.Tabs.Should().Contain(t => t.Kind == EditorTabKind.Diff, "新しい差分タブも並んで開いているはず");
        shell.Graft.Blocks.Should().ContainSingle(b => b.PathText == "b.txt");
    }

    private async Task<(ShellViewModel Shell, Avalonia.Controls.Window Window)> OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(new Settings { ShowPreview = false }).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths, new Settings { ShowPreview = false }, settingsStore, new PatchQueue(appPaths),
            new ProjectStore(appPaths), new RevisionStore(appPaths), new RevisionRestorer(appPaths),
            new AutoConfirmDialogService(), new FakeUiServices(_clipboard), openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        await WaitForShellInitializedAsync(shell).ConfigureAwait(true);
        return (shell, window);
    }

    private static async Task WaitForShellInitializedAsync(ShellViewModel shell)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (shell.Graft.ProjectPane.State == ProjectPaneState.Loading)
            {
                await Task.Delay(20, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 履歴一覧から指定ラベルの行を選択し、HistoryDiffChangedが発火する（＝履歴差分タブの
    /// 開閉が反映される）まで待つ（HistoryDiffTabScenarioTestsと同じ手法）。
    /// </summary>
    private static async Task SelectHistoryRevisionAsync(ShellViewModel shell, string revisionLabel)
    {
        var tcs = new TaskCompletionSource();
        void OnChanged(object? s, EventArgs e) => tcs.TrySetResult();
        shell.Graft.HistoryDiffChanged += OnChanged;
        try
        {
            shell.Graft.History.SelectedItem = shell.Graft.History.Items.Single(i => i.RevisionLabel == revisionLabel);
            await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
        }
        finally
        {
            shell.Graft.HistoryDiffChanged -= OnChanged;
        }
    }

    private static string BuildFullPatch(string relativePath, string content)
        => $"<<<< FILE: {relativePath} MODE=FULL\n{content}\n>>>> END\n";

    /// <summary>SEARCH部が対象ファイルの内容と一致しない（＝0件適用可になる）パッチ。</summary>
    private static string BuildMismatchPatch(string relativePath, string search)
        => "<<<< PATCH\nsummary: test\ntype: fix\n>>>>\n\n<<<< FILE: " + relativePath +
           "\n<<<<<<< SEARCH\n" + search + "\n=======\n置き換え後\n>>>>>>> REPLACE\n>>>> END\n\n";

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        await WaitForCommandIdleAsync(command).ConfigureAwait(true);
    }

    private static async Task WaitForCommandIdleAsync(System.Windows.Input.ICommand command)
    {
        if (command is not AsyncRelayCommand async) return;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (async.IsExecuting && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private sealed class AutoConfirmDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel) => Task.FromResult<bool?>(true);
        public Task<string?> PromptAsync(string title, string message, string? initial = null) => Task.FromResult<string?>(initial ?? "test");
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }
        public void SetText(string text) => Text = text;
        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    private sealed class FakeUiServices : IUiServices
    {
        private readonly AvaloniaUiServices _inner = new();
        public FakeUiServices(IClipboardAccess clipboard) { Clipboard = clipboard; }
        public IClipboardAccess Clipboard { get; }
        public IScreenInfo Screens => _inner.Screens;
        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }
}
