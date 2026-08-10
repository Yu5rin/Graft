using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
/// 要望1（実機からの改善要望）: ペインの境界をマウスドラッグで調整できることの回帰防止テスト。
///
/// 実装当初、GridSplitterは配置しただけで実際にはドラッグが一切効かない不具合が2件あった。
/// 1つはGridSplitter（Thumb派生のTemplatedControl）にControlTemplateを与えておらず、
/// Background等のSetterだけでは何も描画されずヒットテスト対象も存在しなかった不具合
/// （PointerPressedすら発火しない）。もう1つは共有のGridSplitterテーマに既定のWidth="1"が
/// 置かれていたため、水平（行）方向のスプリッタでHorizontalAlignment="Stretch"を指定しても
/// 幅1pxの点として描画されていた不具合（Avaloniaでは明示的なWidthがStretchより優先されるため）。
/// いずれもheadlessテストでPointerPressed/DragDelta等のイベントを直接検証するまで
/// 気付けなかった（画面を見るだけでは背景色の境目としか見えず、実際に掴めるかは分からない）ため、
/// このテストではAvalonia.Headlessの疑似マウス操作（MouseMove/MouseDown/MouseUp）で
/// 実際にドラッグして寸法が変わることまで検証する（Controls.Layout.axamlのGridSplitterテーマ
/// 参照）。
/// </summary>
public class ShellWindowSplitterTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-splitter-tests", Guid.NewGuid().ToString("N"));

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        // 表示したShellWindowを後始末する（ShownWindowTracker参照。「再起動後の復元」を
        // 検証するテストはwindow1のみwindow1.Close()で閉じ、window2は閉じないまま終わって
        // いる。閉じ忘れると「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」がCIで
        // 不定期に出る）。
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

    /// <summary>実際のマウスと同じ「移動してから押す」順序で、複数ステップに分けてドラッグする。</summary>
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

    [AvaloniaFact(DisplayName = "サイドビューとエディタの境界をドラッグすると幅が変わる")]
    public void サイドビューの境界をドラッグすると幅が変わる()
    {
        var shell = BuildShell();
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var sideViewColumn = window.GetControl<Grid>("BodyGrid").ColumnDefinitions[1];
        var before = sideViewColumn.ActualWidth;

        var splitter = window.GetControl<GridSplitter>("SideViewSplitter");
        var from = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, 100), window) ?? default;
        Drag(window, from, from + new Vector(200, 0));

        sideViewColumn.ActualWidth.Should().BeApproximately(before + 200, 1,
            "境界を右へ200pxドラッグしたぶん、サイドビューの幅も広がるはず");
    }

    [AvaloniaFact(DisplayName = "サイドビューの境界は下限（180px）より狭く潰せない")]
    public void サイドビューの境界は最小幅で止まる()
    {
        var shell = BuildShell();
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var sideViewColumn = window.GetControl<Grid>("BodyGrid").ColumnDefinitions[1];
        var splitter = window.GetControl<GridSplitter>("SideViewSplitter");
        var from = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, 100), window) ?? default;

        // ウィンドウ外まで大きく左へドラッグしても、0まで潰れて二度と戻せなくなることはない。
        Drag(window, from, new Point(-500, from.Y));

        // ShellWindow.SideViewMinWidth（内部定数、180px）と一致させている。
        sideViewColumn.ActualWidth.Should().Be(180, "折りたたみ機能（幅0）とは別に、ドラッグでの下限が効く必要がある");
    }

    [AvaloniaFact(DisplayName = "接ぎ木パネルとの境界をドラッグすると高さが変わる")]
    public void 接ぎ木パネルの境界をドラッグすると高さが変わる()
    {
        var shell = BuildShell();
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        shell.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();

        var graftRow = window.GetControl<Grid>("EditorAreaGrid").RowDefinitions[2];
        var before = graftRow.ActualHeight;

        var splitter = window.GetControl<GridSplitter>("GraftSplitter");
        var from = splitter.TranslatePoint(new Point(200, splitter.Bounds.Height / 2), window) ?? default;
        Drag(window, from, from - new Vector(0, 100));

        graftRow.ActualHeight.Should().BeApproximately(before + 100, 1,
            "境界を上へ100pxドラッグしたぶん、接ぎ木パネルの高さも広がるはず");
    }

    [AvaloniaFact(DisplayName = "接ぎ木パネルの境界は下限（120px）より狭く潰せない")]
    public void 接ぎ木パネルの境界は最小高さで止まる()
    {
        var shell = BuildShell();
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        shell.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();

        var graftRow = window.GetControl<Grid>("EditorAreaGrid").RowDefinitions[2];
        var splitter = window.GetControl<GridSplitter>("GraftSplitter");
        var from = splitter.TranslatePoint(new Point(200, splitter.Bounds.Height / 2), window) ?? default;

        // ウィンドウ外まで大きく下へドラッグしても、ヘッダーだけの折りたたみ高さ（32px）まで
        // 潰れることはない（ドラッグの下限とヘッダー折りたたみは別の仕組みのため）。
        Drag(window, from, new Point(from.X, 2000));

        // ShellWindow.GraftPanelMinHeight（内部定数、120px）と一致させている。
        graftRow.ActualHeight.Should().Be(120, "ヘッダーだけの折りたたみ（32px）とは別に、ドラッグでの下限が効く必要がある");
    }

    [AvaloniaFact(DisplayName = "サイドビューを折りたたむとドラッグの下限に関わらず幅0まで畳める")]
    public void 折りたたみはドラッグの下限と衝突しない()
    {
        var shell = BuildShell();
        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var sideViewColumn = window.GetControl<Grid>("BodyGrid").ColumnDefinitions[1];

        // 既定はProjectビューが展開された状態のため、同じビューの再選択で折りたたまれる（9.2）。
        shell.SelectSideView(SideViewKind.Project);
        Dispatcher.UIThread.RunJobs();
        shell.IsSideViewCollapsed.Should().BeTrue();
        sideViewColumn.ActualWidth.Should().Be(0, "折りたたみ時はドラッグの下限（180px）を無視して幅0になる必要がある");

        shell.SelectSideView(SideViewKind.Project);
        Dispatcher.UIThread.RunJobs();
        shell.IsSideViewCollapsed.Should().BeFalse();
        sideViewColumn.ActualWidth.Should().Be(260, "折りたたみを解除すると既定幅（260px）へ戻る");
    }

    [AvaloniaFact(DisplayName = "ドラッグで調整した幅・高さはウィンドウを閉じて開き直すと復元される")]
    public void ドラッグで調整した寸法は再起動後も復元される()
    {
        // 1回目の起動: ドラッグで寸法を調整してから閉じる（OnClosingがlayout.jsonへ保存する）。
        var shell1 = BuildShell();
        var window1 = _windows.Track(new ShellWindow(shell1) { Width = 1280, Height = 800 });
        window1.Show();
        Dispatcher.UIThread.RunJobs();

        shell1.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();

        var sideSplitter = window1.GetControl<GridSplitter>("SideViewSplitter");
        var sideFrom = sideSplitter.TranslatePoint(new Point(sideSplitter.Bounds.Width / 2, 100), window1) ?? default;
        Drag(window1, sideFrom, sideFrom + new Vector(150, 0));

        var graftSplitter = window1.GetControl<GridSplitter>("GraftSplitter");
        var graftFrom = graftSplitter.TranslatePoint(new Point(200, graftSplitter.Bounds.Height / 2), window1) ?? default;
        Drag(window1, graftFrom, graftFrom - new Vector(0, 60));

        var expectedWidth = window1.GetControl<Grid>("BodyGrid").ColumnDefinitions[1].ActualWidth;
        var expectedHeight = window1.GetControl<Grid>("EditorAreaGrid").RowDefinitions[2].ActualHeight;

        window1.Close();

        // 2回目の起動: 同じ保存先(_baseDirectory)から読み込む、別のShellWindow/ShellViewModel。
        var shell2 = BuildShell();
        var window2 = _windows.Track(new ShellWindow(shell2) { Width = 1280, Height = 800 });
        window2.Show();
        Dispatcher.UIThread.RunJobs();

        shell2.IsGraftPanelOpen = true;
        Dispatcher.UIThread.RunJobs();

        var restoredWidth = window2.GetControl<Grid>("BodyGrid").ColumnDefinitions[1].ActualWidth;
        var restoredHeight = window2.GetControl<Grid>("EditorAreaGrid").RowDefinitions[2].ActualHeight;

        restoredWidth.Should().BeApproximately(expectedWidth, 1, "前回ドラッグで調整したサイドビュー幅が復元される必要がある");
        restoredHeight.Should().BeApproximately(expectedHeight, 1, "前回ドラッグで調整した接ぎ木パネル高さが復元される必要がある");
    }
}
