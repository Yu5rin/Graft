using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using FluentAssertions;
using Graft.Themes;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// テーマ・カラートークン・アイコンの移植を検証するテスト（仕様書v2.1 18章・附録A.7、
/// 20章L2）。カラートークン一覧・アイコン一覧はWPF版 Themes/Dark.xaml・Light.xaml・
/// Icons.xaml のキー名をそのまま列挙したものであり、移植漏れがあればここで機械的に
/// 検出できる（1つでも欠けたら失敗する）。
/// </summary>
public class ThemeTests
{
    // WPF版 Dark.xaml / Light.xaml と同一のキー名一覧（9.3）。Color/Brushの対で持つ
    // トークンはどちらも列挙する。SyntaxPlainのみColor版を持たない（8.6の規則どおり
    // text.primaryと同値のBrushのみ）。
    private static readonly string[] ColorTokenKeys =
    {
        "BgBaseColor", "BgBase", "BgSurfaceColor", "BgSurface", "BgElevatedColor", "BgElevated",
        "BgHoverColor", "BgHover", "BgSelectedColor", "BgSelected",
        "TextPrimaryColor", "TextPrimary", "TextSecondaryColor", "TextSecondary",
        "TextDisabledColor", "TextDisabled",
        "AccentColor", "Accent",
        "BorderSubtleColor", "BorderSubtle",
        "DiffAddBgColor", "DiffAddBg", "DiffAddBarColor", "DiffAddBar",
        "DiffDelBgColor", "DiffDelBg", "DiffDelBarColor", "DiffDelBar",
        "StateWarnColor", "StateWarn", "StateErrorColor", "StateError", "StateOkColor", "StateOk",
        "SyntaxKeywordColor", "SyntaxKeyword", "SyntaxStringColor", "SyntaxString",
        "SyntaxNumberColor", "SyntaxNumber", "SyntaxCommentColor", "SyntaxComment",
        "SyntaxFunctionColor", "SyntaxFunction", "SyntaxTypeColor", "SyntaxType",
        "SyntaxOperatorColor", "SyntaxOperator", "SyntaxPlain",
        "GutterAddColor", "GutterAdd", "GutterModColor", "GutterMod", "GutterDelColor", "GutterDel",
        "EditorCurrentLineColor", "EditorCurrentLine", "EditorSelectionColor", "EditorSelection",
    };

    // WPF版 Icons.xaml と同一のキー名一覧（9.5）。21種すべて。
    private static readonly string[] IconGeometryKeys =
    {
        "IconCheckGeometry", "IconAlertTriangleGeometry", "IconXCircleGeometry", "IconPlayGeometry",
        "IconEyeGeometry", "IconRotateCcwGeometry", "IconFolderGeometry", "IconLayersGeometry",
        "IconHistoryGeometry", "IconSettingsGeometry", "IconSearchGeometry", "IconCopyGeometry",
        "IconFileGeometry", "IconFileCodeGeometry", "IconSaveGeometry", "IconXGeometry",
        "IconChevronRightGeometry", "IconChevronDownGeometry", "IconPlusGeometry",
        "IconGitBranchGeometry", "IconPanelBottomGeometry",
    };

    [AvaloniaFact(DisplayName = "全カラートークンがダークテーマで解決できる")]
    public void 全カラートークンがダークテーマで解決できる()
    {
        ThemeManager.SetTheme(AppTheme.Dark);
        AssertAllColorTokensResolve();
    }

    [AvaloniaFact(DisplayName = "全カラートークンがライトテーマで解決できる")]
    public void 全カラートークンがライトテーマで解決できる()
    {
        ThemeManager.SetTheme(AppTheme.Light);
        AssertAllColorTokensResolve();
    }

    private static void AssertAllColorTokensResolve()
    {
        foreach (var key in ColorTokenKeys)
        {
            var found = Application.Current!.TryFindResource(key, null, out var value);
            found.Should().BeTrue($"カラートークン '{key}' が解決できる必要がある（移植漏れの検出）");
            value.Should().NotBeNull($"カラートークン '{key}' の値がnullであってはならない");
        }
    }

