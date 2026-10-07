using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Themes;
using Graft.UiTests.TestSupport;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 利用者からの指摘対応: 「チェック済みのチェックボックスにマウスを乗せると、チェックして
/// いないように見える」。スクリーンショットでは箱の枠が紫（Accent）、中が暗い無地で、
/// チェックマークが見えなかった。
///
/// 【原因】Controls.Input.axamlのCheckBoxのControlThemeで、チェック済みは「箱（PART_Box）を
/// Accentで塗り、チェックマーク（PART_CheckMark）を背景色BgSurfaceで抜いて描く」作法に
/// なっている。ところが <c>^:checked</c> と <c>^:pointerover</c> は詳細度が同じで、同じ
/// PART_BoxのBackgroundを奪い合う。同じ詳細度のスタイルは後に書かれた方が勝つため、
/// 後ろにあった <c>^:pointerover</c>（BgHover）が勝ち、箱の塗りがAccentからBgHoverへ
/// 戻っていた。BorderBrush（枠線）は <c>^:pointerover</c> が触らないのでAccentのまま残る。
/// その結果、BgSurface色のチェックマークがBgHoverの上に描かれる。Darkテーマでは
/// BgSurfaceとBgHover（#2B2F35）の色が近く、チェックマークがほぼ見えなくなる。
///
/// 【対応】<c>^:pointerover</c> の後ろに <c>^:checked:pointerover</c> を足し、チェック済みの
/// ときはホバー中もAccentの塗りを保つ（宣言順が意味を持つので、必ず後ろに置く）。
/// ホバー用のAccentHoverのようなトークンは9テーマのどこにも無く、新設すると9テーマ全部に
/// 色を足してコントラスト比の実測もやり直しになるため作らず、「チェック済みのホバー中は
/// 塗りを変えない」と割り切っている。
///
/// このテストは実際にマウスを乗せた状態（Avalonia.HeadlessのMouseMove）で、箱の塗りと
/// チェックマークの表示を検証する回帰テスト。修正を外すと1番目のテストが失敗する
/// （箱の塗りがBgHoverになる）ことを確認済み。
/// </summary>
public class CheckBoxHoverAppearanceTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// CheckBoxを1個だけ載せたウィンドウを開き、マウスを乗せる前の状態にして返す。
    /// 箱だけを検証したいので、ShellWindowは使わず最小の構成にする
    /// （ShellWindowは重く、本テストの主題と無関係なAvaloniaEditを内包するため）。
    /// </summary>
    private (Window Window, CheckBox CheckBox, Border Box, IconGlyph CheckMark) Open(AppTheme theme, bool isChecked)
    {
        ThemeManager.SetTheme(theme);

        var checkBox = new CheckBox
        {
            Content = "サンプル",
            IsChecked = isChecked,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(20),
        };
        var window = _windows.Track(new Window { Width = 240, Height = 120, Content = checkBox });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var box = checkBox.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Box");
        var checkMark = checkBox.GetVisualDescendants().OfType<IconGlyph>().Single(g => g.Name == "PART_CheckMark");
        return (window, checkBox, box, checkMark);
    }

    /// <summary>マウスをチェックボックスの箱の上へ移動し、ホバー状態になったことを確かめる。</summary>
    private static void Hover(Window window, CheckBox checkBox, Border box)
    {
        // いったんウィンドウの隅（チェックボックスの外）へ置いてから乗せる。最初から乗っている
        // 状態でMouseMoveしても、PointerEnteredが発火しない可能性を避けるため。
        window.MouseMove(new Point(235, 115));
        window.CaptureRenderedFrame().Should().NotBeNull();
        checkBox.IsPointerOver.Should().BeFalse("前提: 外へ置いた時点ではホバーしていない");

        var center = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), window);
        center.Should().NotBeNull("箱がウィンドウ内に配置されていること（レイアウト確定後）");
        window.MouseMove(center!.Value);
        window.CaptureRenderedFrame().Should().NotBeNull();
        checkBox.IsPointerOver.Should().BeTrue("前提: 箱の上へ乗せたのでホバー状態になっている");
    }

    private static Color ResolveColor(string brushKey)
    {
        Application.Current!.TryFindResource(brushKey, null, out var value).Should().BeTrue($"'{brushKey}'が解決できる必要がある");
        return ((ISolidColorBrush)value!).Color;
    }

    private static Color BackgroundOf(Border box) => ((ISolidColorBrush)box.Background!).Color;

    [AvaloniaTheory(DisplayName = "チェック済みのチェックボックスは、マウスを乗せても箱の塗りがAccentのままでチェックマークが見える")]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void チェック済みでホバー中は箱の塗りがAccentのまま(AppTheme theme)
    {
        var (window, checkBox, box, checkMark) = Open(theme, isChecked: true);

        Hover(window, checkBox, box);

        BackgroundOf(box).Should().Be(ResolveColor("Accent"),
            "チェック済みはホバー中も箱をAccentで塗る。BgHoverへ戻るとBgSurface色のチェックマークが" +
            "ほぼ見えなくなり、チェックしていないように見える（今回の不具合）");
        checkMark.IsVisible.Should().BeTrue("チェック済みならホバー中もチェックマークは表示される");
        ((ISolidColorBrush)checkMark.Stroke!).Color.Should().Be(ResolveColor("BgSurface"),
            "チェックマークは背景色（BgSurface）で抜く作法のまま。塗りがAccentなら読める");
    }

    [AvaloniaTheory(DisplayName = "未チェックのチェックボックスは、マウスを乗せると箱の塗りがBgHoverになる（既存の挙動を壊していない）")]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void 未チェックでホバー中は箱の塗りがBgHover(AppTheme theme)
    {
        var (window, checkBox, box, checkMark) = Open(theme, isChecked: false);

        Hover(window, checkBox, box);

        BackgroundOf(box).Should().Be(ResolveColor("BgHover"), "未チェックのホバーは従来どおり箱をBgHoverで塗る");
        checkMark.IsVisible.Should().BeFalse("未チェックならチェックマークは出ない");
    }

    [AvaloniaTheory(DisplayName = "チェック済みでホバーしていないチェックボックスは、箱の塗りがAccentである")]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void チェック済みでホバーなしは箱の塗りがAccent(AppTheme theme)
    {
        var (window, checkBox, box, checkMark) = Open(theme, isChecked: true);

        // マウスはチェックボックスの外に置く（ホバーしていない状態を明示する）。
        window.MouseMove(new Point(235, 115));
        window.CaptureRenderedFrame().Should().NotBeNull();
        checkBox.IsPointerOver.Should().BeFalse("前提: ホバーしていない");

        BackgroundOf(box).Should().Be(ResolveColor("Accent"));
        checkMark.IsVisible.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "チェック済み×無効のチェックボックスは、塗りはAccentのまま枠線だけがTextDisabledになる（:disabledとの組み合わせを壊していない）")]
    public void チェック済みで無効なときは塗りがAccentで枠線がTextDisabled()
    {
        var (window, checkBox, box, _) = Open(AppTheme.Dark, isChecked: true);

        checkBox.IsEnabled = false;
        window.CaptureRenderedFrame().Should().NotBeNull();

        BackgroundOf(box).Should().Be(ResolveColor("Accent"));
        ((ISolidColorBrush)box.BorderBrush!).Color.Should().Be(ResolveColor("TextDisabled"),
            "無効時の枠線はチェック済みでもTextDisabled（DisabledAppearanceTestsと同じ作法）");
    }
}
