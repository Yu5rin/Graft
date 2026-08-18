using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests;

/// <summary>
/// Windows実機報告「行数の多いファイルを開くと、スクロールバーのつまみ（縦幅）が
/// 数ピクセルの線のようになり掴みづらい」への対応の検証。
///
/// Avalonia標準の<see cref="Track"/>（Avalonia.Controls 11.2.3、ilspycmdで逆コンパイルして
/// 確認済み）は、つまみの最小長を「Thumbの MinHeight（縦）／MinWidth（横）が設定されていれば
/// その値、無ければ既定10px」で下限クランプする。Graftの共通ScrollBar ControlTheme
/// （Themes/Controls.Layout.axaml）はこれまでその設定をしておらず既定の10pxのままだったため、
/// 内容が非常に長い／広いスクロール領域ではその10pxすら視認上ほぼ線のようになっていた。
/// ここではThemes/Controls.Layout.axamlに追加した「つまみの最小長24px」の設定が
/// 実際のレイアウトに効いていることを、つまみの<see cref="Visual.Bounds"/>を実測して確認する。
///
/// 併せて、同じTrackの実装には「つまみの最小長がトラック本体の長さを超えたら
/// スクロールバーごと非表示にする（<c>Thumb.IsVisible = false</c>）」という分岐があるため、
/// 最小長を大きくしすぎると背の低い領域でスクロールバーが消えてしまう副作用がありうる。
/// 実際に短いビューポート（高さ60px。設定画面の複数行入力欄と同程度）でも
/// スクロールバーが消えないことを回帰確認する。
/// </summary>
public class ScrollBarThumbMinLengthTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>期待する最小長。Themes/Controls.Layout.axamlに設定した値と揃える
    /// （XAML側の値そのものを読み取っているわけではなく、実測結果と突き合わせる期待値）。</summary>
    private const double ExpectedMinThumbLength = 24.0;

    [AvaloniaFact(DisplayName = "行数が非常に多い一覧でも縦スクロールバーのつまみは最小長を下回らない")]
    public void 縦スクロールバーのつまみは最小長を下回らない()
    {
        // 数万行のファイルを開いた状況を模して、極端に長い一覧を小さいビューポートに収める。
        var list = new ListBox
        {
            ItemsSource = Enumerable.Range(1, 50_000).Select(i => $"{i}行目").ToList(),
            Width = 300,
            Height = 300,
        };
        var window = _windows.Track(new Window { Width = 400, Height = 400, Content = list });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var scrollBar = list.GetVisualDescendants().OfType<ScrollBar>()
            .First(s => s.Orientation == Orientation.Vertical);
        var thumb = scrollBar.GetVisualDescendants().OfType<Thumb>().Single();

        thumb.IsVisible.Should().BeTrue("50000行もあれば必ずスクロール可能で、つまみも表示されるはず");
        thumb.Bounds.Height.Should().BeGreaterThanOrEqualTo(ExpectedMinThumbLength,
            "つまみの縦幅が最小長を下回ると数ピクセルの線になり、ドラッグで掴めなくなる");
        // 最小長のフロアが効いていることの裏取り: フロアが無ければ
        // 300px（ビューポート） / 50000行 * 300px(トラック長) 相当の1px未満になるはずで、
        // 実際に効いている24pxとは大きな差になる。
        thumb.Bounds.Height.Should().BeLessThan(scrollBar.Bounds.Height,
            "つまみは常時表示のトラックより短いはず（そうでなければ計算の前提が崩れている）");
    }

    [AvaloniaFact(DisplayName = "非常に横長の内容でも横スクロールバーのつまみは最小長を下回らない")]
    public void 横スクロールバーのつまみは最小長を下回らない()
    {
        // 圧縮・minify済みファイルのような、極端に横長な1行を模す。
        var wideContent = new Border { Width = 50_000, Height = 20 };
        var scrollViewer = new ScrollViewer
        {
            Width = 300,
            Height = 60,
            Content = wideContent,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(scrollViewer, ScrollBarVisibility.Visible);
        ScrollViewer.SetVerticalScrollBarVisibility(scrollViewer, ScrollBarVisibility.Disabled);

        var window = _windows.Track(new Window { Width = 400, Height = 200, Content = scrollViewer });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var scrollBar = scrollViewer.GetVisualDescendants().OfType<ScrollBar>()
            .First(s => s.Orientation == Orientation.Horizontal);
        var thumb = scrollBar.GetVisualDescendants().OfType<Thumb>().Single();

        thumb.IsVisible.Should().BeTrue("極端に横長の内容があれば横スクロールも可能で、つまみも表示されるはず");
        thumb.Bounds.Width.Should().BeGreaterThanOrEqualTo(ExpectedMinThumbLength,
            "つまみの横幅が最小長を下回ると数ピクセルの線になり、ドラッグで掴めなくなる");
        thumb.Bounds.Width.Should().BeLessThan(scrollBar.Bounds.Width,
            "つまみは常時表示のトラックより短いはず（そうでなければ計算の前提が崩れている）");

        // 最小長が縦方向（Height）を押し広げていないこと（縦・横で担当プロパティを
        // 分けている設計の裏取り）。太さ10pxのままであるはず。
        thumb.Bounds.Height.Should().BeLessThanOrEqualTo(10.0,
            "横スクロールバーのつまみの太さ（Height）は最小長設定の影響を受けてはならない");
    }

    [AvaloniaFact(DisplayName = "ビューポートが低い一覧でも、最小長のせいでスクロールバーごと消えたりしない")]
    public void ビューポートが低くてもスクロールバーは消えない()
    {
        // 設定画面の複数行入力欄（SafetySettingsView、高さ60px）程度の、
        // 実際にありうる背の低い領域を模す。最小長を大きくしすぎると、
        // Track.ComputeScrollBarLengths内の「つまみの長さ > トラック長」判定で
        // スクロールバーごと非表示になる（本テストが検出したい回帰）。
        var list = new ListBox
        {
            ItemsSource = Enumerable.Range(1, 50).Select(i => $"{i}行目").ToList(),
            Width = 300,
            Height = 60,
        };
        var window = _windows.Track(new Window { Width = 400, Height = 200, Content = list });
        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var scrollBar = list.GetVisualDescendants().OfType<ScrollBar>()
            .First(s => s.Orientation == Orientation.Vertical);
        var thumb = scrollBar.GetVisualDescendants().OfType<Thumb>().Single();

        thumb.IsVisible.Should().BeTrue(
            "背の低い領域でも、つまみの最小長がトラック長を超えてスクロールバーごと消えてはならない");
        thumb.Bounds.Height.Should().BeGreaterThan(0,
            "表示されているなら実際の高さも0より大きいはず");
    }
}
