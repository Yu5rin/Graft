namespace Graft.Core;

/// <summary>
/// Python / C# / JavaScript・TypeScript の言語ルール定義。
/// </summary>
internal static class ProgrammingLanguageRules
{
    public static readonly LanguageRule Python = new()
    {
        Name = "Python",
        Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "False", "None", "True", "and", "as", "assert", "async", "await",
            "break", "class", "continue", "def", "del", "elif", "else", "except",
            "finally", "for", "from", "global", "if", "import", "in", "is",
            "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try",
            "while", "with", "yield", "match", "case", "self", "cls",
        },
        LineCommentPrefixes = new[] { "#" },
        BlockComments = Array.Empty<StringSpec>(),
        Strings = new[]
        {
            // 三重引用符（複数行）は単一引用符より先に判定する。
            new StringSpec("\"\"\"", "\"\"\"", Multiline: true),
            new StringSpec("'''", "'''", Multiline: true),
            new StringSpec("\"", "\"", Multiline: false),
            new StringSpec("'", "'", Multiline: false),
        },
        PascalCaseIsType = true,
        TypeIntroducerKeywords = new HashSet<string>(StringComparer.Ordinal) { "class" },
    };

    public static readonly LanguageRule CSharp = new()
    {
        Name = "C#",
        Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
            "char", "checked", "class", "const", "continue", "decimal", "default",
            "delegate", "do", "double", "else", "enum", "event", "explicit",
            "extern", "false", "finally", "fixed", "float", "for", "foreach",
            "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
            "lock", "long", "namespace", "new", "null", "object", "operator",
            "out", "override", "params", "private", "protected", "public",
            "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
            "stackalloc", "static", "string", "struct", "switch", "this", "throw",
            "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
            "ushort", "using", "virtual", "void", "volatile", "while", "var",
            "dynamic", "async", "await", "record", "init", "nameof", "with",
            "global", "partial", "get", "set", "value", "required", "when",
        },
        LineCommentPrefixes = new[] { "//" },
        BlockComments = new[] { new StringSpec("/*", "*/", Multiline: true, EscapeChar: null, Kind: TokenKind.Comment) },
        Strings = new[]
        {
            // 逐語文字列は実際の改行を含み得るため複数行扱いとし、"" による引用符エスケープを認める。
            new StringSpec("@\"", "\"", Multiline: true, EscapeChar: null, DoubledClosingEscapes: true),
            new StringSpec("\"", "\"", Multiline: false),
        },
        PascalCaseIsType = true,
        TypeIntroducerKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "class", "struct", "interface", "enum", "record",
        },
    };

    public static readonly LanguageRule JavaScript = new()
    {
        Name = "JavaScript/TypeScript",
        Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "break", "case", "catch", "class", "const", "continue", "debugger",
            "default", "delete", "do", "else", "export", "extends", "finally",
            "for", "function", "if", "import", "in", "instanceof", "new",
            "return", "super", "switch", "this", "throw", "try", "typeof",
            "var", "void", "while", "with", "yield", "let", "static", "enum",
            "await", "async", "of", "as", "from", "implements", "interface",
            "package", "private", "protected", "public", "type", "namespace",
            "declare", "readonly", "keyof", "infer", "satisfies", "abstract",
            "null", "true", "false", "undefined", "get", "set",
        },
        LineCommentPrefixes = new[] { "//" },
        BlockComments = new[] { new StringSpec("/*", "*/", Multiline: true, EscapeChar: null, Kind: TokenKind.Comment) },
        Strings = new[]
        {
            // テンプレートリテラルは複数行を許容する。
            new StringSpec("`", "`", Multiline: true),
            new StringSpec("\"", "\"", Multiline: false),
            new StringSpec("'", "'", Multiline: false),
        },
        PascalCaseIsType = true,
        TypeIntroducerKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "class", "interface", "type", "extends", "implements",
        },
    };
}
