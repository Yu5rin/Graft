using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using FluentAssertions;
using Graft.Editor;
using Graft.Themes;
using Graft.UiTests.TestSupport;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 課題#73（案2）で<see cref="SyntaxHighlightBridge"/>へ入れたブラシ・書体のキャッシュが、
/// テーマ切替への追従を壊していないことの回帰テスト。
///
/// キャッシュは<c>ThemeManager.ThemeChanged</c>で破棄する設計なので、ここが抜けると
/// 「テーマを切り替えても文字色が古いまま」という分かりにくい退行になる。実際に9テーマの
/// うち2つ（Dark→Light）を往復させ、可視行の要素へ実際に設定された前景ブラシの色が
/// テーマのリソース値どおりに変わることを、描画パスを通して確認する。
/// </summary>
public class SyntaxHighlightThemeCacheTests : IDisposable
{
    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        ThemeManager.SetTheme(AppTheme.Dark); // 他のテストへ影響を残さない。
        _windows.Dispose();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "課題#73: ブラシをキャッシュしてもテーマ切替で構文強調の色が追従する")]
    public void テーマ切替で構文強調の色が変わる()
    {
        ThemeManager.SetTheme(AppTheme.Dark);

        var document = new TextDocument("public class Foo\n{\n}\n");
        var editor = new TextEditor { Document = document, ShowLineNumbers = true };
        var window = _windows.Track(new Window { Width = 800, Height = 400, Content = editor });

        using var bridge = new SyntaxHighlightBridge(editor);
        editor.TextArea.TextView.LineTransformers.Add(bridge);
        bridge.Attach(document, ".cs", syntaxEnabled: true);

        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        var darkKeyword = ResolveThemeColor("SyntaxKeywordColor");
        ForegroundColors(editor, document).Should().Contain(darkKeyword,
            "ダークテーマのキーワード色が実際に適用されているはず");

        ThemeManager.SetTheme(AppTheme.Light);
        window.CaptureRenderedFrame().Should().NotBeNull();

        var lightKeyword = ResolveThemeColor("SyntaxKeywordColor");
        lightKeyword.Should().NotBe(darkKeyword, "ダークとライトでキーワード色は異なるはず（前提の確認）");
        var afterLight = ForegroundColors(editor, document);
        afterLight.Should().Contain(lightKeyword, "テーマ切替後はライトテーマの色へ追従するはず");
        afterLight.Should().NotContain(darkKeyword, "古いブラシがキャッシュに残っていてはならない");

        // 戻したときも同じく追従する（キャッシュの破棄が片道だけでないことの確認）。
        ThemeManager.SetTheme(AppTheme.Dark);
        window.CaptureRenderedFrame().Should().NotBeNull();
        ForegroundColors(editor, document).Should().Contain(darkKeyword).And.NotContain(lightKeyword);
    }

    [AvaloniaFact(DisplayName = "課題#73: 書体をキャッシュしてもコメント行はイタリックのままになる")]
    public void コメント行はイタリックのまま()
    {
        ThemeManager.SetTheme(AppTheme.Dark);

        var document = new TextDocument("// コメント\npublic class Foo\n{\n}\n");
        var editor = new TextEditor { Document = document };
        var window = _windows.Track(new Window { Width = 800, Height = 400, Content = editor });

        using var bridge = new SyntaxHighlightBridge(editor);
        editor.TextArea.TextView.LineTransformers.Add(bridge);
        bridge.Attach(document, ".cs", syntaxEnabled: true);

        window.Show();
        window.CaptureRenderedFrame().Should().NotBeNull();

        // 2回描画しても（＝キャッシュに当たっても）イタリックが維持されること。
        for (var i = 0; i < 2; i++)
        {
            editor.TextArea.TextView.Redraw();
            window.CaptureRenderedFrame().Should().NotBeNull();

            var commentLine = VisualLineOf(editor, document, lineNumber: 1);
            commentLine.Elements.Any(e => e.TextRunProperties.Typeface.Style == FontStyle.Italic)
                .Should().BeTrue("コメント行はイタリック表示のはず（8.6）");
        }

        var codeLine = VisualLineOf(editor, document, lineNumber: 2);
        codeLine.Elements.Should().OnlyContain(e => e.TextRunProperties.Typeface.Style != FontStyle.Italic,
            "コメント以外の行の書体は変えない");
    }

    /// <summary>可視行へ実際に設定された前景ブラシの色を集める。</summary>
    private static IReadOnlyCollection<Color> ForegroundColors(TextEditor editor, TextDocument document)
    {
        // テーマ切替後はSyntaxHighlightBridgeがRedraw()を呼んでいるため可視行は作り直されるが、
        // ここでも確実に作り直してから読む（GetOrConstructVisualLineは有効なキャッシュがあれば
        // それを返すため）。
        editor.TextArea.TextView.Redraw();
        editor.TextArea.TextView.Measure(editor.TextArea.TextView.Bounds.Size);

        return VisualLineOf(editor, document, lineNumber: 1).Elements
            .Select(e => e.TextRunProperties.ForegroundBrush)
            .OfType<ISolidColorBrush>()
            .Select(b => b.Color)
            .ToList();
    }

    private static VisualLine VisualLineOf(TextEditor editor, TextDocument document, int lineNumber)
        => editor.TextArea.TextView.GetOrConstructVisualLine(document.GetLineByNumber(lineNumber));

    private static Color ResolveThemeColor(string key)
    {
        Application.Current!.TryFindResource(key, null, out var value);
        value.Should().NotBeNull($"テーマリソース '{key}' が解決できる必要がある");
        return (Color)value!;
    }
}
