using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Graft.Platform;

/// <summary>
/// <c>Themes/Logo.axaml</c> のベクター素材からトレイアイコン用のビットマップを実行時に描画する
/// （仕様書8.12・附録A.5「アイコンにラスタ画像を使わない」）。
/// v2.0のWPF版の<c>Platform/Windows/WindowsTrayIconRenderer.cs</c> の描画部分の移植で、
/// 配色・図形の重ね順は変えていない。HICONの組み立て（GDI）は不要になった
/// （AvaloniaのTrayIconがビットマップを直接受け取るため）。
/// </summary>
internal static class AvaloniaLogoRenderer
{
    /// <summary>
    /// ロゴを指定サイズのビットマップへ描画する。
    /// </summary>
    /// <param name="lightBackground">
    /// タスクバー等の背景が明るいかどうか。明るい場合はプレートと幹の配色を反転させ、
    /// どちらの背景でも視認できるようにする（v2.0のWPF版と同じ規則）。
    /// </param>
    /// <param name="size">正方形の一辺のピクセル数。</param>
    public static Bitmap? TryRender(bool lightBackground, int size)
    {
        if (Application.Current is not { } app) return null;

        var drawing = BuildLogoDrawing(app, lightBackground);
        if (drawing is null) return null;

        var pixelSize = new PixelSize(size, size);
        var target = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            // ロゴのビューボックスは256x256。要求サイズへ等倍で縮小する。
            using (context.PushTransform(Matrix.CreateScale(size / 256.0, size / 256.0)))
            {
                drawing.Draw(context);
            }
        }

        return target;
    }

    private static DrawingGroup? BuildLogoDrawing(Application app, bool lightBackground)
    {
        if (FindGeometry(app, "LogoBackgroundGeometry") is not { } background
            || FindGeometry(app, "LogoTrunkGeometry") is not { } trunk
            || FindGeometry(app, "LogoStalkGeometry") is not { } stalk
            || FindGeometry(app, "LogoLeafUpperGeometry") is not { } leafUpper
            || FindGeometry(app, "LogoLeafLowerGeometry") is not { } leafLower
            || FindGeometry(app, "LogoVeinUpperGeometry") is not { } veinUpper
            || FindGeometry(app, "LogoVeinLowerGeometry") is not { } veinLower)
        {
            return null;
        }

        var cream = FindColor(app, "LogoBackgroundColor");
        var trunkColor = FindColor(app, "LogoTrunkColor");
        var leafColor = FindColor(app, "LogoLeafColor");

        var plateBrush = new SolidColorBrush(lightBackground ? trunkColor : cream);
        var glyphBrush = new SolidColorBrush(lightBackground ? cream : trunkColor);
        var leafBrush = new SolidColorBrush(leafColor);

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing { Geometry = background, Brush = plateBrush });
        group.Children.Add(new GeometryDrawing { Geometry = trunk, Brush = glyphBrush });
        group.Children.Add(new GeometryDrawing { Geometry = stalk, Brush = leafBrush });
        group.Children.Add(new GeometryDrawing { Geometry = leafUpper, Brush = leafBrush });
        group.Children.Add(new GeometryDrawing { Geometry = leafLower, Brush = leafBrush });
        group.Children.Add(new GeometryDrawing { Geometry = veinUpper, Brush = plateBrush });
        group.Children.Add(new GeometryDrawing { Geometry = veinLower, Brush = plateBrush });
        return group;
    }

    private static Geometry? FindGeometry(Application app, string key)
        => app.Resources.TryGetResource(key, null, out var value) ? value as Geometry : null;

    private static Color FindColor(Application app, string key)
        => app.Resources.TryGetResource(key, null, out var value) && value is Color color
            ? color
            : Colors.Transparent;
}
