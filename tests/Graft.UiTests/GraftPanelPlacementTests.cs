using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
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
/// 利用者からの改善要望: 接ぎ木パネルをコードの下だけでなくコードの右（3列）にも配置できる
/// ようにする機能の回帰テスト。ShellViewModel側の状態遷移（配置の切替・折りたたみとの両立）と、
/// ShellWindow側のGrid付け替え・ドラッグ調整・layout.jsonへの保存/復元（後方互換を含む）を検証する。
/// </summary>
public class GraftPanelPlacementTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-placement-tests", Guid.NewGuid().ToString("N"));

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        // 表示したShellWindowを後始末する（ShownWindowTracker参照。「再起動後の復元」を
        // 検証するテストは1つ目のウィンドウはwindow1.Close()で明示的に閉じるが、2つ目
        // （window2）は閉じないまま終わっており、閉じ忘れると
        // 「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」がCIで不定期に出る）。
        _windows.Dispose();

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

    private ShellViewModel BuildShell()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();
        IDialogService dialogs = new NullDialogService();
        IUiServices ui = new AvaloniaUiServices();
        return StartupCoordinator.BuildShellViewModel(
            appPaths, new Settings(), new SettingsStore(appPaths), new PatchQueue(appPaths),
            new ProjectStore(appPaths), new RevisionStore(appPaths), new RevisionRestorer(appPaths),
            dialogs, ui, openSettings: () => { });
    }

    /// <summary>
    /// window.Show()直後のOnLoaded（ShellWindow.axaml.cs）は内部でGraft.InitializeAsync()の
    /// 完了を待ってからApplyLayoutToWindow（layout.jsonの内容をViewModel・Gridへ反映する箇所。
    /// 本テストが検証したい配置復元の中心）を呼ぶ。InitializeAsync自体は実ファイルI/Oを含む
    /// 非同期処理のため、Dispatcher.UIThread.RunJobs()を1回呼ぶだけでは（他の多くの既存テストでは
    /// たまたま間に合っていても）完了前に後続のアサーションへ進んでしまうことがある
    /// （実測: RunJobsを5回呼んでもまだ完了していないケースを確認済み）。
    /// 待ち合わせ自体はTestSupport.ShellWindowLoadWaiterへ一本化した
    /// （ShellWindowSplitterTestsも同じヘルパを使う。並行実装を増やさないため）。
    /// </summary>
    private static void WaitForWindowLoaded(ShellWindow window) => ShellWindowLoadWaiter.WaitForLayoutApplied(window);

    /// <summary>実際のマウスと同じ「移動してから押す」順序で、複数ステップに分けてドラッグする（ShellWindowSplitterTestsと同じ手法）。</summary>
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
            window.MouseMove(point);
            Dispatcher.UIThread.RunJobs();
        }
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    // ===================== ViewModelの状態遷移 =====================

    [Fact(DisplayName = "既定の配置は下（Bottom）である")]
    public void 既定の配置は下である()
    {
        var shell = BuildShell();
        shell.GraftPanelPlacement.Should().Be(GraftPanelPlacementKind.Bottom);
        shell.IsGraftPanelPlacementRight.Should().BeFalse();
    }

    [Fact(DisplayName = "ToggleGraftPanelPlacementCommandを実行するたびに下と右が交互に切り替わる")]
    public void トグルコマンドで下と右が交互に切り替わる()
    {
        var shell = BuildShell();

        shell.ToggleGraftPanelPlacementCommand.Execute(null);
        shell.GraftPanelPlacement.Should().Be(GraftPanelPlacementKind.Right);
        shell.IsGraftPanelPlacementRight.Should().BeTrue();

        shell.ToggleGraftPanelPlacementCommand.Execute(null);
        shell.GraftPanelPlacement.Should().Be(GraftPanelPlacementKind.Bottom);
        shell.IsGraftPanelPlacementRight.Should().BeFalse();
    }

    /// <summary>
    /// 利用者からの指摘対応: 配置切替ボタンのアイコンは「今の状態」ではなく「押すとどうなるか
    /// （次の状態）」を示すのが一般的な作法。以前は下配置のときにpanel-bottom（現在の状態）が
    /// 見えていたが、下配置のときはpanel-right（押すと右へ移る）、右配置のときはpanel-bottom
    /// （押すと下へ移る）が見えるべきという回帰テスト。GraftPanel.axaml側はジオメトリ自体を
    /// 変更せず、IsVisibleの表示条件だけを入れ替えて対応した。
    /// </summary>
    [AvaloniaFact(DisplayName = "配置切替アイコンは現在の状態ではなく押した後の配置を表す")]
    public void 配置切替アイコンは次の配置を示す()
    {
        var shell = BuildShell();
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        WaitForWindowLoaded(window);

        var toggleButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == "接ぎ木パネルの配置切り替え（下／右）");
        var icons = toggleButton.GetVisualDescendants().OfType<IconGlyph>().ToList();
        icons.Should().HaveCount(2);

        var rightGeometry = (Geometry)toggleButton.FindResource("IconPanelRightGeometry")!;
        var bottomGeometry = (Geometry)toggleButton.FindResource("IconPanelBottomGeometry")!;

        // 既定（下配置）: 押すと右へ移るので、次の配置を表すpanel-rightアイコンが見えているべき。
        icons.Single(i => i.IsVisible).Data.Should().BeSameAs(rightGeometry,
            "下配置のときは、押すと右へ移ることを示すpanel-rightアイコンを表示する必要がある");

        shell.ToggleGraftPanelPlacementCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // 右配置: 押すと下へ戻るので、次の配置を表すpanel-bottomアイコンが見えているべき。
        icons.Single(i => i.IsVisible).Data.Should().BeSameAs(bottomGeometry,
            "右配置のときは、押すと下へ戻ることを示すpanel-bottomアイコンを表示する必要がある");
    }

    [Theory(DisplayName = "ParseGraftPanelPlacement/ToGraftPanelPlacementValueは往復変換できる")]
    [InlineData(GraftPanelPlacementKind.Bottom, "bottom")]
    [InlineData(GraftPanelPlacementKind.Right, "right")]
    public void 配置の文字列変換は往復できる(GraftPanelPlacementKind kind, string text)
    {
        ShellViewModel.ToGraftPanelPlacementValue(kind).Should().Be(text);
        ShellViewModel.ParseGraftPanelPlacement(text).Should().Be(kind);
    }

    [Theory(DisplayName = "未知の値・null（後方互換）は下配置として扱う")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bottom")]
    [InlineData("RIGHT")] // 大文字小文字違いも未知の値として下配置に倒れる（現行実装の仕様）。
    [InlineData("center")]
    public void 未知の値は下配置として扱う(string? value)
    {
        ShellViewModel.ParseGraftPanelPlacement(value).Should().Be(GraftPanelPlacementKind.Bottom);
    }

    // ===================== ShellWindow側のGrid付け替え =====================

    [AvaloniaFact(DisplayName = "展開中に右配置へ切り替えるとGraftPanelは列2・行0へ移動し、下配置用の行は畳まれる")]
    public void 右配置に切り替えるとGraftPanelが列へ移動する()
    {
        var shell = BuildShell();
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        WaitForWindowLoaded(window);

        var editorGrid = window.GetControl<Grid>("EditorAreaGrid");
        var graftPanel = window.GetControl<GraftPanel>("GraftPanelControl");

        // 既定（下配置）: 行2・列0。
        Grid.GetRow(graftPanel).Should().Be(2);
        Grid.GetColumn(graftPanel).Should().Be(0);

        // 指摘2対応: 折りたたみ中の切替は下配置と同じ見た目（行2・列0）のまま据え置く
        // 仕様（右配置での折りたたみはヘッダー帯を下部に表示する）のため、この検証では
        // 展開してから配置を切り替える。
        shell.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();

        shell.ToggleGraftPanelPlacementCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // 右配置: 行0・列2。同じインスタンス（graftPanel）のまま位置だけが変わる。
        Grid.GetRow(graftPanel).Should().Be(0);
        Grid.GetColumn(graftPanel).Should().Be(2);

        // 下配置用の行（[1]スプリッタ・[2]パネル）は使わないため高さ0へ畳まれる。
        editorGrid.RowDefinitions[1].Height.Value.Should().Be(0);
        editorGrid.RowDefinitions[2].Height.Value.Should().Be(0);

        window.GetControl<GridSplitter>("GraftSplitter").IsVisible.Should().BeFalse("右配置中は下配置用スプリッタを隠す必要がある");
    }

    [AvaloniaFact(DisplayName = "右配置は3列（サイドバー・エディタ・接ぎ木パネル）として表示され、ブロック一覧が右パネルに描画される")]
    public void 右配置は3列として描画される()
    {
        var shell = BuildShell();
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        WaitForWindowLoaded(window);

        shell.ToggleGraftPanelPlacementCommand.Execute(null);
        shell.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();

        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("右配置でも破綻せず描画できる必要がある");

        var editorGrid = window.GetControl<Grid>("EditorAreaGrid");
        editorGrid.ColumnDefinitions[2].ActualWidth.Should().BeGreaterThan(0, "右配置では接ぎ木パネルの列が実際に幅を持つ必要がある");

        // BlockListBoxはGraftPanel（別のUserControl）自身のNameScopeに属するため、
        // window.GetControl では見つからない。GraftPanel.axaml.csが公開するListBoxElement経由で参照する。
        var graftPanel = window.GetControl<GraftPanel>("GraftPanelControl");
        graftPanel.ListBoxElement.IsEffectivelyVisible.Should().BeTrue("右配置でもブロック一覧（パッチ解析結果）が表示される必要がある");
    }

    [AvaloniaFact(DisplayName = "下配置に戻すとGraftPanelは行2・列0へ戻り、右配置用の列は畳まれる")]
    public void 下配置へ戻すとGraftPanelが行へ戻る()
    {
        var shell = BuildShell();
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        WaitForWindowLoaded(window);

        var editorGrid = window.GetControl<Grid>("EditorAreaGrid");
        var graftPanel = window.GetControl<GraftPanel>("GraftPanelControl");

        shell.ToggleGraftPanelPlacementCommand.Execute(null); // → 右
        Dispatcher.UIThread.RunJobs();
        shell.ToggleGraftPanelPlacementCommand.Execute(null); // → 下
        Dispatcher.UIThread.RunJobs();

        Grid.GetRow(graftPanel).Should().Be(2);
        Grid.GetColumn(graftPanel).Should().Be(0);
        editorGrid.ColumnDefinitions[1].Width.Value.Should().Be(0);
        editorGrid.ColumnDefinitions[2].Width.Value.Should().Be(0);
        window.GetControl<GridSplitter>("GraftSplitterRight").IsVisible.Should().BeFalse("下配置中は右配置用スプリッタを隠す必要がある");
    }

    // ===================== ドラッグ調整（右配置の垂直スプリッタ） =====================

    [AvaloniaFact(DisplayName = "右配置では垂直スプリッタのドラッグで接ぎ木パネルの幅が変わる")]
    public void 右配置の垂直スプリッタをドラッグすると幅が変わる()
    {
        var shell = BuildShell();
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        WaitForWindowLoaded(window);

        shell.ToggleGraftPanelPlacementCommand.Execute(null);
        shell.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();

        var graftColumn = window.GetControl<Grid>("EditorAreaGrid").ColumnDefinitions[2];
        var before = graftColumn.ActualWidth;

        var splitter = window.GetControl<GridSplitter>("GraftSplitterRight");
        splitter.IsVisible.Should().BeTrue();
        var from = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, 200), window) ?? default;
        Drag(window, from, from - new Vector(80, 0));

        graftColumn.ActualWidth.Should().BeApproximately(before + 80, 1,
            "境界を左へ80pxドラッグしたぶん、接ぎ木パネル（右配置）の幅も広がるはず");
    }

    [AvaloniaFact(DisplayName = "右配置の垂直スプリッタは下限（420px）より狭く潰せない")]
    public void 右配置の垂直スプリッタは最小幅で止まる()
    {
        var shell = BuildShell();
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        WaitForWindowLoaded(window);

        shell.ToggleGraftPanelPlacementCommand.Execute(null);
        shell.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();

        var graftColumn = window.GetControl<Grid>("EditorAreaGrid").ColumnDefinitions[2];
        var splitter = window.GetControl<GridSplitter>("GraftSplitterRight");
        var from = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, 200), window) ?? default;

        // ウィンドウ外まで大きく右へドラッグしても、0まで潰れて戻せなくなることはない。
        Drag(window, from, new Point(2000, from.Y));

        // ShellWindow.GraftPanelMinWidth（内部定数、420px。実機検証で「適用」ボタンまで
        // 収まる最小幅として確認済み）と一致させている。
        graftColumn.ActualWidth.Should().Be(420, "ドラッグでの下限（420px）が効く必要がある");
    }

    // ===================== 折りたたみとの両立 =====================

    [AvaloniaFact(DisplayName = "右配置での折りたたみは下配置と同じ32pxのヘッダー帯を下部に表示し、展開すると右配置へ戻る")]
    public void 右配置での折りたたみはヘッダー帯を下部に表示する()
    {
        // 利用者からの指摘2対応: 右配置で折りたたむと幅0まで完全に消えて掴む対象を失い、
        // Ctrl+Jか配置切替ボタンの存在を知らないと二度と展開できなかった。下配置の折りたたみと
        // 同じ32pxのヘッダー帯を画面下部に表示し、配置の設定値（Right）自体は保持したまま、
        // 展開すると右配置へ戻ることを確認する。
        var shell = BuildShell();
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        WaitForWindowLoaded(window);

        shell.ToggleGraftPanelPlacementCommand.Execute(null);
        shell.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();

        var editorGrid = window.GetControl<Grid>("EditorAreaGrid");
        var graftPanel = window.GetControl<GraftPanel>("GraftPanelControl");
        var graftColumn = editorGrid.ColumnDefinitions[2];
        graftColumn.ActualWidth.Should().BeGreaterThan(0);

        shell.IsGraftPanelOpen = false;
        Dispatcher.UIThread.RunJobs();

        // 配置の設定値は変わらず右配置のまま（アイコン表示・次回展開先の判断基準）。
        shell.GraftPanelPlacement.Should().Be(GraftPanelPlacementKind.Right, "折りたたんでも配置の設定値（右）は保持する必要がある");
        shell.IsGraftPanelPlacementRight.Should().BeTrue();

        // 見た目は下配置の折りたたみと同じ（列は幅0、パネルは行2・列0、行の高さは32pxのヘッダーのみ）。
        graftColumn.ActualWidth.Should().Be(0, "折りたたみ中は列側の幅を占有しない");
        Grid.GetRow(graftPanel).Should().Be(2, "折りたたみ中は下配置と同じ行へ一時的に移す");
        Grid.GetColumn(graftPanel).Should().Be(0);
        editorGrid.RowDefinitions[2].ActualHeight.Should().Be(32, "折りたたみ中は下配置と同じ32pxのヘッダー帯を下部に表示する必要がある");
        window.GetControl<GridSplitter>("GraftSplitterRight").IsVisible.Should().BeFalse("折りたたみ中は右配置用の垂直スプリッタも隠す");

        shell.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();

        // 展開すると右配置（列2）へ戻る。配置の設定値を保持していたことがここで効いてくる。
        Grid.GetRow(graftPanel).Should().Be(0);
        Grid.GetColumn(graftPanel).Should().Be(2);
        graftColumn.ActualWidth.Should().BeApproximately(460, 1, "展開すると右配置・既定幅（460px）へ戻る必要がある");
    }

    // ===================== layout.jsonへの保存・復元（後方互換を含む） =====================

    [AvaloniaFact(DisplayName = "右配置のまま閉じて開き直すと、配置と調整した幅が復元される")]
    public void 右配置と幅は再起動後も復元される()
    {
        var shell1 = BuildShell();
        var window1 = _windows.Track(new ShellWindow(shell1) { Width = 1280, Height = 800 });
        window1.Show();
        WaitForWindowLoaded(window1);

        shell1.ToggleGraftPanelPlacementCommand.Execute(null);
        shell1.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();

        var splitter = window1.GetControl<GridSplitter>("GraftSplitterRight");
        var from = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, 200), window1) ?? default;
        Drag(window1, from, from - new Vector(60, 0));

        var expectedWidth = window1.GetControl<Grid>("EditorAreaGrid").ColumnDefinitions[2].ActualWidth;

        window1.Close();

        var shell2 = BuildShell();
        var window2 = _windows.Track(new ShellWindow(shell2) { Width = 1280, Height = 800 });
        window2.Show();
        WaitForWindowLoaded(window2);

        shell2.GraftPanelPlacement.Should().Be(GraftPanelPlacementKind.Right, "前回右配置にしたまま閉じたので、再起動後も右配置のはず");

        // 開閉状態（IsGraftPanelOpen）はGraft.State（パッチ解析結果の有無）に連動する値で
        // layout.jsonへは保存しない（配置・寸法のみが保存対象）ため、新しいセッションでは
        // 明示的に開いてから寸法を確認する（ShellWindowSplitterTestsの復元テストと同じ流儀）。
        shell2.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();

        var graftPanel2 = window2.GetControl<GraftPanel>("GraftPanelControl");
        Grid.GetColumn(graftPanel2).Should().Be(2);

        var restoredWidth = window2.GetControl<Grid>("EditorAreaGrid").ColumnDefinitions[2].ActualWidth;
        restoredWidth.Should().BeApproximately(expectedWidth, 1, "前回ドラッグで調整した接ぎ木パネル幅（右配置）が復元される必要がある");
    }

    [AvaloniaFact(DisplayName = "右配置かつ折りたたんだまま閉じて開き直すと、下部にヘッダー帯が出て展開すると右へ戻る")]
    public void 右配置かつ折りたたみは再起動後もヘッダー帯として復元される()
    {
        // 完了条件3・実機確認6対応: 「右配置かつ折りたたみ」で終了した場合、再起動直後は
        // 開閉状態（IsGraftPanelOpen）自体はlayout.jsonへ保存されない値のため既定の折りたたみで
        // 始まるが、配置（GraftPanelPlacement=Right）はlayout.jsonから復元されるため、
        // 起動直後から下部に32pxのヘッダー帯が表示され、展開すると右配置へ戻ることを確認する。
        var shell1 = BuildShell();
        var window1 = _windows.Track(new ShellWindow(shell1) { Width = 1280, Height = 800 });
        window1.Show();
        WaitForWindowLoaded(window1);

        shell1.ToggleGraftPanelPlacementCommand.Execute(null); // → 右
        shell1.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();
        shell1.IsGraftPanelOpen = false; // → 右配置のまま折りたたむ
        Dispatcher.UIThread.RunJobs();

        window1.Close();

        var shell2 = BuildShell();
        var window2 = _windows.Track(new ShellWindow(shell2) { Width = 1280, Height = 800 });
        window2.Show();
        WaitForWindowLoaded(window2);

        shell2.GraftPanelPlacement.Should().Be(GraftPanelPlacementKind.Right, "右配置のまま閉じたので再起動後も右配置のはず");
        shell2.IsGraftPanelOpen.Should().BeFalse("開閉状態は保存対象ではないため、既定どおり折りたたまれた状態で始まる");

        var editorGrid2 = window2.GetControl<Grid>("EditorAreaGrid");
        var graftPanel2 = window2.GetControl<GraftPanel>("GraftPanelControl");

        // 復元直後（右配置・折りたたみ）は下部に32pxのヘッダー帯として表示される。
        Grid.GetRow(graftPanel2).Should().Be(2);
        Grid.GetColumn(graftPanel2).Should().Be(0);
        editorGrid2.RowDefinitions[2].ActualHeight.Should().Be(32, "復元直後も右配置の折りたたみは下部の32pxヘッダー帯になる必要がある");
        editorGrid2.ColumnDefinitions[2].ActualWidth.Should().Be(0);

        shell2.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();

        // 展開すると配置の設定値（右）どおり列2へ戻る。
        Grid.GetRow(graftPanel2).Should().Be(0);
        Grid.GetColumn(graftPanel2).Should().Be(2);
        editorGrid2.ColumnDefinitions[2].ActualWidth.Should().BeGreaterThan(0);
    }

    [AvaloniaFact(DisplayName = "下配置に戻して閉じて開き直すと、下配置が復元される")]
    public void 下配置へ戻すと再起動後も下配置になる()
    {
        var shell1 = BuildShell();
        var window1 = _windows.Track(new ShellWindow(shell1) { Width = 1280, Height = 800 });
        window1.Show();
        WaitForWindowLoaded(window1);

        shell1.ToggleGraftPanelPlacementCommand.Execute(null); // → 右
        Dispatcher.UIThread.RunJobs();
        shell1.ToggleGraftPanelPlacementCommand.Execute(null); // → 下（既定へ戻す）
        Dispatcher.UIThread.RunJobs();

        window1.Close();

        var shell2 = BuildShell();
        var window2 = _windows.Track(new ShellWindow(shell2) { Width = 1280, Height = 800 });
        window2.Show();
        WaitForWindowLoaded(window2);

        shell2.GraftPanelPlacement.Should().Be(GraftPanelPlacementKind.Bottom);
        var graftPanel2 = window2.GetControl<GraftPanel>("GraftPanelControl");
        Grid.GetRow(graftPanel2).Should().Be(2);
        Grid.GetColumn(graftPanel2).Should().Be(0);
    }

    [AvaloniaFact(DisplayName = "後方互換: GraftPanelPlacementキーの無い既存layout.jsonを読み込んでも下配置になる")]
    public async System.Threading.Tasks.Task 新キーの無いlayoutJsonは下配置になる()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        // このタスクで追加する前のlayout.json相当（GraftPanelPlacement/GraftPanelWidthを含まない）。
        var layoutPath = Path.Combine(_baseDirectory, "layout.json");
        await File.WriteAllTextAsync(layoutPath, """
            {
              "width": 1280,
              "height": 800,
              "isMaximized": false,
              "projectPaneWidths": {
                "_default": {
                  "sideViewWidth": 260,
                  "graftPanelHeight": 300
                }
              }
            }
            """);

        IDialogService dialogs = new NullDialogService();
        IUiServices ui = new AvaloniaUiServices();
        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths, new Settings(), new SettingsStore(appPaths), new PatchQueue(appPaths),
            new ProjectStore(appPaths), new RevisionStore(appPaths), new RevisionRestorer(appPaths),
            dialogs, ui, openSettings: () => { });
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        WaitForWindowLoaded(window);

        shell.GraftPanelPlacement.Should().Be(GraftPanelPlacementKind.Bottom, "新キーの無い既存layout.jsonは下配置として復元される必要がある");

        // 下配置時の既存の値（graftPanelHeight）もそのまま生きていることを確認する
        // （新機能の追加が既存の保存値を壊していないことの回帰チェック）。
        shell.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();
        var graftRow = window.GetControl<Grid>("EditorAreaGrid").RowDefinitions[2];
        graftRow.ActualHeight.Should().BeApproximately(300, 1);
    }
}
