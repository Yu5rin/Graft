using System.IO;
using Graft.Infra;

namespace Graft.Features;

/// <summary>
/// クイックオープン（Ctrl+P）のファイル列挙を担う。<see cref="FileTreeService"/>・
/// <see cref="CrossFileSearchEngine"/>と同じ除外規則（既定除外パターン・.gitignore・
/// プロジェクト設定の除外パターン）を<see cref="GitignoreFilter"/>で合成して適用することで、
/// 判定ロジックの二重実装を避ける。これに加えて、クイックオープンはパッチ適用対象になり得る
/// テキストファイルの一覧という性質上、<see cref="Graft.Infra.SafetySettings.AllowedExtensions"/>
/// （許可拡張子、13章）でも絞り込む。列挙自体はディレクトリ・ファイル列挙のみの同期処理だが、
/// 呼び出し側（QuickOpenViewModel）がスレッドプールで実行しUIをブロックしない。
/// </summary>
public sealed class QuickOpenFileEnumerator
{
    /// <summary>プロジェクト配下のファイルを列挙する。相対パス（区切りは "/"）の一覧を返す。</summary>
    public async Task<IReadOnlyList<string>> EnumerateAsync(
        Project project, Settings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);

        if (!Directory.Exists(project.Root)) return Array.Empty<string>();

        var filter = await BuildFilterAsync(project, settings, ct).ConfigureAwait(false);
        var allowedExtensions = new HashSet<string>(settings.Safety.AllowedExtensions, StringComparer.OrdinalIgnoreCase);

        return await Task.Run(() =>
        {
            var result = new List<string>();
            Walk(project.Root, project.Root, filter, allowedExtensions, result, ct);
            return (IReadOnlyList<string>)result;
        }, ct).ConfigureAwait(false);
    }

    private static void Walk(
        string root, string dir, GitignoreFilter filter, HashSet<string> allowedExtensions,
        List<string> result, CancellationToken ct)
    {
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
            ct.ThrowIfCancellationRequested();
            var rel = ToRelative(root, subDir);
            if (filter.IsIgnored(rel, isDirectory: true)) continue;
            Walk(root, subDir, filter, allowedExtensions, result, ct);
        }

        foreach (var file in fileEntries)
        {
            ct.ThrowIfCancellationRequested();
            var rel = ToRelative(root, file);
            if (filter.IsIgnored(rel, isDirectory: false)) continue;
            if (allowedExtensions.Count > 0 && !allowedExtensions.Contains(Path.GetExtension(file))) continue;
            result.Add(rel);
        }
    }

    private static async Task<GitignoreFilter> BuildFilterAsync(Project project, Settings settings, CancellationToken ct)
    {
        var defaultFilter = GitignoreFilter.FromPatterns(ContextCollector.DefaultExcludePatterns, "既定除外");
        var gitignoreFilter = settings.Context.RespectGitignore
            ? await GitignoreFilter.LoadAsync(project.Root, ct).ConfigureAwait(false)
            : GitignoreFilter.Empty;
        var overrideFilter = GitignoreFilter.FromPatterns(project.Overrides.Excludes, "プロジェクト設定");
        return defaultFilter.Merge(gitignoreFilter).Merge(overrideFilter);
    }

    private static string ToRelative(string root, string fullPath) => Path.GetRelativePath(root, fullPath).Replace('\\', '/');
}
