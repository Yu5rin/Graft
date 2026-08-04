using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Graft.Features;

/// <summary>
/// 仕様書10.2の除外規則のうち .gitignore 解釈部分を担う。外部ライブラリは使わず自前実装する。
/// ネストした .gitignore（各 .gitignore はそれが置かれたディレクトリ配下にのみ効力を持つ）と、
/// <c>*</c> <c>**</c> <c>?</c> による glob、行頭 <c>!</c> による否定（再包含）、末尾 <c>/</c> の
/// ディレクトリ限定、先頭 <c>/</c> によるアンカー指定に対応する。
///
/// 既定の除外パターン（node_modules 等）やプロジェクト単位の追加除外（<see cref="ProjectOverrides.Excludes"/>）
/// も同じ規則文法で表現できるため、<see cref="FromPatterns"/> で同じエンジンに載せて
/// <see cref="Merge"/> で合成できるようにしてある。合成後の判定は「最後にマッチしたルールが勝つ」
/// という .gitignore 本来の規則に従う。
/// </summary>
public sealed class GitignoreFilter
{
    /// <summary>ルールを1件も持たない空のフィルタ。</summary>
    public static readonly GitignoreFilter Empty = new(Array.Empty<Rule>());

    private readonly IReadOnlyList<Rule> _rules;

    private GitignoreFilter(IReadOnlyList<Rule> rules)
    {
        _rules = rules;
    }

    /// <summary>
    /// root配下の .gitignore をすべて読み込む。ネストした .gitignore にも対応するため、
    /// 各ファイルが置かれたディレクトリを基点（BaseDir）としてルールを保持する。
    /// </summary>
    public static async Task<GitignoreFilter> LoadAsync(string root, CancellationToken ct = default)
    {
        var rules = new List<Rule>();
        if (!Directory.Exists(root))
        {
            return new GitignoreFilter(rules);
        }

        var files = Directory.EnumerateFiles(root, ".gitignore", SearchOption.AllDirectories)
            .OrderBy(f => f.Length);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.GetDirectoryName(file) ?? root;
            var baseDir = NormalizeRelative(Path.GetRelativePath(root, dir));
            var lines = await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false);
            rules.AddRange(ParseLines(lines, baseDir, ".gitignore"));
        }

        return new GitignoreFilter(rules);
    }

    /// <summary>
    /// .gitignore 形式の文字列一覧からフィルタを作る。既定除外パターンやプロジェクト単位の
    /// 追加除外（<see cref="ProjectOverrides.Excludes"/>）を同じ文法で表現するために使う。
    /// ルートからの相対パス全体に対して評価する（BaseDir は空文字列）。
    /// </summary>
    public static GitignoreFilter FromPatterns(IEnumerable<string> patterns, string label = "")
        => new(ParseLines(patterns, string.Empty, label).ToList());

    /// <summary>
    /// 2つのフィルタを合成する。this のルールを先、other のルールを後に評価するため、
    /// other 側に否定ルール（<c>!</c>）を書けば this 側の除外を打ち消せる。
    /// </summary>
    public GitignoreFilter Merge(GitignoreFilter other)
        => new(_rules.Concat(other._rules).ToList());

    /// <summary>指定パスが除外対象かどうかを判定する。</summary>
    public bool IsIgnored(string relativePath, bool isDirectory) => Evaluate(relativePath, isDirectory).Ignored;

    /// <summary>
    /// 除外判定に加え、最後にマッチしたルールのラベル（"既定除外" 等）を返す。
    /// 除外理由をUIへ表示する用途に使う。
    /// </summary>
    public (bool Ignored, string? MatchedLabel) Evaluate(string relativePath, bool isDirectory)
    {
        var normalized = NormalizeRelative(relativePath);
        var ignored = false;
        string? label = null;
        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory)
            {
                continue;
            }
            if (!TryGetPathWithinBase(normalized, rule.BaseDir, out var pathInBase))
            {
                continue;
            }
            if (rule.Matcher.IsMatch(pathInBase))
            {
                ignored = !rule.Negate;
                label = rule.Negate ? null : rule.Label;
            }
        }
        return (ignored, label);
    }

    private static IEnumerable<Rule> ParseLines(IEnumerable<string> lines, string baseDir, string label)
    {
        foreach (var raw in lines)
        {
            var rule = ParseLine(raw, baseDir, label);
            if (rule is not null)
            {
                yield return rule;
            }
        }
    }

    private static Rule? ParseLine(string raw, string baseDir, string label)
    {
        var line = raw.TrimEnd();
        if (line.Length == 0 || line.StartsWith('#'))
        {
            return null;
        }

        var negate = false;
        if (line.StartsWith('!'))
        {
            negate = true;
            line = line[1..];
        }
        if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
        {
            line = line[1..];
        }

        var directoryOnly = false;
        if (line.EndsWith('/'))
        {
            directoryOnly = true;
            line = line[..^1];
        }
        if (line.Length == 0)
        {
            return null;
        }

        var anchored = line.Contains('/');
        if (line.StartsWith('/'))
        {
            line = line[1..];
        }
        if (line.Length == 0)
        {
            return null;
        }

        var core = TranslateGlobToRegex(line);
        var pattern = anchored ? $"^{core}$" : $"(^|.*/){core}$";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        return new Rule(baseDir, negate, directoryOnly, regex, label);
    }

    /// <summary>gitignore の glob（<c>*</c> <c>**</c> <c>?</c>）を正規表現の断片へ変換する。</summary>
    private static string TranslateGlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < glob.Length)
        {
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i = AppendDoubleStar(glob, i, sb);
                continue;
            }
            AppendSingleChar(glob[i], sb);
            i++;
        }
        return sb.ToString();
    }

    private static int AppendDoubleStar(string glob, int i, StringBuilder sb)
    {
        var isLast = i + 2 >= glob.Length;
        var hasSlashAfter = !isLast && glob[i + 2] == '/';
        if (isLast)
        {
            sb.Append(".*");
            return i + 2;
        }
        if (hasSlashAfter)
        {
            sb.Append("(?:.*/)?");
            return i + 3;
        }
        // "**foo" のような稀な形は通常の連続する * として扱う（安全側）。
        sb.Append("[^/]*");
        return i + 2;
    }

    private static void AppendSingleChar(char c, StringBuilder sb)
    {
        switch (c)
        {
            case '*':
                sb.Append("[^/]*");
                break;
            case '?':
                sb.Append("[^/]");
                break;
            default:
                sb.Append(Regex.Escape(c.ToString()));
                break;
        }
    }

    private static bool TryGetPathWithinBase(string path, string baseDir, out string result)
    {
        if (baseDir.Length == 0)
        {
            result = path;
            return true;
        }
        if (path.Equals(baseDir, StringComparison.OrdinalIgnoreCase))
        {
            result = string.Empty;
            return true;
        }
        var prefix = baseDir + "/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            result = path[prefix.Length..];
            return true;
        }
        result = string.Empty;
        return false;
    }

    private static string NormalizeRelative(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        return normalized == "." ? string.Empty : normalized;
    }

    private sealed record Rule(string BaseDir, bool Negate, bool DirectoryOnly, Regex Matcher, string Label);
}
