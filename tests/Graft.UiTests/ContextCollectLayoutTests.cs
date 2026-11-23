using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.UiTests.Vba;
using Graft.ViewModels;
using Graft.Views;
using static Graft.UiTests.TestSupport.CollectLayoutFixtures;

namespace Graft.UiTests;

/// <summary>
/// 「AIに渡すファイルを選ぶ」画面（コンテキスト収集の窓）の配置（設計案A）のテスト。
/// 見た目そのものは <see cref="CollectLayoutScreenshotTests"/> の画像で確かめ、ここでは、
/// 割り付けの切り替え（広い窓＝ファイル一覧の右、狭い窓＝「ブックの情報」タブ）・依頼の観点をたたむ操作・
/// 除外パターンの折りたたみ・左右の端の揃え・Tab キーの順・読み上げ名を、構造として確かめる。
/// </summary>
public class ContextCollectLayoutTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-collect-layout-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _windows.Dispose();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後片付けの失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    private static Settings NoPreview() => new() { ShowPreview = false };

    private async Task<(ShellHarness Harness, ContextCollectViewModel Context)> NormalAsync(Settings? settings = null)
    {
        var h = await ShellHarness.CreateAsync(settings ?? NoPreview()).ConfigureAwait(true);
        return (h, await CollectLayoutFixtures.NormalAsync(h).ConfigureAwait(true));
    }

    private async Task<(VbaP3Rig Rig, ContextCollectViewModel Context)> VbaAsync()
    {
        var rig = await VbaRigAsync(_root).ConfigureAwait(true);
        var context = rig.Graft.ContextCollect!;
        await context.InitializeAsync().ConfigureAwait(true);
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.HeaderRows).ConfigureAwait(true);
        return (rig, context);
    }

    private static TabItem Tab(ContextCollectWindow window, string header)
        => window.FindControl<TabControl>("MainTabs")!.Items.OfType<TabItem>().Single(t => Equals(t.Header, header));

    private static async Task ResizeAsync(ContextCollectWindow window, ContextCollectViewModel context, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        await SettleAsync(context).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // 窓の大きさ
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "配置: 窓の既定は 960×700、最小は 640×560")]
    public void 窓の大きさ()
    {
        var window = _windows.Track(new ContextCollectWindow());

        window.Width.Should().Be(960);
        window.Height.Should().Be(700);
        window.MinWidth.Should().Be(640);
        window.MinHeight.Should().Be(560);
    }

    // ------------------------------------------------------------------
    // 通常のプロジェクト（ブックの情報が無い）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "配置: 通常のプロジェクトでは、ブックの情報の枠もタブも出ず、ファイル一覧が全幅で、最小の窓でも 2 行以上見える")]
    public async Task 通常のプロジェクトはファイル一覧だけ()
    {
        var (h, context) = await NormalAsync().ConfigureAwait(true);
        using var _ = h;

        var window = await OpenAsync(_windows, context).ConfigureAwait(true);

        window.FindControl<ScrollViewer>("WorkbookColumn")!.IsVisible.Should().BeFalse("ブックの情報が無いので、列ごと出さない（余白だけが残らない）");
        Tab(window, "ブックの情報").IsVisible.Should().BeFalse("通常のプロジェクトには「ブックの情報」タブは出ない");
        window.FindControl<WrapPanel>("WorkbookSummaryRow")!.IsVisible.Should().BeFalse();
        window.FindControl<GridSplitter>("WorkbookSplitter")!.IsVisible.Should().BeFalse();
        var list = window.FindControl<ListBox>("FileListBox")!;
        list.Bounds.Width.Should().BeGreaterThan(window.Width - 80, "ファイル一覧は窓の全幅を使う（右の柱が無い）");
        VisibleFileRows(window).Should().BeGreaterThan(8, "960×700 の既定の窓で、ファイル一覧が約 9 行見える");

        await ResizeAsync(window, context, window.MinWidth, window.MinHeight).ConfigureAwait(true);
        VisibleFileRows(window).Should().BeGreaterThanOrEqualTo(2, "最小の窓でも、ファイル一覧が 2 行以上見える");
    }

    // ------------------------------------------------------------------
    // VBA プロジェクト: 広い窓／狭い窓の割り付け
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "配置: VBA の広い窓（既定 960px）では、ブックの情報（シート一覧・リボンの行）がファイル一覧の右に並ぶ")]
    public async Task 広い窓ではファイル一覧の右に並ぶ()
    {
        var (rig, context) = await VbaAsync().ConfigureAwait(true);
        using var _ = rig;

        var window = await OpenAsync(_windows, context).ConfigureAwait(true);

        var grid = window.FindControl<Grid>("FileSelectGrid")!;
        var column = window.FindControl<ScrollViewer>("WorkbookColumn")!;
        column.Parent.Should().BeSameAs(grid, "広い窓では、ファイル選択タブの中、ファイル一覧の右に置く");
        Grid.GetColumn(column).Should().Be(2);
        column.IsVisible.Should().BeTrue();
        Tab(window, "ブックの情報").IsVisible.Should().BeFalse("広い窓では、別のタブにしない");
        window.FindControl<WrapPanel>("WorkbookSummaryRow")!.IsVisible.Should().BeFalse("枠が右に見えているので、要約の 1 行は出さない");
        window.FindControl<GridSplitter>("WorkbookSplitter")!.IsVisible.Should().BeTrue("仕切りをドラッグして幅を変えられる");
        window.FindControl<Border>("WorkbookInfoRow")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<Border>("RibbonRow")!.IsEffectivelyVisible.Should().BeTrue("リボンの行も、ブックの情報と同じ列に収まる");
        column.Bounds.Width.Should().BeGreaterThanOrEqualTo(320);
        window.FindControl<ListBox>("FileListBox")!.Bounds.Width.Should().BeGreaterThanOrEqualTo(280);
        VisibleFileRows(window).Should().BeGreaterThan(8, "ブックの情報を右へ出しても、ファイル一覧は約 9 行見える（以前は 0 行）");
    }

    [AvaloniaFact(DisplayName = "配置: VBA の狭い窓（860px 未満）では、ブックの情報が「ブックの情報」タブへ移り、ファイル選択タブの先頭に値が渡る注意の 1 行が出る")]
    public async Task 狭い窓ではブックの情報タブへ移る()
    {
        var (rig, context) = await VbaAsync().ConfigureAwait(true);
        using var _ = rig;

        var window = await OpenAsync(_windows, context, 760, 720).ConfigureAwait(true);

        var column = window.FindControl<ScrollViewer>("WorkbookColumn")!;
        column.Parent.Should().BeSameAs(window.FindControl<Panel>("WorkbookTabHost"), "狭い窓では、タブの中へ親を付け替える");
        Tab(window, "ブックの情報").IsVisible.Should().BeTrue();
        var summary = window.FindControl<WrapPanel>("WorkbookSummaryRow")!;
        summary.IsVisible.Should().BeTrue("ファイル選択タブの先頭に、要約の 1 行を残す");
        summary.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain(
            t => t != null && t.Contains("1 枚のシートの値が AI へ渡ります"),
            "値が渡るシートがあるあいだは、ファイル選択タブを見ていても注意が出る（仕様 5.3）");
        window.FindControl<GridSplitter>("WorkbookSplitter")!.IsVisible.Should().BeFalse();

        // 「ブックの情報」タブを開くと、シート一覧もリボンの行も、枠いっぱいに見える。
        window.FindControl<TabControl>("MainTabs")!.SelectedItem = Tab(window, "ブックの情報");
        await SettleAsync(context).ConfigureAwait(true);
        window.FindControl<Border>("WorkbookInfoRow")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<Border>("RibbonRow")!.IsEffectivelyVisible.Should().BeTrue();
        column.Bounds.Width.Should().BeGreaterThan(window.Width - 80, "タブの中では全幅");
    }

    [AvaloniaFact(DisplayName = "配置: 窓の幅を変えて往復しても、ブックの情報の部品は 1 つのまま、親を付け替えるだけで、操作の状態も保たれる")]
    public async Task 幅の往復で部品は増えず親だけが替わる()
    {
        var (rig, context) = await VbaAsync().ConfigureAwait(true);
        using var _ = rig;

        var window = await OpenAsync(_windows, context).ConfigureAwait(true);
        var column = window.FindControl<ScrollViewer>("WorkbookColumn")!;
        var info = window.FindControl<Border>("WorkbookInfoRow")!;

        for (var i = 0; i < 2; i++)
        {
            await ResizeAsync(window, context, 760, 720).ConfigureAwait(true);
            column.Parent.Should().BeSameAs(window.FindControl<Panel>("WorkbookTabHost"));
            await ResizeAsync(window, context, 960, 700).ConfigureAwait(true);
            column.Parent.Should().BeSameAs(window.FindControl<Grid>("FileSelectGrid"));
        }

        window.FindControl<ScrollViewer>("WorkbookColumn").Should().BeSameAs(column, "同じ部品の親を付け替えるだけ（作り直さない）");
        window.FindControl<Border>("WorkbookInfoRow").Should().BeSameAs(info);
        window.GetVisualDescendants().OfType<Border>().Count(b => b.Name == "WorkbookInfoRow").Should().Be(1, "ブックの情報の枠は画面に 1 つだけ");
        window.GetVisualDescendants().OfType<Border>().Count(b => b.Name == "RibbonRow").Should().Be(1, "リボンの枠も 1 つだけ");
        // 見せ方のバインドは親を替えても生きている。
        context.WorkbookInfo.Sheets.Single(s => s.CodeName == "Sheet1").SelectedMode.Should().NotBeNull();
    }

    [AvaloniaFact(DisplayName = "配置: 「ブックの情報」タブを見ているときに窓を広げると、消えるタブの代わりにファイル選択タブへ戻る")]
    public async Task ブックタブを見ている間に広げるとファイル選択へ戻る()
    {
        var (rig, context) = await VbaAsync().ConfigureAwait(true);
        using var _ = rig;

        var window = await OpenAsync(_windows, context, 760, 720).ConfigureAwait(true);
        var tabs = window.FindControl<TabControl>("MainTabs")!;
        tabs.SelectedItem = Tab(window, "ブックの情報");
        await SettleAsync(context).ConfigureAwait(true);
        tabs.SelectedIndex.Should().Be(1, "前提: ブックの情報タブを見ている");

        await ResizeAsync(window, context, 1000, 700).ConfigureAwait(true);

        tabs.SelectedIndex.Should().Be(0, "消えたタブを選んだままにしない");
        Tab(window, "ブックの情報").IsVisible.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "配置: Ctrl+F の絞り込み欄への移動は、ブックの情報タブを見ているときもファイル選択タブへ戻って働く")]
    public async Task ブックタブからのCtrlFでファイル選択へ戻る()
    {
        var (rig, context) = await VbaAsync().ConfigureAwait(true);
        using var _ = rig;

        var window = await OpenAsync(_windows, context, 760, 720).ConfigureAwait(true);
        var tabs = window.FindControl<TabControl>("MainTabs")!;
        tabs.SelectedItem = Tab(window, "ブックの情報");
        await SettleAsync(context).ConfigureAwait(true);

        window.KeyPressQwerty(PhysicalKey.F, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        tabs.SelectedIndex.Should().Be(0);
        window.FindControl<TextBox>("FileFilterBox")!.IsFocused.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "配置: VBA の最小の窓でも、ファイル一覧が 2 行以上見える。広い窓で高さだけが足りないときは、ブックの情報の列が縦にスクロールする")]
    public async Task 最小の窓でもつぶれない()
    {
        var (rig, context) = await VbaAsync().ConfigureAwait(true);
        using var _ = rig;

        var window = await OpenAsync(_windows, context, 640, 560).ConfigureAwait(true);
        VisibleFileRows(window).Should().BeGreaterThanOrEqualTo(2);

        await ResizeAsync(window, context, 960, 560).ConfigureAwait(true);
        VisibleFileRows(window).Should().BeGreaterThanOrEqualTo(2);
        var column = window.FindControl<ScrollViewer>("WorkbookColumn")!;
        column.Extent.Height.Should().BeGreaterThan(column.Viewport.Height, "高さが足りないぶんは、列が縦にスクロールして見る");
    }

    // ------------------------------------------------------------------
    // 不具合 7: 幅の切り替えで、フォーカスと入力途中の値を失わない（1.2.2）
    // ------------------------------------------------------------------

    /// <summary>範囲の入力欄（行ごとに作られる。見せ方が「範囲を指定」でない行のものは隠れている）のうち、見えているもの。</summary>
    private static TextBox VisibleRangeBox(ContextCollectWindow window)
        => window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == "例 A1:F30" && t.IsEffectivelyVisible);

    private static void AssertFocusOnRangeBox(ContextCollectWindow window, VbaSheetViewRowViewModel row)
    {
        var focused = window.FocusManager!.GetFocusedElement().Should().BeOfType<TextBox>("フォーカスが入力欄に戻っている").Subject;
        focused.Watermark.Should().Be("例 A1:F30");
        focused.DataContext.Should().BeSameAs(row, "同じシートの行の入力欄");
        focused.IsEffectivelyVisible.Should().BeTrue("入力欄が窓の中に見えている");
        focused.Text.Should().Be("B2:");
    }

    [AvaloniaFact(DisplayName = "不具合 7: 広い窓で範囲欄に「B2:」と打っている途中に窓を狭めても、フォーカスは同じ入力欄に残り、「ブックの情報」タブが選ばれ、途中の値は確定されない（修正前はフォーカスが消え、誤りとして確定された）")]
    public async Task 入力中に狭めてもフォーカスと下書きが残る()
    {
        var (rig, context) = await VbaAsync().ConfigureAwait(true);
        using var _ = rig;
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.Range).ConfigureAwait(true);
        var window = await OpenAsync(_windows, context, 960, 720).ConfigureAwait(true);
        var box = VisibleRangeBox(window);
        var row = (VbaSheetViewRowViewModel)box.DataContext!;
        box.Focus();
        box.Text = "B2:";
        Dispatcher.UIThread.RunJobs();
        window.FocusManager!.GetFocusedElement().Should().BeSameAs(box, "前提: 入力欄にフォーカスがある");

        await ResizeAsync(window, context, 760, 720).ConfigureAwait(true);

        // 付け替えで行の部品は作り直される（元の入力欄は木に戻らない）。同じ行・同じ種類の入力欄にフォーカスが戻っていること。
        AssertFocusOnRangeBox(window, row);
        window.FindControl<TabControl>("MainTabs")!.SelectedItem.Should().BeSameAs(Tab(window, "ブックの情報"), "入力欄が見えるよう、「ブックの情報」タブを選ぶ");
        row.RangeDraft.Should().Be("B2:", "下書きのまま残る");
        row.HasRowsError.Should().BeFalse("入力途中の値を、誤りとして確定していない");
    }

    [AvaloniaFact(DisplayName = "不具合 7: 狭い窓の「ブックの情報」タブで入力している途中に窓を広げても、フォーカスは同じ入力欄に残り、途中の値は確定されない")]
    public async Task 入力中に広げてもフォーカスと下書きが残る()
    {
        var (rig, context) = await VbaAsync().ConfigureAwait(true);
        using var _ = rig;
        await context.SetSheetViewModeAsync("Sheet1", VbaSheetViewMode.Range).ConfigureAwait(true);
        var window = await OpenAsync(_windows, context, 760, 720).ConfigureAwait(true);
        window.FindControl<TabControl>("MainTabs")!.SelectedItem = Tab(window, "ブックの情報");
        await SettleAsync(context).ConfigureAwait(true);
        var box = VisibleRangeBox(window);
        var row = (VbaSheetViewRowViewModel)box.DataContext!;
        box.Focus();
        box.Text = "B2:";
        Dispatcher.UIThread.RunJobs();
        window.FocusManager!.GetFocusedElement().Should().BeSameAs(box, "前提: 入力欄にフォーカスがある");

        await ResizeAsync(window, context, 1000, 700).ConfigureAwait(true);

        AssertFocusOnRangeBox(window, row);
        window.FindControl<TabControl>("MainTabs")!.SelectedIndex.Should().Be(0, "ブックの情報の枠はファイル選択タブの右へ戻る");
        row.RangeDraft.Should().Be("B2:");
        row.HasRowsError.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "不具合 7: フォーカスが枠の外（ファイル一覧など）にあるときは、幅を切り替えてもフォーカスもタブも動かさない")]
    public async Task 枠の外のフォーカスは動かさない()
    {
        var (rig, context) = await VbaAsync().ConfigureAwait(true);
        using var _ = rig;
        var window = await OpenAsync(_windows, context).ConfigureAwait(true);
        var filter = window.FindControl<TextBox>("FileFilterBox")!;
        filter.Focus();
        Dispatcher.UIThread.RunJobs();

        await ResizeAsync(window, context, 760, 720).ConfigureAwait(true);

        window.FocusManager!.GetFocusedElement().Should().BeSameAs(filter);
        window.FindControl<TabControl>("MainTabs")!.SelectedIndex.Should().Be(0, "枠の外にいた利用者を、勝手に別のタブへ連れて行かない");
    }

    // ------------------------------------------------------------------
    // 左右の端の揃え（共通テーマの TabControl の flush クラス）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "配置: タブの中身の左右の端は、上の収集モード・下のボタンと同じ 16px に揃う（負の余白に頼らない）")]
    public async Task 左右の端が揃う()
    {
        var (h, context) = await NormalAsync().ConfigureAwait(true);
        using var _ = h;

        var window = await OpenAsync(_windows, context).ConfigureAwait(true);

        var grid = window.FindControl<Grid>("FileSelectGrid")!;
        var left = grid.TranslatePoint(new Point(0, 0), window)!.Value.X;
        var right = grid.TranslatePoint(new Point(grid.Bounds.Width, 0), window)!.Value.X;
        left.Should().BeApproximately(16, 0.5, "タブの中身の左端は 16px（収集モードの行・ボタンの行と同じ）");
        right.Should().BeApproximately(window.ClientSize.Width - 16, 0.5, "右端も 16px");
        grid.Margin.Left.Should().Be(0, "負の余白で打ち消していない");
        grid.Margin.Right.Should().Be(0);

        var mode = window.FindControl<ComboBox>("ModeComboBox")!;
        mode.TranslatePoint(new Point(0, 0), window)!.Value.X.Should().BeGreaterThan(16, "前提: 収集モードの行は 16px の余白の内側から始まる");
    }

    [AvaloniaFact(DisplayName = "テーマ: TabControl の中身の余白は既定 12px（設定の画面は従来どおり）で、flush クラスを付けたときだけ 0")]
    public void TabControlのflushクラス()
    {
        var normal = new TabControl { Items = { new TabItem { Header = "a", Content = new TextBlock { Text = "x" } } } };
        var flush = new TabControl { Classes = { "flush" }, Items = { new TabItem { Header = "a", Content = new TextBlock { Text = "x" } } } };
        var stack = new StackPanel { Children = { normal, flush } };
        _windows.Track(new Window { Content = stack, Width = 300, Height = 300 }).Show();
        Dispatcher.UIThread.RunJobs();

        PresenterPadding(normal).Should().Be(new Thickness(12), "既定は従来どおり 12px");
        PresenterPadding(flush).Should().Be(new Thickness(0), "flush クラスで 0");

        static Thickness PresenterPadding(TabControl tabs)
            => tabs.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_SelectedContentHost").Padding;
    }

    // ------------------------------------------------------------------
    // 状況の文
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "配置: 状況の文はボタンの上の全幅の行に出て、文が無いときは行ごと消える。読み上げには内容の変化が伝わる")]
    public async Task 状況の文は全幅の行()
    {
        var (h, context) = await NormalAsync(new Settings { ShowPreview = false, Context = new ContextSettings { TokenWarnThreshold = 5000 } }).ConfigureAwait(true);
        using var _ = h;

        var window = await OpenAsync(_windows, context).ConfigureAwait(true);

        var status = window.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == "状況");
        status.IsVisible.Should().BeTrue("前提: 目安を下げたので助言の文が出ている");
        status.Bounds.Width.Should().BeApproximately(window.ClientSize.Width - 32, 1, "全幅の行（ボタンの左の狭い列ではない）");
        status.MaxLines.Should().Be(3);
        AutomationProperties.GetLiveSetting(status).Should().Be(AutomationLiveSetting.Polite);
        var copy = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "コピー"));
        status.TranslatePoint(new Point(0, 0), window)!.Value.Y.Should().BeLessThan(
            copy.TranslatePoint(new Point(0, 0), window)!.Value.Y, "ボタンの行の上にある");

        // 推定トークン数（ボタンの行）と、状況の文の数字が一致する。
        var tokens = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text is { } x && x.StartsWith("推定トークン数"));
        tokens.Text.Should().Contain($"約{context.EstimatedTokens}トークン");
        status.Text.Should().Contain($"約{context.EstimatedTokens}トークン");
        tokens.Classes.Should().Contain("warn", "目安を超えたら数字を警告色にする（上の文でも文で伝える）");

        // 文を消すと行ごと消える（「出さない」まで巡らせて、目安以下にする）。
        foreach (var file in context.Files.Where(f => !f.IsDirectory).ToArray())
        {
            context.CycleStateCommand.Execute(file);
            context.CycleStateCommand.Execute(file);
        }

        Dispatcher.UIThread.RunJobs();
        context.ExceedsWarnThreshold.Should().BeFalse("前提: すべて「出さない」にしたので目安以下");
        status.IsVisible.Should().BeFalse("文が無いときは行ごと消える");
        tokens.Classes.Should().NotContain("warn");
    }

    // ------------------------------------------------------------------
    // 追加の除外パターン（ファイル一覧の下の折りたたみ）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "除外パターン: 初期状態は常に閉じ、件数は見出しに常に出る。読み上げ名にも件数が入る。追加すると件数が増える")]
    public async Task 除外パターンの折りたたみ()
    {
        var (h, context) = await NormalAsync().ConfigureAwait(true);
        using var _ = h;
        context.NewExcludePattern = "*.log";
        await ShellHarness.ExecuteAsync(context.AddExcludeCommand).ConfigureAwait(true);

        var window = await OpenAsync(_windows, context).ConfigureAwait(true);

        var expander = window.FindControl<Expander>("ExcludesExpander")!;
        expander.IsExpanded.Should().BeFalse("登録があっても、初期状態は常に閉じる");
        HeaderCount(expander).Should().Be("1 件", "閉じていても件数が見出しに出る");
        AutomationProperties.GetName(expander).Should().Be("追加の除外パターン（プロジェクト単位）1 件");

        expander.IsExpanded = true;
        context.NewExcludePattern = "lib/";
        await ShellHarness.ExecuteAsync(context.AddExcludeCommand).ConfigureAwait(true);
        await SettleAsync(context).ConfigureAwait(true);

        HeaderCount(expander).Should().Be("2 件");
        AutomationProperties.GetName(expander).Should().Be("追加の除外パターン（プロジェクト単位）2 件");
        var input = expander.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "追加する除外パターン");
        input.Watermark.Should().Contain("node_modules");
        expander.GetVisualDescendants().OfType<Button>().Count(b => Equals(b.Content, "削除")).Should().Be(2, "登録済みの除外パターンの行と「削除」");

        // 開いた状態は記憶しない: 窓を開き直すと、また閉じている。
        window.Close();
        var again = await OpenAsync(_windows, context).ConfigureAwait(true);
        again.FindControl<Expander>("ExcludesExpander")!.IsExpanded.Should().BeFalse("開いた状態は記憶しない");

        static string HeaderCount(Expander e)
            => e.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Single(t => t != null && t.EndsWith(" 件"))!;
    }

    // ------------------------------------------------------------------
    // 依頼の観点のたたむボタン
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "観点: 既定は開いた状態（全チップが最初から並ぶ）。たたむボタンを押すと 1 行の要約になり、選択は変わらず、もう一度押すと戻る")]
    public async Task 観点をたたむ()
    {
        var (h, context) = await NormalAsync().ConfigureAwait(true);
        using var _ = h;
        var window = await OpenAsync(_windows, context).ConfigureAwait(true);
        var panel = window.FindControl<PromptFocusPanel>("FocusPanel")!;
        var button = panel.FindControl<Button>("CollapseButton")!;
        var list = panel.GetVisualDescendants().OfType<ItemsControl>().First(i => AutomationProperties.GetName(i) == "依頼の観点の一覧");

        panel.IsCollapsed.Should().BeFalse("既定は必ず開いた状態");
        button.IsVisible.Should().BeTrue("収集の窓ではたたむボタンが出る");
        list.IsVisible.Should().BeTrue();
        panel.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Count().Should().Be(14, "全チップが最初から並ぶ");
        AutomationProperties.GetName(button).Should().Be("依頼の観点の一覧をたたむ");
        var selectedBefore = context.Focus.SelectedIds.ToArray();
        selectedBefore.Should().HaveCount(3);

        button.Command?.Execute(null);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        panel.IsCollapsed.Should().BeTrue();
        list.IsVisible.Should().BeFalse("たたむとチップは隠れる");
        AutomationProperties.GetName(button).Should().Be("依頼の観点の一覧を開く", "読み上げ名は、いま押すと起きることに合わせて変わる");
        var summary = panel.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == "選んでいる依頼の観点");
        summary.IsVisible.Should().BeTrue();
        summary.Text.Should().Be("読みやすく・不具合を直す・速く", "選んでいる観点の名前が 1 行で見え続ける");
        context.Focus.SelectedIds.Should().Equal(selectedBefore, "たたんでも選択は変わらない");
        panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("依頼の観点 3");
        panel.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "すべて解除")).IsVisible.Should().BeTrue("たたんでいても「すべて解除」は使える");

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        panel.IsCollapsed.Should().BeFalse();
        list.IsVisible.Should().BeTrue("もう一度押すと、すべてのチップが並んだ状態に戻る");
        AutomationProperties.GetName(button).Should().Be("依頼の観点の一覧をたたむ");
    }

    [AvaloniaFact(DisplayName = "観点: たたんだ状態は記憶しない（窓を開き直すと開いている）。選んでいないときの要約は「選んでいません」")]
    public async Task 観点のたたんだ状態は記憶しない()
    {
        var (h, context) = await NormalAsync().ConfigureAwait(true);
        using var _ = h;
        var window = await OpenAsync(_windows, context).ConfigureAwait(true);
        window.FindControl<PromptFocusPanel>("FocusPanel")!.IsCollapsed = true;
        window.Close();

        var again = await OpenAsync(_windows, context).ConfigureAwait(true);
        again.FindControl<PromptFocusPanel>("FocusPanel")!.IsCollapsed.Should().BeFalse("たたんだ状態は記憶しないので、いつも開いた状態から始まる");

        context.Focus.ClearAllCommand.Execute(null);
        context.Focus.SelectedSummary.Should().Be("選んでいません");
    }

    [AvaloniaFact(DisplayName = "観点: 依頼文コピーのドロップダウンと同じ部品でも、既定の置き場ではたたむボタンは出ない（見た目は従来どおり）")]
    public void ドロップダウンの置き場ではたたむボタンが出ない()
    {
        var panel = new PromptFocusPanel { DataContext = new PromptFocusSelectionViewModel(Graft.Features.PromptFocusStore.BuiltIns, true, Array.Empty<string>()) };
        _windows.Track(new Window { Content = panel, Width = 420, Height = 360 }).Show();
        Dispatcher.UIThread.RunJobs();

        panel.IsCollapsible.Should().BeFalse();
        panel.FindControl<Button>("CollapseButton")!.IsVisible.Should().BeFalse();
        panel.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Should().HaveCount(17, "全チップは並んだまま");
    }

    [AvaloniaFact(DisplayName = "観点: 広い窓では見出しのかたまりが横へ流れて行数が減る（通常 2 行・VBA 3 行）。狭い置き場では従来どおり縦に積まれる")]
    public async Task 観点は横へ流れる()
    {
        var (h, context) = await NormalAsync().ConfigureAwait(true);
        using var _ = h;
        var window = await OpenAsync(_windows, context).ConfigureAwait(true);
        var panel = window.FindControl<PromptFocusPanel>("FocusPanel")!;

        ChipRows(panel).Should().BeLessOrEqualTo(2, "960px の窓で、通常のプロジェクトは 2 行（以前は 4 行）");

        // 狭い置き場（幅 420）では、かたまりが 1 行に 1 つしか入らない（従来どおりの縦積み）。
        var narrow = new PromptFocusPanel { DataContext = new PromptFocusSelectionViewModel(Graft.Features.PromptFocusStore.BuiltIns, false, Array.Empty<string>()) };
        _windows.Track(new Window { Content = narrow, Width = 420, Height = 360 }).Show();
        Dispatcher.UIThread.RunJobs();
        ChipRows(narrow).Should().BeGreaterThanOrEqualTo(4, "狭い置き場では、見出しごとに縦へ積まれる");

        static int ChipRows(PromptFocusPanel p)
            => p.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
                .Select(c => Math.Round(c.TranslatePoint(new Point(0, 0), p)!.Value.Y)).Distinct().Count();
    }

    // ------------------------------------------------------------------
    // Tab キーの順
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "キー操作: Tab の順は画面の上から下・左から右（収集モード → たたむ → チップ → すべて解除 → タブ → 絞り込み → git → ファイル一覧 → 除外パターン → ブックの情報 → ボタン）")]
    public async Task Tabの順()
    {
        var (rig, context) = await VbaAsync().ConfigureAwait(true);
        using var _ = rig;
        Select(context.Focus, "focus-readable");
        var window = await OpenAsync(_windows, context).ConfigureAwait(true);

        var order = WalkTab(window, 80);

        // 画面の目印を、出てきた順に確かめる（間に別の部品が挟まってよい）。
        // 「gitの変更ファイルだけ」は、この試験のプロジェクトがリポジトリではなく押せない（Tab が飛ばす）ので、目印に入れない。
        // ファイル一覧は、一覧そのものではなく、その中の行（ListBoxItem）にフォーカスが届く（NameOf が「ファイル一覧の行」と呼ぶ）。
        var landmarks = new[]
        {
            "収集モード",
            "依頼の観点の一覧をたたむ",
            "読みやすく（選択中）",
            "依頼の観点をすべて解除",
            "ファイル選択タブ",
            "ファイル名で一覧を絞り込む",
            "ファイル一覧の行",
            "追加の除外パターン（プロジェクト単位）0 件",
            "ブックの情報（内容も出す）",
            "推定トークン数をプレビュー",
            "コンテキストをクリップボードへコピー",
            "コンテキストをファイルへ保存",
            "ファイルを選ぶ画面を閉じる",
        };
        var positions = landmarks.Select(l => order.IndexOf(l)).ToArray();
        positions.Should().OnlyContain(p => p >= 0, $"Tab で順に届く部品の名前: {string.Join(" / ", order)}");
        positions.Should().BeInAscendingOrder($"上から下・左から右の順。実際の順: {string.Join(" / ", order)}");

        // 依頼の観点のチップは、ほかの部品より前に並ぶ（TabIndex を一部にだけ付けると最後へ回っていた）。
        var chipPos = order.IndexOf("読みやすく（選択中）");
        chipPos.Should().BeLessThan(order.IndexOf("ファイル名で一覧を絞り込む"));
    }

    [AvaloniaFact(DisplayName = "キー操作: 観点のたたむボタンは Enter・Space で、除外パターンの見出しは Space・Enter で、キーボードだけで開閉できる")]
    public async Task たたむ_開くのキー操作()
    {
        var (h, context) = await NormalAsync().ConfigureAwait(true);
        using var _ = h;
        var window = await OpenAsync(_windows, context).ConfigureAwait(true);
        var panel = window.FindControl<PromptFocusPanel>("FocusPanel")!;
        var button = panel.FindControl<Button>("CollapseButton")!;
        var expander = window.FindControl<Expander>("ExcludesExpander")!;
        var toggle = expander.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First();

        // 見出しのボタンにも読み上げ名が付く（Expander の名前を引き継ぐ。名前が空だと、Tab で届いても何か分からない）。
        AutomationProperties.GetName(toggle).Should().Be("追加の除外パターン（プロジェクト単位）0 件");

        button.Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        panel.IsCollapsed.Should().BeTrue("Enter でたためる");
        button.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None); // ボタンの Space は、キーを離したときに押したことになる
        Dispatcher.UIThread.RunJobs();
        panel.IsCollapsed.Should().BeFalse("Space で開ける");

        toggle.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        expander.IsExpanded.Should().BeTrue("見出しに Tab で届き、Space で開く");
        toggle.Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        expander.IsExpanded.Should().BeFalse("Enter で閉じる");
    }

    /// <summary>収集モードから Tab を押し続けて、フォーカスが届いた部品の名前を順に集める（一周したら止める）。</summary>
    private static List<string> WalkTab(ContextCollectWindow window, int max)
    {
        var mode = window.FindControl<ComboBox>("ModeComboBox")!;
        mode.Focus();
        Dispatcher.UIThread.RunJobs();
        var handler = ((IInputRoot)window).KeyboardNavigationHandler!;
        var names = new List<string>();
        var current = (IInputElement)mode;
        for (var i = 0; i < max; i++)
        {
            names.Add(NameOf(current));
            handler.Move(current, NavigationDirection.Next);
            Dispatcher.UIThread.RunJobs();
            var next = window.FocusManager!.GetFocusedElement();
            if (next is null || ReferenceEquals(next, mode)) break;
            current = next;
        }

        return names;
    }

    private static string NameOf(IInputElement element)
    {
        if (element is not Control c) return element.GetType().Name;
        var name = AutomationProperties.GetName(c);
        if (!string.IsNullOrEmpty(name)) return name;
        // 名前の無い部品（TabItem の見出しなど）は、見出し・内容の文字で代用する。
        return c switch
        {
            TabItem t => $"{t.Header}タブ",
            ListBoxItem => "ファイル一覧の行",
            ContentControl cc when cc.Content is string s => s,
            _ => c.GetType().Name,
        };
    }
}
