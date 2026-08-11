using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Graft.Core;

namespace Graft.Views;

/// <summary>
/// 自前の軽量Markdownレンダラ。外部のMarkdownレンダリングライブラリは追加しない
/// （依存を最小限に保つ方針。Graft.csproj参照）。Avaloniaの標準コントロール
/// （<see cref="SelectableTextBlock"/>・<see cref="Border"/>・<see cref="Grid"/>・
/// <see cref="Button"/>・<see cref="CheckBox"/>）へ組み立てる。
///
/// 【由来】元は取扱説明書機能（<c>docs/取扱説明書.md</c>を装飾表示する<see cref="ManualWindow"/>）
/// 専用の簡易パーサとして新設された。その後、エディタの.mdファイルプレビュー機能
/// （<see cref="MarkdownPreviewView"/>）でも同じ描画基盤を再利用したいという要件を受け、
/// フォークせずこのクラス自体を拡張した（取扱説明書機能・プレビュー機能の両方から使う
/// 共用レンダラ）。既存の<see cref="ManualWindowTests"/>が示す取扱説明書側の挙動は
/// 変えないまま、以下を追加している。
///
/// 対応する記法:
/// - 見出し（#〜###。文書内に#/##/###のみ存在）
/// - 段落（**強調**・`インラインコード`・<c>[テキスト](URL)</c>形式のリンクを含む）
/// - 箇条書き（- ）・番号付きリスト（1. 2. ...）
///   - 番号付きリストの項目直下に3スペースインデントで続く説明文（取扱説明書の2.2節）は、
///     同じ項目の続きとして扱う（改行を挟んで同じ項目内に表示する）。
///   - 目次（## 目次）の各項目のように、箇条書き・番号付きリストの項目全体が
///     <c>[見出し名](#アンカー)</c> の形になっている場合は、項目全体をリンクとして描画する
///     （取扱説明書のTOCジャンプ用の特別扱い。<see cref="FullLinkPattern"/>）。
///   - GitHub形式のチェックリスト（<c>- [ ] 未完了</c> / <c>- [x] 完了</c>）はチェックボックスで
///     描画する。表示専用（クリックしてもファイルを書き換えない。詳細は
///     <see cref="BuildUnorderedList"/>のコメント参照）。
/// - コードブロック（```で囲む）。フェンス直後の言語指定（例: <c>```python</c>）を読み取り、
///   対応する言語なら<see cref="SyntaxLexer"/>（構文強調機能で使う自前レキサと同じもの）で
///   色を付ける。言語指定が無い・未対応の場合は等幅のまま色を付けない
///   （<see cref="ResolveLanguageRule"/>参照）。
/// - 表（| セル | セル |形式。ヘッダー行・区切り行・データ行）
/// - 水平線（---）
/// - 段落中のリンク（<c>[テキスト](URL)</c>）: URLの形から3種類に分類して扱いを変える
///   （<see cref="ClassifyLink"/>）。
///   - <c>#アンカー</c>: 同一文書内の見出しへジャンプ（取扱説明書のTOCと同じ経路）。
///   - <c>http://</c>・<c>https://</c>等の絶対URI: 呼び出し側が渡したハンドラ
///     （<see cref="MarkdownPreviewView"/>では確認ダイアログを経てブラウザで開く）。
///   - それ以外（相対パスとみなす）: 呼び出し側が渡したハンドラ（<see cref="MarkdownPreviewView"/>
///     ではGraftのタブとして開く）。
///   ハンドラが渡されていない種別のリンク（<see cref="ManualWindow"/>は相対・外部リンクの
///   ハンドラを渡さない）はクリックしても何も起きないプレーンテキストとして表示する
///   （壊れたリンク・未対応のリンク種別で例外にしない）。
///
/// 見出しのアンカー名はGitHub互換のスラグ化規則（小文字化・空白をハイフンへ・英数字と
/// アンダースコア・非ASCII文字以外の記号を除去）で生成する。取扱説明書.md内の目次リンク
/// （例: <c>#1-graftとは何か</c>）はこの規則で実際に一致することを確認済み。
///
/// 【ブロックと元の行番号】
/// <see cref="RenderedBlock.StartLine"/>で各ブロックの元Markdown内での開始行（1始まり）を
/// 保持する。<see cref="MarkdownPreviewView"/>がプレビュー本文のダブルクリックで対応する行へ
/// カーソルを置く機能（利用者指示の追加要件3）・モード切替時のスクロール位置の目安として使う。
/// </summary>
internal static class ManualMarkdownRenderer
{
    /// <summary>1個のブロックとその描画結果。<see cref="StartLine"/>は元Markdown内の開始行（1始まり）。</summary>
    internal readonly record struct RenderedBlock(Control Control, int StartLine);