    [AvaloniaFact(DisplayName = "全アイコンジオメトリ（21種）が解決でき、パースできる")]
    public void 全アイコンジオメトリが解決できパースできる()
    {
        foreach (var key in IconGeometryKeys)
        {
            var found = Application.Current!.TryFindResource(key, null, out var value);
            found.Should().BeTrue($"アイコン '{key}' が解決できる必要がある（移植漏れの検出）");
            var geometry = value.Should().BeAssignableTo<StreamGeometry>(
                $"アイコン '{key}' はStreamGeometryとして定義されている必要がある").Subject;

            // Boundsへのアクセスはパス文字列が正しくパースされていないと例外になる。
            var act = () => geometry.Bounds;
            act.Should().NotThrow($"アイコン '{key}' のパスデータがパースできる必要がある");
        }
    }

    [AvaloniaFact(DisplayName = "アイコンの数はちょうど21種である")]
    public void アイコンの数はちょうど21種である()
    {
        IconGeometryKeys.Should().HaveCount(21, "9.5の仕様どおりWPF版の21種を維持する必要がある");
    }

    [AvaloniaFact(DisplayName = "テーマを切り替えると実際にトークンの値が変わる")]
    public void テーマを切り替えるとトークンの値が変わる()
    {
        ThemeManager.SetTheme(AppTheme.Dark);
        Application.Current!.TryFindResource("BgBase", null, out var darkValue);
        var darkColor = ((ISolidColorBrush)darkValue!).Color;

        ThemeManager.SetTheme(AppTheme.Light);
        Application.Current!.TryFindResource("BgBase", null, out var lightValue);
        var lightColor = ((ISolidColorBrush)lightValue!).Color;

        darkColor.Should().NotBe(lightColor, "ダークとライトでBgBaseの実際の色が異なる必要がある");
        darkColor.Should().Be(Color.Parse("#151619"), "ダークのBgBaseはWPF版と同一の値を維持する必要がある");
        lightColor.Should().Be(Color.Parse("#FAFAF8"), "ライトのBgBaseはWPF版と同一の値を維持する必要がある");
    }

    [AvaloniaFact(DisplayName = "テーマ切り替えはウィンドウの再構築なしに反映される")]
    public void テーマ切り替えはウィンドウ再構築なしに反映される()
    {
        var window = new ShellWindow();
        window.Show();

        ThemeManager.SetTheme(AppTheme.Dark);
        Layout(window);
        var darkBackground = ((ISolidColorBrush)window.Background!).Color;

        ThemeManager.SetTheme(AppTheme.Light);
        Layout(window);
        var lightBackground = ((ISolidColorBrush)window.Background!).Color;

        darkBackground.Should().NotBe(lightBackground,
            "同一のWindowインスタンスのままDynamicResourceの解決先が切り替わる必要がある");
    }

    [AvaloniaFact(DisplayName = "ダークテーマでシェルウィンドウを描画しスクリーンショットを保存できる")]
    public void ダークテーマでシェルウィンドウのスクリーンショットを保存できる()
    {
        ThemeManager.SetTheme(AppTheme.Dark);
        CaptureShellScreenshot("shell-dark.png");
    }

    [AvaloniaFact(DisplayName = "ライトテーマでシェルウィンドウを描画しスクリーンショットを保存できる")]
    public void ライトテーマでシェルウィンドウのスクリーンショットを保存できる()
    {
        ThemeManager.SetTheme(AppTheme.Light);
        CaptureShellScreenshot("shell-light.png");
    }

    private static void CaptureShellScreenshot(string fileName)
    {
        var window = new ShellWindow { Width = 1280, Height = 800 };
        window.Show();
        Layout(window);

        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");

        var path = Path.Combine(GetScreenshotDirectory(), fileName);
        frame!.Save(path);
        File.Exists(path).Should().BeTrue($"スクリーンショットが '{path}' へ保存されている必要がある");
    }

    private static void Layout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
    }

    private static string GetScreenshotDirectory([CallerFilePath] string sourceFilePath = "")
    {
        var dir = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "screenshots");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
