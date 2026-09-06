using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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

namespace Graft.UiTests;

/// <summary>
/// 不具合4（実機点検）の回帰テスト: 適用完了後、差分タブが閉じてタブが1つも無くなっても
/// 「並列／統合／折り返し／空白を表示／すべて展開」の差分ツールバーと「×」ボタンだけが
/// 「開いているファイルはありません」の上に残り続けていた不具合。
///
/// 原因: <see cref="EditorPaneViewModel.ActiveTab"/>が変化しない限り
/// <c>EditorPane.ApplyActiveTab</c>（延いては<c>ApplyEmptyTab</c>によるDiffHost.IsVisible=false）
/// が呼ばれないため、DiffHostのIsVisibleが「最後にdiffタブを表示していたときの値
/// （true）」のまま取り残される経路が無いかを確認する。
/// </summary>
public class DiffToolbarCleanupTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-diff-toolbar-cleanup", Guid.NewGuid().ToString("N"));
    private readonly string _appDirectory;
    private readonly string _projectDirectory;
    private readonly FakeClipboard _clipboard = new();
    private readonly ShownWindowTracker _windows = new();

    public DiffToolbarCleanupTests()
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

    [AvaloniaFact(DisplayName = "不具合4回帰: 適用完了で差分タブが閉じたら、差分ツールバー(DiffHost)も非表示に戻る")]
    public async Task 適用完了後に差分ツールバーが残らない()
    {
        var targetPath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(targetPath, "1行目\n2行目\n").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);

        _clipboard.Text = BuildSrPatch("sample.txt", "2行目", "2行目（変更後）");
        await ExecuteAsync(shell.Graft.PasteAndParseCommand).ConfigureAwait(true);
        shell.Graft.SelectedBlock = shell.Graft.Blocks[0];
        shell.Editor.ActiveTab!.Kind.Should().Be(EditorTabKind.Diff);
        window.CaptureRenderedFrame().Should().NotBeNull();

        var diffHost = window.GetVisualDescendants().OfType<DiffView>().Single();
        diffHost.IsVisible.Should().BeTrue("差分タブを表示している間はDiffHostが見えているはず");

        await ExecuteAsync(shell.Graft.ApplyCommand).ConfigureAwait(true);
        window.CaptureRenderedFrame().Should().NotBeNull();

        shell.Editor.Tabs.Should().BeEmpty("適用後は差分タブも自動的に閉じるはず");
        shell.Editor.ActiveTab.Should().BeNull();
        diffHost.IsVisible.Should().BeFalse(
            "タブが1つも無いのに差分ツールバー(並列/統合/折り返し等)だけが残ってはならない");
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

    private static string BuildSrPatch(string relativePath, string search, string replace)
        => "<<<< PATCH\nsummary: test\ntype: fix\n>>>>\n\n<<<< FILE: " + relativePath +
           "\n<<<<<<< SEARCH\n" + search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n>>>> END\n\n";

    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting) await Task.Delay(10).ConfigureAwait(true);
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