    /// <summary>レンダリング結果。Blocksを表示用パネルへ並べ、Anchorsで目次ジャンプ先を引く。</summary>
    internal sealed class RenderResult
    {
        public required IReadOnlyList<RenderedBlock> Blocks { get; init; }
        public required IReadOnlyDictionary<string, Control> Anchors { get; init; }
    }

    /// <summary>
    /// 段落中のリンク（<c>[テキスト](URL)</c>）を種別ごとに振り分けるハンドラの束。
    /// いずれも未指定（null）で構わない。その場合、該当種別のリンクはクリックしても
    /// 何も起きないプレーンテキストとして表示される（<see cref="BuildLinkInline"/>参照）。
    /// </summary>
    private sealed class LinkHandlers
    {
        public Action<string>? OnAnchorClicked;
        public Action<string>? OnRelativeLinkClicked;
        public Action<string>? OnExternalLinkClicked;
    }

    // **強調** / `インラインコード` / [テキスト](URL) を検出する。イタリック(*text*)・
    // 打ち消し線などは対象記法（クラス冒頭のコメント）に含まれないため対応しない。
    private static readonly Regex InlinePattern = new(
        @"\*\*(?<bold>[^*]+)\*\*|`(?<code>[^`]+)`|\[(?<linktext>[^\]]+)\]\((?<linkurl>[^)]+)\)",
        RegexOptions.Compiled);

    private static readonly Regex UnorderedListItemPattern = new(@"^\s*[-*]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex OrderedListItemPattern = new(@"^\s*(\d+)\.\s+(.*)$", RegexOptions.Compiled);

    // GitHub形式のチェックリスト項目（- [ ] / - [x]）。大文字Xも許容する。
    private static readonly Regex ChecklistItemPattern =
        new(@"^\[(?<mark>[ xX])\]\s+(?<rest>.*)$", RegexOptions.Compiled);

    // 目次の項目「[見出し名](#アンカー)」だけを特別扱いするための全文一致パターン。
    private static readonly Regex FullLinkPattern =
        new(@"^\[(?<text>[^\]]+)\]\((?<anchor>#[^)]+)\)$", RegexOptions.Compiled);

    // コードフェンス直後の言語指定（```python 等）から、SyntaxLexer.RuleForExtensionが
    // 認識する拡張子へのエイリアス表。フェンスの言語名は慣習的に多様な書き方がある
    // （python/py、csharp/cs/c#等）ため、代表的な書き方を吸収する。未知の指定は
    // ResolveLanguageRuleがnullを返し、色を付けずに等幅表示へフォールバックする。
    private static readonly Dictionary<string, string> CodeFenceLanguageAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["python"] = "py",
            ["py"] = "py",
            ["csharp"] = "cs",
            ["cs"] = "cs",
            ["c#"] = "cs",
            ["javascript"] = "js",
            ["js"] = "js",
            ["typescript"] = "ts",
            ["ts"] = "ts",
            ["jsx"] = "jsx",
            ["tsx"] = "tsx",
            ["html"] = "html",
            ["htm"] = "htm",
            ["xml"] = "xml",
            ["markdown"] = "md",
            ["md"] = "md",
            ["css"] = "css",
            ["json"] = "json",
            ["yaml"] = "yaml",
            ["yml"] = "yml",
            ["sql"] = "sql",
            ["shell"] = "sh",
            ["bash"] = "sh",
            ["sh"] = "sh",
        };

