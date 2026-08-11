using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
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
/// D: タブ見出し右クリックメニュー「右側のタブを閉じる」の回帰テスト。
/// 既存の「他のタブを閉じる」「すべてのタブを閉じる」と同じ作法（保存確認あり）で、
/// Tabsの表示順で対象タブより右側だけを閉じることを確認する。
/// </summary>
public class CloseTabsToTheRightTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-close-right", Guid.NewGuid().ToString("N"));
    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly ShownWindowTracker _windows = new();

    public CloseTabsToTheRightTests()
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

    [AvaloniaFact(DisplayName = "右側のタブを閉じるで対象より右側だけが閉じ、対象自身と左側は残る")]
    public async Task 右側のタブだけが閉じる()
    {
        var pathA = Path.Combine(_projectDirectory, "a.txt");
        var pathB = Path.Combine(_projectDirectory, "b.txt");
        var pathC = Path.Combine(_projectDirectory, "c.txt");
        var pathD = Path.Combine(_projectDirectory, "d.txt");
        await File.WriteAllTextAsync(pathA, "A\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathB, "B\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathC, "C\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathD, "D\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellWithWindowAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var tabA = (await shell.Editor.OpenFileAsync(pathA).ConfigureAwait(true)).Value;
        var tabB = (await shell.Editor.OpenFileAsync(pathB).ConfigureAwait(true)).Value;
        var tabC = (await shell.Editor.OpenFileAsync(pathC).ConfigureAwait(true)).Value;
        var tabD = (await shell.Editor.OpenFileAsync(pathD).ConfigureAwait(true)).Value;
        shell.Editor.Tabs.Should().Equal(tabA, tabB, tabC, tabD);

        var closed = await shell.Editor.CloseTabsToTheRightAsync(tabB).ConfigureAwait(true);

        closed.Should().BeTrue();
        shell.Editor.Tabs.Should().Equal(new[] { tabA, tabB }, "B自身と左側（A）は残り、右側（C・D）だけが閉じる必要がある");
    }

    [AvaloniaFact(DisplayName = "最も右側のタブでは無効化される（閉じる対象が無いため）")]
    public async Task 最右タブでは無効化される()
    {
        var pathA = Path.Combine(_projectDirectory, "a.txt");
        var pathB = Path.Combine(_projectDirectory, "b.txt");
        await File.WriteAllTextAsync(pathA, "A\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathB, "B\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellWithWindowAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var tabA = (await shell.Editor.OpenFileAsync(pathA).ConfigureAwait(true)).Value;
        var tabB = (await shell.Editor.OpenFileAsync(pathB).ConfigureAwait(true)).Value;

        shell.Editor.CloseTabsToTheRightCommand.CanExecute(tabA).Should().BeTrue("Aの右側にBがあるため実行できる");
        shell.Editor.CloseTabsToTheRightCommand.CanExecute(tabB).Should().BeFalse("最右のタブには閉じる右側が無い");
    }

    [AvaloniaFact(DisplayName = "右側のタブに未保存の変更があり保存確認でキャンセルすると中断する")]
    public async Task 保存確認でキャンセルされると中断する()
    {
        var pathA = Path.Combine(_projectDirectory, "a.txt");
        var pathB = Path.Combine(_projectDirectory, "b.txt");
        await File.WriteAllTextAsync(pathA, "A\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathB, "B\n").ConfigureAwait(true);

        var (shell, _) = await OpenShellWithWindowAsync(new CancelSaveDialogService()).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var tabA = (await shell.Editor.OpenFileAsync(pathA).ConfigureAwait(true)).Value;
        var tabB = (await shell.Editor.OpenFileAsync(pathB).ConfigureAwait(true)).Value;
        tabB.Session!.Document.Insert(0, "編集");

        var closed = await shell.Editor.CloseTabsToTheRightAsync(tabA).ConfigureAwait(true);

        closed.Should().BeFalse("保存確認でキャンセルされたら中断する必要がある");
        shell.Editor.Tabs.Should().Contain(tabB, "キャンセルされたタブは閉じずに残す必要がある");
    }

    [AvaloniaFact(DisplayName = "タブ見出しの右クリックメニューに「右側のタブを閉じる」が並び、HelpTip.Standardを持つ")]
    public async Task 右クリックメニューに項目が並びHelpTipを持つ()
    {
        var pathA = Path.Combine(_projectDirectory, "a.txt");
        var pathB = Path.Combine(_projectDirectory, "b.txt");
        await File.WriteAllTextAsync(pathA, "A\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathB, "B\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellWithWindowAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        var tabA = (await shell.Editor.OpenFileAsync(pathA).ConfigureAwait(true)).Value;
        await shell.Editor.OpenFileAsync(pathB).ConfigureAwait(true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 機能改善（タブが増えたときに到達できない問題）: タブ見出しのDataTemplateルートは
        // タブ幅の自動縮小（Title=*列で省略記号表示）のためBorderからGridへ変更した
        // （EditorPane.axaml参照）。ContextMenuの取り付け先もそれに合わせてGridへ移った。
        var tabGrid = window.GetVisualDescendants().OfType<Grid>()
            .Single(g => ReferenceEquals(g.DataContext, tabA) && g.ContextMenu is not null);
        var menuItem = tabGrid.ContextMenu!.GetLogicalDescendants().OfType<MenuItem>()
            .Single(m => Equals(m.Header?.ToString(), "右側のタブを閉じる"));

        menuItem.Command.Should().BeSameAs(shell.Editor.CloseTabsToTheRightCommand);
        HelpTip.GetStandard(menuItem).Should().NotBeNull("追加したメニュー項目にはHelpTip.Standardが必要");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private async Task<(ShellViewModel Shell, ShellWindow Window)> OpenShellWithWindowAsync(IDialogService? dialogs = null)
    {
        var appPaths = new AppPaths(_appDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(new Settings { ShowPreview = false }).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths, new Settings { ShowPreview = false }, settingsStore, new PatchQueue(appPaths),
            new ProjectStore(appPaths), new RevisionStore(appPaths), new RevisionRestorer(appPaths),
            dialogs ?? new AutoConfirmDialogService(), new AvaloniaUiServices(), openSettings: () => { });

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
                "初期化が30秒以内に完了しませんでした（ProjectPane.StateがLoadingのまま）。", ex);
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
}
