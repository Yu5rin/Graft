using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.UiTests.TestSupport;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// シェルウィンドウの構築・描画テスト（仕様書v2.1 附録A.7）。
/// 「主要画面が例外なく構築・描画できること」を保証し、
/// リソース解決の失敗やレイアウト崩れをここで検出する。
/// </summary>
public class ShellWindowTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        // 表示したShellWindowを後始末する（ShownWindowTracker参照。閉じ忘れると
        // 「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」がCIで不定期に出る）。
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "シェルウィンドウを例外なく構築して表示できる")]
    public void シェルウィンドウを構築できる()
    {
        var window = _windows.Track(new ShellWindow());
        window.Show();

        window.IsVisible.Should().BeTrue();
        window.Title.Should().Be("Graft");
    }

    [AvaloniaFact(DisplayName = "シェルウィンドウを実際に描画してフレームを取得できる")]
    public void シェルウィンドウを描画できる()
    {
        var window = _windows.Track(new ShellWindow { Width = 1000, Height = 700 });
        window.Show();

        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");
        frame!.PixelSize.Width.Should().BeGreaterThan(0);
    }

    [AvaloniaFact(DisplayName = "最小サイズは仕様どおり960x600である")]
    public void 最小サイズが仕様どおりである()
    {
        var window = _windows.Track(new ShellWindow());
        window.MinWidth.Should().Be(960);
        window.MinHeight.Should().Be(600);
    }

    // ------------------------------------------------------------------
    // 課題3: コマンドバーのボタン左詰め。
    //
    // 以前はColumnDefinitions="Auto,*,Auto"で、プロジェクト選択ドロップダウン（列0）と
    // ボタン群（列2＝右端）の間に列1の余白（*）が挟まっていた。ボタン群をドロップダウンの
    // すぐ右（列1）へ寄せ、余白は列2へ追い出し、「?」ショートカット一覧ボタンだけを
    // 列3として右端に残す構成（ColumnDefinitions="Auto,Auto,*,Auto"）にした。
    // Grid.Column値はXAMLパース時に確定するため、レイアウト（Measure/Arrange）を経ずに
    // 検証できる。
    //
    // 不具合5対応でボタン列（StackPanel）はScrollViewer（x:Name="ToolbarButtonsScroll"）に
    // 包まれたため、Grid.Columnを持つのはStackPanelそのものではなくScrollViewerになった。
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "課題3: プロジェクト選択は列0、操作ボタン群は列1（ドロップダウンのすぐ右）、ショートカット一覧は列3（右端）にある")]
    public void コマンドバーのボタンは左詰めでショートカットのみ右端にある()
    {
        var window = _windows.Track(new ShellWindow());
        window.Show();

        var projectCombo = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "プロジェクトを選択"));
        var toolbarScroll = window.GetControl<ScrollViewer>("ToolbarButtonsScroll");
        var shortcutsButton = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(AutomationProperties.GetName(b), "キーボードショートカット一覧を開く"));

        Grid.GetColumn(projectCombo).Should().Be(0, "プロジェクト選択ドロップダウンは常に左端に見える位置を維持する");
        Grid.GetColumn(toolbarScroll).Should().Be(
            1, "操作ボタン群を包むScrollViewerはプロジェクト選択のすぐ右（左詰め）へ寄せる");
        Grid.GetColumn(shortcutsButton).Should().Be(
            3, "ショートカット一覧はワークフローの操作ボタン群と性質が異なる補助機能のため、右端に独立させる");
    }

    [AvaloniaFact(DisplayName = "課題3: 最小幅まで狭めてもコマンドバーのボタンが重ならない")]
    public void 最小幅でもコマンドバーのボタンが重ならない()
    {
        var window = _windows.Track(new ShellWindow { Width = 960, Height = 600 });
        window.Show();
        window.Measure(new Avalonia.Size(960, 600));
        window.Arrange(new Avalonia.Rect(0, 0, 960, 600));
        Dispatcher.UIThread.RunJobs();

        // 接ぎ木パネル側にも同名（「適用を実行」）のボタンがあるため、コマンドバーの
        // Grid（x:Name="CommandBarGrid"）配下だけに絞って探す。
        var commandBar = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "CommandBarGrid");
        var buttons = commandBar.GetVisualDescendants().OfType<Button>()
            .Where(b => AutomationProperties.GetName(b) is
                "プロジェクトのファイル一覧とコンテキスト収集を開く" or "クリップボードのパッチを解析" or
                "現在の解析結果をパッチキューへ追加" or "パッチキューを開く" or "適用を実行" or
                "プロンプトテンプレートを選んでコピー" or "履歴ビューを開く" or "設定を開く" or
                "キーボードショートカット一覧を開く")
            .Select(b => (Name: AutomationProperties.GetName(b)!, Bounds: b.Bounds))
            .OrderBy(x => x.Bounds.Left)
            .ToList();

        buttons.Should().HaveCount(9, "コマンドバーの主要ボタン9個すべてが見つかる必要がある");

        // 最小幅960pxでも、幅0（測定されていない＝レイアウトから外れた）ボタンが無いこと、
        // かつX方向の範囲が互いに重ならないことを確認する（欠け・重なりの実機確認の裏付け）。
        // ワークフロー側の8個はScrollViewer（ToolbarButtonsScroll）の中身なので、
        // 収まりきらない分は横スクロールに回る＝Bounds自体はStackPanel内で自然な位置のまま
        // 保たれ、重ならずに並ぶことに変わりはない。
        foreach (var button in buttons)
        {
            button.Bounds.Width.Should().BeGreaterThan(0, $"「{button.Name}」の幅が0＝表示されていない可能性がある");
        }

        for (var i = 1; i < buttons.Count; i++)
        {
            buttons[i].Bounds.Left.Should().BeGreaterOrEqualTo(
                buttons[i - 1].Bounds.Right,
                $"「{buttons[i - 1].Name}」と「{buttons[i].Name}」が重なってはならない");
        }

        // ショートカット一覧（「?」）は右端に寄せた列にあるため、最も右側に描画されるはず。
        buttons[^1].Name.Should().Be("キーボードショートカット一覧を開く");
    }

    /// <summary>
    /// 実機検証で発見した不具合5: layout.jsonの極端な値からの復元でウィンドウが最小幅
    /// （960px）まで縮んだとき、コマンドバーのボタン列（幅Auto）がそのままだと右端の
    /// ボタン（設定など）が画面外へはみ出し、二度と押せなくなっていた。
    /// ScrollViewer（<see cref="ShellWindow.ToolbarButtonsScroll"/>）による横スクロール対応で、
    /// (1) ボタン列がウィンドウ幅を超えて描画されない（はみ出さない）こと、
    /// (2) 実際に横スクロールが必要な状態（Extent＞Viewport）になること、
    /// (3) 右端までスクロールすれば「設定」ボタンがウィンドウの表示範囲内に入る＝
    ///     実際に押せる位置に来ること、の3点を検証する。
    /// </summary>
    [AvaloniaFact(DisplayName = "最小幅960pxでもツールバーのボタンが画面外へはみ出さず、横スクロールで設定ボタンへ到達できる")]
    public void 最小幅でもツールバーが画面内に収まり設定ボタンへ到達できる()
    {
        var window = _windows.Track(new ShellWindow { Width = 960, Height = 600 });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scroll = window.GetControl<ScrollViewer>("ToolbarButtonsScroll");
        var settingsButton = window.GetControl<Button>("SettingsButton");

        // (1) ScrollViewer自身がウィンドウ幅を超えて配置されていないこと
        //     （不具合5そのもの: 以前はAuto幅の子がそのままウィンドウ外へあふれていた）。
        var scrollTopLeft = scroll.TranslatePoint(new Point(0, 0), window) ?? default;
        (scrollTopLeft.X + scroll.Bounds.Width).Should().BeLessThanOrEqualTo(
            window.Bounds.Width + 0.5, "ボタン列を包むScrollViewerがウィンドウ幅を超えてはならない");

        // (2) 横スクロールが必要な状態（Extent＞Viewport）なら、右端までスクロールしてから
        //     到達性を確認する。マージ後、MaxWidthはComboBox・ショートカットボタンの
        //     実測幅（Converters.ToolbarButtonsMaxWidth参照）から計算するようになり、
        //     以前の固定値420pxによる近似より正確になった。その結果、ヘッドレステスト
        //     環境の日本語ボタンラベルのフォント計量では、960px幅でも全ボタンが収まりきり
        //     横スクロール自体が発生しないことを実測で確認している（実機のフォントでは
        //     収まらない可能性があり、収まらない場合の到達性はこの後の分岐で検証する）。
        //     計算式そのものの正しさは ToolbarButtonsMaxWidthConverterTests
        //     （フォント計量に依存しない純粋な単体テスト）で担保する。
        if (scroll.Extent.Width > scroll.Viewport.Width + 0.5)
        {
            scroll.Offset = new Vector(scroll.Extent.Width - scroll.Viewport.Width, 0);
            Dispatcher.UIThread.RunJobs();
        }

        // (3) （横スクロールした場合はその後、不要だった場合はそのままの状態で）
        //     「設定」ボタンがウィンドウの表示範囲内に入り、実際に押せる位置にあること。
        var settingsTopLeft = settingsButton.TranslatePoint(new Point(0, 0), window) ?? default;
        settingsTopLeft.X.Should().BeGreaterThanOrEqualTo(-0.5, "設定ボタンの左端がウィンドウ内に入るはず");
        (settingsTopLeft.X + settingsButton.Bounds.Width).Should().BeLessThanOrEqualTo(
            window.Bounds.Width + 0.5, "設定ボタンの右端もウィンドウ内に収まるはず");
    }

    /// <summary>
    /// 上のテストは実機のフォント計量に依存するため、960px幅では横スクロール自体が
    /// 発生しないことがある（コメント参照）。このテストはMaxWidthバインディングの結果を
    /// 直接狭めて「収まりきらない状態」を確実に作り、横スクロールで実際に右端の
    /// ボタンへ到達できるという不具合5の中核メカニズムそのものを、フォント計量に
    /// 依存せず検証する。
    /// </summary>
    [AvaloniaFact(DisplayName = "コマンドバー: ボタン列が収まりきらない場合、右端までスクロールすれば設定ボタンに到達できる")]
    public void ボタン列が収まりきらない場合は右端までスクロールすれば設定ボタンに到達できる()
    {
        var window = _windows.Track(new ShellWindow { Width = 1280, Height = 800 });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scroll = window.GetControl<ScrollViewer>("ToolbarButtonsScroll");
        var settingsButton = window.GetControl<Button>("SettingsButton");

        // MaxWidthのバインド元（ComboBox・ショートカットボタンの実測幅）は変えず、
        // ScrollViewer自身に極端に狭いMaxWidthをローカルに設定して「収まりきらない状態」を
        // 強制的に再現する（バインディングのソース側は変化しないため、この上書きは
        // 後続のRunJobsで再度計算し直されて消えたりしない）。
        scroll.MaxWidth = 50;
        Dispatcher.UIThread.RunJobs();

        scroll.Extent.Width.Should().BeGreaterThan(
            scroll.Viewport.Width, "MaxWidthを極端に狭めたので横スクロールが有効になっているはず");

        scroll.Offset = new Vector(scroll.Extent.Width - scroll.Viewport.Width, 0);
        Dispatcher.UIThread.RunJobs();

        var settingsTopLeft = settingsButton.TranslatePoint(new Point(0, 0), scroll) ?? default;
        (settingsTopLeft.X + settingsButton.Bounds.Width).Should().BeLessThanOrEqualTo(
            scroll.Viewport.Width + 0.5, "右端までスクロールすれば設定ボタンがビューポート内に入るはず");
    }
}
