using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
/// 実際の起動と同じ依存グラフでシェルを組み立て、表示・描画できることを検証する
/// （仕様書v2.1 附録A.7、20章L3）。
///
/// これまでのViewTestsはDataContextを与えずに各画面を描画していたため、
/// XAMLの構文誤りやリソース解決の失敗は検出できても、バインディング先の
/// プロパティ名の誤りやViewModel構築時の例外までは検出できない。
/// ここでは StartupCoordinator.BuildShellViewModel で本番と同一の構成を作り、
/// ShellWindow へ実際に割り当てて描画する。
/// </summary>
public class StartupTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-ui-tests", Guid.NewGuid().ToString("N"));

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

    [AvaloniaFact(DisplayName = "本番と同じ依存グラフでシェルを構築して描画できる")]
    public void シェルを実際の依存グラフで描画できる()
    {
        var shell = BuildShell();

        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();

        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("バインディング先やリソースの解決に失敗すると描画できない");
        window.Title.Should().Be("Graft");
    }

    [AvaloniaFact(DisplayName = "サイドビューは選択中の1つだけが表示される")]
    public void サイドビューは選択中の1つだけが表示される()
    {
        var shell = BuildShell();
        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();

        // 4つのビューは同じセルに重ねて配置されているため、出し分けを誤ると
        // 選択していないビューが最前面に見えてしまう（実機で発生した不具合）。
        var views = new (SideViewKind Kind, Control View)[]
        {
            (SideViewKind.Explorer, window.GetControl<ExplorerView>("ExplorerViewControl")),
            (SideViewKind.Project, window.GetControl<ProjectPane>("ProjectPaneControl")),
            (SideViewKind.History, window.GetControl<HistoryPane>("HistoryPaneControl")),
        };

        foreach (var (kind, _) in views)
        {
            shell.SelectSideView(kind);
            shell.SelectedSideView.Should().Be(kind);
            window.CaptureRenderedFrame().Should().NotBeNull($"{kind} のサイドビューが描画できる必要がある");

            foreach (var (otherKind, otherView) in views)
            {
                otherView.IsEffectivelyVisible.Should().Be(
                    otherKind == kind, $"{kind} を選択中は {otherKind} のビューが見えてはならない");
            }
        }
    }

    [AvaloniaFact(DisplayName = "サイドビューを折りたたむとどのビューも表示されない")]
    public void サイドビューを折りたたむと非表示になる()
    {
        var shell = BuildShell();
        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();

        // 既定はプロジェクトビューが展開された状態のため、同じアイコンの再クリック相当の
        // 呼び出し1回で折りたたまれる（9.2）。
        shell.SelectedSideView.Should().Be(SideViewKind.Project);
        shell.SelectSideView(SideViewKind.Project);
        shell.IsSideViewCollapsed.Should().BeTrue();

        window.CaptureRenderedFrame().Should().NotBeNull();
    }

    [AvaloniaFact(DisplayName = "検索ビューを表示すると検索テキストボックスへ自動でフォーカスする")]
    public void 検索ビュー表示時に検索欄へ自動フォーカスする()
    {
        var shell = BuildShell();
        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();

        var searchView = window.GetControl<SearchView>("SearchViewControl");

        // サイドバーの虫眼鏡アイコン・Ctrl+Shift+Fのいずれも SelectSideView(Search) を経由する。
        shell.SelectSideView(SideViewKind.Search);
        shell.SelectedSideView.Should().Be(SideViewKind.Search);

        // フォーカスはレイアウト確定後まで遅延されるため、保留中のディスパッチャジョブを流す。
        Dispatcher.UIThread.RunJobs();

        searchView.QueryBoxElement.IsFocused.Should().BeTrue("4.4: 検索ビュー表示時は検索欄へ自動フォーカスする必要がある");
    }

    [AvaloniaFact(DisplayName = "接ぎ木パネルの開閉でレイアウトが破綻しない")]
    public void 接ぎ木パネルを開閉できる()
    {
        var shell = BuildShell();
        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();

        shell.IsGraftPanelOpen = true;
        window.CaptureRenderedFrame().Should().NotBeNull();

        shell.IsGraftPanelOpen = false;
        window.CaptureRenderedFrame().Should().NotBeNull();
    }

    [AvaloniaFact(DisplayName = "CaptureCurrentProjectStateで開いていたタブがProjectPaneLayoutへ記憶される（アプリ終了時の保存経路）")]
    public async Task 終了時にタブ構成が取り込まれる()
    {
        var shell = BuildShell();
        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();
        await shell.Graft.InitializeAsync().ConfigureAwait(true);

        var pathA = Path.Combine(_baseDirectory, "project", "a.txt");
        var pathB = Path.Combine(_baseDirectory, "project", "b.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(pathA)!);
        await File.WriteAllTextAsync(pathA, "A\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathB, "B\n").ConfigureAwait(true);

        var projectDirectory = Path.GetDirectoryName(pathA)!;
        await shell.Graft.ProjectPane.RegisterFolderAsync(projectDirectory).ConfigureAwait(true);
        var project = shell.Graft.ProjectPane.SelectedItem!.Project;

        await shell.Editor.OpenFileAsync(pathA).ConfigureAwait(true);
        var tabB = (await shell.Editor.OpenFileAsync(pathB).ConfigureAwait(true)).Value;
        shell.Editor.ActiveTab = tabB;

        // アプリ終了時（ShellWindow.OnClosing）が SaveLayoutAsync の直前に呼ぶ経路そのもの。
        // これがないと、プロジェクト切替を挟まずに終了した場合にタブ構成が記憶されない。
        shell.CaptureCurrentProjectState();

        var paneLayout = WindowLayoutStore.GetOrCreatePaneLayout(shell.Graft.Layout, project.Id);
        paneLayout.OpenTabs.Should().HaveCount(2, "開いていた2枚のタブが記憶される必要がある");
        paneLayout.OpenTabs.Select(t => t.RelativePath).Should().Contain(new[] { "a.txt", "b.txt" });
        paneLayout.ActiveTabPath.Should().Be("b.txt", "アクティブタブも記憶される必要がある");
    }

    [AvaloniaFact(DisplayName = "プロジェクト未選択のときCaptureCurrentProjectStateを呼んでも何も起きない")]
    public void プロジェクト未選択時のCaptureは何もしない()
    {
        var shell = BuildShell();
        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();

        var act = () => shell.CaptureCurrentProjectState();

        act.Should().NotThrow();
    }

    private ShellViewModel BuildShell()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        // ダイアログはテスト中に開けないため、何もしないNull実装を使う。
        IDialogService dialogs = new NullDialogService();
        IUiServices ui = new AvaloniaUiServices();

        return StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            dialogs,
            ui,
            openSettings: () => { });
    }
}
