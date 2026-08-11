using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Graft.Core;

namespace Graft.Editor;

/// <summary>
/// Markdownプレビュー機能の追加要件（案B）: 編集モードでもMarkdownの構造が一目で分かるよう、
/// 控えめな装飾を行うカラライザ。<see cref="SyntaxHighlightBridge"/>と同じ
/// <see cref="DocumentColorizingTransformer"/>の仕組みを使うが、<see cref="TokenKind"/>
/// （11言語で共有する汎用トークン種別）を増やす代わりに、Markdown専用の単純な正規表現ベースの
/// 行内装飾として独立させた。
///
/// 【TokenKindを増やさなかった理由】
/// 見出し（行頭の#の個数で6段階）・強調（**で囲む）・インラインコード（`で囲む）・リンク
/// （[text](url)）はいずれも「キーワード／文字列／数値／識別子」といった<see cref="SyntaxLexer"/>の
/// 前提（<see cref="LanguageRule"/>の8種のトークン種別）に自然に対応せず、無理に対応付けると
/// 汎用のカラライザ側（11言語すべてに影響する<see cref="SyntaxHighlightBridge"/>）が複雑になる。
/// Markdown（.md）は自前レキサでも既にコードブロック／インラインコードのみを対象にしている
/// （<c>MarkupLanguageRules.Markdown</c>のコメント参照）ため、見出し等の追加装飾は本クラスへ
/// 分離し、既存のカラライザには一切手を入れない方針にした。
///
/// 【記号を隠さない】
/// <c>#</c>や<c>**</c>などの記号は表示したまま、その行・その範囲全体に書体（太字・等幅＋背景色・
/// 色）を適用するだけに留める。記号を隠す方式は編集時（カーソルが記号の位置にある場合の
/// 表示切替等）との整合が難しいため採用しない（利用者指示のとおり）。
///
/// 【行だけを見る単純な実装】
/// フェンス付きコードブロック（```で囲む複数行）のような複数行にまたがる状態は追跡しない
/// （「控えめな装飾」の範囲として意図的に簡略化した）。フェンスの開始／終了行自体
/// （<c>```</c>で始まる行）は強調・リンク等の対象から除外し、素通しする。フェンス内部の行が
/// たまたま<c>#</c>で始まる・<c>**...**</c>を含む場合に誤って装飾されることがあり得るが、
/// エディタ内の見た目だけの問題でありファイルの内容には一切影響しない。
///
/// 【フォントサイズを変えない理由】
/// 見出しは太字のみとし、フォントサイズは変更しない。行の高さが変わるフォントサイズ変更は
/// AvaloniaEdit側の行レイアウト計算に影響しうる（1行だけ高さが変わることでスクロール位置の
/// 見た目が飛ぶ、キャレット移動時の視認性が変わる等）ため、実際に描画して確認した結果
/// （EditorPaneのMarkdown編集モードのスクリーンショット参照）問題は見られなかったものの、
/// 太字・色・背景色だけで見出しは十分に識別できると判断し、サイズ変更という追加リスクは
/// 取らないことにした。
/// </summary>
public sealed class MarkdownInlineColorizer : DocumentColorizingTransformer
{
    private static readonly Regex HeadingPattern = new(@"^#{1,6}\s.*$", RegexOptions.Compiled);
    private static readonly Regex BoldPattern = new(@"\*\*[^*\r\n]+\*\*", RegexOptions.Compiled);
    private static readonly Regex InlineCodePattern = new(@"`[^`\r\n]+`", RegexOptions.Compiled);
    private static readonly Regex LinkPattern = new(@"\[[^\]\r\n]+\]\([^)\r\n]+\)", RegexOptions.Compiled);

    private bool _enabled;

    /// <summary>有効・無効を切り替える。Markdown（.md）以外のファイルでは無効にする。</summary>
    public void SetEnabled(bool enabled) => _enabled = enabled;

    protected override void ColorizeLine(DocumentLine line)
    {
        // 課題3と同じ考え方: 極端に長い行は打ち切ってO(1)化する（SyntaxHighlightBridge.ColorizeLine参照）。
        if (!_enabled || line.Length == 0 || line.Length > DocumentSession.LongLineThreshold) return;

        var text = CurrentContext.Document.GetText(line);
        if (text.TrimStart().StartsWith("```", StringComparison.Ordinal)) return; // フェンス行自体は素通し。

        // 見出し行は行全体を太字にするだけに留め、強調等は重ねて解析しない（十分に区別できるため）。
        if (HeadingPattern.IsMatch(text))
        {
            ChangeLinePart(line.Offset, line.EndOffset, ApplyHeading);
            return;
        }

        foreach (Match m in BoldPattern.Matches(text))
        {
            ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, ApplyBold);
        }

        foreach (Match m in InlineCodePattern.Matches(text))
        {
            ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, ApplyInlineCode);
        }

        foreach (Match m in LinkPattern.Matches(text))
        {
            ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, ApplyLink);
        }
    }

    private static void ApplyHeading(VisualLineElement element) => ApplyBoldTypeface(element);

    private static void ApplyBold(VisualLineElement element) => ApplyBoldTypeface(element);

    private static void ApplyBoldTypeface(VisualLineElement element)
    {
        var current = element.TextRunProperties.Typeface;
        element.TextRunProperties.SetTypeface(new Typeface(current.FontFamily, current.Style, FontWeight.Bold, current.Stretch));
    }

    private static void ApplyInlineCode(VisualLineElement element)
    {
        if (ResolveFontFamily("CodeFontFamily") is { } fontFamily)
        {
            var current = element.TextRunProperties.Typeface;
            element.TextRunProperties.SetTypeface(new Typeface(fontFamily, current.Style, current.Weight, current.Stretch));
        }

        if (ResolveBrush("BgSurface") is { } background)
        {
            element.TextRunProperties.SetBackgroundBrush(background);
        }
    }

    private static void ApplyLink(VisualLineElement element)
    {
        if (ResolveBrush("Accent") is { } brush)
        {
            element.TextRunProperties.SetForegroundBrush(brush);
        }
    }

    private static IBrush? ResolveBrush(string key)
        => Application.Current is { } app && app.TryFindResource(key, null, out var value) && value is IBrush brush
            ? brush
            : null;

    private static FontFamily? ResolveFontFamily(string key)
        => Application.Current is { } app && app.TryFindResource(key, null, out var value) && value is FontFamily family
            ? family
            : null;
}
