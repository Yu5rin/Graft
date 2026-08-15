using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using FluentAssertions;
using Graft.Themes;
using Graft.UiTests.TestSupport;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// テーマ・カラートークン・アイコンの移植を検証するテスト（仕様書v2.1 18章・附録A.7、
/// 20章L2）。カラートークン一覧・アイコン一覧はv2.0のWPF版 Themes/Dark.xaml・Light.xaml・
/// Icons.xaml のキー名をそのまま列挙したものであり、移植漏れがあればここで機械的に
/// 検出できる（1つでも欠けたら失敗する）。
/// </summary>
public class ThemeTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        // 表示したShellWindowを後始末する（ShownWindowTracker参照。閉じ忘れると
        // 「Unable to locate 'Avalonia.Platform.IFontManagerImpl'」がCIで不定期に出る）。
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    // v2.0のWPF版 Dark.xaml / Light.xaml と同一のキー名一覧（9.3）。Color/Brushの対で持つ
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

    // v2.0のWPF版 Icons.xaml と同一のキー名一覧（9.5）。21種に加え、エクスプローラの
    // 「更新」用に新設したIconRefreshGeometryを含め22種。
    private static readonly string[] IconGeometryKeys =
    {
        "IconCheckGeometry", "IconAlertTriangleGeometry", "IconXCircleGeometry", "IconPlayGeometry",
        "IconEyeGeometry", "IconRotateCcwGeometry", "IconRefreshGeometry", "IconFolderGeometry",
        "IconLayersGeometry", "IconHistoryGeometry", "IconSettingsGeometry", "IconSearchGeometry",
        "IconCopyGeometry", "IconFileGeometry", "IconFileCodeGeometry", "IconSaveGeometry",
        "IconXGeometry", "IconChevronRightGeometry", "IconChevronDownGeometry", "IconPlusGeometry",
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

    // 検討書「テーマプリセット9種」。既定ライト/既定ダーク（Dark/Light）以外の7プリセット。
    // Dark.axaml/Light.axamlと全く同じキー名を過不足なく持つことを、1つでも欠けたら
    // 失敗する形で機械的に検証する（ThemeTestsクラスドキュメント参照）。
    public static readonly TheoryData<AppTheme> PresetThemes = new()
    {
        AppTheme.Sepia, AppTheme.Github, AppTheme.SolarizedLight, AppTheme.SolarizedDark,
        AppTheme.Nord, AppTheme.Dracula, AppTheme.Night,
    };

    [AvaloniaTheory(DisplayName = "テーマプリセット7種それぞれで、全カラートークンが過不足なく解決できる")]
    [MemberData(nameof(PresetThemes))]
    public void プリセットの全カラートークンが解決できる(AppTheme theme)
    {
        ThemeManager.SetTheme(theme);
        AssertAllColorTokensResolve();
    }

    // 検討書「各テーマが『暗いか明るいか』の判定…も、9テーマに合わせて正しく返す必要がある」。
    // sepia/github/solarized-lightは明るいテーマ、nord/dracula/solarized-dark/nightは
    // 暗いテーマとして扱う（Pane（github.com/Yu5rin/pane）のsrc/themes.cssのdata-theme
    // 属性と一致させてある）。
    public static readonly TheoryData<AppTheme, bool> ThemeDarkness = new()
    {
        { AppTheme.Dark, true }, { AppTheme.Light, false },
        { AppTheme.Sepia, false }, { AppTheme.Github, false }, { AppTheme.SolarizedLight, false },
        { AppTheme.SolarizedDark, true }, { AppTheme.Nord, true }, { AppTheme.Dracula, true },
        { AppTheme.Night, true },
    };

    [AvaloniaTheory(DisplayName = "各テーマの明暗判定（IsDarkResolved）が正しい")]
    [MemberData(nameof(ThemeDarkness))]
    public void テーマごとの明暗判定が正しい(AppTheme theme, bool expectedIsDark)
    {
        ThemeManager.SetTheme(theme);
        ThemeManager.IsDarkResolved.Should().Be(expectedIsDark, $"{theme}の明暗判定");
    }

    [AvaloniaFact(DisplayName = "9プリセットそれぞれのシェルウィンドウのスクリーンショットを保存できる（sepia）")]
    public void セピアテーマのスクリーンショットを保存できる() => CaptureThemeScreenshot(AppTheme.Sepia, "shell-sepia.png");

    [AvaloniaFact(DisplayName = "9プリセットそれぞれのシェルウィンドウのスクリーンショットを保存できる（github）")]
    public void GitHub風テーマのスクリーンショットを保存できる() => CaptureThemeScreenshot(AppTheme.Github, "shell-github.png");

    [AvaloniaFact(DisplayName = "9プリセットそれぞれのシェルウィンドウのスクリーンショットを保存できる（solarized-light）")]
    public void SolarizedLightテーマのスクリーンショットを保存できる() => CaptureThemeScreenshot(AppTheme.SolarizedLight, "shell-solarized-light.png");

    [AvaloniaFact(DisplayName = "9プリセットそれぞれのシェルウィンドウのスクリーンショットを保存できる（solarized-dark）")]
    public void SolarizedDarkテーマのスクリーンショットを保存できる() => CaptureThemeScreenshot(AppTheme.SolarizedDark, "shell-solarized-dark.png");

    [AvaloniaFact(DisplayName = "9プリセットそれぞれのシェルウィンドウのスクリーンショットを保存できる（nord）")]
    public void Nordテーマのスクリーンショットを保存できる() => CaptureThemeScreenshot(AppTheme.Nord, "shell-nord.png");

    [AvaloniaFact(DisplayName = "9プリセットそれぞれのシェルウィンドウのスクリーンショットを保存できる（dracula）")]
    public void Draculaテーマのスクリーンショットを保存できる() => CaptureThemeScreenshot(AppTheme.Dracula, "shell-dracula.png");

    [AvaloniaFact(DisplayName = "9プリセットそれぞれのシェルウィンドウのスクリーンショットを保存できる（night）")]
    public void Nightテーマのスクリーンショットを保存できる() => CaptureThemeScreenshot(AppTheme.Night, "shell-night.png");

    private void CaptureThemeScreenshot(AppTheme theme, string fileName)
    {
        ThemeManager.SetTheme(theme);
        CaptureShellScreenshot(fileName);
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

    [AvaloniaFact(DisplayName = "全アイコンジオメトリ（22種）が解決でき、パースできる")]
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

    [AvaloniaFact(DisplayName = "アイコンの数はちょうど22種である")]
    public void アイコンの数はちょうど22種である()
    {
        IconGeometryKeys.Should().HaveCount(22,
            "9.5の仕様どおりv2.0のWPF版の21種に加え、更新アイコン用のIconRefreshGeometryを1種追加した");
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
        darkColor.Should().Be(Color.Parse("#151619"), "ダークのBgBaseはv2.0のWPF版と同一の値を維持する必要がある");
        lightColor.Should().Be(Color.Parse("#FAFAF8"), "ライトのBgBaseはv2.0のWPF版と同一の値を維持する必要がある");
    }

    [AvaloniaFact(DisplayName = "テーマ切り替えはウィンドウの再構築なしに反映される")]
    public void テーマ切り替えはウィンドウ再構築なしに反映される()
    {
        var window = _windows.Track(new ShellWindow());
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

    private void CaptureShellScreenshot(string fileName)
    {
        var window = _windows.Track(new ShellWindow { Width = 1280, Height = 800 });
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

    [AvaloniaFact(DisplayName = "settings.jsonのthemeは起動時の選択肢へ読み替えられる")]
    public void 保存したテーマは起動時に反映される()
    {
        // 起動時に設定を読み直してテーマを当てないと、選んだテーマが再起動のたびに
        // システム追従へ戻る（実機で発生した不具合）。読み替え規則が起動処理と
        // 設定画面で食い違わないよう、対応表はThemeManagerに集約している。
        ThemeManager.ParseTheme("light").Should().Be(AppTheme.Light);
        ThemeManager.ParseTheme("dark").Should().Be(AppTheme.Dark);
        ThemeManager.ParseTheme("system").Should().Be(AppTheme.System);
        ThemeManager.ParseTheme(null).Should().Be(AppTheme.System, "未知の値は追従として扱う");
        ThemeManager.ParseTheme("なにか").Should().Be(AppTheme.System);

        // 検討書「テーマプリセット9種」。7プリセットのidも同じ読み替え規則で対応する。
        ThemeManager.ParseTheme("sepia").Should().Be(AppTheme.Sepia);
        ThemeManager.ParseTheme("github").Should().Be(AppTheme.Github);
        ThemeManager.ParseTheme("solarized-light").Should().Be(AppTheme.SolarizedLight);
        ThemeManager.ParseTheme("solarized-dark").Should().Be(AppTheme.SolarizedDark);
        ThemeManager.ParseTheme("nord").Should().Be(AppTheme.Nord);
        ThemeManager.ParseTheme("dracula").Should().Be(AppTheme.Dracula);
        ThemeManager.ParseTheme("night").Should().Be(AppTheme.Night);

        // 読み替えた結果を当てると、実際に選択中のテーマが変わること。
        ThemeManager.SetTheme(ThemeManager.ParseTheme("light"));
        ThemeManager.SelectedTheme.Should().Be(AppTheme.Light);
        ThemeManager.SetTheme(ThemeManager.ParseTheme("dark"));
        ThemeManager.SelectedTheme.Should().Be(AppTheme.Dark);
        ThemeManager.SetTheme(ThemeManager.ParseTheme("nord"));
        ThemeManager.SelectedTheme.Should().Be(AppTheme.Nord);
    }

    [AvaloniaFact(DisplayName = "システム追従は9プリセットへは倒れず、既定ライト/既定ダークのどちらかへ解決される")]
    public void システム追従はプリセットへ倒れない()
    {
        // 検討書「既存のsystem（OS追従）は残す。9テーマ＋システム追従、という形になるはず」。
        // System選択時は常にDark.axaml/Light.axamlのどちらかへ解決され、7プリセットの
        // 明るさに関わらずプリセット側の辞書は使わない（ThemeManager.ResolveThemeFile参照）。
        ThemeManager.SetTheme(AppTheme.System);
        var isDark = ThemeManager.IsDarkResolved;
        Application.Current!.TryFindResource("BgBaseColor", null, out var systemValue);

        ThemeManager.SetTheme(isDark ? AppTheme.Dark : AppTheme.Light);
        Application.Current!.TryFindResource("BgBaseColor", null, out var explicitValue);

        systemValue.Should().Be(explicitValue, "システム追従は既定ライト/既定ダークと完全に同じ辞書へ解決される必要がある");
    }
}
