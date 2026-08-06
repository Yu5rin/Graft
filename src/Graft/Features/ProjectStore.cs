using System.IO;
using System.Security.Cryptography;
using System.Text;
using Graft.Core;
using Graft.Infra;

namespace Graft.Features;

/// <summary>
/// projects.json の読み書きと、登録・検証・並べ替えといったプロジェクト管理の
/// 基本操作を提供する。仕様書3.1・3.2に対応する。
/// </summary>
public sealed class ProjectStore
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _store;

    public ProjectStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _store = new JsonFileStore();
    }

    /// <summary>
    /// projects.json を読み込む。ファイルが存在しない場合や破損している場合は
    /// <see cref="JsonFileStore"/> の共通復旧手順（13.1章）に従い、既定値
    /// （空一覧）から再生成する。
    /// </summary>
    public async Task<GraftResult<IReadOnlyList<Project>>> LoadAsync(CancellationToken ct = default)
    {
        var result = await _store
            .ReadWithRecoveryAsync(_paths.ProjectsFilePath, static () => new ProjectCatalog(), JsonFileStore.DefaultOptions, ct)
            .ConfigureAwait(false);
        return GraftResult<IReadOnlyList<Project>>.Ok(result.Value.Projects, result.Issues);
    }

    /// <summary>
    /// projects.json を書き込む。<see cref="Project.IsDisconnected"/> は起動時の検証で
    /// 都度算出する実行時のみの値であるため、保存前に常に false へ戻してから書き出す。
    /// </summary>
    public async Task<GraftResult<bool>> SaveAsync(IReadOnlyList<Project> projects, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var persisted = projects.Select(p => p.IsDisconnected ? p with { IsDisconnected = false } : p).ToList();
        var catalog = new ProjectCatalog { Projects = persisted };
        await _store.WriteAsync(_paths.ProjectsFilePath, catalog, JsonFileStore.DefaultOptions, ct).ConfigureAwait(false);
        return GraftResult<bool>.Ok(true);
    }

    /// <summary>
    /// ルートパスから決定的にIDを生成する。正規化（絶対パス化 → 区切りを "/" →
    /// 末尾スラッシュ除去 → 小文字化）した文字列のSHA-256先頭6桁に "p_" を付ける。
    /// フォルダ名を変更してもルートパスが同じであれば同一IDになる。
    /// </summary>
    public static string CreateId(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var normalized = NormalizeRootForHash(root);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"p_{hex[..6]}";
    }

    /// <summary>
    /// フォルダから新規登録する。既に同じルートが登録済みの場合は、そのプロジェクトの
    /// 最終使用日時と接続状態のみ更新して返す（重複登録はしない）。
    /// </summary>
    public async Task<GraftResult<Project>> RegisterAsync(string root, string? name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return GraftResult<Project>.Fail(ErrorCode.E201, "ルートパスが空です。", path: root);
        }

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return GraftResult<Project>.Fail(ErrorCode.E201, $"不正なパスです: {ex.Message}", path: root);
        }

        var loaded = await LoadAsync(ct).ConfigureAwait(false);
        var projects = loaded.Value.ToList();
        var project = BuildOrUpdateProject(projects, fullRoot, name);
        await SaveAsync(projects, ct).ConfigureAwait(false);
        return GraftResult<Project>.Ok(project, loaded.Issues);
    }

    /// <summary>
    /// 起動時の検証。ルートフォルダが存在しないプロジェクトは削除せず
    /// <see cref="Project.IsDisconnected"/> を立てるだけにとどめる（仕様書3.2）。
    /// </summary>
    public Task<GraftResult<IReadOnlyList<Project>>> ValidateAsync(
        IReadOnlyList<Project> projects, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var validated = new List<Project>(projects.Count);
        var issues = new List<GraftIssue>();

        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            var exists = Directory.Exists(project.Root);
            validated.Add(project with { IsDisconnected = !exists });
            if (!exists)
            {
                issues.Add(GraftIssue.Of(
                    ErrorCode.E404,
                    detail: $"プロジェクト「{project.Name}」のルート（{project.Root}）が見つからないため未接続にしました。",
                    path: project.Root,
                    severity: Severity.Warning));
            }
        }

        return Task.FromResult(GraftResult<IReadOnlyList<Project>>.Ok(validated, issues));
    }

    /// <summary>
    /// ピン留めを先頭に、次に最終使用日時の降順で並べる（仕様書3.2）。
    /// 上位9件が数字キーショートカットの割り当て対象になる。
    /// </summary>
    public static IReadOnlyList<Project> Sort(IEnumerable<Project> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        return projects
            .OrderByDescending(p => p.Pinned)
            .ThenByDescending(p => p.LastUsedAt)
            .ToList();
    }

    /// <summary>
    /// nextRevision を実体（back/ 配下）の最大リビジョン+1へ補正する（仕様書13.1）。
    /// 既に十分大きい場合は何もしない。実体の最大値は <c>RevisionStore</c> 側から渡す。
    /// </summary>
    public static Project ReconcileRevision(Project project, int actualMaxRevision)
    {
        ArgumentNullException.ThrowIfNull(project);
        var minimumNext = actualMaxRevision + 1;
        return project.NextRevision < minimumNext ? project with { NextRevision = minimumNext } : project;
    }

    private static Project BuildOrUpdateProject(List<Project> projects, string fullRoot, string? name)
    {
        var id = CreateId(fullRoot);
        var isDisconnected = !Directory.Exists(fullRoot);
        var now = DateTimeOffset.Now;
        var index = projects.FindIndex(p => p.Id == id);

        if (index >= 0)
        {
            var updated = projects[index] with { Root = fullRoot, LastUsedAt = now, IsDisconnected = isDisconnected };
            projects[index] = updated;
            return updated;
        }

        var created = new Project
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? DeriveDefaultName(fullRoot) : name,
            Root = fullRoot,
            LastUsedAt = now,
            NextRevision = 1,
            IsDisconnected = isDisconnected,
        };
        projects.Add(created);
        return created;
    }

    private static string DeriveDefaultName(string fullRoot)
    {
        var trimmed = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static string NormalizeRootForHash(string root)
    {
        var full = Path.GetFullPath(root);
        var withSlashes = full.Replace('\\', '/');
        var trimmed = withSlashes.Length > 1 ? withSlashes.TrimEnd('/') : withSlashes;

        // 仕様書v2.1 3章: パスの比較規則はプラットフォームへ委ねる。Windowsは大文字小文字を
        // 無視するため小文字へ正規化し、区別するLinuxではそのまま使う。ここで一律に
        // 小文字化すると、Linuxで大文字小文字だけが異なる別フォルダが同一プロジェクトIDになる。
        return OperatingSystem.IsWindows() ? trimmed.ToLowerInvariant() : trimmed;
    }
}
