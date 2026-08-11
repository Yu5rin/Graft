using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Themes;
using Graft.UiTests.TestSupport;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// GitHub形式の基本記法拡張（利用者指示）の検証用スクリーンショット。<see cref="ThemeTests"/>と
/// 同じ手法（Avalonia Headlessの<c>CaptureRenderedFrame</c>。実際のレンダリングパイプラインを
/// 使うがXvfb等の物理ディスプレイは介さない）で、全記法を含むサンプルMarkdownのプレビュー・
/// 取扱説明書（F1）をライト・ダーク両テーマで実際に描画してPNGへ保存する。目視確認用の
/// スクリーンショットを残すことに主眼があるため、細かいアサーションは持たない
/// （表示崩れの自動検出は他の回帰テストが担う）。
/// </summary>
public class ManualMarkdownRendererScreenshotSmokeTests : IDisposable
{
    private const string SampleMarkdown = """
        # 見出しレベル1

        GitHub形式の基本記法をひととおり試すサンプルです。

        ## 見出しレベル2

        ### 見出しレベル3

        #### 見出しレベル4

        ##### 見出しレベル5

        ###### 見出しレベル6

        ## インライン装飾

        これは**太字**、こちらは*斜体アスタリスク*、こちらは_斜体アンダースコア_、
        これは~~打ち消し線~~、これは`インラインコード`です。

        入り組んだ例: **太字の中の*斜体***、`コード内の**アスタリスク**`、
        [**太字**のリンク](https://example.com)、計算式は 2 * 3 = 6 です。

        エスケープ: \*これは斜体にならない\* \_これも斜体にならない\_ \`これはコードにならない\`

        ## 箇条書き（入れ子）

        - 親項目A
          - 子項目A1
          - 子項目A2
            1. 孫項目1
            2. 孫項目2
        - 親項目B
        - チェックリスト
          - [ ] 未完了のタスク
          - [x] 完了したタスク

        ## 引用

        > これは引用です。
        >
        > > 入れ子になった引用です。
        >
        > 引用の中の箇条書き:
        > - 引用内の項目A
        > - 引用内の項目B

        ## コードブロック

        ```python
        def greet(name: str) -> str:
            # あいさつを返す
            return f"こんにちは、{name}さん"
        ```

        ## 表

        | 項目 | 説明 |
        | --- | --- |
        | 太字 | `**text**` |
        | 斜体 | `*text*` / `_text_` |

        ## リンクと画像

        [外部サイトへ](https://example.com/path)

        ![存在しない画像](./missing.png)

        ![外部の画像](https://example.com/pic.png)

        ## 脚注

        本文中に脚注を挿入できます[^1]。

        ---

        [^1]: これは脚注の本文です。
        """;

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "全記法サンプルのMarkdownプレビューをライトテーマでスクリーンショット保存できる")]
    public void 全記法サンプルをライトテーマでスクリーンショット保存できる()
        => CapturePreviewScreenshot(AppTheme.Light, "gfm-sample-light.png");

    [AvaloniaFact(DisplayName = "全記法サンプルのMarkdownプレビューをダークテーマでスクリーンショット保存できる")]
    public void 全記法サンプルをダークテーマでスクリーンショット保存できる()
        => CapturePreviewScreenshot(AppTheme.Dark, "gfm-sample-dark.png");

    [AvaloniaFact(DisplayName = "取扱説明書（F1）をライトテーマでスクリーンショット保存できる")]
    public void 取扱説明書をライトテーマでスクリーンショット保存できる()
        => CaptureManualScreenshot(AppTheme.Light, "manual-light.png");

    [AvaloniaFact(DisplayName = "取扱説明書（F1）をダークテーマでスクリーンショット保存できる")]
    public void 取扱説明書をダークテーマでスクリーンショット保存できる()
        => CaptureManualScreenshot(AppTheme.Dark, "manual-dark.png");

    private void CapturePreviewScreenshot(AppTheme theme, string fileName)
    {
        var originalTheme = ThemeManager.SelectedTheme;
        try
        {
            ThemeManager.SetTheme(theme);

            var result = ManualMarkdownRenderer.Render(
                SampleMarkdown, _ => { }, _ => { }, _ => { });
            var panel = new StackPanel { Margin = new Avalonia.Thickness(20, 16) };
            foreach (var block in result.Blocks) panel.Children.Add(block.Control);
            var scroll = new ScrollViewer { Content = panel };
            var window = _windows.Track(new Window { Width = 1000, Height = 2600, Content = scroll });
            window.Bind(Window.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("BgBase"));
            window.Show();

            SaveScreenshot(window, fileName);
        }
        finally
        {
            ThemeManager.SetTheme(originalTheme);
        }
    }

    private void CaptureManualScreenshot(AppTheme theme, string fileName)
    {
        var originalTheme = ThemeManager.SelectedTheme;
        try
        {
            ThemeManager.SetTheme(theme);
            var window = _windows.Track(new ManualWindow());
            window.Show();
            SaveScreenshot(window, fileName);
        }
        finally
        {
            ThemeManager.SetTheme(originalTheme);
        }
    }

    private static void SaveScreenshot(Window window, string fileName)
    {
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull("リソース解決に失敗すると描画そのものができない");

        var path = Path.Combine(GetScreenshotDirectory(), fileName);
        frame!.Save(path);
        File.Exists(path).Should().BeTrue($"スクリーンショットが '{path}' へ保存されている必要がある");
    }

    private static string GetScreenshotDirectory([CallerFilePath] string sourceFilePath = "")
    {
        var dir = Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "screenshots");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
