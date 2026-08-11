using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// B: 接ぎ木パネル（GraftPanel.axaml）のブロック右クリックメニューと、
/// C: 差分表示（DiffView.axaml）の右クリックメニューの回帰テスト。
/// 両方ともブロック一覧・diffの実データ（ドライラン結果）を必要とするため、ScenarioTests.csと
/// 同じ「クリップボードのパッチを解析する」フル通しのシナリオ設定を共有する。
/// </summary>
public class GraftPanelAndDiffContextMenuScenarioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-ctxmenu", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly ShownWindowTracker _windows = new();

    public GraftPanelAndDiffContextMenuScenarioTests()
    {
        _appDirectory = Path.Combine(_root, "app");
        _projectDirectory = Path.Combine(_root, "project");
        Directory.CreateDirectory(_appDirectory);
        Directory.CreateDirectory(_projectDirectory);
    }

    public void Dispose()
    {
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

    // ===================== B: GraftPanel =====================

    [AvaloniaFact(DisplayName = "ブロック行の右クリックメニューに4項目が並び、全てHelpTip.Standardを持つ")]
    public async Task ブロック行の右クリックメニューに4項目並ぶ()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs(); // ブロック一覧の行（コンテナ）を実体化させる。

        var blockRow = FindBlockContextMenu(window, shell.Graft.Blocks[0]);
        var headers = blockRow.ContextMenu!.GetLogicalDescendants().OfType<MenuItem>()
            .Select(m => m.Header?.ToString()).ToList();

        headers.Should().Contain("対象ファイルを開く");
        headers.Should().Contain("このブロックの差分をコピー");
        headers.Should().Contain("修正依頼プロンプトをコピー");
        headers.Should().Contain("チェックを外す (Space)", "適用可のブロックは既定でチェック済みのため「外す」表記になる");

        var missing = blockRow.ContextMenu!.GetLogicalDescendants().OfType<MenuItem>()
            .Where(m => HelpTip.GetStandard(m) is null)
            .Select(m => m.Header?.ToString() ?? "(名前無し)").ToList();
        missing.Should().BeEmpty($"次の項目にHelpTip.Standardが付いていません: {string.Join(", ", missing)}");
    }

    [AvaloniaFact(DisplayName = "「このブロックの差分をコピー」はunified diff形式でクリップボードへコピーし、UnifiedDiffAdapterで解析できる")]
    public async Task ブロックの差分をunified_diffでコピーできる()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        var block = shell.Graft.Blocks[0];
        shell.CopyBlockDiffCommand.CanExecute(block).Should().BeTrue();
        shell.CopyBlockDiffCommand.Execute(block);

        _clipboard.Text.Should().Contain("sample.txt");
        _clipboard.Text.Should().Contain("-2行目").And.Contain("+2行目（変更後）");

        var parsed = UnifiedDiffAdapter.Parse(_clipboard.Text!);
        parsed.IsSuccess.Should().BeTrue("既存のunified diff取り込みで解析できる形式である必要がある");
    }

    [AvaloniaFact(DisplayName = "「修正依頼プロンプトをコピー」は失敗ブロックのときだけ有効（成功ブロックでは無効）")]
    public async Task 修正依頼プロンプトは失敗ブロックのときだけ有効()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        // 1件は成功、1件は失敗（存在しない行）するパッチを解析する。
        _clipboard.Text = """
            <<<< FILE: sample.txt
            summary: テスト
            <<<<<<< SEARCH
            2行目
            =======
            2行目（変更後）
            >>>>>>> REPLACE
            <<<<<<< SEARCH
            存在しない行
            =======
            置換後
            >>>>>>> REPLACE
            >>>> END

            """;
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs(); // ブロック一覧の行（コンテナ）を実体化させる。

        shell.Graft.Blocks.Should().HaveCount(2);
        var okBlock = shell.Graft.Blocks.Single(b => b.IsOk);
        var errorBlock = shell.Graft.Blocks.Single(b => b.IsError);

        var okRow = FindBlockContextMenu(window, okBlock);
        var okMenuItem = okRow.ContextMenu!.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => Equals(m.Header?.ToString(), "修正依頼プロンプトをコピー"));
        okMenuItem.IsEnabled.Should().BeFalse("成功ブロックの行では無効化されている必要がある");

        var errorRow = FindBlockContextMenu(window, errorBlock);
        var errorMenuItem = errorRow.ContextMenu!.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => Equals(m.Header?.ToString(), "修正依頼プロンプトをコピー"));
        errorMenuItem.IsEnabled.Should().BeTrue("失敗ブロックの行では有効になっている必要がある");
        errorMenuItem.Command.Should().BeSameAs(shell.Graft.CopyRecoveryPromptCommand,
            "既存のCopyRecoveryPromptCommandを再利用する必要がある");
    }

    [AvaloniaFact(DisplayName = "「対象ファイルを開く」を実行すると対象ファイルがエディタで開く")]
    public async Task 対象ファイルを開くでエディタに開く()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        var block = shell.Graft.Blocks[0];
        shell.OpenBlockInEditorCommand.CanExecute(block).Should().BeTrue();
        shell.OpenBlockInEditorCommand.Execute(block);

        // OpenBlockInEditorはasync void（fire-and-forget）のため、完了するまでポンプしながら待つ。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!shell.Editor.Tabs.Any(t => t.Kind == EditorTabKind.Document && t.Session.FullPath == targetPath))
        {
            Dispatcher.UIThread.RunJobs();
            if (cts.IsCancellationRequested) break;
            await Task.Delay(10).ConfigureAwait(true);
        }

        shell.Editor.Tabs.Should().Contain(t => t.Kind == EditorTabKind.Document
            && t.Session.FullPath == targetPath);
    }

    [AvaloniaFact(DisplayName = "「対象ファイルを開く」はフォルダ作成ブロック（Mkdir）では無効化される")]
    public async Task フォルダ作成ブロックでは対象ファイルを開くが無効()
    {
        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = """
            <<<< MKDIR: newdir

            """;
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        shell.Graft.Blocks.Should().ContainSingle();
        var block = shell.Graft.Blocks[0];
        block.Plan.Operation.Should().Be(EntryOperation.Mkdir);
        shell.OpenBlockInEditorCommand.CanExecute(block).Should().BeFalse("フォルダは「開く」対象になり得ない");
    }

    [AvaloniaFact(DisplayName = "「チェックを付ける／外す」はブロックのIsSelectedを切り替え、失敗ブロックには効かない")]
    public async Task チェックの切り替えができる()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);

        var block = shell.Graft.Blocks[0];
        block.IsSelected.Should().BeTrue("適用可のブロックは既定でチェック済み");

        shell.ToggleBlockCheckCommand.CanExecute(block).Should().BeTrue();
        shell.ToggleBlockCheckCommand.Execute(block);
        block.IsSelected.Should().BeFalse();

        shell.ToggleBlockCheckCommand.Execute(block);
        block.IsSelected.Should().BeTrue();

        // 失敗ブロックには効かない。
        _clipboard.Text = BuildPatch("sample.txt", "存在しない行", "置換後");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        var failedBlock = shell.Graft.Blocks[0];
        failedBlock.IsError.Should().BeTrue();
        shell.ToggleBlockCheckCommand.CanExecute(failedBlock).Should().BeFalse();
    }

    // ===================== C: DiffView =====================

    [AvaloniaFact(DisplayName = "差分表示の右クリックメニューに3項目並び、全てHelpTip.Standardを持つ")]
    public void 差分表示の右クリックメニューに3項目並ぶ()
    {
        // 実際の適用パイプラインを経由しない、独立したDiffViewModelで検証する
        // （ShellWindowのEditorPaneが既にshell.Graft.Diffを表示している状態と同じインスタンスを
        // 別のWindowへ二重にアタッチすると、描画が終わらなくなる問題が実機テストで判明したため
        // 避ける。中身のクリップボード検証は「差分の各コピーが正しい内容になる」で別途行う）。
        var diffVm = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        diffVm.Load(MakePlan("sample.txt", "1行目\n2行目\n3行目\n", "1行目\n2行目（変更後）\n3行目\n"));

        var view = new DiffView { DataContext = diffVm };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();

            var diffListBox = view.GetVisualDescendants().OfType<ListBox>().Single(lb => lb.ContextMenu is not null);
            var headers = diffListBox.ContextMenu!.GetLogicalDescendants().OfType<MenuItem>()
                .Select(m => m.Header?.ToString()).ToList();
            headers.Should().Contain("変更前をコピー");
            headers.Should().Contain("変更後をコピー");
            headers.Should().Contain("この差分をunified diff形式でコピー");

            var missing = diffListBox.ContextMenu!.GetLogicalDescendants().OfType<MenuItem>()
                .Where(m => HelpTip.GetStandard(m) is null)
                .Select(m => m.Header?.ToString() ?? "(名前無し)").ToList();
            missing.Should().BeEmpty($"次の項目にHelpTip.Standardが付いていません: {string.Join(", ", missing)}");
        }
        finally
        {
            window.Close();
        }
    }

    private static BlockPlan MakePlan(string path, string? before, string? after) => new()
    {
        Block = new DeleteBlock { Path = path },
        Path = path,
        Operation = EntryOperation.Modify,
        CanApply = true,
        IsSelected = true,
        BeforeText = before,
        AfterText = after,
    };

    [AvaloniaFact(DisplayName = "変更前・変更後・unified diffのコピーが正しい内容でクリップボードへ入り、unified diffはUnifiedDiffAdapterで解析できる")]
    public async Task 差分の各コピーが正しい内容になる()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n3行目\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        _clipboard.Text = BuildPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.SelectedBlock = shell.Graft.Blocks[0];

        var diff = shell.Graft.Diff;
        diff.CopyBeforeCommand.CanExecute(null).Should().BeTrue();
        diff.CopyBeforeCommand.Execute(null);
        _clipboard.Text.Should().Contain("1行目").And.Contain("2行目").And.Contain("3行目")
            .And.NotContain("変更後", "変更前のコピーには変更後の内容が含まれてはならない");

        diff.CopyAfterCommand.CanExecute(null).Should().BeTrue();
        diff.CopyAfterCommand.Execute(null);
        _clipboard.Text.Should().Contain("1行目").And.Contain("2行目（変更後）").And.Contain("3行目");

        diff.CopyUnifiedDiffCommand.CanExecute(null).Should().BeTrue();
        diff.CopyUnifiedDiffCommand.Execute(null);
        _clipboard.Text.Should().Contain("sample.txt").And.Contain("-2行目").And.Contain("+2行目（変更後）");

        var parsed = UnifiedDiffAdapter.Parse(_clipboard.Text!);
        parsed.IsSuccess.Should().BeTrue("差分表示から出力したunified diffは既存の取り込み側で解析できる必要がある");
    }

    [AvaloniaFact(DisplayName = "新規作成ブロックでは「変更前をコピー」が無効化される")]
    public async Task 新規作成では変更前コピーが無効()
    {
        var (shell, _) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = "<<<< FILE: new.txt MODE=FULL\nはじめまして\n>>>> END\n";
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.Blocks.Should().ContainSingle();
        shell.Graft.SelectedBlock = shell.Graft.Blocks[0];

        var diff = shell.Graft.Diff;
        diff.FilePath.Should().Be("new.txt");
        diff.CopyBeforeCommand.CanExecute(null).Should().BeFalse("変更前が無い新規作成では無効化される必要がある");
        diff.CopyAfterCommand.CanExecute(null).Should().BeTrue();
        diff.CopyUnifiedDiffCommand.CanExecute(null).Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // ヘルパ（ScenarioTests.csと同じ構成）
    // ------------------------------------------------------------------

    /// <summary>
    /// ブロック行（GraftPanel.axamlのGrid.ContextMenu）を、対応するBlockItemViewModelから探す。
    /// ContextMenu内部のバインディング（Header・IsEnabled等）はPopupを開いた時点で初めて
    /// DataContextが伝播するため（EditorSelectionPromptTests.OpenContextMenuAndFindPromptItemと
    /// 同じ理由）、ContextRequestedイベントを実際に発火させてから返す。
    /// </summary>
    private static Grid FindBlockContextMenu(Window window, BlockItemViewModel block)
    {
        var grid = window.GetVisualDescendants().OfType<Grid>()
            .Single(g => ReferenceEquals(g.DataContext, block) && g.ContextMenu is not null);
        grid.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        Dispatcher.UIThread.RunJobs();
        return grid;
    }


    private async Task<(ShellViewModel Shell, Window Window)> OpenShellAsync()
    {
        var appPaths = new AppPaths(_appDirectory);
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
                await Task.Delay(10, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException(
                "ShellWindow.OnLoaded経由の初期化が30秒以内に完了しませんでした（ProjectPane.StateがLoadingのまま）。", ex);
        }
    }

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

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; set; }

        public void SetText(string text) => Text = text;

        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

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
