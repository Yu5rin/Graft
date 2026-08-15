using System.IO;
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
using Avalonia.Media.Imaging;
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
/// （<see cref="MarkdownPreviewView"/>）でも同じ描画基盤を再利用したいという要件を受けて拡張し、
/// GitHub形式の基本記法（斜体・打ち消し線・引用・画像・箇条書きの入れ子・脚注・見出し6段階）を
/// 追加する際に、インライン記法の解釈を正規表現の単純な非重複マッチから、1文字ずつ左から
/// 読み進める手書きの字句解析（<see cref="ParseInline"/>）へ全面的に作り直した
/// （太字の中の斜体・コード内のアスタリスク・リンク内の装飾等、入れ子や優先順位が絡む
/// ケースは正規表現の非重複マッチでは表現できないため）。
///
/// 対応する記法:
/// - 見出し（#〜######の6段階）
/// - 段落（**強調**・*斜体*/_斜体_・~~打ち消し線~~・`インラインコード`・
///   <c>[テキスト](URL)</c>形式のリンク・<c>![alt](path)</c>形式の画像・<c>[^id]</c>脚注参照を含む）
/// - 箇条書き（- ）・番号付きリスト（1. 2. ...）。インデントした子項目（箇条書き・番号付きの
///   混在を含む）で入れ子にできる。
///   - チェックリスト（<c>- [ ] 未完了</c> / <c>- [x] 完了</c>）はCheckBoxで描画する。
///     <see cref="Render"/>に <c>onChecklistToggled</c> を渡した場合のみ操作可能（クリック・
///     キーボードでON/OFFでき、呼び出し側へ元Markdownの行番号と新しい状態を通知する）。
///     渡さない場合（<see cref="ManualWindow"/>）は従来どおり表示専用。
/// - 引用（<c>&gt; text</c>）。入れ子の引用（<c>&gt;&gt;</c>）・引用内の箇条書き・コードブロックにも対応する
///   （内部は見出し以下の全ブロック文法を再帰的に適用する）。
/// - コードブロック（```で囲む）。フェンス直後の言語指定（例: <c>```python</c>）を読み取り、
///   対応する言語なら<see cref="SyntaxLexer"/>（構文強調機能で使う自前レキサと同じもの）で
///   色を付ける。言語指定が無い・未対応の場合は等幅のまま色を付けない
///   （<see cref="ResolveLanguageRule"/>参照）。
/// - 表（| セル | セル |形式。ヘッダー行・区切り行・データ行）
/// - 水平線（---）
/// - 段落中のリンク（<c>[テキスト](URL)</c>）: URLの形から3種類に分類して扱いを変える
///   （<see cref="ClassifyLink"/>）。
///   - <c>#アンカー</c>: 同一文書内の見出しへジャンプ（取扱説明書のTOCと同じ経路）。脚注参照
///     （<c>[^id]</c>）も同じ経路で脚注定義へジャンプする（<c>fn:id</c>という専用のアンカー名）。
///   - <c>http://</c>・<c>https://</c>等の絶対URI: 呼び出し側が渡したハンドラ
///     （<see cref="MarkdownPreviewView"/>では確認ダイアログを経てブラウザで開く）。
///   - それ以外（相対パスとみなす）: 呼び出し側が渡したハンドラ（<see cref="MarkdownPreviewView"/>
///     ではGraftのタブとして開く）。
///   ハンドラが渡されていない種別のリンク（<see cref="ManualWindow"/>は相対・外部リンクの
///   ハンドラを渡さない）はクリックしても何も起きないプレーンテキストとして表示する
///   （壊れたリンク・未対応のリンク種別で例外にしない）。
/// - 画像（<c>![alt](path)</c>）: プロジェクト内の相対パス画像のみ自動で読み込んで表示する
///   （<see cref="Render"/>に<c>baseDirectory</c>を渡した場合のみ・幅と高さに上限あり）。
///   <c>http://</c>・<c>https://</c>等の外部画像は自動で読み込まず、既存の外部リンクと同じ
///   確認ダイアログを経てブラウザで開くプレースホルダ表示にする（.mdを開いただけで外部へ
///   通信が飛ぶのを避けるため）。ファイルが無い・壊れている場合も例外にせず代替表示にする。
///
/// 見出しのアンカー名はGitHub互換のスラグ化規則（小文字化・空白をハイフンへ・英数字と
/// アンダースコア・非ASCII文字以外の記号を除去）で生成する。取扱説明書.md内の目次リンク
/// （例: <c>#1-graftとは何か</c>）はこの規則で実際に一致することを確認済み。
///
/// 【ブロックと元の行番号】
/// <see cref="RenderedBlock.StartLine"/>で各ブロックの元Markdown内での開始行（1始まり）を
/// 保持する。<see cref="MarkdownPreviewView"/>がプレビュー本文のダブルクリックで対応する行へ
/// カーソルを置く機能・モード切替時のスクロール位置の目安として使う。引用・表・コードブロック・
/// 箇条書きは（入れ子であっても）1個のブロックとして扱う（ダブルクリックの粒度は引用・表・
/// リストの先頭行止まりで、既存の表・コードブロックと同じ粒度に揃えている）。
/// </summary>
internal static class ManualMarkdownRenderer
{
    /// <summary>1個のブロックとその描画結果。<see cref="StartLine"/>は元Markdown内の開始行（1始まり）。</summary>
    internal readonly record struct RenderedBlock(Control Control, int StartLine);

    /// <summary>レンダリング結果。Blocksを表示用パネルへ並べ、Anchorsで目次・脚注ジャンプ先を引く。</summary>
    internal sealed class RenderResult
    {
        public required IReadOnlyList<RenderedBlock> Blocks { get; init; }
        public required IReadOnlyDictionary<string, Control> Anchors { get; init; }
    }

    /// <summary>
    /// 段落中のリンク（<c>[テキスト](URL)</c>）を種別ごとに振り分けるハンドラの束。
    /// いずれも未指定（null）で構わない。その場合、該当種別のリンクはクリックしても
    /// 何も起きないプレーンテキストとして表示される（<see cref="BuildLinkInlineNested"/>参照）。
    /// </summary>
    private sealed class LinkHandlers
    {
        public Action<string>? OnAnchorClicked;
        public Action<string>? OnRelativeLinkClicked;
        public Action<string>? OnExternalLinkClicked;
    }

    /// <summary>脚注定義（<c>[^id]: text</c>）。複数行の続き（字下げ）は改行で連結してTextへ保持する。</summary>
    private sealed record FootnoteDef(string Id, string Text, int StartLine);

    /// <summary>
    /// ブロック・インライン構築を通じて共有する読み取り専用の文脈。1回の<see cref="Render"/>
    /// 呼び出しにつき1個作る（Anchorsのみ構築中に書き込まれる可変辞書）。
    /// </summary>
    private sealed class RenderContext
    {
        public required LinkHandlers Links;
        public required Action<string> OnTocLinkClicked;
        public Action<int, bool>? OnChecklistToggled;
        public required Dictionary<string, Control> Anchors;
        public required Dictionary<string, FootnoteDef> FootnoteDefs;
        public string? BaseDirectory;
    }

    // ------------------------------------------------------------------
    // ブロック文法用の正規表現
    // ------------------------------------------------------------------

