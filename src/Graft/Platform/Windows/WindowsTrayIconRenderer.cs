using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Graft.Platform.Windows;

/// <summary>
/// 仕様書8.12・附録A.5「アイコンにラスタ画像を使わない」に対応する。<c>Themes/Logo.xaml</c> の
/// ベクター素材（<see cref="Geometry"/> リソース）を <see cref="DrawingVisual"/> →
/// <see cref="RenderTargetBitmap"/> で描画し、GDIの<c>CreateDIBSection</c>/<c>CreateIconIndirect</c>
/// を用いてHICONを組み立てる。タスクバーのライト／ダーク判定もここで行う。
/// 移設元は <c>Views/TrayIconRenderer.cs</c>。ロジックは変更していない。ITrayIcon自体は
/// WPFへ依存しないまま、Windows実装であるこのクラスがベクターからアイコンを生成する
/// （インターフェース側でバイト列を受け渡す方式は採らない）。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsTrayIconRenderer
{
    /// <summary>Themes/Logo.xaml のジオメトリ資源を、タスクバーの明暗に応じた配色で描画しHICONを作る。</summary>
    public static IntPtr BuildHIcon(bool lightTaskbar, int size)
    {
        var visual = BuildLogoVisual(lightTaskbar, size);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);

        return CreateHIconFromPixels(pixels, size);
    }

    /// <summary>生成したHICONを破棄する。</summary>
    public static void DestroyIcon(IntPtr icon)
    {
        if (icon != IntPtr.Zero)
        {
            WindowsTrayNativeMethods.DestroyIcon(icon);
        }
    }

    /// <summary>
    /// タスクバー（エクスプローラ）のライト／ダーク設定を読み取り専用で参照する。書き込みは行わない
    /// （附録A.5）。取得できない場合はダーク扱いとする。
    /// </summary>
    public static bool DetectLightTaskbar()
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        const string valueName = "SystemUsesLightTheme";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            if (key?.GetValue(valueName) is int value)
            {
                return value != 0;
            }
        }
        catch (Exception)
        {
            // 読み取り専用の最善努力の参照。取得できない場合は既定（ダーク）にフォールバックする。
        }
        return false;
    }

    /// <summary>
    /// ダークタスクバーでは既定配色（明るいプレート＋濃い幹）、ライトタスクバーではプレートと
    /// 幹の配色を反転させ、どちらのタスクバー上でも視認できるようにする。
    /// </summary>
    private static DrawingVisual BuildLogoVisual(bool lightTaskbar, int size)
    {
        var resources = Application.Current.Resources;
        var background = (Geometry)resources["LogoBackgroundGeometry"];
        var trunk = (Geometry)resources["LogoTrunkGeometry"];
        var stalk = (Geometry)resources["LogoStalkGeometry"];
        var leafUpper = (Geometry)resources["LogoLeafUpperGeometry"];
        var leafLower = (Geometry)resources["LogoLeafLowerGeometry"];
        var veinUpper = (Geometry)resources["LogoVeinUpperGeometry"];
        var veinLower = (Geometry)resources["LogoVeinLowerGeometry"];

        var cream = (Color)resources["LogoBackgroundColor"];
        var trunkColor = (Color)resources["LogoTrunkColor"];
        var leafColor = (Color)resources["LogoLeafColor"];

        var plateBrush = FrozenBrush(lightTaskbar ? trunkColor : cream);
        var glyphBrush = FrozenBrush(lightTaskbar ? cream : trunkColor);
        var leafBrush = FrozenBrush(leafColor);

        var visual = new DrawingVisual { Transform = new ScaleTransform(size / 256.0, size / 256.0) };
        using (var dc = visual.RenderOpen())
        {
            dc.DrawGeometry(plateBrush, null, background);
            dc.DrawGeometry(glyphBrush, null, trunk);
            dc.DrawGeometry(leafBrush, null, stalk);
            dc.DrawGeometry(leafBrush, null, leafUpper);
            dc.DrawGeometry(leafBrush, null, leafLower);
            dc.DrawGeometry(plateBrush, null, veinUpper);
            dc.DrawGeometry(plateBrush, null, veinLower);
        }
        return visual;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// PBGRA32のピクセル配列から、アルファブレンド対応のHICONを組み立てる。ANDマスクは全て0
    /// （＝マスクなし）とし、32bpp色ビットマップのアルファチャンネルのみで透過を表現する
    /// （Windows XP以降の標準的な手法）。
    /// </summary>
    private static IntPtr CreateHIconFromPixels(byte[] pixelsBgra, int size)
    {
        var screenDc = WindowsTrayNativeMethods.GetDC(IntPtr.Zero);
        IntPtr colorBitmap;
        try
        {
            var header = new WindowsTrayNativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<WindowsTrayNativeMethods.BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            colorBitmap = WindowsTrayNativeMethods.CreateDIBSection(screenDc, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (colorBitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }
            Marshal.Copy(pixelsBgra, 0, bits, pixelsBgra.Length);
        }
        finally
        {
            WindowsTrayNativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }

        var maskStrideBytes = ((size + 15) / 16) * 2;
        var maskBits = new byte[maskStrideBytes * size];
        var maskBitmap = WindowsTrayNativeMethods.CreateBitmap(size, size, 1, 1, maskBits);

        var iconInfo = new WindowsTrayNativeMethods.ICONINFO { fIcon = true, hbmMask = maskBitmap, hbmColor = colorBitmap };
        var hIcon = WindowsTrayNativeMethods.CreateIconIndirect(ref iconInfo);

        WindowsTrayNativeMethods.DeleteObject(colorBitmap);
        if (maskBitmap != IntPtr.Zero)
        {
            WindowsTrayNativeMethods.DeleteObject(maskBitmap);
        }
        return hIcon;
    }
}
