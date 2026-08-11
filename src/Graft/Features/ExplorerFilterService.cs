using System.IO;
using Graft.Infra;

namespace Graft.Features;

/// <summary>結果</summary>
/// <param name="MatchedRelativePaths">一致したファイルのプロジェクトルートからの相対パス（区切りは "/"）。</param>
/// <param name="Truncated">上限（<see cref="ExplorerFilterService.MaxMatches"/>）に達し、途中で打ち切ったかどうか。</param>
public sealed record ExplorerFilterResult(IReadOnlyList<string> MatchedRelativePaths, bool Truncated);

/// <summary>
/// 細かいユーザビリティ改善4: エクスプローラのファイル名絞り込み。<see cref="QuickOpenFileEnumerator"/>
/// と同じ「ディレクトリ・ファイル列挙のみの同期処理を<c>Task.Run</c>で流す」方針だが、目的が異なる
/// ため別クラスにしている。
/// <list type="bullet">
/// <item>クイックオープンは「開く」ための一覧で許可拡張子（<see cref="Infra.SafetySettings.AllowedExtensions"/>）
/// による絞り込みも行うが、こちらは「ツリーで眺める」ための絞り込みであり全ファイル種別が対象。</item>
/// <item>クイックオープンはあいまい一致で全件をスコア付けするが、こちらはツリー上に実際に表示する
/// 項目を決めるための単純な部分一致（ファイル名に含まれるか、大文字小文字を無視）。</item>
/// </list>
/// 除外規則（<see cref="GitignoreFilter"/>、既定除外＋.gitignore＋プロジェクト設定）は
/// <see cref="ExplorerViewModel"/>がツリー表示に使っているものをそのまま受け取り、
/// 「除外ファイルを表示」がオフの間は除外ファイル配下を検索対象にも含めない
/// （ツリーに出せない項目を絞り込みだけヒットさせても混乱するだけのため）。
///
/// 性能: 大きなプロジェクトでの入力のたびのフリーズを避けるため、(1)呼び出し側
/// （ExplorerViewModel）が入力変更を300msデバウンスしてから呼ぶ、(2)本クラス自体は
/// <see cref="MaxMatches"/>件に達し次第それ以上の列挙を打ち切る、(3)同期のディレクトリ走査は
/// 呼び出し側で<c>Task.Run</c>によりスレッドプールへ逃がしUIスレッドを塞がない、の3段構え。
/// </summary>
public sealed class ExplorerFilterService
{
    /// <summary>
    /// 一致件数の上限。大半のプロジェクトでは絞り込み文字列を数文字入れれば十分絞られるため、
    /// 実用上ここに達するのは「絞り込み文字列が短すぎる／存在しない」場合がほとんどで、
    /// その場合はどのみち一覧しきれないため打ち切って構わない。QuickOpenの上限とは別に定める
    /// （クイックオープンは全件スコア付けが前提で上限の意味合いが異なるため）。
    /// </summary>
    public const int MaxMatches = 500;

    /// <summary>
    /// プロジェクト配下からファイル名に<paramref name="query"/>を含むファイルを探す。
    /// 呼び出し元スレッドをブロックしないよう、内部で<c>Task.Run</c>によりスレッドプールへ逃がす。
    /// </summary>
    public async Task<ExplorerFilterResult> FindMatchesAsync(
        Project project, GitignoreFilter filter, string query, bool includeExcluded, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(filter);

        if (string.IsNullOrWhiteSpace(query) || !Directory.Exists(project.Root))
        {
            return new ExplorerFilterResult(Array.Empty<string>(), Truncated: false);
        }

        return await Task.Run(() =>
        {
            var matches = new List<string>();
            var truncated = false;
            Walk(project.Root, project.Root, filter, includeExcluded, query, matches, ref truncated, ct);
            return new ExplorerFilterResult(matches, truncated);
        }, ct).ConfigureAwait(false);
    }

    private static void Walk(
        string root, string dir, GitignoreFilter filter, bool includeExcluded, string query,
        List<string> matches, ref bool truncated, CancellationToken ct)
    {
        if (matches.Count >= MaxMatches)
        {
            truncated = true;
            return;
        }
        ct.ThrowIfCancellationRequested();

        List<string> dirEntries;
        List<string> fileEntries;
        try
        {
            dirEntries = Directory.EnumerateDirectories(dir).ToList();
            fileEntries = Directory.EnumerateFiles(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var subDir in dirEntries)
        {
            if (matches.Count >= MaxMatches) { truncated = true; return; }
            ct.ThrowIfCancellationRequested();
            var rel = ToRelative(root, subDir);
            if (filter.IsIgnored(rel, isDirectory: true) && !includeExcluded) continue;
            Walk(root, subDir, filter, includeExcluded, query, matches, ref truncated, ct);
        }

        foreach (var file in fileEntries)
        {
            if (matches.Count >= MaxMatches) { truncated = true; return; }
            ct.ThrowIfCancellationRequested();
            var rel = ToRelative(root, file);
            if (filter.IsIgnored(rel, isDirectory: false) && !includeExcluded) continue;
            var name = Path.GetFileName(file);
            if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) matches.Add(rel);
        }
    }

    private static string ToRelative(string root, string fullPath) => Path.GetRelativePath(root, fullPath).Replace('\\', '/');
}
