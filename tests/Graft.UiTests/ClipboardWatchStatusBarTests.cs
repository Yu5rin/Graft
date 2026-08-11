using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// クリップボード監視のステータスバー表示（<c>ShellViewModel.ClipboardWatch.cs</c>）の回帰テスト。
/// 9件目の不具合修正: 実装（<c>IClipboardMonitor</c>）は監視の開始・停止・パッチ検知の
/// イベントまでは持っていたが、それを利用者へ伝える表示が一切無かった
/// （「クリップボード監視が反応しない・ステータスバーに監視中の表示も出ない」という
/// 実機報告）。ここではStartupCoordinatorが呼ぶ2つの入口
/// （<see cref="ShellViewModel.SetClipboardWatchActive"/>・
/// <see cref="ShellViewModel.NotifyClipboardPatchDetected"/>）から、実際に画面へ表示すべき
/// 状態（<see cref="ShellViewModel.IsClipboardWatchActive"/>・
/// <see cref="ShellViewModel.HasClipboardPatchNotice"/>）が正しく作られることを検証する。
/// </summary>
public class ClipboardWatchStatusBarTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-clipboard-statusbar", Guid.NewGuid().ToString("N"));

    private readonly FakeClipboard _clipboard = new();
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        // 表示したShellWindowを後始末する（ShownWindowTracker参照。閉じ忘れると
        // 「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」がCIで不定期に出る）。
        _windows.Dispose();

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

    [AvaloniaFact(DisplayName = "SetClipboardWatchActive(true)で「クリップボード監視中」表示が出て、falseで消える")]
    public async Task 監視中表示のオンオフ()
    {
        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);

        shell.IsClipboardWatchActive.Should().BeFalse("既定は監視オフのため表示も出ていないはず");

        shell.SetClipboardWatchActive(true);
        shell.IsClipboardWatchActive.Should().BeTrue("設定オン→監視開始に対応してステータスバー表示も出る必要がある");

        shell.SetClipboardWatchActive(false);
        shell.IsClipboardWatchActive.Should().BeFalse("設定オフ→監視停止に対応して表示も消える必要がある");
    }

    [AvaloniaFact(DisplayName = "監視を停止すると、出ていたパッチ検知通知も一緒に消える")]
    public async Task 監視停止で検知通知も消える()
    {
        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);

        shell.SetClipboardWatchActive(true);
        shell.NotifyClipboardPatchDetected();
        shell.HasClipboardPatchNotice.Should().BeTrue();

        shell.SetClipboardWatchActive(false);

        shell.HasClipboardPatchNotice.Should().BeFalse("監視を止めたら、古い検知通知を残したままにしない");
    }

    [AvaloniaFact(DisplayName = "NotifyClipboardPatchDetectedで通知が立ち、クリック（AnalyzeClipboardPatchCommand）で消えて実際に解析される")]
    public async Task 検知通知をクリックすると解析される()
    {
        var projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectDirectory);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sample.txt"), "old\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDirectory).ConfigureAwait(true);
        shell.SetClipboardWatchActive(true);

        shell.HasClipboardPatchNotice.Should().BeFalse();
        shell.NotifyClipboardPatchDetected();
        shell.HasClipboardPatchNotice.Should().BeTrue("パッチ形式のテキストを検知したら通知を出す必要がある");

        // クリックするまではクリップボードを読み直さない（確認なしに解析・適用しない）。
        _clipboard.Text = BuildPatch("sample.txt", "old", "new");
        shell.Graft.Blocks.Should().BeEmpty("クリックする前にブロック一覧が変化してはならない");

        shell.AnalyzeClipboardPatchCommand.Execute(null);
        shell.HasClipboardPatchNotice.Should().BeFalse("クリックした瞬間に通知は消える");

        // PasteAndParseCommandはAsyncRelayCommand（クリップボード読み取りが非同期）のため、
        // 解析が終わってBlocksへ反映されるまでを待つ（ScenarioTests.ExecuteAsyncと同じ考え方）。
        await WaitUntilAsync(() => shell.Graft.Blocks.Count > 0).ConfigureAwait(true);

        shell.Graft.Blocks.Should().ContainSingle("クリックでコマンドバーの「解析」と同じ処理が実行され、パッチが読み込まれる必要がある");
    }

    [AvaloniaFact(DisplayName = "機能追加: 自動解析オンかつ未処理が無ければ、検知した瞬間に自動で解析され通知は出ない")]
    public async Task 自動解析オンかつ未処理無しなら自動で解析される()
    {
        var projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectDirectory);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sample.txt"), "old\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDirectory).ConfigureAwait(true);
        shell.SetClipboardWatchActive(true);

        _clipboard.Text = BuildPatch("sample.txt", "old", "new");

        var autoParsed = shell.HandleClipboardPatchDetected(autoParseEnabled: true);

        autoParsed.Should().BeTrue("自動解析の設定がオンで未処理の内容も無いため、その場で解析される必要がある");
        shell.HasClipboardPatchNotice.Should().BeFalse("自動解析した場合はクリック待ちの通知を出す必要が無い");

        await WaitUntilAsync(() => shell.Graft.Blocks.Count > 0).ConfigureAwait(true);
        shell.Graft.Blocks.Should().ContainSingle("検知した瞬間に解析まで済ませ、接ぎ木パネルへ結果が反映される必要がある");
    }

    [AvaloniaFact(DisplayName = "機能追加: 自動解析オフのときは、従来どおり通知のみで自動では解析されない")]
    public async Task 自動解析オフなら通知のみに留まる()
    {
        var projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectDirectory);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sample.txt"), "old\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDirectory).ConfigureAwait(true);
        shell.SetClipboardWatchActive(true);

        _clipboard.Text = BuildPatch("sample.txt", "old", "new");

        var autoParsed = shell.HandleClipboardPatchDetected(autoParseEnabled: false);

        autoParsed.Should().BeFalse("自動解析の設定がオフのため、検知しても自動では解析しない");
        shell.HasClipboardPatchNotice.Should().BeTrue("従来どおり通知だけを出す必要がある");
        shell.Graft.Blocks.Should().BeEmpty("クリックする前にブロック一覧が変化してはならない");
    }

    [AvaloniaFact(DisplayName = "機能追加: 自動解析オンでも、未処理の解析結果が残っていれば自動解析せず通知に留まる")]
    public async Task 自動解析オンでも未処理の解析結果があれば通知に留まる()
    {
        var projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectDirectory);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sample.txt"), "old\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "other.txt"), "foo\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDirectory).ConfigureAwait(true);
        shell.SetClipboardWatchActive(true);

        // 1件目のパッチを解析させ、「適用」も「破棄」もされていない未処理の状態を作る。
        _clipboard.Text = BuildPatch("sample.txt", "old", "new");
        shell.AnalyzeClipboardPatchCommand.Execute(null);
        await WaitUntilAsync(() => shell.Graft.Blocks.Count > 0).ConfigureAwait(true);
        shell.Graft.HasUnprocessedResult.Should().BeTrue("解析結果が接ぎ木パネルに残ったまま（未適用）のはず");

        // 2件目のパッチを検知させる。1件目を先頭から差し替えてはならない。
        _clipboard.Text = BuildPatch("other.txt", "foo", "bar");
        var autoParsed = shell.HandleClipboardPatchDetected(autoParseEnabled: true);

        autoParsed.Should().BeFalse("未処理の解析結果が残っている間は、自動解析で先頭から差し替えてはならない");
        shell.HasClipboardPatchNotice.Should().BeTrue("自動解析を見送った分、従来どおり通知を出す必要がある");

        // 2件目を解析するかどうかは利用者の判断に委ねるため、1件目の結果がそのまま残ることを確認する。
        await Task.Delay(200); // 誤って裏で解析が走っていないことを確認するための猶予。
        shell.Graft.Blocks.Should().ContainSingle(b => b.Plan.Path == "sample.txt",
            "未処理だった1件目の解析結果が、自動解析によって勝手に差し替わってはならない");
    }

    [AvaloniaFact(DisplayName = "機能追加: 自動解析オンでも、パッチキューに未適用のブロックが残っていれば自動解析せず通知に留まる")]
    public async Task 自動解析オンでもキューに未適用があれば通知に留まる()
    {
        var projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectDirectory);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "sample.txt"), "old\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "other.txt"), "foo\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDirectory).ConfigureAwait(true);
        shell.SetClipboardWatchActive(true);

        // 1件目のパッチを解析し、手動でキューへ追加する（4.10）。キューへ追加すると
        // DiscardCurrentPatchが呼ばれ_currentPatchはnullへ戻るため、
        // 「キューに残っている」ことだけを未処理条件として検証できる。
        _clipboard.Text = BuildPatch("sample.txt", "old", "new");
        shell.AnalyzeClipboardPatchCommand.Execute(null);
        await WaitUntilAsync(() => shell.Graft.Blocks.Count > 0).ConfigureAwait(true);
        shell.Graft.AddCurrentPatchToQueueCommand.Execute(null);
        await WaitUntilAsync(() => shell.Graft.PatchQueue.Items.Count > 0).ConfigureAwait(true);
        shell.Graft.HasUnprocessedResult.Should().BeTrue("キューに未適用のブロックが残っているはず");

        _clipboard.Text = BuildPatch("other.txt", "foo", "bar");
        var autoParsed = shell.HandleClipboardPatchDetected(autoParseEnabled: true);

        autoParsed.Should().BeFalse("キューに未適用のブロックが残っている間は自動解析しない");
        shell.HasClipboardPatchNotice.Should().BeTrue();
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, cts.Token).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// ScenarioTests.OpenShellAsyncと同じ理由（window.Show()経由の初期化と明示的な
    /// InitializeAsyncの二重実行による競合を避けるため）で、window.Show()後は
    /// ProjectPane.Stateの変化だけを待つ。
    /// </summary>
    private async Task<(ShellViewModel Shell, Avalonia.Controls.Window Window)> OpenShellAsync()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
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
            new AutoConfirmDialogService(),
            new FakeUiServices(_clipboard),
            openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (shell.Graft.ProjectPane.State == ProjectPaneState.Loading)
        {
            await Task.Delay(10, cts.Token).ConfigureAwait(true);
        }

        return (shell, window);
    }

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
