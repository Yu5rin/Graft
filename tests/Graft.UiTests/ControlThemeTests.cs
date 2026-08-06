using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.VisualTree;
using FluentAssertions;

namespace Graft.UiTests;

/// <summary>
/// 共通コントロールの見た目に関する要件（仕様書9.1、v2.0で利用者から指摘された不具合）を
/// 実際にレイアウトさせて検証する。
/// </summary>
public class ControlThemeTests
{
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
        var window = new Window { Width = 800, Height = 200, Content = combo };
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
        var window = new Window { Width = 800, Height = 400, Content = tabs };
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
}
