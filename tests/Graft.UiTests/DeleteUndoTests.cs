using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// 課題2: ごみ箱削除のアプリ内復元（Ctrl+Z）のキー配線・アプリ終了時の掃除を、実際の
/// ShellWindow経由のキー操作で検証する。退避→復元の往復自体（ファイル・フォルダ・複数件
/// スタック・同名衝突）は<see cref="DeleteUndoStore"/>単体でGraft.Tests側（高速・確実）が
/// 押さえているため、ここではキールーティングとステータスバー通知・終了時の掃除に絞る。
///
/// エディタ本体（AvaloniaEdit）へのheadlessでのキー入力ルーティングは、他のテスト
/// （ShortcutsWindowTests・EditorTests参照）と同じ理由で素直に届かないため、「テキスト入力欄に
/// フォーカスがある」ことは標準のTextBox（クイックオープンの検索欄）で代表させる。
/// ShellWindow.Keyboard.csのinTextInput判定はTextBox/TextPresenter/TextAreaのいずれでも
/// 同じ分岐（早期return）を通るため、これでエディタのCtrl+Zと同じ非干渉を担保できる。
/// </summary>
public class DeleteUndoTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-delete-undo", Guid.NewGuid().ToString("N"));

    private readonly string _appDirectory;
    private readonly string _projectDirectory;

    public DeleteUndoTests()
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

    [AvaloniaFact(DisplayName = "削除するとステータスバーに案内が出て、エクスプローラにフォーカスがある状態のCtrl+Zで元の場所へ戻る（内容も一致）")]
    public async Task エクスプローラフォーカス中のCtrlZで削除が元に戻る()
    {
        var filePath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(filePath, "取り消しの確認用の内容\n2行目").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count > 0).ConfigureAwait(true);

        // エクスプローラのペインを表示し、ツリーへフォーカスを移した状態で削除する
        // （実際の操作と同じ順序。削除直後はツリーの再構築でフォーカスを失うため、
        // ExplorerView.axaml.csが取り消し通知と同時にフォーカスを戻す仕組みも合わせて検証する）。
        shell.SelectSideView(SideViewKind.Explorer);
        var treeView = window.GetControl<ExplorerView>("ExplorerViewControl").GetControl<TreeView>("FileTreeView");
        treeView.Focus();

        var node = shell.Explorer.RootNodes.Single(n => n.Name == "sample.txt");
        shell.Explorer.SelectedNode = node;
        shell.Explorer.DeleteCommand.Execute(node);
        await WaitForAsync(() => !File.Exists(filePath)).ConfigureAwait(true);

        shell.Explorer.HasDeleteUndoNotice.Should().BeTrue("削除直後はステータスバーへ「Ctrl+Zで元に戻せます」の案内を出す必要がある");
        shell.Explorer.DeleteUndoNoticeText.Should().Contain("sample.txt").And.Contain("Ctrl+Z");

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await WaitForAsync(() => File.Exists(filePath)).ConfigureAwait(true);

        File.Exists(filePath).Should().BeTrue("Ctrl+Zで元の場所へ復元される必要がある");
        (await File.ReadAllTextAsync(filePath).ConfigureAwait(true)).Should().Be("取り消しの確認用の内容\n2行目", "内容も完全に一致している必要がある");
    }

    [AvaloniaFact(DisplayName = "フォルダ（複数ファイル入り）を削除した場合も、Ctrl+Zで丸ごと元に戻る")]
    public async Task フォルダ削除もCtrlZで丸ごと戻る()
    {
        var folderPath = Path.Combine(_projectDirectory, "folder");
        Directory.CreateDirectory(folderPath);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "a.txt"), "A").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "b.txt"), "B").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count > 0).ConfigureAwait(true);

        shell.SelectSideView(SideViewKind.Explorer);
        window.GetControl<ExplorerView>("ExplorerViewControl").GetControl<TreeView>("FileTreeView").Focus();

        var node = shell.Explorer.RootNodes.Single(n => n.Name == "folder");
        shell.Explorer.DeleteCommand.Execute(node);
        await WaitForAsync(() => !Directory.Exists(folderPath)).ConfigureAwait(true);

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await WaitForAsync(() => File.Exists(Path.Combine(folderPath, "a.txt")) && File.Exists(Path.Combine(folderPath, "b.txt"))).ConfigureAwait(true);

        Directory.Exists(folderPath).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(folderPath, "a.txt")).ConfigureAwait(true)).Should().Be("A");
        (await File.ReadAllTextAsync(Path.Combine(folderPath, "b.txt")).ConfigureAwait(true)).Should().Be("B");
    }

    [AvaloniaFact(DisplayName = "連続で2件削除すると、エクスプローラフォーカス中のCtrl+Zを2回押すことで新しい順に戻る")]
    public async Task 連続削除はCtrlZ2回で新しい順に戻る()
    {
        var first = Path.Combine(_projectDirectory, "first.txt");
        var second = Path.Combine(_projectDirectory, "second.txt");
        await File.WriteAllTextAsync(first, "1件目").ConfigureAwait(true);
        await File.WriteAllTextAsync(second, "2件目").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count >= 2).ConfigureAwait(true);

        shell.SelectSideView(SideViewKind.Explorer);
        window.GetControl<ExplorerView>("ExplorerViewControl").GetControl<TreeView>("FileTreeView").Focus();

        shell.Explorer.DeleteCommand.Execute(shell.Explorer.RootNodes.Single(n => n.Name == "first.txt"));
        await WaitForAsync(() => !File.Exists(first)).ConfigureAwait(true);
        shell.Explorer.DeleteCommand.Execute(shell.Explorer.RootNodes.Single(n => n.Name == "second.txt"));
        await WaitForAsync(() => !File.Exists(second)).ConfigureAwait(true);

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await WaitForAsync(() => File.Exists(second)).ConfigureAwait(true);
        File.Exists(second).Should().BeTrue("後から削除した2件目が1回目のCtrl+Zで先に戻るはず");
        File.Exists(first).Should().BeFalse("1件目はまだ戻していない");

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await WaitForAsync(() => File.Exists(first)).ConfigureAwait(true);
        File.Exists(first).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "テキスト入力欄にフォーカスがある間のCtrl+Zは削除の取り消しを発火しない（エディタのCtrl+Z非干渉）")]
    public async Task テキスト入力中はCtrlZで誤発火しない()
    {
        var filePath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(filePath, "内容").ConfigureAwait(true);

        var (shell, window) = await OpenShellAsync().ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count > 0).ConfigureAwait(true);

        var node = shell.Explorer.RootNodes.Single(n => n.Name == "sample.txt");
        shell.Explorer.DeleteCommand.Execute(node);
        await WaitForAsync(() => !File.Exists(filePath)).ConfigureAwait(true);
        shell.Explorer.HasDeleteUndoNotice.Should().BeTrue();

        // クイックオープン（Ctrl+P）を開き、検索欄（標準のTextBox）へフォーカスを移す
        // （ShortcutsWindowTestsと同じ手法。テキスト入力欄にフォーカスがある状況の代表）。
        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
        shell.QuickOpen.IsOpen.Should().BeTrue();
        await SettleAsync().ConfigureAwait(true);

        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        await SettleAsync().ConfigureAwait(true);

        File.Exists(filePath).Should().BeFalse(
            "テキスト入力欄にフォーカスがある間のCtrl+Zは、エディタの取り消し（ここではネイティブ処理へ委ねる）に使われるべきで、削除の取り消しを誤発火してはならない");
        shell.Explorer.HasDeleteUndoNotice.Should().BeTrue("取り消しが実行されていないので通知も消えていないはず");
    }

    [AvaloniaFact(DisplayName = "アプリ終了時（ExplorerViewModel.Dispose）に退避ディレクトリ（back/trash/）が空になる")]
    public async Task 終了時に退避が掃除される()
    {
        var filePath = Path.Combine(_projectDirectory, "sample.txt");
        await File.WriteAllTextAsync(filePath, "内容").ConfigureAwait(true);

        var appPaths = new AppPaths(_appDirectory);
        var (shell, window) = await OpenShellAsync(appPaths).ConfigureAwait(true);
        await shell.Graft.ProjectPane.RegisterFolderAsync(_projectDirectory).ConfigureAwait(true);
        await WaitForAsync(() => shell.Explorer.RootNodes.Count > 0).ConfigureAwait(true);

        var node = shell.Explorer.RootNodes.Single(n => n.Name == "sample.txt");
        shell.Explorer.DeleteCommand.Execute(node);
        await WaitForAsync(() => !File.Exists(filePath)).ConfigureAwait(true);
        shell.Explorer.HasDeleteUndoNotice.Should().BeTrue();

        Directory.Exists(appPaths.TrashStagingDirectory).Should().BeTrue("削除直後は退避コピーが残っているはず");
        Directory.EnumerateFileSystemEntries(appPaths.TrashStagingDirectory).Should().NotBeEmpty();

        // StartupCoordinator.DisposeAsyncが実際に呼ぶ経路（ShellViewModel.Dispose→Explorer.Dispose）。
        shell.Dispose();

        Directory.Exists(appPaths.TrashStagingDirectory).Should().BeFalse(
            "セッション内のみ保持する方針のため、アプリ終了時には退避コピーを必ず消す（OSのごみ箱側には残る）");
    }

    /// <summary>条件が満たされるかタイムアウトするまで待つ（非同期の確定待ち。他のテストと同じ作法）。</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    /// <summary>ディスパッチャに積まれた非同期継続（フォーカス移動等）が終わるまで待つ。</summary>
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private Task<(ShellViewModel Shell, ShellWindow Window)> OpenShellAsync() => OpenShellAsync(new AppPaths(_appDirectory));

    private async Task<(ShellViewModel Shell, ShellWindow Window)> OpenShellAsync(AppPaths appPaths)
    {
        appPaths.EnsureCoreDirectoriesExist();

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            new AutoConfirmDialogService(),
            new AvaloniaUiServices(),
            openSettings: () => { });

        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();
        await shell.Graft.InitializeAsync().ConfigureAwait(true);
        return (shell, window);
    }

    /// <summary>削除確認等をすべて許諾するダイアログ。ファイル選択系は使わないためnullを返す。</summary>
    private sealed class AutoConfirmDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial ?? "テスト");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
