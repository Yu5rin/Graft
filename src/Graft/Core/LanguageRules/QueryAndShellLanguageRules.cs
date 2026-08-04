using System.Text.RegularExpressions;

namespace Graft.Core;

/// <summary>
/// SQL / シェルスクリプトの言語ルール定義。
/// </summary>
internal static class QueryAndShellLanguageRules
{
    // <<EOF, <<-EOF, <<'EOF', <<"EOF" のいずれの書式も終端語を名前付きグループ term で捕捉する。
    private static readonly Regex HeredocStartPattern = new(
        @"\G<<-?\s*(?:'(?<term>[A-Za-z_]\w*)'|""(?<term>[A-Za-z_]\w*)""|(?<term>[A-Za-z_]\w*))",
        RegexOptions.None);

    public static readonly LanguageRule Sql = new()
    {
        Name = "SQL",
        Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "select", "from", "where", "insert", "into", "values", "update",
            "set", "delete", "create", "table", "alter", "drop", "index",
            "view", "join", "inner", "left", "right", "full", "outer", "on",
            "group", "by", "order", "having", "limit", "offset", "union",
            "all", "distinct", "as", "and", "or", "not", "null", "is", "in",
            "exists", "between", "like", "case", "when", "then", "else", "end",
            "primary", "key", "foreign", "references", "default", "constraint",
            "unique", "check", "cascade", "transaction", "commit", "rollback",
            "begin", "declare", "procedure", "function", "returns", "return",
            "trigger", "with", "exec", "execute", "grant", "revoke", "int",
            "integer", "varchar", "char", "text", "date", "datetime",
            "timestamp", "boolean", "float", "decimal", "numeric", "bigint",
            "smallint", "asc", "desc",
        },
        LineCommentPrefixes = new[] { "--" },
        BlockComments = new[] { new StringSpec("/*", "*/", Multiline: true, EscapeChar: null, Kind: TokenKind.Comment) },
        Strings = new[]
        {
            // 標準SQLの文字列リテラルは '' で引用符自体をエスケープする。
            new StringSpec("'", "'", Multiline: false, EscapeChar: null, DoubledClosingEscapes: true),
            new StringSpec("\"", "\"", Multiline: false, EscapeChar: null, DoubledClosingEscapes: true),
        },
        TypeIntroducerKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "table" },
    };

    public static readonly LanguageRule Shell = new()
    {
        Name = "シェルスクリプト",
        Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "then", "elif", "else", "fi", "for", "while", "until", "do",
            "done", "case", "esac", "function", "return", "in", "select",
            "time", "break", "continue", "exit", "export", "local", "readonly",
            "declare", "unset", "shift", "trap", "eval", "exec", "source",
            "alias", "unalias", "set", "echo", "read", "printf", "cd", "test",
        },
        LineCommentPrefixes = new[] { "#" },
        BlockComments = Array.Empty<StringSpec>(),
        Strings = new[]
        {
            // bashではクォートを閉じないまま改行しても、次のクォートまでリテラルとして継続する。
            new StringSpec("\"", "\"", Multiline: true),
            new StringSpec("'", "'", Multiline: true, EscapeChar: null),
        },
        Heredoc = new HeredocSpec(HeredocStartPattern),
    };
}
