using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Graft.Views;

/// <summary>
/// ベクターアイコン表示用の軽量コントロール（仕様書9.5）。
///
/// 背景: Themes/Icons.axamlの21種のジオメトリはいずれも24×24のビューボックスを前提に
/// 描かれているが、以前は&lt;Path Classes="icon"&gt;に対してStretch="Uniform"を使っていた。
/// Stretch="Uniform"は「ジオメトリ自身のバウンディングボックス」を表示枠へフィットさせるため、
/// 24×24という前提は実際には無視され、図形ごとに拡大率・線の太さの見え方・視覚的な中心が
/// バラバラになっていた（利用者からの指摘: 中央がズレていて大きさが不揃い。例:
/// IconXGeometryのbboxは12×12、IconCheckGeometryは14×10で、Stretch="Uniform"は
/// それぞれ別倍率で16×16へ引き伸ばす）。
///
/// 対策: 本コントロールはTemplatedControlとし、ControlTemplate（Themes/Icons.axaml側の
/// ControlTheme）内にIconViewboxSize（24）四方の固定Canvasを1枚だけ持たせ、その中に
/// Stretch="None"のPathを1枚描く。Canvasのサイズは常に24×24で固定なので、外側のViewbox
/// （既定Stretch="Uniform"、正方形なので縦横同倍率）は個々のジオメトリのbboxに関係なく
/// 常に「表示サイズ÷24」という同じ倍率で縮小する。これで全アイコンの拡大率・線の太さの
/// 見え方が揃う。
///
/// 利用側は&lt;views:IconGlyph Data="{DynamicResource IconXxxGeometry}" /&gt;と書けばよく、
/// 以前の&lt;Path Data="..." Classes="icon" /&gt;と同程度に簡潔。Width/Height/Strokeの
/// 個別上書き（状態アイコンでのStateOk等への差し替えを含む）は本コントロール自身の
/// 同名プロパティへそのまま書けば、これまでどおり効く（Data/StrokeはControlTemplate内で
/// TemplateBindingにより内部Pathへ伝播する）。
/// </summary>
public sealed class IconGlyph : TemplatedControl
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<IconGlyph, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<IconGlyph, IBrush?>(nameof(Stroke));

    /// <summary>表示するアイコンのジオメトリ（24×24の座標系で描かれたStreamGeometry）。</summary>
    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    /// <summary>線の色。既定はTextPrimaryだが、状態アイコンはStateOk/StateError/StateWarn等へ
    /// 利用側で上書きする（v2.0のWPF版・移植当初からの想定を維持）。</summary>
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
}