    private static readonly Regex UnorderedListItemPattern = new(@"^\s*[-*]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex OrderedListItemPattern = new(@"^\s*(\d+)\.\s+(.*)$", RegexOptions.Compiled);

    // インデントを事前に取り除いた文字列に対して使う版（ネストしたリスト項目の判定用）。
    private static readonly Regex UnorderedMarkerPattern = new(@"^[-*]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex OrderedMarkerPattern = new(@"^(\d+)\.\s+(.*)$", RegexOptions.Compiled);

    // GitHub形式のチェックリスト項目（- [ ] / - [x]）。大文字Xも許容する。
    private static readonly Regex ChecklistItemPattern =
        new(@"^\[(?<mark>[ xX])\]\s+(?<rest>.*)$", RegexOptions.Compiled);

    // チェックリスト行そのもの（インデント・箇条書き記号を保ったまま）のON/OFF切替用。
    private static readonly Regex ChecklistLinePattern =
        new(@"^(?<prefix>\s*[-*]\s+)\[(?<mark>[ xX])\](?<rest>.*)$", RegexOptions.Compiled);

    // 目次の項目「[見出し名](#アンカー)」だけを特別扱いするための全文一致パターン。
    private static readonly Regex FullLinkPattern =
        new(@"^\[(?<text>[^\]]+)\]\((?<anchor>#[^)]+)\)$", RegexOptions.Compiled);

    // 引用行（0〜3個の先頭空白 + '>' + 任意で1個の空白/タブ）。
    private static readonly Regex QuoteLinePattern = new(@"^ {0,3}>[ \t]?(.*)$", RegexOptions.Compiled);

    // 脚注定義（[^id]: 本文）。参照側（[^id]）はインライン解析側で個別に処理する。
    private static readonly Regex FootnoteDefPattern =
        new(@"^\[\^(?<id>[^\]\s]+)\]:[ \t]?(?<text>.*)$", RegexOptions.Compiled);

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

    // 箇条書き・チェックボックスのマーカー列の幅。CheckBoxの既定コントロールテーマ
    // （Themes/Controls.Input.axaml）が22x22の領域（フォーカスリング込み）を必要とするため、
    // これより狭い列に置くと左端が欠けて表示される（実機不具合の回帰）。番号付きリストの
    // 番号はテキストのみで幅の制約が無いため従来どおり28を使う。
    private const double BulletColumnWidth = 22;
    private const double OrderedColumnWidth = 28;

    private const double ImageMaxWidth = 480;
    private const double ImageMaxHeight = 360;

    /// <summary>
    /// Markdownを描画する。<paramref name="onTocLinkClicked"/>は既存の必須引数（同一文書内
    /// アンカーへのジャンプ。取扱説明書のTOC・段落中の<c>#アンカー</c>リンク・脚注参照の
    /// いずれからも使う）。それ以外は任意引数で、いずれも未指定（null）なら該当機能は
    /// 無効化される（<see cref="ManualWindow"/>は指定しないため、これまでどおりの挙動を保つ）。
    /// </summary>
    /// <param name="markdown">描画対象のMarkdown全文。</param>
    /// <param name="onTocLinkClicked">同一文書内アンカー（#で始まるリンク・脚注参照）がクリックされたときに呼ばれる。</param>
    /// <param name="onRelativeLinkClicked">
    /// プロジェクト内の相対パスとみなされるリンクがクリックされたときに呼ばれる
    /// （<see cref="MarkdownPreviewView"/>用。未指定ならその種のリンクは非活性表示になる）。
    /// </param>
    /// <param name="onExternalLinkClicked">
    /// <c>http://</c>・<c>https://</c>等の絶対URIリンク・外部画像のプレースホルダがクリックされたときに
    /// 呼ばれる（<see cref="MarkdownPreviewView"/>用。未指定ならその種のリンク・画像は非活性表示になる）。
    /// </param>
    /// <param name="onBlockDoubleClicked">
    /// いずれかのブロックがダブルクリックされたときに、そのブロックの開始行（1始まり）を
    /// 引数に呼ばれる（<see cref="MarkdownPreviewView"/>用。未指定なら配線しない）。
    /// </param>
    /// <param name="baseDirectory">
    /// 相対パス画像の解決に使う基準ディレクトリ（通常は表示中の.mdファイルのあるフォルダ）。
    /// 未指定（null）なら相対パス画像も読み込めず代替表示になる（<see cref="ManualWindow"/>の
    /// 埋め込みリソースにはファイルシステム上の基準が無いため）。
    /// </param>
    /// <param name="onChecklistToggled">
    /// チェックリストのチェックボックスがクリック・キーボード操作でON/OFFされたときに、
    /// 元Markdownの行番号（1始まり）と新しい状態を引数に呼ばれる。未指定（null）なら
    /// チェックボックスは表示専用（クリックしても状態・ファイルが変わらない）になる
    /// （<see cref="ManualWindow"/>は埋め込みリソースで編集対象が無いため未指定のまま）。
    /// </param>
    public static RenderResult Render(
        string markdown,
        Action<string> onTocLinkClicked,
        Action<string>? onRelativeLinkClicked = null,
        Action<string>? onExternalLinkClicked = null,
        Action<int>? onBlockDoubleClicked = null,
        string? baseDirectory = null,
        Action<int, bool>? onChecklistToggled = null)
    {
        var links = new LinkHandlers
        {
            OnAnchorClicked = onTocLinkClicked,
            OnRelativeLinkClicked = onRelativeLinkClicked,
            OnExternalLinkClicked = onExternalLinkClicked,
        };

        var rawLines = markdown.Replace("\r\n", "\n").Split('\n');
        var (processedLines, footnoteDefs) = ExtractFootnoteDefinitions(rawLines);
        var anchors = new Dictionary<string, Control>();
        var ctx = new RenderContext
        {
            Links = links,
            OnTocLinkClicked = onTocLinkClicked,
            OnChecklistToggled = onChecklistToggled,
            Anchors = anchors,
            FootnoteDefs = footnoteDefs.ToDictionary(d => d.Id, d => d),
            BaseDirectory = baseDirectory,
        };

        var rawBlocks = RenderBlocksInto(processedLines, 0, ctx);

        if (footnoteDefs.Count > 0)
        {
            rawBlocks.Add(new RenderedBlock(BuildHorizontalRule(), footnoteDefs[0].StartLine));
            foreach (var def in footnoteDefs)
            {
                var control = BuildFootnoteDefBlock(def, ctx);
                anchors[$"fn:{def.Id}"] = control;
                rawBlocks.Add(new RenderedBlock(control, def.StartLine));
            }
        }

        var blocks = new List<RenderedBlock>(rawBlocks.Count);
        foreach (var rb in rawBlocks)
        {
            if (onBlockDoubleClicked is not null)
            {
                var line = rb.StartLine;
                rb.Control.DoubleTapped += (_, _) => onBlockDoubleClicked(line);
            }
            blocks.Add(rb);
        }

        return new RenderResult { Blocks = blocks, Anchors = anchors };
    }

    /// <summary>
    /// チェックリスト行（例: <c>"- [ ] text"</c> / <c>"  - [x] text"</c>）の完了マークを反転した
    /// 行を返す。マッチしない行（チェックリストではない行）は<c>null</c>を返す。インデント・
    /// 箇条書き記号（<c>-</c>/<c>*</c>）・マーク後のテキストはそのまま維持する。
    /// 対応する記法は<c>-</c>/<c>*</c>始まりの箇条書きのみ（<c>+</c>始まりの箇条書き・番号付き
    /// リストのチェックリストは本レンダラの箇条書き解析自体が対象外のため非対応）。
    /// </summary>
    internal static string? ToggleChecklistLine(string lineText)
    {
        var m = ChecklistLinePattern.Match(lineText);
        if (!m.Success) return null;
        var newMark = m.Groups["mark"].Value is "x" or "X" ? " " : "x";
        return $"{m.Groups["prefix"].Value}[{newMark}]{m.Groups["rest"].Value}";
    }

    /// <summary>
    /// チェックリスト行の完了マークを<paramref name="isChecked"/>の状態へ明示的に設定した行を
    /// 返す（<see cref="ToggleChecklistLine"/>と異なり現在の状態を読まず、狙った状態へ確実に
    /// 合わせる）。マッチしない行は<c>null</c>を返す。
    /// </summary>
    internal static string? SetChecklistLineChecked(string lineText, bool isChecked)
    {
        var m = ChecklistLinePattern.Match(lineText);
        if (!m.Success) return null;
        var newMark = isChecked ? "x" : " ";
        return $"{m.Groups["prefix"].Value}[{newMark}]{m.Groups["rest"].Value}";
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
           || OrderedListItemPattern.IsMatch(line)
           || QuoteLinePattern.IsMatch(line);

    private static int CountLeadingSpaces(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }

    // ------------------------------------------------------------------
    // ブロック分割（見出し・段落・リスト・引用・コード・表・水平線）
    //
    // 引用ブロックの中身は同じ文法をそのまま再帰的に適用する（本関数を再帰呼び出しする）ため、
    // 引用の中に箇条書き・コードブロック・ネストした引用が現れても破綻しない。
    // 戻り値はダブルクリックのイベント配線をしていない生のブロック一覧で、配線は
    // <see cref="Render"/>が最後に1回だけ行う（引用の中身を個別に配線すると、引用の外枠にも
    // 配線した場合にダブルクリックイベントが二重発火するのを避けるため。引用は表・リスト・
    // コードブロックと同じく「引用全体で1ブロック」という粒度に揃えている）。
    // </summary>
    private static List<RenderedBlock> RenderBlocksInto(string[] lines, int lineOffset, RenderContext ctx)
    {
        var blocks = new List<RenderedBlock>();
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            var startLine = lineOffset + i + 1;

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
                blocks.Add(new RenderedBlock(BuildCodeBlock(string.Join('\n', codeLines), language), startLine));
                continue;
            }

            if (line.StartsWith('#'))
            {
                var level = 0;
                while (level < line.Length && line[level] == '#') level++;
                var text = line[level..].Trim();
                var heading = BuildHeading(text, level, ctx);
                blocks.Add(new RenderedBlock(heading, startLine));
                ctx.Anchors[Slugify(text)] = heading;
                i++;
                continue;
            }

            if (line.Trim() == "---")
            {
                blocks.Add(new RenderedBlock(BuildHorizontalRule(), startLine));
                i++;
                continue;
            }

            if (QuoteLinePattern.IsMatch(line))
            {
                var quoteLines = new List<string>();
                while (i < lines.Length && QuoteLinePattern.IsMatch(lines[i]))
                {
                    quoteLines.Add(QuoteLinePattern.Match(lines[i]).Groups[1].Value);
                    i++;
                }
                var innerBlocks = RenderBlocksInto(quoteLines.ToArray(), startLine - 1, ctx);
                blocks.Add(new RenderedBlock(BuildBlockquote(innerBlocks), startLine));
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
                blocks.Add(new RenderedBlock(BuildTable(tableLines, ctx), startLine));
                continue;
            }

            if (UnorderedListItemPattern.IsMatch(line) || OrderedListItemPattern.IsMatch(line))
            {
                var indentThreshold = CountLeadingSpaces(line);
                var (items, next) = ParseListItems(lines, i, indentThreshold);
                blocks.Add(new RenderedBlock(BuildListTree(items, ctx, depth: 0), startLine));
                i = next;
                continue;
            }

            // それ以外は段落。空行・新しいブロックの開始行が来るまで1つの段落として連結する。
            var paragraphLines = new List<string>();
            while (i < lines.Length && !IsBlockBoundary(lines[i]))
            {
                paragraphLines.Add(lines[i]);
                i++;
            }
            blocks.Add(new RenderedBlock(BuildParagraph(string.Join(string.Empty, paragraphLines), ctx), startLine));
        }

        return blocks;
    }

    // ------------------------------------------------------------------
    // 脚注定義の抽出（本文走査の前処理）
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>[^id]: 本文</c>形式の脚注定義を全文から抜き出し、該当行を空行へ置き換えた配列を返す
    /// （通常のブロック解析からは見えなくなる＝空行として無視される）。字下げされた続きの行
    /// （番号付きリストの2.2節と同じ規則）も同じ定義へ連結する。
    /// </summary>
    private static (string[] ProcessedLines, List<FootnoteDef> Defs) ExtractFootnoteDefinitions(string[] lines)
    {
        var processed = (string[])lines.Clone();
        var defs = new List<FootnoteDef>();

        for (var i = 0; i < processed.Length; i++)
        {
            var m = FootnoteDefPattern.Match(processed[i]);
            if (!m.Success) continue;

            var id = m.Groups["id"].Value;
            var startLine = i + 1;
            var textLines = new List<string> { m.Groups["text"].Value };
            processed[i] = string.Empty;
            i++;
            while (i < processed.Length && IsIndentedContinuation(processed[i]))
            {
                textLines.Add(processed[i].Trim());
                processed[i] = string.Empty;
                i++;
            }
            i--; // for文のi++で正しい次位置へ戻す

            defs.Add(new FootnoteDef(id, string.Join("\n", textLines), startLine));
        }

        return (processed, defs);
    }

    private static Control BuildFootnoteDefBlock(FootnoteDef def, RenderContext ctx)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 4) };

        var label = new SelectableTextBlock
        {
            Text = $"[{def.Id}]",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 12,
        };
        BindForeground(label, "TextSecondary");
        Grid.SetColumn(label, 0);

        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
        BindForeground(text, "TextSecondary");
        var parts = def.Text.Split('\n');
        for (var p = 0; p < parts.Length; p++)
        {
            if (p > 0) text.Inlines!.Add(new LineBreak());
            AddInlineRuns(text.Inlines!, parts[p], ctx);
        }
        Grid.SetColumn(text, 1);

        row.Children.Add(label);
        row.Children.Add(text);
        return row;
    }

    // ------------------------------------------------------------------
    // 引用
    // ------------------------------------------------------------------

    private static Control BuildBlockquote(List<RenderedBlock> innerBlocks)
    {
        var inner = new StackPanel();
        foreach (var block in innerBlocks) inner.Children.Add(block.Control);

        var border = new Border
        {
            Child = inner,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 8),
        };
        BindBorderBrush(border, "BorderSubtle");
        return border;
    }

    // ------------------------------------------------------------------
    // 見出し・段落
    // ------------------------------------------------------------------

    private static Control BuildHeading(string text, int level, RenderContext ctx)
    {
        var block = new SelectableTextBlock
        {
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        BindForeground(block, "TextPrimary");
        AddInlineRuns(block.Inlines!, text, ctx);

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
            case 3:
                block.FontSize = 14;
                block.Margin = new Thickness(0, 16, 0, 4);
                return block;
            case 4:
                block.FontSize = 13;
                block.Margin = new Thickness(0, 12, 0, 4);
                return block;
            case 5:
                block.FontSize = 12;
                block.Margin = new Thickness(0, 10, 0, 4);
                return block;
            default: // 6段階目（7個以上の#が続く壊れたMarkdownもここへ丸める）
                block.FontSize = 12;
                block.Margin = new Thickness(0, 8, 0, 4);
                BindForeground(block, "TextSecondary");
                return block;
        }
    }

    private static Control BuildParagraph(string text, RenderContext ctx)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        BindForeground(block, "TextPrimary");
        AddInlineRuns(block.Inlines!, text, ctx);
        return block;
    }

    // ------------------------------------------------------------------
    // 箇条書き・番号付きリスト（入れ子対応）
    // ------------------------------------------------------------------

    /// <summary>1個のリスト項目。子項目を持つことで箇条書き・番号付きの入れ子を表現する。</summary>
    private sealed class ListItemNode
    {
        public required int LineNumber;
        public required string RawText;
        public bool IsOrdered;
        public bool IsChecklist;
        public bool Checked;
        public string ChecklistRest = string.Empty;
        public List<string> ContinuationLines { get; } = new();
        public List<ListItemNode> Children { get; } = new();
    }

    /// <summary>
    /// <paramref name="indentThreshold"/>と同じインデント幅を持つ箇条書き・番号付き項目を
    /// 連続して読み取る。項目直下でさらに深くインデントされた行は、それ自体がリスト項目の
    /// マーカーを持てば子リストとして再帰的に読み取り、そうでなければ項目の続きの説明文
    /// （取扱説明書2.2節の3スペースインデント継続と同じ扱い）として連結する。
    /// </summary>
    private static (List<ListItemNode> Items, int NextIndex) ParseListItems(string[] lines, int start, int indentThreshold)
    {
        var items = new List<ListItemNode>();
        var i = start;

        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) break;
            var indent = CountLeadingSpaces(line);
            if (indent != indentThreshold) break;

            var content = line[indent..];
            var uMatch = UnorderedMarkerPattern.Match(content);
            var isOrdered = false;
            var match = uMatch;
            if (!uMatch.Success)
            {
                var oMatch = OrderedMarkerPattern.Match(content);
                if (!oMatch.Success) break;
                match = oMatch;
                isOrdered = true;
            }

            var rawText = isOrdered ? match.Groups[2].Value : match.Groups[1].Value;
            var node = new ListItemNode { LineNumber = i + 1, RawText = rawText, IsOrdered = isOrdered };
            i++;

            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && CountLeadingSpaces(lines[i]) > indent)
            {
                var subIndent = CountLeadingSpaces(lines[i]);
                var subContent = lines[i][subIndent..];
                if (UnorderedMarkerPattern.IsMatch(subContent) || OrderedMarkerPattern.IsMatch(subContent))
                {
                    var (children, next) = ParseListItems(lines, i, subIndent);
                    node.Children.AddRange(children);
                    i = next;
                }
                else
                {
                    node.ContinuationLines.Add(lines[i].Trim());
                    i++;
                }
            }

            if (!isOrdered)
            {
                var checklistMatch = ChecklistItemPattern.Match(node.RawText);
                if (checklistMatch.Success)
                {
                    node.IsChecklist = true;
                    node.Checked = checklistMatch.Groups["mark"].Value is "x" or "X";
                    node.ChecklistRest = checklistMatch.Groups["rest"].Value;
                }
            }

            items.Add(node);
        }

        return (items, i);
    }

    private static Control BuildListTree(IReadOnlyList<ListItemNode> items, RenderContext ctx, int depth)
    {
        var panel = new StackPanel { Spacing = 4 };
        if (depth == 0) panel.Margin = new Thickness(0, 0, 0, 8);

        var orderIndex = 0;
        foreach (var item in items)
        {
            var columnWidth = item.IsOrdered ? OrderedColumnWidth : BulletColumnWidth;
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(
                    columnWidth.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",*"),
            };

            if (item.IsChecklist)
            {
                BuildChecklistCell(row, item, ctx);
            }
            else if (item.IsOrdered)
            {
                orderIndex++;
                BuildOrderedMarkerCell(row, orderIndex);
            }
            else
            {
                BuildBulletMarkerCell(row);
            }

            BuildListItemContentCell(row, item, ctx);
            panel.Children.Add(row);

            if (item.Children.Count > 0)
            {
                var child = BuildListTree(item.Children, ctx, depth + 1);
                child.Margin = new Thickness(columnWidth, 2, 0, 0);
                panel.Children.Add(child);
            }
        }

        return panel;
    }

    private static void BuildBulletMarkerCell(Grid row)
    {
        var bullet = new SelectableTextBlock { Text = "•", VerticalAlignment = VerticalAlignment.Top };
        BindForeground(bullet, "TextPrimary");
        Grid.SetColumn(bullet, 0);
        row.Children.Add(bullet);
    }

    private static void BuildOrderedMarkerCell(Grid row, int number)
    {
        var text = new SelectableTextBlock { Text = $"{number}.", VerticalAlignment = VerticalAlignment.Top };
        BindForeground(text, "TextSecondary");
        Grid.SetColumn(text, 0);
        row.Children.Add(text);
    }

    /// <summary>
    /// チェックリスト項目のCheckBoxセル。
    ///
    /// 【表示専用/操作可能の切り替え】<see cref="RenderContext.OnChecklistToggled"/>が渡されて
    /// いれば実際にクリック・キーボード（Tab+Space、CheckBoxの既定動作）で操作できるようにする。
    /// 渡されていなければ（<see cref="ManualWindow"/>）<see cref="InputElement.IsHitTestVisible"/>を
    /// falseにしてポインタ操作を一切受け付けない、これまでどおりの表示専用にする。
    /// 【幅22】<see cref="BulletColumnWidth"/>参照（実機不具合の回帰対応）。
    /// </summary>
    private static void BuildChecklistCell(Grid row, ListItemNode item, RenderContext ctx)
    {
        var interactive = ctx.OnChecklistToggled is not null;
        var checkbox = new CheckBox
        {
            IsChecked = item.Checked,
            IsHitTestVisible = interactive,
            Focusable = interactive,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (interactive) checkbox.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        AutomationProperties.SetName(checkbox, item.Checked ? "完了" : "未完了");

        if (interactive)
        {
            var lineNumber = item.LineNumber;
            // IsCheckedプロパティの初期値設定（上のオブジェクト初期化子）より後に購読することで、
            // 初期表示時にこのハンドラが誤発火して即座に書き換えが走らないようにしている。
            checkbox.IsCheckedChanged += (_, _) => ctx.OnChecklistToggled!(lineNumber, checkbox.IsChecked == true);
        }

        Grid.SetColumn(checkbox, 0);
        row.Children.Add(checkbox);
    }

    private static void BuildListItemContentCell(Grid row, ListItemNode item, RenderContext ctx)
    {
        var rawText = item.IsChecklist ? item.ChecklistRest : item.RawText;
        var isLeaf = item.Children.Count == 0 && item.ContinuationLines.Count == 0;

        if (item.IsOrdered && isLeaf)
        {
            var linkMatch = FullLinkPattern.Match(rawText.Trim());
            if (linkMatch.Success)
            {
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
                link.Click += (_, _) => ctx.OnTocLinkClicked(anchor);
                Grid.SetColumn(link, 1);
                row.Children.Add(link);
                return;
            }
        }

        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
        BindForeground(text, "TextPrimary");
        var parts = new List<string> { rawText };
        parts.AddRange(item.ContinuationLines);
        for (var p = 0; p < parts.Count; p++)
        {
            if (p > 0) text.Inlines!.Add(new LineBreak());
            AddInlineRuns(text.Inlines!, parts[p], ctx);
        }
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
    }

    // ------------------------------------------------------------------
    // コードブロック
    // ------------------------------------------------------------------

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
        ReserveSpaceForHorizontalScrollBar(scroll, textBlock);

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

    // ------------------------------------------------------------------
    // 表
    // ------------------------------------------------------------------

    private static Control BuildTable(IReadOnlyList<string> tableLines, RenderContext ctx)
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
                AddInlineRuns(textBlock.Inlines!, cellText, ctx);
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

        var tableScroll = new ScrollViewer
        {
            Content = border,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 8),
        };
        ReserveSpaceForHorizontalScrollBar(tableScroll, border);
        return tableScroll;
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
    // インライン記法: 字句解析（トークナイザ）
    //
    // 【設計】1文字ずつ左から読み進める手書きの字句解析にした（正規表現の非重複マッチの積み
    // 重ねではない）。理由は入り組んだケースが正規表現の単純な非重複マッチでは表現できない
    // ため: 太字の中の斜体（**太字の中の*斜体***）、コード内のアスタリスク
    // （`コード内の**アスタリスク**`はコード内なので装飾しない）、リンク内の装飾
    // （[**太字**のリンク](url)）、本文中の`*`（2 * 3 = 6 は前後が空白なので強調にしない）。
    //
    // 【強調（*/_）の入れ子解決】CommonMarkの区切り文字スタック方式を簡略化して実装した。
    // 各`*`/`_`の連続（1〜3文字。4文字以上は超過分をリテラル扱い）ごとに「開始できるか」
    // 「終了できるか」を前後の文字（空白かどうか。`_`はさらに単語境界かどうか）で判定し、
    // 開始側スタック（<see cref="EmphasisFrame"/>）に積む。終了側に来たら直近の同じ文字の
    // フレームから閉じていく。1個のフレームだけでは連続長が余る場合（例: ***の3文字を
    // 1文字ぶんの斜体に使ったあと2文字ぶんの太字に使う）は、使った分だけ消費してフレームを
    // 積み直す。両方とも開始・終了どちらにも使える（前後とも非空白）曖昧な区切りが見つかった
    // ときは、CommonMark仕様の「3の倍数ルール」（規則9/10の簡略版。開始側・終了側の少なくとも
    // 一方が両用可能で、かつ両者の連続長の合計が3の倍数のとき、両方が3の倍数である場合を除いて
    // その組み合わせでの終了を禁止する）を適用し、それに反する組み合わせは終了に使わず
    // 開始側の候補として積み直す。これにより「**太字の中の*斜体***」が太字(斜体)の入れ子に、
    // 「cat*meow*dog」（前後とも英数字の単一`*`）が単純な斜体になる、という一般的な期待どおりの
    // 結果になる（<see cref="ManualMarkdownRendererInlineTests"/>参照）。
    // ------------------------------------------------------------------

    /// <summary>強調（斜体/太字）の開始側フレーム。同じ文字の入れ子を区切り文字スタックで表現する。</summary>
    private sealed class EmphasisFrame
    {
        public required char Ch;
        public required int Remaining;
        public required int OriginalLen;
        public required bool OpenAmbidextrous;
        public List<Inline> Content { get; } = new();
    }

    private static void AddInlineRuns(InlineCollection inlines, string text, RenderContext ctx)
    {
        if (text.Length == 0) return;
        foreach (var inline in ParseInline(text, 0, text.Length, ctx)) inlines.Add(inline);
    }

    private static List<Inline> ParseInline(string s, int start, int end, RenderContext ctx)
    {
        var root = new List<Inline>();
        var stack = new List<EmphasisFrame>();
        var sb = new StringBuilder();

        List<Inline> Current() => stack.Count > 0 ? stack[^1].Content : root;
        void FlushText()
        {
            if (sb.Length == 0) return;
            Current().Add(new Run(sb.ToString()));
            sb.Clear();
        }

        var i = start;
        while (i < end)
        {
            var c = s[i];

            if (c == '\\' && i + 1 < end && IsEscapable(s[i + 1]))
            {
                sb.Append(s[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                FlushText();
                i = ParseCodeSpan(s, i, end, Current());
                continue;
            }

            if (c == '!' && i + 1 < end && s[i + 1] == '[')
            {
                var image = TryParseImage(s, i, end);
                if (image is { } img)
                {
                    FlushText();
                    Current().Add(BuildImageInline(img.Alt, img.Url, ctx));
                    i = img.NextIndex;
                    continue;
                }
            }

            if (c == '[')
            {
                if (i + 1 < end && s[i + 1] == '^')
                {
                    var footref = TryParseFootnoteRef(s, i, end, ctx);
                    if (footref is { } fr)
                    {
                        FlushText();
                        Current().Add(fr.Inline);
                        i = fr.NextIndex;
                        continue;
                    }
                }

                var link = TryParseLink(s, i, end, ctx);
                if (link is { } lk)
                {
                    FlushText();
                    Current().Add(lk.Inline);
                    i = lk.NextIndex;
                    continue;
                }
            }

            if (c == '~' && i + 1 < end && s[i + 1] == '~')
            {
                var strike = TryParseStrikethrough(s, i, end, ctx);
                if (strike is { } st)
                {
                    FlushText();
                    Current().Add(st.Inline);
                    i = st.NextIndex;
                    continue;
                }
            }

            if (c == '*' || c == '_')
            {
                i = HandleEmphasisDelimiter(s, i, end, c, stack, Current, FlushText, sb);
                continue;
            }

            sb.Append(c);
            i++;
        }

        FlushText();

        // 閉じられなかった残りのフレームは、装飾記号ごとリテラルとして出力する
        // （壊れたMarkdown・閉じ忘れでも例外にしない）。
        while (stack.Count > 0)
        {
            var frame = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            var target = Current();
            target.Add(new Run(new string(frame.Ch, frame.Remaining)));
            foreach (var inline in frame.Content) target.Add(inline);
        }

        return root;
    }

    /// <summary>
    /// <c>*</c>/<c>_</c>の連続1個ぶんを処理し、次に読み進めるべき位置を返す。
    /// 開始・終了の判定と3の倍数ルールはクラスコメント参照。
    /// </summary>
    private static int HandleEmphasisDelimiter(
        string s, int i, int end, char c, List<EmphasisFrame> stack,
        Func<List<Inline>> current, Action flushText, StringBuilder sb)
    {
        var runStart = i;
        while (i < end && s[i] == c) i++;
        var rawLen = i - runStart;
        var runLen = Math.Min(rawLen, 3);
        var extra = rawLen - runLen;

        var before = runStart > 0 ? s[runStart - 1] : ' ';
        var after = i < end ? s[i] : ' ';
        var canOpen = CanOpenDelimiter(c, before, after);
        var canClose = CanCloseDelimiter(c, before, after);
        var ambidextrous = canOpen && canClose;

        var remaining = runLen;
        if (canClose && stack.Count > 0 && stack[^1].Ch == c)
        {
            flushText();
            ClosePeel(stack, c, ref remaining, current,
                f => !ForbiddenByRuleOfThree(f.OriginalLen, runLen, f.OpenAmbidextrous, ambidextrous));
        }

        if (remaining == runLen)
        {
            // 3の倍数ルールで禁止された、またはそもそも閉じる対象が無かった場合。
            if (canOpen)
            {
                flushText();
                stack.Add(new EmphasisFrame { Ch = c, Remaining = runLen, OriginalLen = runLen, OpenAmbidextrous = ambidextrous });
                remaining = 0;
            }
            else if (canClose && stack.Count > 0 && stack[^1].Ch == c)
            {
                // 開始にも使えず、ルールでも禁止されたが、他に手段が無いフォールバック:
                // ルールを無視して強制的に閉じる（フレームが永遠に残り続けるのを避ける）。
                flushText();
                ClosePeel(stack, c, ref remaining, current, allow: null);
            }
            else
            {
                sb.Append(c, runLen);
                remaining = 0;
            }
        }

        if (remaining > 0) current().Add(new Run(new string(c, remaining)));
        if (extra > 0) sb.Append(c, extra);

        return i;
    }

    /// <summary>
    /// 区切り文字スタックの先頭（直近に開始した同じ文字のフレーム）から、<paramref name="remaining"/>
    /// が尽きるかフレームが尽きるまで閉じていく。<paramref name="allow"/>がfalseを返したフレームは
    /// 閉じずにそこで打ち切る（3の倍数ルールでの禁止判定に使う。nullなら常に許可）。
    /// </summary>
    private static void ClosePeel(
        List<EmphasisFrame> stack, char c, ref int remaining, Func<List<Inline>> current, Func<EmphasisFrame, bool>? allow)
    {
        while (remaining > 0 && stack.Count > 0 && stack[^1].Ch == c && (allow is null || allow(stack[^1])))
        {
            var frame = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            var step = Math.Min(frame.Remaining, remaining);
            var wrapped = WrapEmphasis(frame.Content, step);
            frame.Remaining -= step;
            remaining -= step;

            if (frame.Remaining > 0)
            {
                var reopened = new EmphasisFrame
                {
                    Ch = c, Remaining = frame.Remaining, OriginalLen = frame.OriginalLen, OpenAmbidextrous = frame.OpenAmbidextrous,
                };
                reopened.Content.Add(wrapped);
                stack.Add(reopened);
            }
            else
            {
                current().Add(wrapped);
            }
        }
    }

    private static Inline WrapEmphasis(List<Inline> content, int step)
    {
        var span = new Span();
        if (step >= 2) span.FontWeight = FontWeight.Bold;
        if (step == 1 || step == 3) span.FontStyle = FontStyle.Italic;
        foreach (var inline in content) span.Inlines.Add(inline);
        return span;
    }

    /// <summary>CommonMarkの「3の倍数ルール」（規則9/10）の簡略版。詳細はクラスコメント参照。</summary>
    private static bool ForbiddenByRuleOfThree(int openerOriginalLen, int closerOriginalLen, bool openerAmbidextrous, bool closerAmbidextrous)
    {
        if (!openerAmbidextrous && !closerAmbidextrous) return false;
        var sum = openerOriginalLen + closerOriginalLen;
        if (sum % 3 != 0) return false;
        return !(openerOriginalLen % 3 == 0 && closerOriginalLen % 3 == 0);
    }

    private static bool CanOpenDelimiter(char ch, char before, char after)
    {
        if (char.IsWhiteSpace(after)) return false;
        if (ch == '_' && IsWordChar(before)) return false;
        return true;
    }

    private static bool CanCloseDelimiter(char ch, char before, char after)
    {
        if (char.IsWhiteSpace(before)) return false;
        if (ch == '_' && IsWordChar(after)) return false;
        return true;
    }

    // `_`の単語内抑制規則（"foo_bar_baz"を斜体にしない）はASCIIの英数字連続を対象にした規則
    // であり、char.IsLetterOrDigitをそのまま使うと日本語の文字（ひらがな・カタカナ・漢字は
    // Unicode上「文字」に分類される）まで「単語の一部」とみなされ、日本語の地の文では
    // "_斜体_"のようなアンダースコア強調が常に抑制されてしまう（実装時に発見した不具合）。
    // ASCII英数字のみを対象にすることで、英単語の"_"抑制は保ちつつ日本語文中の強調は働かせる。
    private static bool IsWordChar(char c) => (c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z') || c == '_';

    private static bool IsEscapable(char c) => c is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']'
        or '(' or ')' or '#' or '+' or '-' or '.' or '!' or '~' or '>' or '|' or '"' or '\'';

    // ------------------------------------------------------------------
    // インラインコード
    // ------------------------------------------------------------------

    private static int ParseCodeSpan(string s, int i, int end, List<Inline> target)
    {
        var runStart = i;
        while (i < end && s[i] == '`') i++;
        var fenceLen = i - runStart;
        var searchFrom = i;
        var j = searchFrom;
        var closeIdx = -1;
        while (j < end)
        {
            if (s[j] == '`')
            {
                var closeStart = j;
                while (j < end && s[j] == '`') j++;
                if (j - closeStart == fenceLen) { closeIdx = closeStart; break; }
            }
            else
            {
                j++;
            }
        }

        if (closeIdx < 0)
        {
            target.Add(new Run(s.Substring(runStart, fenceLen)));
            return searchFrom;
        }

        var raw = s.Substring(searchFrom, closeIdx - searchFrom);
        target.Add(BuildCodeRun(TrimCodeSpan(raw)));
        return closeIdx + fenceLen;
    }

    private static string TrimCodeSpan(string raw)
    {
        if (raw.Length >= 2 && raw[0] == ' ' && raw[^1] == ' ' && raw.Trim().Length > 0) return raw[1..^1];
        return raw;
    }

    private static Inline BuildCodeRun(string code)
    {
        var run = new Run(code);
        run.Bind(TextElement.FontFamilyProperty, new DynamicResourceExtension("CodeFontFamily"));
        run.Bind(TextElement.BackgroundProperty, new DynamicResourceExtension("BgSurface"));
        return run;
    }

    // ------------------------------------------------------------------
    // 打ち消し線
    // ------------------------------------------------------------------

    private static (Inline Inline, int NextIndex)? TryParseStrikethrough(string s, int i, int end, RenderContext ctx)
    {
        var searchStart = i + 2;
        if (searchStart >= end) return null;
        var closeIdx = s.IndexOf("~~", searchStart, end - searchStart, StringComparison.Ordinal);
        if (closeIdx < 0) return null;

        var innerInlines = ParseInline(s, searchStart, closeIdx, ctx);
        var span = new Span { TextDecorations = TextDecorations.Strikethrough };
        foreach (var inline in innerInlines) span.Inlines.Add(inline);
        return (span, closeIdx + 2);
    }

    // ------------------------------------------------------------------
    // リンク・脚注参照
    // ------------------------------------------------------------------

    private static (Inline Inline, int NextIndex)? TryParseLink(string s, int i, int end, RenderContext ctx)
    {
        var textEnd = FindMatchingBracket(s, i + 1, end, '[', ']');
        if (textEnd < 0) return null;
        var afterText = textEnd + 1;
        if (afterText >= end || s[afterText] != '(') return null;
        var urlEnd = FindMatchingBracket(s, afterText + 1, end, '(', ')');
        if (urlEnd < 0) return null;

        var urlRaw = s.Substring(afterText + 1, urlEnd - (afterText + 1)).Trim();
        var url = ExtractUrlBeforeTitle(urlRaw);
        var innerInlines = ParseInline(s, i + 1, textEnd, ctx);
        return (BuildLinkInlineNested(innerInlines, url, ctx), urlEnd + 1);
    }

    /// <summary>
    /// リンクの種別（<see cref="ClassifyLink"/>）に応じたハンドラが指定されていればクリック可能な
    /// <see cref="InlineUIContainer"/>（中身は透明な<see cref="Button"/>）を、無ければ非活性の
    /// プレーンテキストを返す。リンクテキストが単一の飾りなしテキストのときはBoolButton.Contentへ
    /// 文字列をそのまま設定する（既存の呼び出し側テスト・挙動を変えないため）。太字等の装飾を
    /// 含む場合のみ<see cref="TextBlock"/>（Inlinesに入れ子のSpanを積める）へ包む。
    /// </summary>
    private static Inline BuildLinkInlineNested(List<Inline> innerInlines, string url, RenderContext ctx)
    {
        var kind = ClassifyLink(url);
        var handler = kind switch
        {
            LinkKind.Anchor => ctx.Links.OnAnchorClicked,
            LinkKind.External => ctx.Links.OnExternalLinkClicked,
            _ => ctx.Links.OnRelativeLinkClicked,
        };

        var plainText = ExtractPlainText(innerInlines);

        if (handler is null)
        {
            if (innerInlines.Count == 1 && innerInlines[0] is Run singleRun) return singleRun;
            var plainSpan = new Span();
            foreach (var inline in innerInlines) plainSpan.Inlines.Add(inline);
            return plainSpan;
        }

        var target = kind == LinkKind.Anchor ? url.TrimStart('#') : url;
        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };

        if (innerInlines.Count == 1 && innerInlines[0] is Run onlyRun)
        {
            button.Content = onlyRun.Text;
        }
        else
        {
            var textBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
            foreach (var inline in innerInlines) textBlock.Inlines!.Add(inline);
            button.Content = textBlock;
        }

        AutomationProperties.SetName(button, $"リンク: {plainText}");
        BindForeground(button, "Accent");
        button.Click += (_, _) => handler(target);
        return new InlineUIContainer(button);
    }

    private static string ExtractPlainText(IEnumerable<Inline> inlines)
    {
        var sb = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run: sb.Append(run.Text); break;
                case Span span: sb.Append(ExtractPlainText(span.Inlines)); break;
            }
        }
        return sb.ToString();
    }

    private static (Inline Inline, int NextIndex)? TryParseFootnoteRef(string s, int i, int end, RenderContext ctx)
    {
        var idStart = i + 2;
        var closeIdx = s.IndexOf(']', idStart);
        if (closeIdx < 0 || closeIdx >= end) return null;
        var id = s.Substring(idStart, closeIdx - idStart);
        if (id.Length == 0 || id.Contains(' ')) return null;
        if (!ctx.FootnoteDefs.ContainsKey(id)) return null;

        var button = new Button
        {
            Content = $"[{id}]",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 0, 0),
            FontSize = 11,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        AutomationProperties.SetName(button, $"脚注{id}へジャンプ");
        BindForeground(button, "Accent");
        var handler = ctx.OnTocLinkClicked;
        button.Click += (_, _) => handler($"fn:{id}");
        return (new InlineUIContainer(button), closeIdx + 1);
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
    /// 画像（<c>![alt](url)</c>）のURL分類にも同じ規則を再利用する。
    /// </summary>
    private static LinkKind ClassifyLink(string url)
    {
        if (url.StartsWith('#')) return LinkKind.Anchor;
        if (Uri.TryCreate(url, UriKind.Absolute, out _)) return LinkKind.External;
        return LinkKind.Relative;
    }

    // ------------------------------------------------------------------
    // 括弧の対応取り（リンク・画像共通）
    // ------------------------------------------------------------------

    /// <summary>
    /// <paramref name="start"/>（開き括弧の直後）から数えて、対応する閉じ括弧の位置を返す
    /// （入れ子の括弧・<c>\</c>エスケープを考慮する）。見つからなければ-1。
    /// </summary>
    private static int FindMatchingBracket(string s, int start, int end, char openCh, char closeCh)
    {
        var depth = 1;
        var i = start;
        while (i < end)
        {
            var ch = s[i];
            if (ch == '\\' && i + 1 < end) { i += 2; continue; }
            if (ch == openCh) depth++;
            else if (ch == closeCh)
            {
                depth--;
                if (depth == 0) return i;
            }
            i++;
        }
        return -1;
    }

    private static string ExtractUrlBeforeTitle(string raw)
    {
        if (raw.Length > 0 && raw[0] == '<')
        {
            var close = raw.IndexOf('>');
            if (close > 0) return raw[1..close];
        }
        var spaceIdx = raw.IndexOfAny(new[] { ' ', '\t' });
        return spaceIdx >= 0 ? raw[..spaceIdx] : raw;
    }

    private static string StripEscapes(string raw)
    {
        if (raw.IndexOf('\\') < 0) return raw;
        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length && IsEscapable(raw[i + 1])) { sb.Append(raw[i + 1]); i++; }
            else sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // 画像
    //
    // 【安全性】外部（http/https等の絶対URI）画像は自動で読み込まない（.mdを開いただけで
    // 外部へ通信が飛ぶのを避けるため）。既存の外部リンクと同じ確認ダイアログを経由してから
    // ブラウザで開くプレースホルダ表示にする。プロジェクト内の相対パス画像のみ、baseDirectoryが
    // 指定されている場合に限って実際に読み込む。見つからない・壊れている場合も例外にせず
    // 代替表示にする。幅・高さの上限（<see cref="ImageMaxWidth"/>・<see cref="ImageMaxHeight"/>）で
    // 大きな画像によるレイアウト崩れを防ぐ。
    // ------------------------------------------------------------------

    private static (string Alt, string Url, int NextIndex)? TryParseImage(string s, int i, int end)
    {
        var altEnd = FindMatchingBracket(s, i + 2, end, '[', ']');
        if (altEnd < 0) return null;
        var altText = s.Substring(i + 2, altEnd - (i + 2));
        var afterAlt = altEnd + 1;
        if (afterAlt >= end || s[afterAlt] != '(') return null;
        var urlEnd = FindMatchingBracket(s, afterAlt + 1, end, '(', ')');
        if (urlEnd < 0) return null;

        var urlRaw = s.Substring(afterAlt + 1, urlEnd - (afterAlt + 1)).Trim();
        var url = ExtractUrlBeforeTitle(urlRaw);
        return (StripEscapes(altText), url, urlEnd + 1);
    }

    private static Inline BuildImageInline(string alt, string url, RenderContext ctx)
    {
        var kind = ClassifyLink(url);

        if (kind == LinkKind.External) return BuildExternalImagePlaceholder(alt, url, ctx);

        if (kind == LinkKind.Relative && ctx.BaseDirectory is not null)
        {
            var resolved = TryResolveImagePath(ctx.BaseDirectory, url);
            if (resolved is not null)
            {
                var bitmap = TryLoadBitmap(resolved);
                if (bitmap is not null) return BuildLoadedImageInline(bitmap, alt);
            }
        }

        return BuildBrokenImagePlaceholder(alt);
    }

    private static string? TryResolveImagePath(string baseDirectory, string url)
    {
        var anchorIdx = url.IndexOfAny(new[] { '#', '?' });
        var cleaned = (anchorIdx >= 0 ? url[..anchorIdx] : url).Trim();
        if (cleaned.Length == 0) return null;

        try
        {
            var combined = Path.GetFullPath(Path.Combine(baseDirectory, cleaned));
            return File.Exists(combined) ? combined : null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static Bitmap? TryLoadBitmap(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            // 画像デコードの失敗要因（未対応形式・壊れたファイル・SkiaSharp内部例外等）は
            // 多岐にわたり実用上すべて列挙できないため、プレビュー表示を落とさないことを
            // 優先して包括的に捕捉し、代替表示（BuildBrokenImagePlaceholder）へフォールバックする。
            return null;
        }
    }

    private static Inline BuildLoadedImageInline(Bitmap bitmap, string alt)
    {
        var image = new Image
        {
            Source = bitmap,
            MaxWidth = ImageMaxWidth,
            MaxHeight = ImageMaxHeight,
            Stretch = Stretch.Uniform,
        };
        AutomationProperties.SetName(image, string.IsNullOrEmpty(alt) ? "画像" : alt);
        if (!string.IsNullOrEmpty(alt)) ToolTip.SetTip(image, alt);
        return new InlineUIContainer(image);
    }

    private static Inline BuildBrokenImagePlaceholder(string alt)
    {
        var text = string.IsNullOrEmpty(alt) ? "[画像を表示できません]" : $"[画像: {alt}（表示できません）]";
        var run = new Run(text) { FontStyle = FontStyle.Italic };
        run.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension("TextSecondary"));
        return run;
    }

    private static Inline BuildExternalImagePlaceholder(string alt, string url, RenderContext ctx)
    {
        var label = string.IsNullOrEmpty(alt) ? "外部画像" : $"外部画像: {alt}";
        var handler = ctx.Links.OnExternalLinkClicked;
        if (handler is null)
        {
            var run = new Run($"[{label}]") { FontStyle = FontStyle.Italic };
            run.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension("TextSecondary"));
            return run;
        }

        var button = new Button
        {
            Content = $"🖼 {label}（クリックで開く）",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        AutomationProperties.SetName(button, $"外部画像を開く: {label}");
        BindForeground(button, "TextSecondary");
        button.Click += (_, _) => handler(url);
        return new InlineUIContainer(button);
    }

    // ------------------------------------------------------------------
    // 見出しアンカー（GitHub互換のスラグ化）
    // ------------------------------------------------------------------

    /// <summary>
    /// 見出しテキストからGitHub互換のアンカー名を作る。取扱説明書.md内の目次リンク
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
    // 横スクロールバーの重なり対策（コードブロック・表）
    //
    // 【不具合】利用者からの実機報告（Windows）: コードブロックの横スクロールバーが
    // コード末尾の行に重なって読めなくなる。原因はAvaloniaのScrollViewer既定テンプレート
    // （Fluentテーマ、ScrollViewer.axaml。ilspycmdで逆コンパイルして確認）にある。
    // <see cref="ScrollViewer.AllowAutoHide"/>（既定true）のとき、コンテンツ描画部分
    // （PART_ContentPresenter）はGrid.RowSpan/ColumnSpanでスクロールバー用の行・列にも
    // 重ねて配置される（ホバー時だけ太くなる「浮いた」スクロールバーに見せるための仕組み）。
    // そのため、水平スクロールバーが実際に表示される場面ではコンテンツの最終行に重なる。
    //
    // AllowAutoHideをfalseにすればレイアウト上は重ならなくなる（コンテンツ側の行・列が
    // スクロールバー側に食い込まなくなる）が、副作用としてスクロールバー自体の見た目も
    // 変わってしまう（ScrollBar.axaml参照。falseだと常時「展開」状態＝ホバーしていなくても
    // 太いバーと矢印ボタンが出っぱなしになり、他のScrollViewer（既定のまま使っている）と
    // 見た目が揃わなくなる）。今回は見た目を変えず、横スクロールバーが実際に必要なとき
    // だけコンテンツの下に余白を確保する方式にした。
    //
    // 【余白は ScrollViewer.Padding ではなく中身側の Margin に付ける】 最初はScrollViewer.
    // Paddingを試したが、実機描画で確認したところ2行目（コードの末尾行）がまるごと
    // 消える不具合を新たに作ってしまった。VerticalScrollBarVisibility=Disabled（縦方向は
    // スクロール不可）のScrollViewerは、縦方向をStackPanelなど親から与えられた高さで
    // クリップする挙動になっており、Paddingを増やしても外側のサイズ（延いては親の
    // StackPanelが確保する高さ）までは連動して増えない（横方向はスクロール対象なので
    // 増えなくて当然だが、縦方向も同じ土俵で計算されてしまう）。一方、中身（コード本文の
    // <see cref="SelectableTextBlock"/>・表の<see cref="Border"/>）自身のMarginは測定
    // （Measure）の時点で中身のDesiredSizeへ直接組み込まれるため、ScrollViewer越しに
    // 親のStackPanelまで正しく高さが伝播し、末尾行を欠けさせずに下へ余白を確保できる
    // （Xvfb実機での見た目確認込み。検証方法は本ファイルのテスト・PR説明を参照）。
    // ------------------------------------------------------------------

    /// <summary>
    /// 横スクロールバーが実際に必要なときだけ、<paramref name="content"/>（ScrollViewerの
    /// 中身。コードブロックなら本文の<see cref="SelectableTextBlock"/>、表なら外枠の
    /// <see cref="Border"/>）の下にスクロールバーの高さぶんの<see cref="Layoutable.Margin"/>を
    /// 確保する（ScrollViewer.Paddingを使わない理由は上のコメント参照）。呼び出し時点で
    /// 既に付いていた余白（表のブロック間隔など）はBottomへ加算するだけで消さない。不要なとき
    /// （横に収まっているとき）は元の余白まで戻す（増やしたぶんだけを外す）ので、必要かどうかは
    /// 一度きりでなく<paramref name="scroll"/>の<see cref="ScrollViewer.Extent"/>と
    /// <see cref="ScrollViewer.Viewport"/>の比較で都度判定し直す。
    /// <see cref="ScrollViewer.ScrollChanged"/>はExtent/Viewport/Offsetのいずれかが
    /// 変わるたびに発火するため、ウィンドウ幅の変更やフォントサイズ変更で
    /// 「収まる⇔収まらない」が切り替わっても追従する。
    /// </summary>
    private static void ReserveSpaceForHorizontalScrollBar(ScrollViewer scroll, Control content)
    {
        // フォールバック既定値。Avalonia.Themes.Fluent 11.2.3の既定リソース"ScrollBarSize"
        // （ScrollBar.axamlがScrollBarのMinWidth/MinHeightに束ねている値）をilspycmdで
        // 逆コンパイルして確認した既定値（16px）。実測・リソースいずれも取得できない
        // 場合のみ使う、固定値に頼らないための最後の保険。
        var scrollBarHeight = 16.0;

        // 呼び出し元が元々設定していた余白（例: BuildTableの外枠Borderは表ブロック同士の
        // 間隔としてMargin(0,0,0,8)を既に持っている）を上書きしないよう保持しておく。
        // 下でThickness全体を書き換えるのではなくBottomにだけ加算する。
        var baseMargin = content.Margin;

        void UpdateSpacing()
        {
            var needsHorizontalScroll = scroll.Extent.Width > scroll.Viewport.Width + 0.5;
            content.Margin = needsHorizontalScroll
                ? new Thickness(baseMargin.Left, baseMargin.Top, baseMargin.Right, baseMargin.Bottom + scrollBarHeight)
                : baseMargin;
        }

        // テンプレート適用（visualツリーへ接続された後）で初めてPART_HorizontalScrollBarと
        // テーマリソースの両方に到達できる。それより前（構築直後）にリソースを引こうとしても
        // 論理ツリーにまだ繋がっておらず解決できない。
        scroll.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<ScrollBar>("PART_HorizontalScrollBar") is { } bar)
            {
                // テンプレート適用直後はまだ計測前で高さ0のことが多いので、まずテーマの
                // "ScrollBarSize"リソースを反映しておく。
                if (scroll.TryFindResource("ScrollBarSize", out var resourceValue) && resourceValue is double size && size > 0)
                    scrollBarHeight = size;

                // 実際にレイアウトされた高さが判明し次第、そちらを優先する
                // （テーマ・OSの違いでリソース値と実測値がずれても実測を信用する）。
                bar.SizeChanged += (_, sizeArgs) =>
                {
                    if (sizeArgs.NewSize.Height > 0)
                    {
                        scrollBarHeight = sizeArgs.NewSize.Height;
                        UpdateSpacing();
                    }
                };
            }
            UpdateSpacing();
        };
        scroll.ScrollChanged += (_, _) => UpdateSpacing();
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
