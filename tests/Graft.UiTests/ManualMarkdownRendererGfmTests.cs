using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.UiTests.TestSupport;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// <see cref="ManualMarkdownRenderer"/>のGitHub形式の基本記法（利用者指示）の回帰テスト。
///
/// 検証する7項目: 斜体・打ち消し線・引用（入れ子・箇条書き/コードブロック内包含）・画像
/// （安全性込み）・箇条書きの入れ子・脚注（参照→定義ジャンプ）・見出し6段階。
/// 加えて、インライン記法を1個のトークナイザで左から順に解析する設計にした理由となった
/// 入り組んだケース（太字の中の斜体・コード内のアスタリスク・リンク内の装飾・
/// <c>2 * 3 = 6</c>・エスケープ）と、実機不具合の回帰（チェックボックスの左端の欠け）を
/// 直接<see cref="ManualMarkdownRenderer.Render"/>を呼んで検証する（<see cref="MarkdownPreviewTests"/>・
/// <see cref="ManualWindowTests"/>はEditorPane/ManualWindow経由の統合的な確認を担当し、
/// こちらはレンダラ単体の記法カバレッジに専念する）。
/// </summary>
public class ManualMarkdownRendererGfmTests : IDisposable
{
    // 1x1透明PNG。テストで「実在する画像ファイル」を用意するための最小限のバイト列。
    private static readonly byte[] TinyPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "graft-md-gfm", Guid.NewGuid().ToString("N"));
    private readonly ShownWindowTracker _windows = new();

    public ManualMarkdownRendererGfmTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _windows.Dispose();
        TempDirectoryCleanup.TryDeleteRecursive(_root);
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // ホスト用ヘルパ: レンダリング結果を実際のウィンドウへ載せて視覚ツリーを辿れるようにする。
    // ------------------------------------------------------------------

    private Window Host(string markdown, Action<string>? onToc = null, Action<string>? onRelative = null,
        Action<string>? onExternal = null, string? baseDirectory = null)
    {
        var result = ManualMarkdownRenderer.Render(
            markdown, onToc ?? (_ => { }), onRelative, onExternal, baseDirectory: baseDirectory);
        var panel = new StackPanel();
        foreach (var block in result.Blocks) panel.Children.Add(block.Control);
        var window = _windows.Track(new Window { Width = 900, Height = 700, Content = panel });
        window.Show();
        return window;
    }

    private static IEnumerable<Inline> Flatten(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
            {
                foreach (var child in Flatten(span.Inlines)) yield return child;
            }
        }
    }

    private static IEnumerable<Inline> AllInlines(Window window)
        => window.GetVisualDescendants().OfType<SelectableTextBlock>()
            .SelectMany(t => Flatten(t.Inlines ?? new InlineCollection()))
            .Concat(window.GetVisualDescendants().OfType<TextBlock>().Where(t => t is not SelectableTextBlock)
                .SelectMany(t => Flatten(t.Inlines ?? new InlineCollection())));

    private static string TextOf(Inline inline) => inline switch
    {
        Run run => run.Text ?? string.Empty,
        Span span => string.Concat(span.Inlines.Select(TextOf)),
        _ => string.Empty,
    };

    private static string AllText(Window window)
        => string.Join(string.Empty, window.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Select(t => t.Inlines?.Text ?? t.Text ?? string.Empty));

    // ------------------------------------------------------------------
    // 1. 斜体
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "*text*と_text_の両方が斜体（FontStyle.Italic）になる")]
    public void 斜体が両方の記法で描画される()
    {
        var window = Host("これは*斜体アスタリスク*です。そして_斜体アンダースコア_です。\n");

        var italics = AllInlines(window).OfType<Span>().Where(s => s.FontStyle == FontStyle.Italic).ToList();
        italics.Should().Contain(s => TextOf(s) == "斜体アスタリスク");
        italics.Should().Contain(s => TextOf(s) == "斜体アンダースコア");
    }

    // ------------------------------------------------------------------
    // 2. 打ち消し線
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "~~text~~がTextDecorations.Strikethroughになる")]
    public void 打ち消し線が描画される()
    {
        var window = Host("これは~~打ち消し~~です。\n");

        var strikes = AllInlines(window).OfType<Span>()
            .Where(s => s.TextDecorations is not null && s.TextDecorations.Count > 0).ToList();
        strikes.Should().Contain(s => TextOf(s) == "打ち消し");
    }

    // ------------------------------------------------------------------
    // 3. 引用（単純・入れ子・箇条書き/コードブロックの内包）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "引用行は左に太い枠線を持つBorderで囲まれ、本文が読める")]
    public void 引用が描画される()
    {
        var window = Host("> 引用1行目\n> 続き\n");

        var quoteBorder = window.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.BorderThickness.Left > 0 && b.BorderThickness is { Top: 0, Right: 0, Bottom: 0 });
        quoteBorder.Should().NotBeNull("引用は左枠線だけを持つBorderで表現されるはず");

        var text = AllText(window);
        text.Should().Contain("引用1行目");
        text.Should().Contain("続き");
    }

    [AvaloniaFact(DisplayName = "入れ子の引用（>>）は引用の中にさらに引用のBorderが入る")]
    public void 入れ子の引用が描画される()
    {
        var window = Host("> レベル1\n>> レベル2\n");

        var quoteBorders = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.BorderThickness.Left > 0 && b.BorderThickness is { Top: 0, Right: 0, Bottom: 0 })
            .ToList();
        quoteBorders.Should().HaveCountGreaterThanOrEqualTo(2, "引用の中にさらに引用のBorderが入れ子になっているはず");

        var outer = quoteBorders[0];
        var nestedInsideOuter = outer.GetVisualDescendants().OfType<Border>()
            .Any(b => b.BorderThickness.Left > 0 && b.BorderThickness is { Top: 0, Right: 0, Bottom: 0 });
        nestedInsideOuter.Should().BeTrue("外側の引用Borderの中に内側の引用Borderが入っている必要がある");

        AllText(window).Should().Contain("レベル2");
    }

    [AvaloniaFact(DisplayName = "引用の中に箇条書き・コードブロックが入っても破綻しない")]
    public void 引用の中の箇条書きとコードブロックが破綻しない()
    {
        var md = "> 見出し的な説明\n>\n> - 項目A\n> - 項目B\n>\n> ```\n> code_in_quote\n> ```\n";
        var window = Host(md);

        var text = AllText(window);
        text.Should().Contain("項目A");
        text.Should().Contain("項目B");
        text.Should().Contain("code_in_quote");

        var bullets = window.GetVisualDescendants().OfType<SelectableTextBlock>().Count(t => t.Text == "•");
        bullets.Should().Be(2, "引用の中の箇条書きも通常どおり2個の行頭記号を持つはず");
    }

    // ------------------------------------------------------------------
    // 4. 画像（安全性込み）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "プロジェクト内の相対パス画像は自動で読み込んで表示される")]
    public void ローカル画像が自動で読み込まれる()
    {
        var imagePath = Path.Combine(_root, "pic.png");
        File.WriteAllBytes(imagePath, TinyPngBytes);

        var window = Host("本文 ![説明文](./pic.png) 続き\n", baseDirectory: _root);

        var image = window.GetVisualDescendants().OfType<Image>().SingleOrDefault();
        image.Should().NotBeNull("存在するローカル画像はImageコントロールとして描画されるはず");
        image!.Source.Should().NotBeNull();
        image.MaxWidth.Should().BeLessThanOrEqualTo(600, "大きな画像でもレイアウトが崩れないよう幅の上限が必要");
    }

    [AvaloniaFact(DisplayName = "見つからないローカル画像は例外にならず代替表示になる")]
    public void 見つからない画像は代替表示になる()
    {
        var act = () => Host("![無い画像](./missing.png)\n", baseDirectory: _root);
        act.Should().NotThrow();

        var window = Host("![無い画像](./missing.png)\n", baseDirectory: _root);
        window.GetVisualDescendants().OfType<Image>().Should().BeEmpty("見つからない画像はImageを作らないはず");
        AllText(window).Should().Contain("無い画像", "alt文字を含む代替表示になるはず");
    }

    [AvaloniaFact(DisplayName = "壊れた（デコードできない）画像ファイルも例外にならず代替表示になる")]
    public void 壊れた画像は代替表示になる()
    {
        var imagePath = Path.Combine(_root, "broken.png");
        File.WriteAllText(imagePath, "これは画像ではない");

        var act = () => Host("![壊れた画像](./broken.png)\n", baseDirectory: _root);
        act.Should().NotThrow();

        var window = Host("![壊れた画像](./broken.png)\n", baseDirectory: _root);
        window.GetVisualDescendants().OfType<Image>().Should().BeEmpty();
        AllText(window).Should().Contain("壊れた画像");
    }

    [AvaloniaFact(DisplayName = "外部（https）画像は自動で読み込まれず、クリックで開くプレースホルダになる")]
    public void 外部画像は自動で読み込まれない()
    {
        var openedUrls = new List<string>();
        var window = Host("![外部の絵](https://example.com/pic.png)\n", onExternal: openedUrls.Add, baseDirectory: _root);

        window.GetVisualDescendants().OfType<Image>().Should().BeEmpty("外部画像を自動で読み込んではならない");
        var placeholder = window.GetVisualDescendants().OfType<Button>()
            .SingleOrDefault(b => (b.Content as string)?.Contains("クリックで開く") == true);
        placeholder.Should().NotBeNull("外部画像は「クリックで開く」プレースホルダのボタンになるはず");

        placeholder!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        openedUrls.Should().ContainSingle().Which.Should().Be("https://example.com/pic.png",
            "既存の外部リンクと同じハンドラ（確認ダイアログ経由）へ委譲されるはず");
    }

    [AvaloniaFact(DisplayName = "外部画像ハンドラを渡さない場合（取扱説明書相当）は非活性のプレーンテキストになる")]
    public void 外部画像はハンドラ無しでは非活性表示になる()
    {
        var window = Host("![外部の絵](https://example.com/pic.png)\n");

        window.GetVisualDescendants().OfType<Image>().Should().BeEmpty();
        window.GetVisualDescendants().OfType<Button>().Should().BeEmpty("ハンドラが無ければボタン化しないはず");
        AllText(window).Should().Contain("外部画像");
    }

    // ------------------------------------------------------------------
    // 5. 箇条書きの入れ子
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "箇条書きの入れ子（- と番号付きの混在）が正しく描画される")]
    public void 箇条書きの入れ子が描画される()
    {
        var md = "- 親A\n  - 子A1\n  - 子A2\n    1. 孫1\n    2. 孫2\n- 親B\n";
        var window = Host(md);

        var text = AllText(window);
        foreach (var expected in new[] { "親A", "子A1", "子A2", "孫1", "孫2", "親B" })
        {
            text.Should().Contain(expected);
        }

        var bulletCount = window.GetVisualDescendants().OfType<SelectableTextBlock>().Count(t => t.Text == "•");
        bulletCount.Should().Be(4, "親A・子A1・子A2・親Bの4個が箇条書きの行頭記号を持つはず");

        var orderedMarkers = window.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Select(t => t.Text).Where(t => t is "1." or "2.").ToList();
        orderedMarkers.Should().BeEquivalentTo(new[] { "1.", "2." }, "孫の番号付きリストが1.と2.で描画されるはず");
    }

    // ------------------------------------------------------------------
    // 6. 脚注
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "脚注参照をクリックすると定義へのジャンプ（onTocLinkClicked経由）が呼ばれる")]
    public void 脚注参照から定義へジャンプできる()
    {
        var md = "本文中の参照[^1]です。\n\n[^1]: 脚注の中身です。\n";
        var jumped = new List<string>();
        var window = Host(md, onToc: jumped.Add);

        AllText(window).Should().Contain("脚注の中身です。", "脚注定義が文末に描画されるはず");

        var refButton = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "[1]"));
        refButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        jumped.Should().ContainSingle().Which.Should().Be("fn:1", "脚注専用のアンカー名（fn:プレフィックス）でジャンプハンドラが呼ばれるはず");
    }

    [AvaloniaFact(DisplayName = "定義の無い脚注参照はクリックできないプレーンテキストのまま残る")]
    public void 定義の無い脚注参照はプレーンテキストになる()
    {
        var window = Host("参照だけ[^未定義]あり。\n");

        window.GetVisualDescendants().OfType<Button>().Should().NotContain(b => Equals(b.Content, "[未定義]"));
        AllText(window).Should().Contain("[^未定義]", "定義が無い参照はそのままの文字列で表示されるはず");
    }

    // ------------------------------------------------------------------
    // 7. 見出し6段階
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "見出しは#〜######の6段階すべてが描画され、上位ほど大きい")]
    public void 見出し6段階が描画される()
    {
        var md = "# h1\n## h2\n### h3\n#### h4\n##### h5\n###### h6\n";
        var window = Host(md);

        SelectableTextBlock Find(string text) => window.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Single(t => (t.Inlines?.Text ?? t.Text) == text);

        var h1 = Find("h1"); var h2 = Find("h2"); var h3 = Find("h3");
        var h4 = Find("h4"); var h5 = Find("h5"); var h6 = Find("h6");

        h1.FontSize.Should().BeGreaterThan(h2.FontSize);
        h2.FontSize.Should().BeGreaterThan(h3.FontSize);
        h3.FontSize.Should().BeGreaterThan(h4.FontSize);
        h4.FontSize.Should().BeGreaterThan(h5.FontSize);
        h5.FontSize.Should().BeGreaterThanOrEqualTo(h6.FontSize);

        foreach (var h in new[] { h1, h2, h3, h4, h5, h6 }) h.FontWeight.Should().Be(FontWeight.SemiBold);
    }

    // ------------------------------------------------------------------
    // 入り組んだケース（利用者指示で明示された回帰）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "太字の中の斜体（**太字の中の*斜体***）が入れ子で正しく描画される")]
    public void 太字の中の斜体が入れ子で描画される()
    {
        var window = Host("**太字の中の*斜体***\n");

        var text = AllText(window);
        text.Should().Be("太字の中の斜体", "余分な*が残らず、装飾記号を除いた本文どおりのテキストになるはず");

        var boldSpans = AllInlines(window).OfType<Span>().Where(s => s.FontWeight == FontWeight.Bold).ToList();
        boldSpans.Should().Contain(s => TextOf(s) == "太字の中の斜体", "全体が太字で囲まれているはず");

        var italicInsideBold = boldSpans
            .SelectMany(s => Flatten(s.Inlines))
            .OfType<Span>()
            .Where(s => s.FontStyle == FontStyle.Italic)
            .ToList();
        italicInsideBold.Should().Contain(s => TextOf(s) == "斜体", "太字の中に斜体が入れ子になっているはず");
    }

    [AvaloniaFact(DisplayName = "コード内の**アスタリスク**は装飾されず、そのままの文字列で表示される")]
    public void コード内のアスタリスクは装飾されない()
    {
        var window = Host("`コード内の**アスタリスク**`\n");

        var boldSpans = AllInlines(window).OfType<Span>().Where(s => s.FontWeight == FontWeight.Bold).ToList();
        boldSpans.Should().BeEmpty("コード内の**はコードの一部であり太字として解釈してはならない");

        AllText(window).Should().Contain("コード内の**アスタリスク**", "コード内の**はそのままの文字として残るはず");
    }

    [AvaloniaFact(DisplayName = "リンクの中の装飾（[**太字**のリンク](url)）が保たれたままクリックできる")]
    public void リンク内の装飾が保たれる()
    {
        var openedUrls = new List<string>();
        var window = Host("[**太字**のリンク](https://example.com/x)\n", onExternal: openedUrls.Add);

        var button = window.GetVisualDescendants().OfType<Button>().Single();
        var innerText = button.Content switch
        {
            string s => s,
            TextBlock tb => tb.Inlines?.Text ?? tb.Text ?? string.Empty,
            _ => string.Empty,
        };
        innerText.Should().Be("太字のリンク");

        if (button.Content is TextBlock contentBlock)
        {
            var boldInLink = Flatten(contentBlock.Inlines ?? new InlineCollection())
                .OfType<Span>().Where(s => s.FontWeight == FontWeight.Bold).ToList();
            boldInLink.Should().Contain(s => TextOf(s) == "太字", "リンクテキスト中の太字が保たれているはず");
        }
        else
        {
            Assert.Fail("装飾を含むリンクテキストはTextBlockへ包まれているはず");
        }

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        openedUrls.Should().ContainSingle().Which.Should().Be("https://example.com/x");
    }

    [AvaloniaFact(DisplayName = "前後に空白を挟む2 * 3 = 6は強調と解釈されず、そのまま表示される")]
    public void アスタリスクを含む式は強調と解釈されない()
    {
        var window = Host("計算式は 2 * 3 = 6 です。\n");

        AllText(window).Should().Contain("2 * 3 = 6");
        AllInlines(window).OfType<Span>().Where(s => s.FontWeight == FontWeight.Bold || s.FontStyle == FontStyle.Italic)
            .Should().BeEmpty("前後が空白のアスタリスクは強調として解釈してはならない");
    }

    [AvaloniaFact(DisplayName = "エスケープ（\\*・\\_・\\`）は装飾記号として解釈されず、そのままの文字になる")]
    public void エスケープされた記号は装飾されない()
    {
        var window = Host(@"\*これは斜体ではない\* \_これも斜体ではない\_ \`これはコードではない\`" + "\n");

        var text = AllText(window);
        text.Should().Contain("*これは斜体ではない*");
        text.Should().Contain("_これも斜体ではない_");
        text.Should().Contain("`これはコードではない`");

        AllInlines(window).OfType<Span>().Where(s => s.FontWeight == FontWeight.Bold || s.FontStyle == FontStyle.Italic)
            .Should().BeEmpty("エスケープされたアスタリスク・アンダースコアは強調にならないはず");
    }

    // ------------------------------------------------------------------
    // 実機不具合の回帰: チェックボックスの左端の欠け・表示専用/操作可能の切り替え
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "実機不具合の回帰: チェックボックスは行の左境界内に収まり、左端が欠けない")]
    public void チェックボックスは左端が欠けない()
    {
        var window = Host("- [ ] 未完了\n- [x] 完了\n- 通常の項目\n");
        window.CaptureRenderedFrame();

        var checkboxes = window.GetVisualDescendants().OfType<CheckBox>().ToList();
        checkboxes.Should().HaveCount(2);
        foreach (var checkbox in checkboxes)
        {
            checkbox.Bounds.X.Should().BeGreaterThanOrEqualTo(
                0, "チェックボックスの左端が親の境界より外（負の位置）へはみ出してはならない（実機不具合の回帰）");
        }

        // チェックボックス・通常の箇条書きの行頭記号は同じ列幅（22px）のGridセル（列0）に
        // 置かれるため、各行のGridの列0の幅が一致する＝行頭の位置が視覚的に揃う。
        var rows = window.GetVisualDescendants().OfType<Grid>()
            .Where(g => g.ColumnDefinitions.Count == 2 && g.Children.Count == 2).ToList();
        var columnWidths = rows.Select(g => g.ColumnDefinitions[0].ActualWidth).Distinct().ToList();
        columnWidths.Should().ContainSingle(
            "チェックボックス行・通常の箇条書き行のマーカー列幅が同じ（22px）で、行頭が揃っている必要がある");
    }

    [AvaloniaFact(DisplayName = "onChecklistToggledを渡さない場合（取扱説明書相当）チェックボックスは表示専用のまま")]
    public void チェックリストはハンドラ無しでは表示専用になる()
    {
        var window = Host("- [ ] 未完了\n- [x] 完了\n");

        var checkboxes = window.GetVisualDescendants().OfType<CheckBox>().ToList();
        checkboxes.Should().HaveCount(2);
        checkboxes.Should().OnlyContain(c => !c.IsHitTestVisible, "onChecklistToggled未指定時は表示専用（ManualWindowと同じ）でなければならない");
    }

    [AvaloniaFact(DisplayName = "onChecklistToggledを渡すとチェックボックスが操作可能になる")]
    public void チェックリストはハンドラ指定時に操作可能になる()
    {
        var toggled = new List<(int Line, bool Checked)>();
        var result = ManualMarkdownRenderer.Render(
            "- [ ] 未完了\n", _ => { }, onChecklistToggled: (line, isChecked) => toggled.Add((line, isChecked)));
        var panel = new StackPanel();
        foreach (var block in result.Blocks) panel.Children.Add(block.Control);
        var window = _windows.Track(new Window { Content = panel });
        window.Show();

        var checkbox = window.GetVisualDescendants().OfType<CheckBox>().Single();
        checkbox.IsHitTestVisible.Should().BeTrue();
        checkbox.Focusable.Should().BeTrue("Tabでのキーボード操作にはFocusableである必要がある");
    }
}
