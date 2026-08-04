using System.Text.RegularExpressions;

namespace Graft.Core;

/// <summary>
/// 文字列リテラルの区切り定義。開始・終了トークン、複数行許容、エスケープ方式を表す。
/// </summary>
/// <param name="Open">開始トークン（例: <c>"</c>、<c>"""</c>、<c>@"</c>）。</param>
/// <param name="Close">終了トークン。</param>
/// <param name="Multiline">終了トークンが見つからないまま行末に達した場合、次行へ継続するかどうか。</param>
/// <param name="EscapeChar">直後の1文字を無条件で読み飛ばすエスケープ文字。使わない言語は null。</param>
/// <param name="DoubledClosingEscapes">終了トークンを2つ連続で書くとエスケープになる方式（SQLの <c>''</c>、C#逐語文字列の <c>""</c> 等）かどうか。</param>
/// <param name="Kind">この区切りが表すトークン種別。文字列区切りは <see cref="TokenKind.String"/>、
/// ブロックコメントの区切りは <see cref="TokenKind.Comment"/> を指定する。</param>
public readonly record struct StringSpec(
    string Open,
    string Close,
    bool Multiline,
    char? EscapeChar = '\\',
    bool DoubledClosingEscapes = false,
    TokenKind Kind = TokenKind.String);

/// <summary>
/// ヒアドキュメントのように、終了トークンが開始行の内容から動的に決まる複数行文字列の定義。
/// <c>StartPattern</c> は行中の開始位置にマッチし、終端語を名前付きグループ <c>term</c> で捕捉する。
/// </summary>
public sealed record HeredocSpec(Regex StartPattern);

/// <summary>
/// 言語ごとの字句規則。キーワード集合、コメント・文字列の区切り、識別子/数値/演算子の判定パターン、
/// 関数呼び出し・型判定のヒントを保持する。<see cref="SyntaxLexer"/> はこれを参照してトークン化する。
/// 色情報は一切持たない（配色は Themes/Syntax.xaml の責務）。
/// </summary>
public sealed class LanguageRule
{
    /// <summary>言語名（表示用）。</summary>
    public required string Name { get; init; }

    /// <summary>予約語の集合。大文字小文字の扱いは呼び出し側が渡す比較子で決める。</summary>
    public IReadOnlySet<string> Keywords { get; init; } = new HashSet<string>();

    /// <summary>行コメントの開始トークン（例: <c>//</c>、<c>#</c>、<c>--</c>）。無ければ空。</summary>
    public IReadOnlyList<string> LineCommentPrefixes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// ブロックコメントの区切り定義（<see cref="StringSpec.Kind"/> は常に <see cref="TokenKind.Comment"/>）。
    /// 未終了の場合は常に次行へ継続するとみなすため <see cref="StringSpec.Multiline"/> は true を指定する。無ければ空。
    /// </summary>
    public IReadOnlyList<StringSpec> BlockComments { get; init; } = Array.Empty<StringSpec>();

    /// <summary>文字列リテラルの区切り定義。優先度の高いもの（より長い開始トークン）を先に置く。</summary>
    public IReadOnlyList<StringSpec> Strings { get; init; } = Array.Empty<StringSpec>();

    /// <summary>ヒアドキュメントの定義。対応しない言語は null。</summary>
    public HeredocSpec? Heredoc { get; init; }

    /// <summary>数値リテラルの判定パターン。既定は10進数・16進数・指数表記に対応する共通パターン。</summary>
    public Regex NumberPattern { get; init; } = DefaultNumberPattern;

    /// <summary>識別子の判定パターン。既定はASCII英数字とアンダースコア。</summary>
    public Regex IdentifierPattern { get; init; } = DefaultIdentifierPattern;

    /// <summary>
    /// 演算子・記号とみなす1文字の集合。1文字判定のみのため正規表現ではなく集合で持つ
    /// （行の大半を占める記号の判定を高速化するため）。
    /// </summary>
    public IReadOnlySet<char> OperatorChars { get; init; } = DefaultOperatorChars;

    /// <summary>PascalCase の識別子を型名とみなすかどうか（C#/JS/TS等）。</summary>
    public bool PascalCaseIsType { get; init; }

    /// <summary>
    /// 直前の単語がこの集合に含まれる場合、次の識別子を型名とみなす
    /// （例: <c>class</c> の直後、<c>struct</c> の直後）。
    /// </summary>
    public IReadOnlySet<string> TypeIntroducerKeywords { get; init; } = new HashSet<string>();

    /// <summary>数値リテラルの既定パターン（16進数・10進数・指数表記）。</summary>
    public static readonly Regex DefaultNumberPattern = new(
        @"\G(?:0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)",
        RegexOptions.None);

    /// <summary>識別子の既定パターン（ASCII英字・アンダースコア始まり）。</summary>
    public static readonly Regex DefaultIdentifierPattern = new(
        @"\G[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.None);

    /// <summary>演算子・記号とみなす1文字の既定集合。</summary>
    public static readonly IReadOnlySet<char> DefaultOperatorChars =
        new HashSet<char>("-+*/%=<>!&|^~?:;,.()[]{}");

    private static readonly Dictionary<string, LanguageRule> ExtensionMap = BuildExtensionMap();

    /// <summary>
    /// 拡張子から対応する言語ルールを取得する。未対応の拡張子は null を返す（エラーにしない）。
    /// 先頭の <c>.</c> の有無、大文字小文字は問わない。
    /// </summary>
    public static LanguageRule? ForExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var normalized = extension.StartsWith('.') ? extension[1..] : extension;
        normalized = normalized.Trim().ToLowerInvariant();
        return ExtensionMap.TryGetValue(normalized, out var rule) ? rule : null;
    }

    private static Dictionary<string, LanguageRule> BuildExtensionMap()
    {
        var map = new Dictionary<string, LanguageRule>();

        void Add(LanguageRule rule, params string[] extensions)
        {
            foreach (var extension in extensions)
            {
                map[extension] = rule;
            }
        }

        Add(ProgrammingLanguageRules.Python, "py");
        Add(ProgrammingLanguageRules.CSharp, "cs");
        Add(ProgrammingLanguageRules.JavaScript, "js", "jsx", "ts", "tsx");

        Add(MarkupLanguageRules.Html, "html", "htm");
        Add(MarkupLanguageRules.Xml, "xml");
        Add(MarkupLanguageRules.Markdown, "md");

        Add(StyleAndDataLanguageRules.Css, "css");
        Add(StyleAndDataLanguageRules.Json, "json");
        Add(StyleAndDataLanguageRules.Yaml, "yaml", "yml");

        Add(QueryAndShellLanguageRules.Sql, "sql");
        Add(QueryAndShellLanguageRules.Shell, "sh", "bash");

        return map;
    }
}
