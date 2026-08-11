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
    // すぐ右（列1）へ寄せ、余白は列2へ追い出し、「?」ヘルプメニューボタンだけを
    // 列3として右端に残す構成（ColumnDefinitions="Auto,Auto,*,Auto"）にした。
    // Grid.Column値はXAMLパース時に確定するため、レイアウト（Measure/Arrange）を経ずに
    // 検証できる。
    //
    // 不具合5対応でボタン列（StackPanel）はScrollViewer（x:Name="ToolbarButtonsScroll"）に
    // 包まれたため、Grid.Columnを持つのはStackPanelそのものではなくScrollViewerになった。
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "課題3: プロジェクト選択は列0、操作ボタン群は列1（ドロップダウンのすぐ右）、設定・ショートカット一覧の右端グループは列3にある")]
    public void コマンドバーのボタンは左詰めで設定とショートカットの右端グループのみ右端にある()
    {
        var window = _windows.Track(new ShellWindow());
        window.Show();

        var projectCombo = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => Equals(AutomationProperties.GetName(c), "プロジェクトを選択"));
        var toolbarScroll = window.GetControl<ScrollViewer>("ToolbarButtonsScroll");
        // 「設定」「?」は列3のStackPanel（CommandBarRightGroup）の子であり、Grid.Column
        // 添付プロパティはGridの直接の子にしか効かないため、判定はGridの直接の子である
        // このStackPanel側で行う（各ボタン自身のGrid.GetColumnは既定値の0を返してしまい、
        // 誤って「列0にある」と誤認しかねない）。
        var rightGroup = window.GetControl<StackPanel>("CommandBarRightGroup");

        Grid.GetColumn(projectCombo).Should().Be(0, "プロジェクト選択ドロップダウンは常に左端に見える位置を維持する");
        Grid.GetColumn(toolbarScroll).Should().Be(
            1, "操作ボタン群を包むScrollViewerはプロジェクト選択のすぐ右（左詰め）へ寄せる");
        Grid.GetColumn(rightGroup).Should().Be(
            3, "設定・ヘルプメニューはワークフローの操作ボタン群と性質が異なる補助機能のため、右端に独立させる");
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
        // Control.Boundsは直接の親要素基準の座標であり、ワークフロー側（StackPanel＞
        // ScrollViewer＞Grid）と右端グループ（StackPanel＞Grid）とでは親の入れ子段数が
        // 異なるため、生のBounds.Leftどうしは単純比較できない（設定・?ボタンは
        // 「右端固定グループ」対応でStackPanelの子になった）。TranslatePointで
        // commandBar基準の座標へ揃えてから比較する。
        var buttons = commandBar.GetVisualDescendants().OfType<Button>()
            .Where(b => AutomationProperties.GetName(b) is
                "プロジェクトのファイル一覧とコンテキスト収集を開く" or "クリップボードのパッチを解析" or
                "現在の解析結果をパッチキューへ追加" or "パッチキューを開く" or "適用を実行" or
                "プロンプトテンプレートを選んでコピー" or "履歴ビューを開く" or "設定を開く" or
                "ヘルプメニューを開く")
            .Select(b => (
                Name: AutomationProperties.GetName(b)!,
                Bounds: new Rect(b.TranslatePoint(new Point(0, 0), commandBar) ?? default, b.Bounds.Size)))
            .OrderBy(x => x.Bounds.Left)
            .ToList();

        buttons.Should().HaveCount(9, "コマンドバーの主要ボタン9個すべてが見つかる必要がある");

        // 最小幅960pxでも、幅0（測定されていない＝レイアウトから外れた）ボタンが無いこと、
        // かつX方向の範囲が互いに重ならないことを確認する（欠け・重なりの実機確認の裏付け）。
        // ワークフロー側の7個（ファイル〜履歴）はScrollViewer（ToolbarButtonsScroll）の
        // 中身なので、収まりきらない分は横スクロールに回る＝Bounds自体はStackPanel内で
        // 自然な位置のまま保たれ、重ならずに並ぶことに変わりはない。「設定」「?」の2個は
        // 列3の右端固定グループの中身で、常にウィンドウ内に収まる。
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

        // ヘルプメニュー（「?」）は右端に寄せた列にあるため、最も右側に描画されるはず。
        buttons[^1].Name.Should().Be("ヘルプメニューを開く");
    }

    /// <summary>
    /// 実機検証で発見した不具合5: layout.jsonの極端な値からの復元でウィンドウが最小幅
    /// （960px）まで縮んだとき、コマンドバーのボタン列（幅Auto）がそのままだと右端の
    /// ボタン（履歴など）が画面外へはみ出し、二度と押せなくなっていた。
    /// ScrollViewer（<see cref="ShellWindow.ToolbarButtonsScroll"/>）による横スクロール対応で、
    /// (1) ボタン列がウィンドウ幅を超えて描画されない（はみ出さない）こと、
    /// (2) 実際に横スクロールが必要な状態（Extent＞Viewport）になること、
    /// (3) 右端までスクロールすれば「履歴」ボタンがウィンドウの表示範囲内に入る＝
    ///     実際に押せる位置に来ること、の3点を検証する。
    /// 「設定は右端に」対応で設定ボタンは列3の固定グループ（列1のScrollViewerの外）へ
    /// 移り横スクロールの対象ではなくなったため、ここではワークフロー側（列1）の
    /// 末尾に残った「履歴」ボタンを到達性確認の対象にする。
    /// </summary>
    [AvaloniaFact(DisplayName = "最小幅960pxでもツールバーのボタンが画面外へはみ出さず、横スクロールで履歴ボタンへ到達できる")]
    public void 最小幅でもツールバーが画面内に収まり履歴ボタンへ到達できる()
    {
        var window = _windows.Track(new ShellWindow { Width = 960, Height = 600 });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scroll = window.GetControl<ScrollViewer>("ToolbarButtonsScroll");
        var historyButton = window.GetControl<Button>("HistoryButton");

        // (1) ScrollViewer自身がウィンドウ幅を超えて配置されていないこと
        //     （不具合5そのもの: 以前はAuto幅の子がそのままウィンドウ外へあふれていた）。
        var scrollTopLeft = scroll.TranslatePoint(new Point(0, 0), window) ?? default;
        (scrollTopLeft.X + scroll.Bounds.Width).Should().BeLessThanOrEqualTo(
            window.Bounds.Width + 0.5, "ボタン列を包むScrollViewerがウィンドウ幅を超えてはならない");

        // (2) 横スクロールが必要な状態（Extent＞Viewport）なら、右端までスクロールしてから
        //     到達性を確認する。マージ後、MaxWidthはComboBox・設定ボタン・ショートカット
        //     ボタンの実測幅（Converters.ToolbarButtonsMaxWidth参照）から計算するように
        //     なり、以前の固定値420pxによる近似より正確になった。その結果、ヘッドレス
        //     テスト環境の日本語ボタンラベルのフォント計量では、960px幅でも全ボタンが
        //     収まりきり横スクロール自体が発生しないことを実測で確認している（実機の
        //     フォントでは収まらない可能性があり、収まらない場合の到達性はこの後の
        //     分岐で検証する）。計算式そのものの正しさは
        //     ToolbarButtonsMaxWidthConverterTests（フォント計量に依存しない純粋な
        //     単体テスト）で担保する。
        if (scroll.Extent.Width > scroll.Viewport.Width + 0.5)
        {
            scroll.Offset = new Vector(scroll.Extent.Width - scroll.Viewport.Width, 0);
            Dispatcher.UIThread.RunJobs();
        }

        // (3) （横スクロールした場合はその後、不要だった場合はそのままの状態で）
        //     「履歴」ボタンがウィンドウの表示範囲内に入り、実際に押せる位置にあること。
        var historyTopLeft = historyButton.TranslatePoint(new Point(0, 0), window) ?? default;
        historyTopLeft.X.Should().BeGreaterThanOrEqualTo(-0.5, "履歴ボタンの左端がウィンドウ内に入るはず");
        (historyTopLeft.X + historyButton.Bounds.Width).Should().BeLessThanOrEqualTo(
            window.Bounds.Width + 0.5, "履歴ボタンの右端もウィンドウ内に収まるはず");
    }

    /// <summary>
    /// 上のテストは実機のフォント計量に依存するため、960px幅では横スクロール自体が
    /// 発生しないことがある（コメント参照）。このテストはMaxWidthバインディングの結果を
    /// 直接狭めて「収まりきらない状態」を確実に作り、横スクロールで実際に右端の
    /// ボタンへ到達できるという不具合5の中核メカニズムそのものを、フォント計量に
    /// 依存せず検証する。
    /// </summary>
    [AvaloniaFact(DisplayName = "コマンドバー: ボタン列が収まりきらない場合、右端までスクロールすれば履歴ボタンに到達できる")]
    public void ボタン列が収まりきらない場合は右端までスクロールすれば履歴ボタンに到達できる()
    {
        var window = _windows.Track(new ShellWindow { Width = 1280, Height = 800 });
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scroll = window.GetControl<ScrollViewer>("ToolbarButtonsScroll");
        var historyButton = window.GetControl<Button>("HistoryButton");

        // MaxWidthのバインド元（ComboBox・設定ボタン・ショートカットボタンの実測幅）は
        // 変えず、ScrollViewer自身に極端に狭いMaxWidthをローカルに設定して「収まりきらない
        // 状態」を強制的に再現する（バインディングのソース側は変化しないため、この上書きは
        // 後続のRunJobsで再度計算し直されて消えたりしない）。
        scroll.MaxWidth = 50;
        Dispatcher.UIThread.RunJobs();

        scroll.Extent.Width.Should().BeGreaterThan(
            scroll.Viewport.Width, "MaxWidthを極端に狭めたので横スクロールが有効になっているはず");

        scroll.Offset = new Vector(scroll.Extent.Width - scroll.Viewport.Width, 0);
        Dispatcher.UIThread.RunJobs();

        var historyTopLeft = historyButton.TranslatePoint(new Point(0, 0), scroll) ?? default;
        (historyTopLeft.X + historyButton.Bounds.Width).Should().BeLessThanOrEqualTo(
            scroll.Viewport.Width + 0.5, "右端までスクロールすれば履歴ボタンがビューポート内に入るはず");
    }

    /// <summary>
    /// 「設定は右端に」対応の回帰テスト: 利用者からの指摘（一般的なソフトでは設定は
    /// 右側にある）で設定ボタンをワークフロー順のボタン群（列1）から、ヘルプメニュー
    /// （「?」）と同じ右端固定グループ（列3）へ移した。将来また左のワークフロー側へ
    /// 戻ってしまったら気付けるように、Grid.Column値と親要素（右端グループ）の両方で
    /// 判定する。「履歴」はこの対応で移していない（「戻す」操作の入口であるため、
    /// 参照系の右端グループではなくワークフロー側に残す）ことも合わせて確認する。
    /// </summary>
    [AvaloniaFact(DisplayName = "設定ボタンは右端グループ（列3、ヘルプメニューと同じ）にあり、ワークフロー側のボタン列には含まれない")]
    public void 設定ボタンは右端グループにありワークフロー側のボタン列には含まれない()
    {
        var window = _windows.Track(new ShellWindow());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var rightGroup = window.GetControl<StackPanel>("CommandBarRightGroup");
        var settingsButton = window.GetControl<Button>("SettingsButton");
        var shortcutsButton = window.GetControl<Button>("ShortcutsButton");
        var historyButton = window.GetControl<Button>("HistoryButton");
        var toolbarScroll = window.GetControl<ScrollViewer>("ToolbarButtonsScroll");

        Grid.GetColumn(rightGroup).Should().Be(3, "設定・ヘルプメニューの右端固定グループは列3にある");

        rightGroup.GetVisualChildren().Should().Contain(settingsButton,
            "設定ボタンは右端グループの直接の子であるはず（列1のワークフロー側からここへ移した）");
        rightGroup.GetVisualChildren().Should().Contain(shortcutsButton,
            "ヘルプメニューボタンも同じ右端グループの直接の子であるはず");

        // 将来また設定ボタンがワークフロー側（列1のScrollViewer配下）へ戻ってしまったら
        // 気付けるように、明示的にスクロール領域の子孫でないことも確認する。
        toolbarScroll.GetVisualDescendants().Should().NotContain(settingsButton,
            "設定ボタンはワークフローの操作ボタン列（横スクロールする列1）には含めない");

        // 「履歴」は移していない（依頼の要件）。ワークフロー側のScrollViewer配下に
        // 残っていることを確認する。
        toolbarScroll.GetVisualDescendants().Should().Contain(historyButton,
            "履歴ボタンは「戻す」操作の入口でもあるため、ワークフロー側（列1）に残すはず");
    }
}
