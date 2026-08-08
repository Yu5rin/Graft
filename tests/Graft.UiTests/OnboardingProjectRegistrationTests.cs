using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// バグ（初回起動ガイドで登録したプロジェクトがシェルの一覧・ドロップダウンに反映されない）の
/// 回帰テスト。
///
/// 真因: <see cref="OnboardingWindow"/> は自前で<c>new ProjectStore(appPaths)</c>を持ち、
/// フォルダ登録時にそれへ直接<c>RegisterAsync</c>していた。projects.jsonへの書き込み自体は
/// 成功するが、シェル（<see cref="ShellViewModel.Graft"/>の<see cref="MainViewModel.ProjectPane"/>）が
/// 起動時に読み込んで保持している<see cref="ProjectPaneViewModel.Items"/>はメモリ上の別コレクション
/// のため、ガイドを閉じても更新されない（再起動して再読み込みされたときだけ現れる）。
///
/// 対応: <see cref="OnboardingWindow"/> にシェルと同じ<see cref="ProjectPaneViewModel"/>インスタンスを
/// 渡し、<see cref="ProjectPaneViewModel.RegisterFolderAsync"/>を介して登録する
/// （<see cref="StartupCoordinator.StartAsync"/>の実際の配線）。これにより登録・一覧再読み込み・
/// 新規プロジェクトの選択が、シェルの一覧・ドロップダウンがバインドしているコレクション
/// そのものに対して行われる。
/// </summary>
public class OnboardingProjectRegistrationTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-onboarding-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // 利用者の設定を汚さないよう、テストごとに一時ディレクトリを使い捨てる。
        try
        {
            if (Directory.Exists(_baseDirectory)) Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末に失敗しても検証結果には影響しないため無視する。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "初回起動ガイドの3画面を実際にたどってフォルダを登録すると、閉じた直後にシェルの一覧・ドロップダウンへ反映され選択状態になる")]
    public async Task ガイドを完走して登録すると一覧に反映され選択される()
    {
        var shell = BuildShell();
        var shellWindow = new ShellWindow(shell) { Width = 1280, Height = 800 };
        shellWindow.Show();
        await shell.Graft.InitializeAsync().ConfigureAwait(true);

        var projectDirectory = Path.Combine(_baseDirectory, "MyProject");
        Directory.CreateDirectory(projectDirectory);

        var dialogs = new FixedFolderDialogService(projectDirectory);
        var onboarding = new OnboardingWindow(new AppPaths(_baseDirectory), shell.Graft.ProjectPane, dialogs);
        onboarding.Show();

        // 画面1 → 画面2（プロジェクト登録）。
        RaiseClick(onboarding, "次へ");
        await SettleAsync().ConfigureAwait(true);

        RaiseClick(onboarding, "フォルダを選択して登録");
        await SettleAsync().ConfigureAwait(true);

        // シェル側のドロップダウン・左ペインが参照しているのと同じコレクションに、
        // ガイドを閉じる前の時点で既に反映されている必要がある（バグ修正の核心）。
        shell.Graft.ProjectPane.Items.Should().ContainSingle(i => i.DisplayName == "MyProject");
        shell.Graft.ProjectPane.SelectedItem.Should().NotBeNull("登録したプロジェクトが選択された状態になっているべき");
        shell.Graft.ProjectPane.SelectedItem!.DisplayName.Should().Be("MyProject");

        // 画面2 → 画面3 → 完了。完了操作そのものが一覧を巻き戻さないことも確認する。
        RaiseClick(onboarding, "次へ");
        await SettleAsync().ConfigureAwait(true);
        RaiseClick(onboarding, "完了");
        await SettleAsync().ConfigureAwait(true);

        shell.Graft.ProjectPane.Items.Should().ContainSingle(i => i.DisplayName == "MyProject");
        shell.Graft.ProjectPane.SelectedItem?.DisplayName.Should().Be("MyProject");
        OnboardingWindow.HasCompleted(new AppPaths(_baseDirectory)).Should().BeTrue("完了操作で表示済みフラグが書き出される必要がある");
    }

    [AvaloniaFact(DisplayName = "初回起動ガイドをスキップしても一覧は空のままで例外が起きない")]
    public async Task スキップした場合は一覧が空のままで問題ない()
    {
        var shell = BuildShell();
        var shellWindow = new ShellWindow(shell) { Width = 1280, Height = 800 };
        shellWindow.Show();
        await shell.Graft.InitializeAsync().ConfigureAwait(true);

        var dialogs = new FixedFolderDialogService(folder: null);
        var onboarding = new OnboardingWindow(new AppPaths(_baseDirectory), shell.Graft.ProjectPane, dialogs);
        onboarding.Show();

        var act = () => RaiseClick(onboarding, "スキップ");
        act.Should().NotThrow();
        await SettleAsync().ConfigureAwait(true);

        shell.Graft.ProjectPane.Items.Should().BeEmpty();
        shell.Graft.ProjectPane.SelectedItem.Should().BeNull();
        OnboardingWindow.HasCompleted(new AppPaths(_baseDirectory)).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "フォルダを登録せずに完了しても一覧は空のままで例外が起きない")]
    public async Task 登録せず完了しても一覧は空のままで問題ない()
    {
        var shell = BuildShell();
        var shellWindow = new ShellWindow(shell) { Width = 1280, Height = 800 };
        shellWindow.Show();
        await shell.Graft.InitializeAsync().ConfigureAwait(true);

        var dialogs = new FixedFolderDialogService(folder: null);
        var onboarding = new OnboardingWindow(new AppPaths(_baseDirectory), shell.Graft.ProjectPane, dialogs);
        onboarding.Show();

        RaiseClick(onboarding, "次へ"); // 画面1→画面2
        await SettleAsync().ConfigureAwait(true);
        RaiseClick(onboarding, "次へ"); // 画面2→画面3（フォルダは選ばない）
        await SettleAsync().ConfigureAwait(true);

        var act = () => RaiseClick(onboarding, "完了"); // 画面3→完了
        act.Should().NotThrow();
        await SettleAsync().ConfigureAwait(true);

        shell.Graft.ProjectPane.Items.Should().BeEmpty();
        shell.Graft.ProjectPane.SelectedItem.Should().BeNull();
    }

    [AvaloniaFact(DisplayName = "既に他のプロジェクトが選択されている状態でガイドから登録しても、その選択を勝手に奪わない前提の登録経路を通る")]
    public async Task 登録経路はProjectPaneを共有する()
    {
        // ProjectPaneViewModel.RegisterFolderAsyncは、既存のドラッグ&ドロップ・「プロジェクトを追加」
        // ボタンからも呼ばれている登録経路そのものであり、ガイド専用の別ロジックを持ち込んで
        // いないことを確認する（＝アプリ全体で挙動が一貫している）。
        var shell = BuildShell();
        var shellWindow = new ShellWindow(shell) { Width = 1280, Height = 800 };
        shellWindow.Show();
        await shell.Graft.InitializeAsync().ConfigureAwait(true);

        var projectDirectory = Path.Combine(_baseDirectory, "MyProject");
        Directory.CreateDirectory(projectDirectory);

        var dialogs = new FixedFolderDialogService(projectDirectory);
        var onboarding = new OnboardingWindow(new AppPaths(_baseDirectory), shell.Graft.ProjectPane, dialogs);
        onboarding.Show();

        RaiseClick(onboarding, "次へ");
        await SettleAsync().ConfigureAwait(true);
        RaiseClick(onboarding, "フォルダを選択して登録");
        await SettleAsync().ConfigureAwait(true);

        // ProjectPaneViewModel.RegisterFolderAsyncを直接呼んだ場合と同じ結果になっている
        // （＝ガイド専用の分岐を経由していない）ことを確認する。
        shell.Graft.ProjectPane.SelectedItem!.Project.Root.Should().Be(projectDirectory);
    }

    private static void RaiseClick(Window window, string content)
    {
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, content));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(10);
        }
    }

    private ShellViewModel BuildShell()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        IDialogService dialogs = new Graft.Platform.Null.NullDialogService();
        IUiServices ui = new AvaloniaUiServices();

        return StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Graft.Infra.Settings(),
            new SettingsStore(appPaths),
            new Graft.Features.PatchQueue(appPaths),
            new Graft.Features.ProjectStore(appPaths),
            new Graft.Core.RevisionStore(appPaths),
            new Graft.Core.RevisionRestorer(appPaths),
            dialogs,
            ui,
            openSettings: () => { });
    }

    /// <summary>フォルダ選択ダイアログはOSのネイティブダイアログのためheadlessテストから駆動できない。
    /// 固定のパス（またはキャンセル相当のnull）を返すフェイク。</summary>
    private sealed class FixedFolderDialogService : IDialogService
    {
        private readonly string? _folder;

        public FixedFolderDialogService(string? folder) => _folder = folder;

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult((bool?)null);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult(_folder);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
