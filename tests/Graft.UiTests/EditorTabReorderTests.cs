using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// 機能改善（タブのドラッグ並べ替え）の回帰テスト。
///
/// 1. ロジック層（EditorPaneViewModel.ReorderTab / EditorTabManager.MoveTab）の並べ替え
///    そのものと、既存操作（Ctrl+TabのMRU順・差分タブが末尾に固定される不変条件）を
///    壊さないこと。
/// 2. 実際のドラッグ（Avalonia.Headlessの疑似マウス操作、ShellWindowSplitterTestsと同じ手法）
///    でタブの表示順が変わること。
/// 3. 並び順がプロジェクト状態の保存（ShellViewModel.CaptureProjectState→layout.json）にも
///    反映され、再起動相当（新しいShellViewModelでの復元）でも保たれること。
/// </summary>
public class EditorTabReorderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-tab-reorder", Guid.NewGuid().ToString("N"));

    private readonly ShownWindowTracker _windows = new();

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

    // ------------------------------------------------------------------
    // 1. ロジック層
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "ReorderTabでドキュメントタブの表示順が変わる")]
    public async Task ReorderTabで表示順が変わる()
    {
        var dir = Path.Combine(_root, "project1");
        Directory.CreateDirectory(dir);
        var (vm, tabA, tabB, tabC) = await OpenThreeTabsAsync(dir).ConfigureAwait(true);

        // 開いた順は a, b, c。cをいちばん前（index 0）へ移動する。
        vm.Tabs.Select(t => t.Title).Should().Equal("a.txt", "b.txt", "c.txt");

        vm.ReorderTab(tabC, 0);

        vm.Tabs.Select(t => t.Title).Should().Equal("c.txt", "a.txt", "b.txt");
        _ = (tabA, tabB);
    }

    [AvaloniaFact(DisplayName = "差分タブ・履歴差分タブは常に末尾のままで、ReorderTabの対象にならない")]
    public async Task 差分タブは並べ替え対象にならず末尾のまま()
    {
        var dir = Path.Combine(_root, "project2");
        Directory.CreateDirectory(dir);
        var (vm, tabA, tabB, tabC) = await OpenThreeTabsAsync(dir).ConfigureAwait(true);

        var diffVm = new DiffViewModel(new Settings(), new AvaloniaUiServices());
        vm.ShowDiffTab(diffVm);
        vm.Tabs.Select(t => t.Kind).Should().Equal(
            EditorTabKind.Document, EditorTabKind.Document, EditorTabKind.Document, EditorTabKind.Diff);

        // ドキュメントタブを並べ替えても差分タブは末尾のまま。
        vm.ReorderTab(tabA, 2);
        vm.Tabs.Select(t => t.Kind).Should().Equal(
            EditorTabKind.Document, EditorTabKind.Document, EditorTabKind.Document, EditorTabKind.Diff);
        vm.Tabs.Last().Kind.Should().Be(EditorTabKind.Diff);

        // 差分タブ自体をReorderTabへ渡しても無視される（ドキュメントタブ専用）。
        var before = vm.Tabs.ToList();
        var diffTab = vm.Tabs.Single(t => t.Kind == EditorTabKind.Diff);
        vm.ReorderTab(diffTab, 0);
        vm.Tabs.Should().Equal(before);
        _ = (tabB, tabC);
    }

    [AvaloniaFact(DisplayName = "並べ替えてもCtrl+Tabの直近使用順（MRU）は変わらない")]
    public async Task 並べ替えてもMRU順は変わらない()
    {
        var dir = Path.Combine(_root, "project3");
        Directory.CreateDirectory(dir);
        var (vm, tabA, tabB, tabC) = await OpenThreeTabsAsync(dir).ConfigureAwait(true);
        // 開いた順にActiveTabになるため、MRUは新しい順で [c, b, a]。今アクティブなのはc。
        vm.ActiveTab.Should().Be(tabC);
        vm.PeekMruNeighbor().Should().Be(tabB, "Ctrl+Tabで直近2番目に使ったb.txtへ切り替わるはず");

        // 表示順を大きく変えても、MRU順（＝Ctrl+Tabの挙動）は表示順とは独立のはず。
        vm.ReorderTab(tabA, 0);
        vm.ReorderTab(tabC, 0);
        vm.Tabs.Select(t => t.Title).Should().Equal("c.txt", "a.txt", "b.txt");

        vm.ActiveTab.Should().Be(tabC, "並べ替え自体はActiveTabを変えない");
        vm.PeekMruNeighbor().Should().Be(tabB, "並べ替えてもCtrl+Tabの切替先は変わらないはず");
    }

    [Fact(DisplayName = "ResolveDropIndex: ポインタ位置に応じて挿入先インデックスを求める")]
    public void ResolveDropIndexの判定()
    {
        var centers = new List<double> { 20, 60, 100 }; // 3つのタブの中心X座標

        EditorPane.ResolveDropIndex(centers, -10).Should().Be(0, "先頭より左なら先頭へ挿入");
        EditorPane.ResolveDropIndex(centers, 19).Should().Be(0, "1番目の中心より少しでも左なら1番目の前");
        EditorPane.ResolveDropIndex(centers, 20).Should().Be(1, "中心ちょうどはその項目自体を超えた扱い（右半分に含める）");
        EditorPane.ResolveDropIndex(centers, 40).Should().Be(1, "1番目と2番目の間");
        EditorPane.ResolveDropIndex(centers, 90).Should().Be(2, "2番目と3番目の間");
        EditorPane.ResolveDropIndex(centers, 1000).Should().Be(3, "末尾より右なら末尾へ挿入");
        EditorPane.ResolveDropIndex(Array.Empty<double>(), 0).Should().Be(0, "タブが無ければ常に0");
    }

    // ------------------------------------------------------------------
    // 2. 実際のドラッグ（疑似マウス操作）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "実際にタブをドラッグすると表示順が入れ替わる")]
    public async Task 実ドラッグでタブの表示順が入れ替わる()
    {
        var dir = Path.Combine(_root, "project-drag");
        Directory.CreateDirectory(dir);
        var (vm, tabA, _, tabC) = await OpenThreeTabsAsync(dir).ConfigureAwait(true);

        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 900, Height = 600, Content = pane });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var itemA = FindTabItem(window, tabA);
        var itemC = FindTabItem(window, tabC);

        // a.txtのタブ中央から、c.txtのタブ中央より少し右まで（末尾側）ドラッグする。
        var from = itemA.TranslatePoint(new Point(itemA.Bounds.Width / 2, itemA.Bounds.Height / 2), window)!.Value;
        var to = itemC.TranslatePoint(new Point(itemC.Bounds.Width - 2, itemC.Bounds.Height / 2), window)!.Value;

        Drag(window, from, to);

        vm.Tabs.Select(t => t.Title).Should().Equal(
            new[] { "b.txt", "c.txt", "a.txt" },
            "a.txtをc.txtより後ろへドラッグしたので、b, c, aの順になるはず");
    }

    [AvaloniaFact(DisplayName = "タブをわずかに動かしただけ（しきい値未満）では並べ替えず、通常のクリック選択のまま")]
    public async Task わずかな移動では並べ替わらずクリック選択のまま()
    {
        var dir = Path.Combine(_root, "project-click");
        Directory.CreateDirectory(dir);
        var (vm, tabA, tabB, _) = await OpenThreeTabsAsync(dir).ConfigureAwait(true);

        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 900, Height = 600, Content = pane });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var itemA = FindTabItem(window, tabA);
        var point = itemA.TranslatePoint(new Point(itemA.Bounds.Width / 2, itemA.Bounds.Height / 2), window)!.Value;

        window.MouseMove(point);
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        vm.Tabs.Select(t => t.Title).Should().Equal(new[] { "a.txt", "b.txt", "c.txt" }, "並べ替わらないはず");
        vm.ActiveTab.Should().Be(tabA, "通常のクリックとして、そのタブがアクティブになるはず");
        _ = tabB;
    }

    // ------------------------------------------------------------------
    // 3. 保存・復元（layout.json）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "並べ替えた順序はlayout.jsonへ保存され、再起動相当の復元でも保たれる")]
    public async Task 並べ替えた順序は保存され復元後も保たれる()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDirectory = Path.Combine(_root, "project-persist");
        Directory.CreateDirectory(projectDirectory);
        var pathA = Path.Combine(projectDirectory, "a.txt");
        var pathB = Path.Combine(projectDirectory, "b.txt");
        await File.WriteAllTextAsync(pathA, "A\n").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathB, "B\n").ConfigureAwait(true);

        var shell1 = BuildShell(appPaths);
        var window1 = _windows.Track(new ShellWindow(shell1) { Width = 1000, Height = 700 });
        window1.CloseBehavior = "exit";
        window1.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window1);

        await shell1.Graft.ProjectPane.RegisterFolderAsync(projectDirectory).ConfigureAwait(true);
        var tabAResult = await shell1.Editor.OpenFileAsync(pathA).ConfigureAwait(true);
        var tabBResult = await shell1.Editor.OpenFileAsync(pathB).ConfigureAwait(true);
        shell1.Editor.Tabs.Select(t => t.Title).Should().Equal(new[] { "a.txt", "b.txt" }, "開いた順のはず");

        // b.txtを先頭へ並べ替える。
        shell1.Editor.ReorderTab(tabBResult.Value, 0);
        shell1.Editor.Tabs.Select(t => t.Title).Should().Equal("b.txt", "a.txt");

        window1.Close();

        var layoutPath = Path.Combine(appPaths.BaseDirectory, "layout.json");
        File.Exists(layoutPath).Should().BeTrue();
        var project = shell1.Graft.ProjectPane.SelectedItem!.Project;

        // 再起動相当: 新しいShellViewModel/ShellWindowで同じappPathsを読み込み、復元する。
        var shell2 = BuildShell(appPaths);
        var window2 = _windows.Track(new ShellWindow(shell2) { Width = 1000, Height = 700 });
        window2.CloseBehavior = "exit";
        window2.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(window2);

        await shell2.Graft.ProjectPane.RegisterFolderAsync(projectDirectory).ConfigureAwait(true);
        var restoredProject = shell2.Graft.ProjectPane.Items.Single(i => i.Project.Id == project.Id);
        shell2.Graft.ProjectPane.SelectedItem = restoredProject;
        await WaitUntilAsync(() => Task.FromResult(shell2.Editor.Tabs.Count == 2)).ConfigureAwait(true);

        shell2.Editor.Tabs.Select(t => t.Title).Should().Equal(
            new[] { "b.txt", "a.txt" }, "並べ替えた順序（b, a）が再起動相当の復元後も保たれている必要がある");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private static async Task<(EditorPaneViewModel Vm, EditorTabViewModel A, EditorTabViewModel B, EditorTabViewModel C)>
        OpenThreeTabsAsync(string dir)
    {
        var pathA = Path.Combine(dir, "a.txt");
        var pathB = Path.Combine(dir, "b.txt");
        var pathC = Path.Combine(dir, "c.txt");
        await File.WriteAllTextAsync(pathA, "a").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathB, "b").ConfigureAwait(true);
        await File.WriteAllTextAsync(pathC, "c").ConfigureAwait(true);

        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(dir);
        var a = (await vm.OpenFileAsync(pathA).ConfigureAwait(true)).Value;
        var b = (await vm.OpenFileAsync(pathB).ConfigureAwait(true)).Value;
        var c = (await vm.OpenFileAsync(pathC).ConfigureAwait(true)).Value;
        return (vm, a, b, c);
    }

    private static ListBoxItem FindTabItem(Window window, EditorTabViewModel tab)
        => window.GetVisualDescendants().OfType<ListBoxItem>().Single(i => ReferenceEquals(i.DataContext, tab));

    private ShellViewModel BuildShell(AppPaths appPaths)
    {
        IDialogService dialogs = new NullDialogService();
        IUiServices ui = new AvaloniaUiServices();
        return StartupCoordinator.BuildShellViewModel(
            appPaths, new Settings(), new SettingsStore(appPaths), new PatchQueue(appPaths),
            new ProjectStore(appPaths), new RevisionStore(appPaths), new RevisionRestorer(appPaths),
            dialogs, ui, openSettings: () => { });
    }

    /// <summary>
    /// 実際のマウスと同じ「移動してから押す」順序で、複数ステップに分けてドラッグする
    /// （ShellWindowSplitterTestsと同じ手法）。
    /// 注意: Avalonia.HeadlessのMouseMoveはMouseDownと違い、ボタンの押下状態を自動的に
    /// 引き継がない（PointerEventArgs.GetCurrentPoint().Properties.IsLeftButtonPressedは
    /// 明示的にRawInputModifiers.LeftMouseButtonを渡さない限りfalseのまま）。ドラッグ中の
    /// 移動と判定させるため、区間中の各MouseMoveにこの修飾子を渡す。
    /// </summary>
    private static void Drag(Window window, Point from, Point to, int steps = 10)
    {
        window.MouseMove(from);
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(from, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        for (var i = 1; i <= steps; i++)
        {
            var point = new Point(
                from.X + (to.X - from.X) * i / steps,
                from.Y + (to.Y - from.Y) * i / steps);
            window.MouseMove(point, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition().ConfigureAwait(true)) return;
            await Task.Delay(10).ConfigureAwait(true);
        }
    }
}
