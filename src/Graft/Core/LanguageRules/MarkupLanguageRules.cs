namespace Graft.Core;

/// <summary>
/// HTML / XML / Markdown の言語ルール定義。
/// </summary>
internal static class MarkupLanguageRules
{
    // よく使われるHTML要素名。タグ名を強調表示するためキーワードとして扱う。
    private static readonly HashSet<string> HtmlTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "head", "body", "title", "meta", "link", "script", "style",
        "div", "span", "a", "p", "br", "hr", "img", "table", "thead", "tbody",
        "tfoot", "tr", "td", "th", "ul", "ol", "li", "dl", "dt", "dd", "form",
        "input", "button", "select", "option", "textarea", "label", "nav",
        "header", "footer", "main", "section", "article", "aside", "figure",
        "figcaption", "canvas", "svg", "video", "audio", "source", "iframe",
        "h1", "h2", "h3", "h4", "h5", "h6", "code", "pre", "blockquote", "em",
        "strong", "small", "b", "i", "u", "template", "slot",
    };

    public static readonly LanguageRule Html = new()
    {
        Name = "HTML",
        Keywords = HtmlTagNames,
        LineCommentPrefixes = Array.Empty<string>(),
        BlockComments = new[] { new StringSpec("<!--", "-->", Multiline: true, EscapeChar: null, Kind: TokenKind.Comment) },
        Strings = new[]
        {
            // 属性値の引用符。HTMLはバックスラッシュエスケープを持たない。
            new StringSpec("\"", "\"", Multiline: false, EscapeChar: null),
            new StringSpec("'", "'", Multiline: false, EscapeChar: null),
        },
    };

    public static readonly LanguageRule Xml = new()
    {
        Name = "XML",
        Keywords = new HashSet<string>(StringComparer.Ordinal),
        LineCommentPrefixes = Array.Empty<string>(),
        BlockComments = new[] { new StringSpec("<!--", "-->", Multiline: true, EscapeChar: null, Kind: TokenKind.Comment) },
        Strings = new[]
        {
            // CDATAセクションは複数行にまたがり得るため、文字列区切りの一種として扱う。
            new StringSpec("<![CDATA[", "]]>", Multiline: true, EscapeChar: null),
            new StringSpec("\"", "\"", Multiline: false, EscapeChar: null),
            new StringSpec("'", "'", Multiline: false, EscapeChar: null),
        },
    };

    public static readonly LanguageRule Markdown = new()
    {
        Name = "Markdown",
        Keywords = new HashSet<string>(StringComparer.Ordinal),
        // 見出し・強調・リンク等は8種のトークン種別に自然対応しないため対象外とし、
        // フェンス付きコードブロック／インラインコードと埋め込みHTMLコメントのみ扱う。
        LineCommentPrefixes = Array.Empty<string>(),
        BlockComments = new[] { new StringSpec("<!--", "-->", Multiline: true, EscapeChar: null, Kind: TokenKind.Comment) },
        Strings = new[]
        {
            new StringSpec("```", "```", Multiline: true, EscapeChar: null),
            new StringSpec("`", "`", Multiline: false, EscapeChar: null),
        },
    };
}
