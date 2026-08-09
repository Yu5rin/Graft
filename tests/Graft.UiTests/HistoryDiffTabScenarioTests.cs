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
/// 修正1・修正3: 履歴のリビジョン選択と接ぎ木パネルの分離、履歴差分タブの使い回し、
/// 「破棄」ボタンの通しシナリオテスト。HistoryRestoreThroughScenarioTests.csと同じ手法
/// （ProjectPane登録 → クリップボード貼り付け → 解析 → 適用）でShellViewModelを組み立てる。
/// </summary>
public class HistoryDiffTabScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-history-diff-tab-scenario", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly RecordingDialogService _dialogs = new();

    public HistoryDiffTabScenarioTests()
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

    [AvaloniaFact(DisplayName = "修正1: 履歴のリビジョンを選択しても接ぎ木パネル（ブロック一覧・選択ブロック・diff）は一切変化しない")]
    public async Task 履歴選択で接ぎ木パネルが変化しない()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "a.txt", "a1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "a.txt", "a2").ConfigureAwait(true); // r2

        // 接ぎ木パネルに「これから適用するもの」を用意する（まだ適用しない）。
        await ParseFullAsync(shell, "b.txt", "解析中の内容").ConfigureAwait(true);
        shell.Graft.Blocks.Should().NotBeEmpty("解析直後は接ぎ木パネルにブロックがあるはず");
        var blocksBefore = shell.Graft.Blocks.ToList();
        var stateBefore = shell.Graft.State;
        var selectedBefore = shell.Graft.SelectedBlock;
        var diffPathBefore = shell.Graft.Diff.FilePath;

        await SelectHistoryRevisionAsync(shell, "r1").ConfigureAwait(true);

        shell.Graft.Blocks.Should().Equal(blocksBefore, "履歴を選んでも接ぎ木パネルのブロック一覧は変わらないはず");
        shell.Graft.State.Should().Be(stateBefore, "履歴を選んでも中央ペインの状態は変わらないはず");
        ReferenceEquals(shell.Graft.SelectedBlock, selectedBefore).Should().BeTrue("選択中ブロックも変わらないはず");
        shell.Graft.Diff.FilePath.Should().Be(diffPathBefore, "接ぎ木パネル用のDiffは履歴と無関係に現在の解析結果のままのはず");

        // 履歴側は履歴側で専用の表示（HistoryDiff）へ正しく反映されていること。
        shell.Graft.HistoryDiff.Files.Should().ContainSingle(f => f.PathText == "a.txt");
    }

    [AvaloniaFact(DisplayName = "修正1: 履歴のリビジョン選択を切り替えても差分タブは1枚のまま中身だけ差し替わる")]
    public async Task 差分タブは1枚のまま使い回される()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "a.txt", "a1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "a.txt", "a2").ConfigureAwait(true); // r2
        await ApplyFullAsync(shell, "a.txt", "a3").ConfigureAwait(true); // r3

        await SelectHistoryRevisionAsync(shell, "r1").ConfigureAwait(true);
        shell.Editor.Tabs.Should().ContainSingle(t => t.IsHistoryDiffTab);
        var tab = shell.Editor.Tabs.Single(t => t.IsHistoryDiffTab);
        tab.Title.Should().Be("差分: r1");

        await SelectHistoryRevisionAsync(shell, "r2").ConfigureAwait(true);
        shell.Editor.Tabs.Should().ContainSingle(t => t.IsHistoryDiffTab, "タブが増えてはならない");
        ReferenceEquals(shell.Editor.Tabs.Single(t => t.IsHistoryDiffTab), tab).Should().BeTrue("同じタブインスタンスが使い回されるはず");
        tab.Title.Should().Be("差分: r2", "タブ見出しが選択中のリビジョンへ追従するはず");

        await SelectHistoryRevisionAsync(shell, "r3").ConfigureAwait(true);
        shell.Editor.Tabs.Should().ContainSingle(t => t.IsHistoryDiffTab);
        tab.Title.Should().Be("差分: r3");
    }

    [AvaloniaFact(DisplayName = "修正1: 複数ファイルを変更したリビジョンでは、両方のファイルの差分がHistoryDiffに含まれる")]
    public async Task 複数ファイルのリビジョンで全ファイルの差分が確認できる()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ApplyFullAsync(shell, "a.txt", "a1").ConfigureAwait(true); // r1
        // r2: 2ファイルを同時に変更する（RestoreThroughTests.BuildFullPatchの連結と同じ書式）。
        _clipboard.Text = BuildFullPatch("a.txt", "a2") + "\n" + BuildFullPatch("b.txt", "b1");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);

        await SelectHistoryRevisionAsync(shell, "r2").ConfigureAwait(true);

        shell.Graft.HistoryDiff.Files.Select(f => f.PathText).Should().BeEquivalentTo(new[] { "a.txt", "b.txt" },
            "1リビジョンが変更した全ファイルの差分が確認できるはず");
    }

    [AvaloniaFact(DisplayName = "修正1: 履歴差分タブを×で閉じると履歴側の選択も解除される")]
    public async Task タブを閉じると履歴の選択も解除される()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await ApplyFullAsync(shell, "a.txt", "a1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "a.txt", "a2").ConfigureAwait(true); // r2

        await SelectHistoryRevisionAsync(shell, "r1").ConfigureAwait(true);
        var tab = shell.Editor.Tabs.Single(t => t.IsHistoryDiffTab);

        var closed = await shell.Editor.CloseTabAsync(tab).ConfigureAwait(true);

        closed.Should().BeTrue();
        shell.Editor.Tabs.Should().NotContain(t => t.IsHistoryDiffTab, "タブは閉じられているはず");
        shell.Graft.History.SelectedItem.Should().BeNull("タブを閉じたら履歴側の選択も解除されるはず");
    }

    [AvaloniaFact(DisplayName = "修正1: 履歴の選択を解除するとタブも閉じる")]
    public async Task 選択解除でタブが閉じる()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await ApplyFullAsync(shell, "a.txt", "a1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "a.txt", "a2").ConfigureAwait(true); // r2

        await SelectHistoryRevisionAsync(shell, "r1").ConfigureAwait(true);
        shell.Editor.Tabs.Should().Contain(t => t.IsHistoryDiffTab);

        await SelectHistoryRevisionAsync(shell, null).ConfigureAwait(true);

        shell.Editor.Tabs.Should().NotContain(t => t.IsHistoryDiffTab, "選択解除でタブも閉じるはず");
    }

    [AvaloniaFact(DisplayName = "修正3: 接ぎ木パネルの「破棄」でパネルが空に戻り、ファイルは変更されない")]
    public async Task 破棄ボタンでパネルが空に戻る()
    {
        var target = Path.Combine(_projectDirectory, "c.txt");
        await File.WriteAllTextAsync(target, "元の内容\n").ConfigureAwait(true);

        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        await ParseFullAsync(shell, "c.txt", "解析だけした内容").ConfigureAwait(true);
        shell.Graft.Blocks.Should().NotBeEmpty();
        shell.Graft.DiscardCommand.CanExecute(null).Should().BeTrue("解析結果があるので破棄できるはず");

        shell.Graft.DiscardCommand.Execute(null);

        shell.Graft.Blocks.Should().BeEmpty("破棄後は接ぎ木パネルが空に戻るはず");
        shell.Graft.State.Should().Be(CenterPaneState.Empty);
        shell.Graft.DiscardCommand.CanExecute(null).Should().BeFalse("解析結果が無いときは破棄も無効のはず");

        (await File.ReadAllTextAsync(target).ConfigureAwait(true)).Should().Be("元の内容\n", "破棄してもファイルには一切書き込まれないはず");
    }

    [AvaloniaFact(DisplayName = "修正1: 履歴閲覧中でも、新しいパッチを解析すると接ぎ木パネルに正しく解析結果が出る（衝突しない）")]
    public async Task 履歴閲覧中でも新しい解析は接ぎ木パネルへ正しく反映される()
    {
        var shell = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await ApplyFullAsync(shell, "a.txt", "a1").ConfigureAwait(true); // r1
        await ApplyFullAsync(shell, "a.txt", "a2").ConfigureAwait(true); // r2

        await SelectHistoryRevisionAsync(shell, "r1").ConfigureAwait(true);
        shell.Graft.Blocks.Should().BeEmpty("履歴を見ているだけの間は接ぎ木パネルは空のはず（直前の適用でDiscard済み）");

        await ParseFullAsync(shell, "b.txt", "新しい解析結果").ConfigureAwait(true);

        shell.Graft.Blocks.Should().ContainSingle(b => b.PathText == "b.txt", "履歴閲覧中でも新しい解析結果が接ぎ木パネルへ正しく反映されるはず");
        shell.Graft.State.Should().Be(CenterPaneState.Content);
        // 履歴側の表示は解析によって巻き込まれず、r1のままのはず。
        shell.Graft.HistoryDiff.RevisionLabel.Should().Be("r1");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private async Task<ShellViewModel> OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        var settings = new Settings { ShowPreview = false };
        await new SettingsStore(appPaths).SaveAsync(settings).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            settings,
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            _dialogs,
            new FakeUiServices(_clipboard),
            openSettings: () => { });

        await shell.Graft.InitializeAsync().ConfigureAwait(true);
        return shell;
    }

    /// <summary>FULL形式のパッチを貼り付け→解析→適用まで実際に通す（HistoryRestoreThroughScenarioTestsと同じ）。</summary>
    private async Task ApplyFullAsync(ShellViewModel shell, string relativePath, string content)
    {
        await ParseFullAsync(shell, relativePath, content).ConfigureAwait(true);
        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
    }

    /// <summary>FULL形式のパッチを貼り付け→解析のみ行う（まだ適用しない。接ぎ木パネルの状態を作るため）。</summary>
    private async Task ParseFullAsync(ShellViewModel shell, string relativePath, string content)
    {
        _clipboard.Text = BuildFullPatch(relativePath, content);
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
    }

    /// <summary>FULL形式でファイル全体を書き換えるパッチ本文を組み立てる（RestoreThroughTestsと同じ書式）。</summary>
    private static string BuildFullPatch(string relativePath, string content)
        => $"<<<< FILE: {relativePath} MODE=FULL\n{content}\n>>>> END\n";

    /// <summary>
    /// 履歴一覧から指定ラベルの行を選択する（nullは選択解除）。OnRevisionSelectedは非同期
    /// （History.BuildDiffPlansAsyncを待つ）ため、MainViewModel.HistoryDiffChangedが発火するまで
    /// 待ってから返す。
    /// </summary>
    private static async Task SelectHistoryRevisionAsync(ShellViewModel shell, string? revisionLabel)
    {
        var tcs = new TaskCompletionSource();
        void OnChanged(object? s, EventArgs e) => tcs.TrySetResult();
        shell.Graft.HistoryDiffChanged += OnChanged;
        try
        {
            shell.Graft.History.SelectedItem = revisionLabel is null
                ? null
                : shell.Graft.History.Items.Single(i => i.RevisionLabel == revisionLabel);
            await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true);
        }
        finally
        {
            shell.Graft.HistoryDiffChanged -= OnChanged;
        }
    }

    /// <summary>非同期コマンドを実行し、完了するまで待つ（HistoryRestoreThroughScenarioTestsと同じ役割）。</summary>
    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting)
            {
                await Task.Delay(10).ConfigureAwait(true);
            }
        }
    }

    /// <summary>確認をすべて承諾するダイアログサービス（HistoryRestoreThroughScenarioTestsと同じ役割）。</summary>
    private sealed class RecordingDialogService : IDialogService
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
