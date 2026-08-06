using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
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
}
