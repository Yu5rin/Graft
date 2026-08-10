using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests;

/// <summary>
/// 共通コントロールの見た目に関する要件（仕様書9.1、v2.0で利用者から指摘された不具合）を
/// 実際にレイアウトさせて検証する。
/// </summary>
public class ControlThemeTests : IDisposable
{
    // 各テストがWindowをShow()するが、以前はどれもClose()もShownWindowTrackerへの登録も
    // しないまま終わっていた（閉じ忘れの実例）。他のシナリオテストと同じ後始末に揃える。
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "セレクトボックスは一番長い項目と矢印が収まる幅を確保する")]
    public void セレクトボックスは最長項目の幅を確保する()
    {
        // v2.0で「セレクトボックスのVが文字とかぶる／文字が切れる」との指摘があったため、
        // 短い項目を選んでいても最長項目が収まる幅になることを確認する。
        var combo = new ComboBox
        {
            ItemsSource = new[] { "短", "非常に長い項目名をここに入れて幅の確保を確認する" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var window = _windows.Track(new Window { Width = 800, Height = 200, Content = combo });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var withShortSelected = combo.Bounds.Width;

        combo.SelectedIndex = 1;
        window.CaptureRenderedFrame().Should().NotBeNull();
        var withLongSelected = combo.Bounds.Width;

        withShortSelected.Should().BeApproximately(withLongSelected, 0.5,
            "どの項目を選んでいても最長項目が収まる幅である必要がある");
        withShortSelected.Should().BeGreaterThan(100, "最長項目と矢印が収まる幅が確保されている必要がある");
    }

    [AvaloniaFact(DisplayName = "タブ位置を左にすると見出しが本文の左側へ並ぶ")]
    public void タブ位置を左にすると左側へ並ぶ()
    {
        // 設定画面は TabStripPlacement="Left" を指定している。共通テーマがこれを無視すると
        // 見出しが上へ横並びになり、数が多いと折り返して2段になる（実機で発生した不具合）。
        var tabs = new TabControl
        {
            TabStripPlacement = Dock.Left,
            ItemsSource = new[]
            {
                new TabItem { Header = "一般", Content = new TextBlock { Text = "本文" } },
                new TabItem { Header = "エディタ", Content = new TextBlock { Text = "本文" } },
                new TabItem { Header = "プロンプトテンプレート", Content = new TextBlock { Text = "本文" } },
            },
        };
        var window = _windows.Track(new Window { Width = 800, Height = 400, Content = tabs });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var strip = tabs.GetVisualDescendants().OfType<ItemsPresenter>().First();
        var content = tabs.GetVisualDescendants().OfType<ContentPresenter>()
            .First(c => c.Name == "PART_SelectedContentHost");

        // 各要素のタブコントロール内での位置で比較する（見出しの右端 <= 本文の左端）。
        var stripLeft = strip.TranslatePoint(default, tabs)!.Value.X;
        var contentLeft = content.TranslatePoint(default, tabs)!.Value.X;
        contentLeft.Should().BeGreaterThanOrEqualTo(stripLeft + strip.Bounds.Width,
            "タブ見出しは本文の左側に並ぶ必要がある");
        strip.Bounds.Width.Should().BeLessThan(tabs.Bounds.Width / 2,
            "見出しの列が本文を押し出すほど広がってはならない");
    }

    [AvaloniaFact(DisplayName = "トグルボタンは既定テーマではなく自前のトークンで描かれる")]
    public void トグルボタンは自前のテーマが当たる()
    {
        // 定義が無いとAvaloniaのFluent既定テーマが当たり、9.1のトークン参照から外れて
        // 他のボタンと揃わない見た目になる（ライトテーマで灰色に塗られる不具合が出た）。
        // 自前テンプレートの目印である PART_FocusRing の有無で判定する。
        var toggle = new ToggleButton { Content = "折り返し" };
        var window = _windows.Track(new Window { Width = 400, Height = 200, Content = toggle });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var focusRing = toggle.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "PART_FocusRing");
        focusRing.Should().NotBeNull("自前のControlThemeが当たっている必要がある");

        var border = toggle.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Border");
        var unchecked_ = border.Background;

        toggle.IsChecked = true;
        window.CaptureRenderedFrame().Should().NotBeNull();
        border.Background.Should().NotBe(unchecked_, "オンとオフは見た目で区別できる必要がある");
    }

    [AvaloniaFact(DisplayName = "入力欄は透かし文字を表示し、入力があると消す")]
    public void 入力欄は透かし文字を表示する()
    {
        // 履歴の期間絞り込みは書式の例を透かし文字で示す。共通テーマが透かし文字を
        // 描画しないと、利用者は入力すべき書式を知る手がかりを失う。
        var box = new TextBox { Watermark = "2026-01-01", Width = 200 };
        var window = _windows.Track(new Window { Width = 400, Height = 200, Content = box });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var watermark = box.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Name == "PART_Watermark");
        watermark.Text.Should().Be("2026-01-01");
        watermark.IsVisible.Should().BeTrue("未入力のときは書式の例を示す必要がある");

        box.Text = "2026-05-01";
        window.CaptureRenderedFrame().Should().NotBeNull();
        watermark.IsVisible.Should().BeFalse("入力済みの文字と重なってはならない");
    }

    [AvaloniaFact(DisplayName = "複数行の入力欄はスクロールバーの指定が届き、内容を上から表示する")]
    public void 複数行の入力欄はスクロールできる()
    {
        // 初回起動ガイドのプロンプト見本は、枠より長い文章を固定の高さで表示する。
        // 共通テーマがスクロールバーの指定を無視して中央寄せのままだと、内容が
        // 上下均等に隠れて行の途中で切れて見える（実機で発生した不具合）。
        var box = new TextBox
        {
            AcceptsReturn = true,
            Height = 120,
            Text = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"{i}行目のテキスト")),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);

        var window = _windows.Track(new Window { Width = 400, Height = 300, Content = box });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var scrollViewer = box.GetVisualDescendants().OfType<ScrollViewer>()
            .First(s => s.Name == "PART_ScrollViewer");
        scrollViewer.VerticalScrollBarVisibility.Should().Be(ScrollBarVisibility.Auto,
            "利用側の指定がテンプレートへ届く必要がある");
        scrollViewer.Extent.Height.Should().BeGreaterThan(scrollViewer.Viewport.Height,
            "内容が枠より高い状態を作れている前提の確認");

        var presenter = box.GetVisualDescendants().OfType<TextPresenter>()
            .First(p => p.Name == "PART_TextPresenter");
        presenter.VerticalAlignment.Should().Be(VerticalAlignment.Top,
            "複数行では上寄せにしないと先頭の行が隠れる");
    }
}
