namespace Graft.Core;

/// <summary>
/// CSS / JSON / YAML の言語ルール定義。
/// </summary>
internal static class StyleAndDataLanguageRules
{
    public static readonly LanguageRule Css = new()
    {
        Name = "CSS",
        Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "inherit", "initial", "unset", "revert", "none", "auto", "block",
            "inline", "inline-block", "flex", "inline-flex", "grid", "absolute",
            "relative", "fixed", "sticky", "static", "important", "solid",
            "dashed", "dotted", "bold", "normal", "italic", "underline",
            "center", "left", "right", "top", "bottom", "hidden", "visible",
            "transparent", "currentColor",
        },
        LineCommentPrefixes = Array.Empty<string>(),
        BlockComments = new[] { new StringSpec("/*", "*/", Multiline: true, EscapeChar: null, Kind: TokenKind.Comment) },
        Strings = new[]
        {
            new StringSpec("\"", "\"", Multiline: false),
            new StringSpec("'", "'", Multiline: false),
        },
    };

    public static readonly LanguageRule Json = new()
    {
        Name = "JSON",
        Keywords = new HashSet<string>(StringComparer.Ordinal) { "true", "false", "null" },
        LineCommentPrefixes = Array.Empty<string>(),
        BlockComments = Array.Empty<StringSpec>(),
        Strings = new[]
        {
            // JSON文字列は実際の改行を含めない。未終端の場合も行末で打ち切る。
            new StringSpec("\"", "\"", Multiline: false),
        },
    };

    public static readonly LanguageRule Yaml = new()
    {
        Name = "YAML",
        Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "true", "false", "null", "yes", "no", "on", "off", "~",
        },
        LineCommentPrefixes = new[] { "#" },
        BlockComments = Array.Empty<StringSpec>(),
        Strings = new[]
        {
            new StringSpec("\"", "\"", Multiline: false),
            // 単一引用符文字列はバックスラッシュを解釈せず、'' の連続でリテラルの引用符を表す。
            new StringSpec("'", "'", Multiline: false, EscapeChar: null, DoubledClosingEscapes: true),
        },
    };
}
