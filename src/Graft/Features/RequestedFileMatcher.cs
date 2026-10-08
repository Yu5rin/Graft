namespace Graft.Features;

/// <summary>要求されたパス1件の照合結果の種別。</summary>
public enum RequestedFileMatchKind
{
    /// <summary>プロジェクト内の、選べるファイルに該当した。</summary>
    Matched,
    /// <summary>プロジェクト内にあるが、除外規則（既定除外・.gitignore・サイズ超過・バイナリ等）で選べない。</summary>
    Excluded,
    /// <summary>該当するのがファイルではなくフォルダだった（フォルダ単位では指定できない）。</summary>
    Directory,
    /// <summary>プロジェクト内に見つからない（または大文字小文字違いの候補が複数あり特定できない）。</summary>
    NotFound,
}

/// <summary>
/// 要求されたパス1件の照合結果。
/// </summary>
/// <param name="Requested">AIが書いたままのパス（表示用）。</param>
/// <param name="Kind">照合結果の種別。</param>
/// <param name="RelativePath">
/// 該当したノードのプロジェクト相対パス（区切りは "/"）。<see cref="RequestedFileMatchKind.NotFound"/> では null。
/// 除外ディレクトリの配下にあるファイルは、ファイル自体が走査されていないため、除外されている
/// 祖先ディレクトリのパスを入れる。
/// </param>
/// <param name="ExcludeReason"><see cref="RequestedFileMatchKind.Excluded"/> のときの除外理由。</param>
public sealed record RequestedFileMatch(
    string Requested, RequestedFileMatchKind Kind, string? RelativePath, string? ExcludeReason);

/// <summary>
/// AIが「このファイルも見せて」と求めたパス（<c>NEED_MORE_CONTEXT: &lt;パス&gt;</c>）を、
/// コンテキスト収集が走査したノードの一覧と突き合わせる純ロジック。
///
/// 【なぜ文字列の完全一致ではなく正規化して照合するか】AIは自分が見ているパスの書き方
/// （Windows のバックスラッシュ、先頭の <c>./</c>、プロジェクトの絶対パス付き、行末の空白など）で
/// 返してくる。Graft 内部のパスはプロジェクトルートからの相対・区切りは "/" なので、そのまま
/// 比べると実在するファイルが「見つからない」になり、利用者が手で探し直すことになる。
///
/// 【大文字小文字の扱い】まず完全一致を探し、無いときに限って大文字小文字を無視した一致を
/// 探す。後者が**ちょうど1件**に定まるときだけ採用する。Windows（大文字小文字を区別しない
/// ファイルシステム）では常に1件に定まるので違いを吸収できる。大文字小文字を区別する
/// ファイルシステムで <c>A.cs</c> と <c>a.cs</c> が両方ある場合に、どちらを指しているか
/// 分からないまま片方を選んでしまうことは避ける（見つからない扱いにして利用者に知らせる）。
/// OSで場合分けしないのは、OS別の分岐を持つとテストがその環境でしか通らなくなるため。
/// </summary>
public static class RequestedFileMatcher
{
    /// <summary>
    /// 要求されたパスを、走査結果と同じ形（プロジェクト相対・区切りは "/"）に整える。
    /// 区切りの <c>\</c> を <c>/</c> へ、先頭の <c>./</c>・<c>/</c> と重なった区切り・末尾の区切りを除く。
    /// <paramref name="projectRoot"/> を指定すると、プロジェクトルートの絶対パスで始まるときはその部分を除く。
    /// </summary>
    public static string Normalize(string requested, string? projectRoot = null)
    {
        var path = requested.Trim().Replace('\\', '/');

        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var root = projectRoot.Trim().Replace('\\', '/').TrimEnd('/');
            if (root.Length > 0
                && path.Length > root.Length
                && path[root.Length] == '/'
                && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                path = path[(root.Length + 1)..];
            }
        }

