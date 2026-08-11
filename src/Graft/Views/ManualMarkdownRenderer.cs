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

namespace Graft.Views;

/// <summary>
/// 取扱説明書機能: <c>docs/取扱説明書.md</c> を装飾表示するための自前の軽量Markdownレンダラ。
///
/// 【方針】外部のMarkdownレンダリングライブラリは追加しない（依存を最小限に保つ方針。
/// Graft.csproj参照）。実際に取扱説明書.mdで使われている記法だけを対象にした簡易パーサで、
/// Avaloniaの標準コントロール（<see cref="SelectableTextBlock"/>・<see cref="Border"/>・
/// <see cref="Grid"/>・<see cref="Button"/>）へ組み立てる。汎用のMarkdown仕様（表の配置指定・
/// 入れ子リスト・イタリック・画像・見出しレベル4以上など）は取扱説明書.mdで使われていないため
/// 意図的に対応しない。
///
/// 対応する記法:
/// - 見出し（#〜###。文書内に#/##/###のみ存在）
/// - 段落（**強調**・`インラインコード`を含む）
/// - 箇条書き（- ）・番号付きリスト（1. 2. ...）
///   - 番号付きリストの項目直下に3スペースインデントで続く説明文（2.2節）は、同じ項目の
///     続きとして扱う（改行を挟んで同じ項目内に表示する）。
///   - 目次（## 目次）の各項目は <c>[見出し名](#アンカー)</c> の形なので、リンクとして
///     特別扱いし、クリックで該当見出しへスクロールする（目次ジャンプ）。それ以外の箇所に
///     Markdownリンクは登場しないため、リンクの一般対応はしない。
/// - コードブロック（```で囲む。言語指定は無視してよい内容のみ）
/// - 表（| セル | セル |形式。ヘッダー行・区切り行・データ行）
/// - 水平線（---）
///
/// 見出しのアンカー名はGitHub互換のスラグ化規則（小文字化・空白をハイフンへ・英数字と
/// アンダースコア・非ASCII文字以外の記号を除去）で生成する。取扱説明書.md内の目次リンク
/// （例: <c>#1-graftとは何か</c>）はこの規則で実際に一致することを確認済み。
/// </summary>
internal static class ManualMarkdownRenderer
{
    /// <summary>レンダリング結果。Blocksを表示用パネルへ並べ、Anchorsで目次ジャンプ先を引く。</summary>
    internal sealed class RenderResult
    {
        public required IReadOnlyList<Control> Blocks { get; init; }
        public required IReadOnlyDictionary<string, Control> Anchors { get; init; }
    }

    // **強調** / `インラインコード` を検出する。取扱説明書.mdにイタリック(*text*)・
    // 打ち消し線などは登場しないため対応しない（ファイル冒頭のコメント参照）。
    private static readonly Regex InlinePattern =
        new(@"\*\*(?<bold>[^*]+)\*\*|`(?<code>[^`]+)`", RegexOptions.Compiled);

    private static readonly Regex UnorderedListItemPattern = new(@"^\s*[-*]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex OrderedListItemPattern = new(@"^\s*(\d+)\.\s+(.*)$", RegexOptions.Compiled);

    // 目次の項目「[見出し名](#アンカー)」だけを特別扱いするための全文一致パターン。
    private static readonly Regex FullLinkPattern =
        new(@"^\[(?<text>[^\]]+)\]\((?<anchor>#[^)]+)\)$", RegexOptions.Compiled);

    public static RenderResult Render(string markdown, Action<string> onTocLinkClicked)
    {
        var blocks = new List<Control>();
        var anchors = new Dictionary<string, Control>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                i++;
                var codeLines = new List<string>();
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }
                i++; // 閉じ```を読み飛ばす（末尾に無い壊れたMarkdownでもi<=lines.Lengthで止まる）
                blocks.Add(BuildCodeBlock(string.Join('\n', codeLines)));
                continue;
            }

            if (line.StartsWith('#'))
            {
                var level = 0;
                while (level < line.Length && line[level] == '#') level++;
                var text = line[level..].Trim();
                var heading = BuildHeading(text, level);
                blocks.Add(heading);
                anchors[Slugify(text)] = heading;
                i++;
                continue;
            }

            if (line.Trim() == "---")
            {
                blocks.Add(BuildHorizontalRule());
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
                blocks.Add(BuildTable(tableLines));
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
                blocks.Add(BuildUnorderedList(items));
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
                blocks.Add(BuildOrderedList(items, onTocLinkClicked));
                continue;
            }

            // それ以外は段落。空行・新しいブロックの開始行が来るまで1つの段落として連結する。
            var paragraphLines = new List<string>();
            while (i < lines.Length && !IsBlockBoundary(lines[i]))
            {
                paragraphLines.Add(lines[i]);
                i++;
            }
            blocks.Add(BuildParagraph(string.Join(string.Empty, paragraphLines)));
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

    private static Control BuildHeading(string text, int level)
    {
        var block = new SelectableTextBlock
        {
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        BindForeground(block, "TextPrimary");
        AddInlineRuns(block.Inlines!, text);

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

    private static Control BuildParagraph(string text)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        BindForeground(block, "TextPrimary");
        AddInlineRuns(block.Inlines!, text);
        return block;
    }

    private static Control BuildUnorderedList(IReadOnlyList<string> items)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8), Spacing = 4 };
        foreach (var item in items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*") };
            var bullet = new SelectableTextBlock { Text = "•" };
            BindForeground(bullet, "TextPrimary");
            Grid.SetColumn(bullet, 0);

            var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
            BindForeground(text, "TextPrimary");
            AddInlineRuns(text.Inlines!, item);
            Grid.SetColumn(text, 1);

            row.Children.Add(bullet);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return panel;
    }

    private static Control BuildOrderedList(IReadOnlyList<string> items, Action<string> onTocLinkClicked)
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
                    AddInlineRuns(text.Inlines!, parts[p]);
                }
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
            }

            panel.Children.Add(row);
        }
        return panel;
    }

    private static Control BuildCodeBlock(string code)
    {
        var text = new SelectableTextBlock
        {
            Text = code,
            TextWrapping = TextWrapping.NoWrap,
        };
        text.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("CodeFontFamily"));
        text.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("CodeFontSize"));
        BindForeground(text, "TextPrimary");

        var scroll = new ScrollViewer
        {
            Content = text,
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

    private static Control BuildHorizontalRule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 16) };
        BindBackground(rule, "BorderSubtle");
        return rule;
    }

    private static Control BuildTable(IReadOnlyList<string> tableLines)
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
                AddInlineRuns(textBlock.Inlines!, cellText);
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
    // インライン装飾（**強調**・`インラインコード`）
    // ------------------------------------------------------------------

    private static void AddInlineRuns(InlineCollection inlines, string text)
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
            else
            {
                var codeRun = new Run(m.Groups["code"].Value);
                codeRun.Bind(TextElement.FontFamilyProperty, new DynamicResourceExtension("CodeFontFamily"));
                codeRun.Bind(TextElement.BackgroundProperty, new DynamicResourceExtension("BgSurface"));
                inlines.Add(codeRun);
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length) inlines.Add(new Run(text[pos..]));
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
