using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 「タブが増えたときに目的のタブへ到達できない」問題の回帰テスト。依頼の4点
/// （幅の自動縮小・スクロールボタン・タブ一覧ドロップダウン・ホイールスクロール）と、
/// 「選択中のタブが常に見える」自動スクロールを確認する。
/// 既存のタブ操作（閉じる・MRU・ドラッグ並べ替え）が壊れていないことは
/// TabCloseButtonVisibilityTests・RecentlyClosedTabTests・EditorTabReorderTestsが担う。
/// </summary>
public class EditorTabStripOverflowTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-tabstrip-overflow", Guid.NewGuid().ToString("N"));

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
    // 依頼1: タブ幅の均等縮小アルゴリズム（TabStripPanel.ComputeWidths、純粋なロジック）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ComputeWidths: 合計が収まるなら自然な幅のまま並べる")]
    public void 収まる場合は自然な幅のまま()
    {
        var natural = new double[] { 60, 80, 100 };
        var widths = TabStripPanel.ComputeWidths(natural, availableWidth: 400, minWidth: 40);

        widths.Should().Equal(natural, "合計240は利用可能幅400に収まるので縮める必要が無い");
    }

    [Fact(DisplayName = "ComputeWidths: 短いタブは縮めず、長いタブから優先的に縮む")]
    public void 短いタブを縮めすぎない()
    {
        // 短い(30)・普通(100)・長い(200)の3枚。合計330 > 利用可能210。
        var natural = new double[] { 30, 100, 200 };
        var widths = TabStripPanel.ComputeWidths(natural, availableWidth: 210, minWidth: 20);

        widths[0].Should().Be(30, "短いタブ(30)はもともと平均(70)より狭いので縮めない");
        // 残り2枚(100,200)で残り幅(210-30=180)を均等分配 → 90ずつ。100は90超なので縮む対象、
        // 200も90に縮む。
        widths[1].Should().Be(90);
        widths[2].Should().Be(90);
        (widths[0] + widths[1] + widths[2]).Should().Be(210, "縮小後の合計は利用可能幅ちょうどに収まる（あふれない）");
    }

    [Fact(DisplayName = "ComputeWidths: 最小幅を下回るところまでは縮めず、あふれた分はそのまま返す")]
    public void 最小幅で床を打つ()
    {
        var natural = new double[] { 200, 200, 200, 200 };
        var widths = TabStripPanel.ComputeWidths(natural, availableWidth: 200, minWidth: 96);

        widths.Should().AllBeEquivalentTo(96.0, "4枚とも利用可能幅を大きく超えるので全員最小幅まで縮む");
        (widths.Sum() > 200).Should().BeTrue("最小幅×4枚は利用可能幅を超えるので、この分はスクロールに委ねる（縮めない）");
    }

    [Fact(DisplayName = "ComputeWidths: タブが無ければ空配列を返す")]
    public void タブが無い場合()
    {
        TabStripPanel.ComputeWidths(Array.Empty<double>(), availableWidth: 400, minWidth: 40).Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // 依頼1/2: タブが少ないときはスクロールボタンが出ない／多いときは出る
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "タブが少なく幅に収まるときはスクロールボタンが出ない")]
    public async Task タブが少ないときはスクロールボタン非表示()
    {
        var (_, window, panel, leftButton, rightButton) =
            await BuildWindowWithTabsAsync(tabCount: 2, windowWidth: 900).ConfigureAwait(true);
        _ = window;

        panel.HasOverflow.Should().BeFalse("2枚なら900px幅に楽に収まる");
        leftButton.IsVisible.Should().BeFalse();
        rightButton.IsVisible.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "タブが増えて最小幅でも収まらないときはスクロールボタンが出る")]
    public async Task タブが多いときはスクロールボタン表示()
    {
        var (vm, window, panel, leftButton, rightButton) =
            await BuildWindowWithTabsAsync(tabCount: 15, windowWidth: 700).ConfigureAwait(true);
        _ = window;

        panel.HasOverflow.Should().BeTrue("15枚を700px幅（最小幅96px×15=1440pxが必要）には収められない");
        rightButton.IsVisible.Should().BeTrue();
        leftButton.IsVisible.Should().BeTrue();
        // 開いた直後は最後に開いたタブ（15番目）がアクティブで、選択中のタブが常に見えるように
        // する自動スクロール（依頼の注意点）によって右端寄りへスクロール済みのはず。
        leftButton.IsEnabled.Should().BeTrue("最後のタブを見せるため右へスクロール済みなので、左へはまだ戻れる");
        rightButton.IsEnabled.Should().BeFalse("最後のタブ（＝タブ列の右端）まで既にスクロール済みのはず");

        // 依頼1: 収まらない分は依頼1の最小幅まで縮んでいるはず（ファイル名が短いため、
        // 均等縮小の過程で全タブが最小幅まで縮む）。
        var items = window.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(i => i.DataContext is EditorTabViewModel { Kind: EditorTabKind.Document })
            .ToList();
        items.Should().HaveCount(15);
        foreach (var item in items)
        {
            item.Bounds.Width.Should().BeApproximately(TabStripPanel.MinTabWidth, 1.0,
                "収まりきらないほどタブが増えたら最小幅まで縮むはず");
        }
        _ = vm;
    }

    // ------------------------------------------------------------------
    // 依頼4: マウスホイールで左右にスクロールする
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "タブ列の上でホイールを回すとスクロール位置が変わる")]
    public async Task ホイールでスクロールする()
    {
        var (_, window, panel, _, _) = await BuildWindowWithTabsAsync(tabCount: 15, windowWidth: 700).ConfigureAwait(true);

        // 選択中タブの自動スクロールで既に右寄りになっているため、まず左端へ戻してから検証する
        // （ホイール操作そのものの検証に絞るため）。
        panel.Offset = 0;
        Dispatcher.UIThread.RunJobs();
        panel.Offset.Should().Be(0);

        var tabStripRow = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TabStripRow");
        var pos = tabStripRow.TranslatePoint(new Point(tabStripRow.Bounds.Width / 2, tabStripRow.Bounds.Height / 2), window)!.Value;

        // 下方向へのホイール（Delta.Y負）は右（後のタブ側）へスクロールする。
        window.MouseWheel(pos, new Vector(0, -3));
        Dispatcher.UIThread.RunJobs();

        panel.Offset.Should().BeGreaterThan(0, "ホイールでスクロール位置が動いたはず");

        var afterFirstScroll = panel.Offset;
        // 上方向（Delta.Y正）で逆に戻る。
        window.MouseWheel(pos, new Vector(0, 3));
        Dispatcher.UIThread.RunJobs();

        panel.Offset.Should().BeLessThan(afterFirstScroll, "逆方向のホイールでスクロール位置が戻るはず");
    }

    // ------------------------------------------------------------------
    // 選択中タブの自動スクロール（Ctrl+TabやクイックオープンなどActiveTabが変わる経路全般の代表として、
    // ActiveTabを直接切り替える経路で検証する。実際のキー操作はEditorPaneViewModel.PeekMruNeighbor
    // 側のロジックとして別テストで検証済みのため、ここではView側の「見えるようにする」部分に絞る）。
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "選択中のタブが画面外にあると自動でスクロールして見える位置に来る")]
    public async Task 選択中タブが画面外なら自動スクロールされる()
    {
        var (vm, window, panel, _, rightButton) =
            await BuildWindowWithTabsAsync(tabCount: 15, windowWidth: 700).ConfigureAwait(true);

        // 最後（15番目）のタブがアクティブ＝スクロール済み（右端寄り）になっているはず。
        var lastTab = vm.Tabs.Last(t => t.Kind == EditorTabKind.Document);
        vm.ActiveTab.Should().Be(lastTab, "最後に開いたタブがアクティブなはず");
        var lastItem = window.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(i => ReferenceEquals(i.DataContext, lastTab));
        IsWithinTabStripViewport(lastItem, window).Should().BeTrue("開いた直後の自動スクロールで最後のタブは見えているはず");

        // 先頭タブへ切り替える（Ctrl+Tab・クイックオープン等いずれも最終的にActiveTabを
        // 差し替える点は共通のため、ここで代表して検証する。ApplyActiveTab→ScheduleEnsureTabVisible
        // 経由で自動スクロールされるはず）。
        var firstTab = vm.Tabs.First(t => t.Kind == EditorTabKind.Document);
        vm.ActiveTab = firstTab;
        Dispatcher.UIThread.RunJobs();

        var firstItem = window.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(i => ReferenceEquals(i.DataContext, firstTab));
        IsWithinTabStripViewport(firstItem, window).Should().BeTrue(
            "先頭タブへ切り替えたら、画面外にあっても自動でスクロールして見える位置に来るはず");
        _ = rightButton;
    }

    // ------------------------------------------------------------------
    // 依頼3: タブ一覧ドロップダウン
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "タブ一覧ドロップダウンから選ぶとそのタブが選択される")]
    public async Task ドロップダウンから選ぶと選択される()
    {
        var (vm, window, _, _, _) = await BuildWindowWithTabsAsync(tabCount: 5, windowWidth: 900).ConfigureAwait(true);

        var dropDownButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TabListDropDownButton");
        dropDownButton.Flyout.Should().NotBeNull();
        dropDownButton.Flyout!.ShowAt(dropDownButton);
        Dispatcher.UIThread.RunJobs();

        var pickerList = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TabPickerList");
        var targetTab = vm.Tabs.First(t => t.Kind == EditorTabKind.Document && t.Title == "file03.txt");

        pickerList.SelectedItem = targetTab;
        Dispatcher.UIThread.RunJobs();

        vm.ActiveTab.Should().Be(targetTab, "ドロップダウンの一覧から選ぶとそのタブへ切り替わるはず");
    }

    [AvaloniaFact(DisplayName = "タブ一覧ドロップダウンはファイル名で絞り込める")]
    public async Task ドロップダウンはファイル名で絞り込める()
    {
        var (vm, window, _, _, _) = await BuildWindowWithTabsAsync(tabCount: 5, windowWidth: 900).ConfigureAwait(true);
        _ = vm;

        var dropDownButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TabListDropDownButton");
        dropDownButton.Flyout!.ShowAt(dropDownButton);
        Dispatcher.UIThread.RunJobs();

        var searchBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "TabPickerSearchBox");
        var pickerList = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TabPickerList");

        pickerList.ItemsSource!.Cast<EditorTabViewModel>().Should().HaveCount(5, "絞り込み前は全タブが並ぶ");

        searchBox.Text = "file03";
        Dispatcher.UIThread.RunJobs();

        var filtered = pickerList.ItemsSource!.Cast<EditorTabViewModel>().ToList();
        filtered.Should().ContainSingle().Which.Title.Should().Be("file03.txt");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    /// <summary>指定枚数のタブを開き、指定幅のウィンドウで表示する。最後に開いたタブが
    /// アクティブになる（EditorPaneViewModel.OpenFileAsyncの既定挙動）。</summary>
    private async Task<(EditorPaneViewModel Vm, Window Window, TabStripPanel Panel, Button LeftButton, Button RightButton)>
        BuildWindowWithTabsAsync(int tabCount, double windowWidth)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var vm = new EditorPaneViewModel(new Settings(), new NullDialogService(), new AvaloniaUiServices());
        vm.SetProject(dir);
        for (var i = 1; i <= tabCount; i++)
        {
            var path = Path.Combine(dir, $"file{i:D2}.txt");
            await File.WriteAllTextAsync(path, $"content {i}").ConfigureAwait(true);
            (await vm.OpenFileAsync(path).ConfigureAwait(true)).IsSuccess.Should().BeTrue();
        }

        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = windowWidth, Height = 600, Content = pane });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();
        Dispatcher.UIThread.RunJobs();

        var panel = window.GetVisualDescendants().OfType<TabStripPanel>().Single();
        var leftButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TabScrollLeftButton");
        var rightButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TabScrollRightButton");

        return (vm, window, panel, leftButton, rightButton);
    }

    /// <summary>タブのコンテナ（ListBoxItem）が、TabStrip（タブ本体のビューポート）の
    /// 表示範囲内に完全に収まっているかどうかを、実際に描画された座標から判定する。</summary>
    private static bool IsWithinTabStripViewport(ListBoxItem item, Window window)
    {
        var tabStrip = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TabStrip");
        var left = item.TranslatePoint(new Point(0, 0), tabStrip)!.Value.X;
        var right = item.TranslatePoint(new Point(item.Bounds.Width, 0), tabStrip)!.Value.X;
        return left >= -0.5 && right <= tabStrip.Bounds.Width + 0.5;
    }
}