        while (path.Contains("//", StringComparison.Ordinal)) path = path.Replace("//", "/", StringComparison.Ordinal);
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return path.Trim('/');
    }

    /// <summary>
    /// 要求されたパスを順に照合する。結果は<paramref name="requested"/>と同じ並び・同じ件数で返す
    /// （同じファイルを指す重複した要求は、同じ結果が重複して返る。数え直すのは呼び出し側）。
    /// </summary>
    /// <param name="nodes">コンテキスト収集の走査結果（除外分・ディレクトリを含む）。</param>
    /// <param name="requested">AIが要求したパス。</param>
    /// <param name="projectRoot">プロジェクトルートの絶対パス。省略可。</param>
    public static IReadOnlyList<RequestedFileMatch> Match(
        IReadOnlyList<ContextFileNode> nodes, IEnumerable<string> requested, string? projectRoot = null)
    {
        var byExact = new Dictionary<string, ContextFileNode>(StringComparer.Ordinal);
        var byIgnoreCase = new Dictionary<string, List<ContextFileNode>>(StringComparer.OrdinalIgnoreCase);
        var excludedDirectories = new List<ContextFileNode>();
        foreach (var node in nodes)
        {
            byExact.TryAdd(node.RelativePath, node);
            if (!byIgnoreCase.TryGetValue(node.RelativePath, out var list))
            {
                list = new List<ContextFileNode>();
                byIgnoreCase[node.RelativePath] = list;
            }
            list.Add(node);
            if (node.IsDirectory && node.IsExcluded) excludedDirectories.Add(node);
        }

        var results = new List<RequestedFileMatch>();
        foreach (var raw in requested)
        {
            var normalized = Normalize(raw, projectRoot);
            results.Add(MatchOne(raw, normalized, byExact, byIgnoreCase, excludedDirectories));
        }
        return results;
    }

    private static RequestedFileMatch MatchOne(
        string raw,
        string normalized,
        Dictionary<string, ContextFileNode> byExact,
        Dictionary<string, List<ContextFileNode>> byIgnoreCase,
        List<ContextFileNode> excludedDirectories)
    {
        if (normalized.Length == 0) return NotFound(raw);

        var node = FindNode(normalized, byExact, byIgnoreCase);
        if (node is not null) return Classify(raw, node);

        // 除外ディレクトリ（node_modules/ 等）は配下を走査していないため、その中のファイルは
        // ノードとして存在しない。「見つからない」と言うより「除外されている」と伝えるほうが
        // 利用者の次の一手（除外パターンの見直し）につながる。
        foreach (var dir in excludedDirectories)
        {
            if (normalized.StartsWith(dir.RelativePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                return new RequestedFileMatch(raw, RequestedFileMatchKind.Excluded, dir.RelativePath, dir.ExcludeReason);
            }
        }

        return NotFound(raw);
    }

    private static ContextFileNode? FindNode(
        string normalized, Dictionary<string, ContextFileNode> byExact, Dictionary<string, List<ContextFileNode>> byIgnoreCase)
    {
        if (byExact.TryGetValue(normalized, out var exact)) return exact;

        // 完全一致が無いときだけ大文字小文字を無視する。1件に定まらなければ採用しない。
        return byIgnoreCase.TryGetValue(normalized, out var candidates) && candidates.Count == 1 ? candidates[0] : null;
    }

    private static RequestedFileMatch Classify(string raw, ContextFileNode node)
    {
        if (node.IsDirectory)
        {
            return node.IsExcluded
                ? new RequestedFileMatch(raw, RequestedFileMatchKind.Excluded, node.RelativePath, node.ExcludeReason)
                : new RequestedFileMatch(raw, RequestedFileMatchKind.Directory, node.RelativePath, null);
        }

        return node.IsExcluded
            ? new RequestedFileMatch(raw, RequestedFileMatchKind.Excluded, node.RelativePath, node.ExcludeReason)
            : new RequestedFileMatch(raw, RequestedFileMatchKind.Matched, node.RelativePath, null);
    }

    private static RequestedFileMatch NotFound(string raw)
        => new(raw, RequestedFileMatchKind.NotFound, null, null);
}