    /// <summary>
    /// Markdownを描画する。<paramref name="onTocLinkClicked"/>は既存の必須引数（同一文書内
    /// アンカーへのジャンプ。取扱説明書のTOC・段落中の<c>#アンカー</c>リンクの双方で使う）。
    /// それ以外は新設の任意引数で、いずれも未指定（null）なら該当機能は無効化される
    /// （<see cref="ManualWindow"/>は指定しないため、これまでどおりの挙動を保つ）。
    /// </summary>
    /// <param name="markdown">描画対象のMarkdown全文。</param>
    /// <param name="onTocLinkClicked">同一文書内アンカー（#で始まるリンク）がクリックされたときに呼ばれる。</param>
    /// <param name="onRelativeLinkClicked">
    /// プロジェクト内の相対パスとみなされるリンクがクリックされたときに呼ばれる
    /// （<see cref="MarkdownPreviewView"/>用。未指定ならその種のリンクは非活性表示になる）。
    /// </param>
    /// <param name="onExternalLinkClicked">
    /// <c>http://</c>・<c>https://</c>等の絶対URIリンクがクリックされたときに呼ばれる
    /// （<see cref="MarkdownPreviewView"/>用。未指定ならその種のリンクは非活性表示になる）。
    /// </param>
    /// <param name="onBlockDoubleClicked">
    /// いずれかのブロックがダブルクリックされたときに、そのブロックの開始行（1始まり）を
    /// 引数に呼ばれる（<see cref="MarkdownPreviewView"/>用。未指定なら配線しない）。
    /// </param>
    public static RenderResult Render(
        string markdown,
        Action<string> onTocLinkClicked,
        Action<string>? onRelativeLinkClicked = null,
        Action<string>? onExternalLinkClicked = null,
        Action<int>? onBlockDoubleClicked = null)
    {
        var links = new LinkHandlers
        {
            OnAnchorClicked = onTocLinkClicked,
            OnRelativeLinkClicked = onRelativeLinkClicked,
            OnExternalLinkClicked = onExternalLinkClicked,
        };

        var blocks = new List<RenderedBlock>();
        var anchors = new Dictionary<string, Control>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;

        void AddBlock(Control control, int startLine)
        {
            if (onBlockDoubleClicked is not null)
            {
                control.DoubleTapped += (_, _) => onBlockDoubleClicked(startLine);
            }
            blocks.Add(new RenderedBlock(control, startLine));
        }

        while (i < lines.Length)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            var startLine = i + 1;

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var language = line[3..].Trim();
                i++;
                var codeLines = new List<string>();
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }
                i++; // 閉じ```を読み飛ばす（末尾に無い壊れたMarkdownでもi<=lines.Lengthで止まる）
                AddBlock(BuildCodeBlock(string.Join('\n', codeLines), language), startLine);
                continue;
            }

            if (line.StartsWith('#'))
            {
                var level = 0;
                while (level < line.Length && line[level] == '#') level++;
                var text = line[level..].Trim();
                var heading = BuildHeading(text, level, links);
                AddBlock(heading, startLine);
                anchors[Slugify(text)] = heading;
                i++;
                continue;
            }

            if (line.Trim() == "---")
            {
                AddBlock(BuildHorizontalRule(), startLine);
                i++;
                continue;
            }

            if (line.TrimStart().StartsWith('|'))
            {
                var tableLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                {
                    tableLines.Add(lines[i]);
                    i++;
                }
                AddBlock(BuildTable(tableLines, links), startLine);
                continue;
            }

            if (UnorderedListItemPattern.IsMatch(line))
            {
                var items = new List<string>();
                while (i < lines.Length && UnorderedListItemPattern.IsMatch(lines[i]))
                {
                    items.Add(UnorderedListItemPattern.Match(lines[i]).Groups[1].Value);
                    i++;
                }
                AddBlock(BuildUnorderedList(items, links), startLine);
                continue;
            }

            if (OrderedListItemPattern.IsMatch(line))
            {
                var items = new List<string>();
                while (i < lines.Length && OrderedListItemPattern.IsMatch(lines[i]))
                {
                    var itemText = OrderedListItemPattern.Match(lines[i]).Groups[2].Value;
                    i++;
                    // 項目直下の字下げされた続き（2.2節）は同じ項目のテキストへ改行して連結する。
                    while (i < lines.Length && IsIndentedContinuation(lines[i]))
                    {
                        itemText += "\n" + lines[i].Trim();
                        i++;
                    }
                    items.Add(itemText);
                }
                AddBlock(BuildOrderedList(items, onTocLinkClicked, links), startLine);
                continue;
            }

            // それ以外は段落。空行・新しいブロックの開始行が来るまで1つの段落として連結する。
            var paragraphLines = new List<string>();
            while (i < lines.Length && !IsBlockBoundary(lines[i]))
            {
                paragraphLines.Add(lines[i]);
                i++;
            }
            AddBlock(BuildParagraph(string.Join(string.Empty, paragraphLines), links), startLine);
        }

        return new RenderResult { Blocks = blocks, Anchors = anchors };
    }

    private static bool IsIndentedContinuation(string line)
        => line.Length > 0 && char.IsWhiteSpace(line[0]) && !string.IsNullOrWhiteSpace(line)
           && !OrderedListItemPattern.IsMatch(line) && !UnorderedListItemPattern.IsMatch(line);

    private static bool IsBlockBoundary(string line)
        => string.IsNullOrWhiteSpace(line)
           || line.StartsWith('#')
           || line.Trim() == "---"
           || line.StartsWith("```", StringComparison.Ordinal)
           || line.TrimStart().StartsWith('|')
           || UnorderedListItemPattern.IsMatch(line)
           || OrderedListItemPattern.IsMatch(line);

    // ------------------------------------------------------------------
    // ブロック構築
    // ------------------------------------------------------------------

    private static Control BuildHeading(string text, int level, LinkHandlers links)
    {
        var block = new SelectableTextBlock
        {
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        BindForeground(block, "TextPrimary");
        AddInlineRuns(block.Inlines!, text, links);

        switch (level)
        {
            case 1:
                block.FontSize = 21;
                block.Margin = new Thickness(0, 0, 0, 8);
                return block;
            case 2:
                block.FontSize = 17;
                var border = new Border
                {
                    Child = block,
                    Margin = new Thickness(0, 24, 0, 8),
                    Padding = new Thickness(0, 0, 0, 6),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                };
                BindBorderBrush(border, "BorderSubtle");
                return border;
            default:
                block.FontSize = 14;
                block.Margin = new Thickness(0, 16, 0, 4);
                return block;
        }
    }

    private static Control BuildParagraph(string text, LinkHandlers links)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        BindForeground(block, "TextPrimary");
        AddInlineRuns(block.Inlines!, text, links);
        return block;
    }

    /// <summary>
    /// 箇条書き。GitHub形式のチェックリスト項目（<c>- [ ] ...</c> / <c>- [x] ...</c>）は
    /// <see cref="CheckBox"/>で描画する。
    ///
    /// 【表示専用にした理由（利用者指示の追加要件5）】
    /// チェックボックスをクリックできるようにすると、その状態変化が元のMarkdownファイルへ
    /// 書き戻されると誤解されやすい。プレビューは「読むための表示」であり、そこからの
    /// 書き換えは利用者の意図しない編集になりうるため、<see cref="InputElement.IsHitTestVisible"/>を
    /// falseにしてポインタ操作を一切受け付けないようにした（クリックしても状態が変わらない、
    /// 見た目どおりの静的な表示に徹する）。チェック状態を変えたい場合は編集モードへ切り替えて
    /// 本文の<c>[ ]</c>/<c>[x]</c>を書き換えてもらう。
    /// </summary>
    private static Control BuildUnorderedList(IReadOnlyList<string> items, LinkHandlers links)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Spacing = 4 };
        foreach (var item in items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*") };
            var checklistMatch = ChecklistItemPattern.Match(item);

            if (checklistMatch.Success)
            {
                var isChecked = checklistMatch.Groups["mark"].Value is "x" or "X";
                var checkbox = new CheckBox
                {
                    IsChecked = isChecked,
                    IsHitTestVisible = false,
                    Focusable = false,
                    Padding = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                AutomationProperties.SetName(checkbox, isChecked ? "完了" : "未完了");
                Grid.SetColumn(checkbox, 0);
                row.Children.Add(checkbox);

                var checklistText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
                BindForeground(checklistText, "TextPrimary");
                AddInlineRuns(checklistText.Inlines!, checklistMatch.Groups["rest"].Value, links);
                Grid.SetColumn(checklistText, 1);
                row.Children.Add(checklistText);
            }
            else
            {
                var bullet = new SelectableTextBlock { Text = "•" };
                BindForeground(bullet, "TextPrimary");
                Grid.SetColumn(bullet, 0);

                var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
                BindForeground(text, "TextPrimary");
                AddInlineRuns(text.Inlines!, item, links);
                Grid.SetColumn(text, 1);

                row.Children.Add(bullet);
                row.Children.Add(text);
            }

            panel.Children.Add(row);
        }
        return panel;
    }

    private static Control BuildOrderedList(IReadOnlyList<string> items, Action<string> onTocLinkClicked, LinkHandlers links)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Spacing = 4 };
        for (var index = 0; index < items.Count; index++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*") };
            var number = new SelectableTextBlock { Text = $"{index + 1}." };
            BindForeground(number, "TextSecondary");
            Grid.SetColumn(number, 0);
            row.Children.Add(number);

            var linkMatch = FullLinkPattern.Match(items[index].Trim());
            if (linkMatch.Success)
            {
                // 目次の項目: クリックで該当見出しへジャンプするリンクとして表示する。
                var anchor = linkMatch.Groups["anchor"].Value.TrimStart('#');
                var linkText = linkMatch.Groups["text"].Value;
                var link = new Button
                {
                    Content = linkText,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                };
                AutomationProperties.SetName(link, $"目次: {linkText}へジャンプ");
                BindForeground(link, "Accent");
                link.Click += (_, _) => onTocLinkClicked(anchor);
                Grid.SetColumn(link, 1);
                row.Children.Add(link);
            }
            else
            {
                var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
                BindForeground(text, "TextPrimary");
                // 項目内の"\n"（2.2節の続き文）は改行として描画する。
                var parts = items[index].Split('\n');
                for (var p = 0; p < parts.Length; p++)
                {
                    if (p > 0) text.Inlines!.Add(new LineBreak());
                    AddInlineRuns(text.Inlines!, parts[p], links);
                }
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
            }

            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>
    /// コードブロック。<paramref name="language"/>が対応言語なら<see cref="SyntaxLexer"/>
    /// （構文強調機能と同じ自前レキサ）で行ごとにトークン化し、テーマの構文色ブラシ
    /// （<c>Themes/Dark.axaml</c>・<c>Themes/Light.axaml</c>の<c>SyntaxXxx</c>）を
    /// <see cref="DynamicResourceExtension"/>で結んで色を付ける。未対応・言語指定無しの場合は
    /// 元どおり単一の<see cref="TextBlock.Text"/>として描画する（色を付けない分、コストも小さい）。
    /// </summary>
    private static Control BuildCodeBlock(string code, string? language)
    {
        var textBlock = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
        };
        textBlock.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("CodeFontFamily"));
        textBlock.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("CodeFontSize"));
        BindForeground(textBlock, "TextPrimary");

        var rule = ResolveLanguageRule(language);
        if (rule is null)
        {
            textBlock.Text = code;
        }
        else
        {
            var codeLines = code.Split('\n');
            var lexer = new SyntaxLexer(rule);
            lexer.Scan(codeLines);
            for (var lineIndex = 0; lineIndex < codeLines.Length; lineIndex++)
            {
                if (lineIndex > 0) textBlock.Inlines!.Add(new LineBreak());
                AddHighlightedCodeLine(textBlock.Inlines!, lexer, lineIndex, codeLines[lineIndex]);
            }
        }

        var scroll = new ScrollViewer
        {
            Content = textBlock,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var border = new Border
        {
            Child = scroll,
            Padding = new Thickness(12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
        };
        BindBackground(border, "BgSurface");
        BindBorderBrush(border, "BorderSubtle");
        return border;
    }

    /// <summary>フェンス直後の言語指定を、構文強調機能が認識する拡張子経由の言語ルールへ解決する。</summary>
    private static LanguageRule? ResolveLanguageRule(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        return CodeFenceLanguageAliases.TryGetValue(language.Trim(), out var extension)
            ? LanguageRule.ForExtension(extension)
            : null;
    }

    /// <summary>1行分をトークン化し、色付きのRunへ分割して追加する。トークンが無ければ無色のまま1個のRunにする。</summary>
    private static void AddHighlightedCodeLine(InlineCollection inlines, SyntaxLexer lexer, int lineIndex, string lineText)
    {
        var tokens = lexer.TokenizeLine(lineIndex, lineText);
        if (tokens.Count == 0)
        {
            if (lineText.Length > 0) inlines.Add(new Run(lineText));
            return;
        }

        var pos = 0;
        foreach (var token in tokens)
        {
            if (token.Start > pos) inlines.Add(new Run(lineText[pos..token.Start]));

            var segment = lineText.Substring(token.Start, token.Length);
            if (token.Kind == TokenKind.Plain)
            {
                inlines.Add(new Run(segment));
            }
            else
            {
                var run = new Run(segment);
                run.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension(SyntaxBrushKeyFor(token.Kind)));
                // 8.6と同じ方針: コメントトークンのみイタリック表示にする（SyntaxHighlightBridge.ApplyColor参照）。
                if (token.Kind == TokenKind.Comment) run.FontStyle = FontStyle.Italic;
                inlines.Add(run);
            }

            pos = token.Start + token.Length;
        }

        if (pos < lineText.Length) inlines.Add(new Run(lineText[pos..]));
    }

    /// <summary>
    /// トークン種別からテーマの構文色ブラシキーへ。<see cref="Graft.Editor.SyntaxHighlightBridge"/>の
    /// <c>ColorKeyFor</c>と同じ対応関係だが、あちらはAvaloniaEditの<c>Color</c>リソース
    /// （<c>SyntaxXxxColor</c>）を都度<see cref="SolidColorBrush"/>へ包む方式、こちらは
    /// <see cref="TextElement.Foreground"/>（<see cref="IBrush"/>）へ直接束ねられるよう、
    /// 同じ色を指す既存の<see cref="SolidColorBrush"/>リソース（<c>SyntaxXxx</c>、末尾Colorなし）
    /// を使う（Themes/Dark.axaml・Themes/Light.axaml参照）。
    /// </summary>
    private static string SyntaxBrushKeyFor(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => "SyntaxKeyword",
        TokenKind.String => "SyntaxString",
        TokenKind.Number => "SyntaxNumber",
        TokenKind.Comment => "SyntaxComment",
        TokenKind.Function => "SyntaxFunction",
        TokenKind.Type => "SyntaxType",
        TokenKind.Operator => "SyntaxOperator",
        _ => "SyntaxPlain",
    };

    private static Control BuildHorizontalRule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 16) };
        BindBackground(rule, "BorderSubtle");
        return rule;
    }

    private static Control BuildTable(IReadOnlyList<string> tableLines, LinkHandlers links)
    {
        var rows = tableLines
            .Select(SplitTableRow)
            .Where(cells => !IsSeparatorRow(cells))
            .ToList();
        var columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.Count);

        var grid = new Grid();
        for (var c = 0; c < columnCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(c == columnCount - 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto));
        }
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var isHeader = r == 0;
            for (var c = 0; c < columnCount; c++)
            {
                var cellText = c < rows[r].Count ? rows[r][c] : string.Empty;
                var cell = new Border
                {
                    Padding = new Thickness(10, 6),
                    BorderThickness = new Thickness(0, 0, c == columnCount - 1 ? 0 : 1, r == rows.Count - 1 ? 0 : 1),
                };
                BindBorderBrush(cell, "BorderSubtle");
                if (isHeader) BindBackground(cell, "BgSurface");

                var textBlock = new SelectableTextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = isHeader ? FontWeight.SemiBold : FontWeight.Normal,
                };
                BindForeground(textBlock, "TextPrimary");
                AddInlineRuns(textBlock.Inlines!, cellText, links);
                cell.Child = textBlock;

                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        var border = new Border
        {
            Child = grid,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
        };
        BindBorderBrush(border, "BorderSubtle");

        return new ScrollViewer
        {
            Content = border,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 8),
        };
    }

    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return trimmed.Split('|').Select(cell => cell.Trim()).ToList();
    }

    private static bool IsSeparatorRow(IReadOnlyList<string> cells)
        => cells.Count > 0 && cells.All(cell => Regex.IsMatch(cell, @"^:?-+:?$"));

    // ------------------------------------------------------------------
    // インライン装飾（**強調**・`インラインコード`・[テキスト](URL)）
    // ------------------------------------------------------------------

    private static void AddInlineRuns(InlineCollection inlines, string text, LinkHandlers links)
    {
        if (text.Length == 0) return;

        var pos = 0;
        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > pos) inlines.Add(new Run(text[pos..m.Index]));

            if (m.Groups["bold"].Success)
            {
                inlines.Add(new Run(m.Groups["bold"].Value) { FontWeight = FontWeight.Bold });
            }
            else if (m.Groups["code"].Success)
            {
                var codeRun = new Run(m.Groups["code"].Value);
                codeRun.Bind(TextElement.FontFamilyProperty, new DynamicResourceExtension("CodeFontFamily"));
                codeRun.Bind(TextElement.BackgroundProperty, new DynamicResourceExtension("BgSurface"));
                inlines.Add(codeRun);
            }
            else
            {
                inlines.Add(BuildLinkInline(m.Groups["linktext"].Value, m.Groups["linkurl"].Value, links));
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length) inlines.Add(new Run(text[pos..]));
    }

    /// <summary>
    /// リンクの種別（<see cref="ClassifyLink"/>）に応じたハンドラが指定されていればクリック可能な
    /// <see cref="InlineUIContainer"/>（中身は透明な<see cref="Button"/>）を、無ければ非活性の
    /// プレーンテキスト（<see cref="Run"/>）を返す。壊れたリンク・未対応のリンク種別で
    /// クリックしても例外にしない・何も起きないようにするための設計（クラス冒頭のコメント参照）。
    /// </summary>
    private static Inline BuildLinkInline(string text, string url, LinkHandlers links)
    {
        var kind = ClassifyLink(url);
        var handler = kind switch
        {
            LinkKind.Anchor => links.OnAnchorClicked,
            LinkKind.External => links.OnExternalLinkClicked,
            _ => links.OnRelativeLinkClicked,
        };

        if (handler is null)
        {
            return new Run(text);
        }

        var target = kind == LinkKind.Anchor ? url.TrimStart('#') : url;
        var button = new Button
        {
            Content = text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        AutomationProperties.SetName(button, $"リンク: {text}");
        BindForeground(button, "Accent");
        button.Click += (_, _) => handler(target);
        return new InlineUIContainer(button);
    }

    private enum LinkKind
    {
        Anchor,
        External,
        Relative,
    }

    /// <summary>
    /// リンクURLを3種類に分類する（利用者指示の追加要件2）。
    /// - <c>#</c>始まり: 同一文書内アンカー。
    /// - スキーム付きの絶対URI（<c>http://</c>・<c>https://</c>・<c>mailto:</c>等）: 外部リンク。
    ///   無警告での遷移は悪意あるMarkdownファイルへの対策として避けたいため、スキームを
    ///   問わず絶対URIはすべて「外部」扱いにして確認ダイアログの対象にする（呼び出し側の
    ///   <see cref="MarkdownPreviewView"/>参照）。
    /// - それ以外: プロジェクト内の相対パスとみなす。
    /// </summary>
    private static LinkKind ClassifyLink(string url)
    {
        if (url.StartsWith('#')) return LinkKind.Anchor;
        if (Uri.TryCreate(url, UriKind.Absolute, out _)) return LinkKind.External;
        return LinkKind.Relative;
    }

    // ------------------------------------------------------------------
    // 見出しアンカー（GitHub互換のスラグ化）
    // ------------------------------------------------------------------

    /// <summary>
    /// 見出しテキストからGitHub互換のアンカー名を作る。取扱説明書.mdの目次リンク
    /// （例: <c>[Graftとは何か](#1-graftとは何か)</c>）はこの規則で生成した値と一致する
    /// （小文字化・空白をハイフンへ・英数字/アンダースコア/非ASCII文字（日本語）以外の
    /// 記号を除去）。
    /// </summary>
    internal static string Slugify(string headingText)
    {
        var lowered = headingText.ToLowerInvariant();
        var sb = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            if (char.IsWhiteSpace(ch)) sb.Append('-');
            else if (ch == '-' || ch == '_' || char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // テーマトークンへのバインド（コードから組み立てる制御にDynamicResourceを効かせる）
    // ------------------------------------------------------------------

    private static void BindForeground(TemplatedControl control, string key)
        => control.Bind(TemplatedControl.ForegroundProperty, new DynamicResourceExtension(key));

    private static void BindForeground(TextBlock block, string key)
        => block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(key));

    private static void BindBackground(Border border, string key)
        => border.Bind(Border.BackgroundProperty, new DynamicResourceExtension(key));

    private static void BindBorderBrush(Border border, string key)
        => border.Bind(Border.BorderBrushProperty, new DynamicResourceExtension(key));
}
