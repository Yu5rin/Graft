using System.IO;
using System.Text;
using Graft.Core;
using Graft.Infra;

namespace Graft.Features;

/// <summary>収集モード。仕様書10.1。</summary>
public enum ContextMode
{
    /// <summary>フォルダ構成のみ。</summary>
    TreeOnly,
    /// <summary>チェックしたファイルの全文のみ。</summary>
    SelectedFiles,
    /// <summary>ツリーと選択ファイルの併用（既定）。</summary>
    TreeAndSelected,
    /// <summary>指定リビジョン以降に変更されたファイルのみ。</summary>
    ChangedSince,
}

/// <summary>コンテキスト収集の要求。仕様書10章。</summary>
public sealed record ContextRequest
{
    /// <summary>対象プロジェクト。</summary>
    public required Project Project { get; init; }
    /// <summary>収集モード。既定はツリー＋選択。</summary>
    public ContextMode Mode { get; init; } = ContextMode.TreeAndSelected;
    /// <summary>選択ファイルのプロジェクト相対パス（SelectedFiles / TreeAndSelected で使用）。</summary>
    public IReadOnlyList<string> SelectedPaths { get; init; } = Array.Empty<string>();
    /// <summary>ChangedSince のとき、この番号より新しいリビジョンで変更されたファイルを集める。</summary>
    public int? SinceRevision { get; init; }
    /// <summary>適用する設定。</summary>
    public required Settings Settings { get; init; }
}

/// <summary>選択用ツリーの1エントリ（ファイルまたはディレクトリ）。</summary>
public sealed record ContextFileNode
{
    /// <summary>プロジェクトルートからの相対パス（区切りは "/"）。</summary>
    public required string RelativePath { get; init; }
    /// <summary>ディレクトリかどうか。</summary>
    public bool IsDirectory { get; init; }
    /// <summary>ファイルサイズ（バイト）。ディレクトリは0。</summary>
    public long SizeBytes { get; init; }
    /// <summary>除外規則により除外されているか。</summary>
    public bool IsExcluded { get; init; }
    /// <summary>除外理由（UI表示用）。除外されていない場合はnull。</summary>
    public string? ExcludeReason { get; init; }
}

/// <summary>コンテキスト収集の結果。仕様書10.3・10.4。</summary>
public sealed record ContextResult
{
    /// <summary>出力テキスト全体。</summary>
    public required string Text { get; init; }
    /// <summary>推定トークン数。</summary>
    public int EstimatedTokens { get; init; }
    /// <summary>走査済みの全ファイル・ディレクトリ一覧（選択UI用。除外分も含む）。</summary>
    public IReadOnlyList<ContextFileNode> Files { get; init; } = Array.Empty<ContextFileNode>();
    /// <summary>tokenWarnThreshold を超えたかどうか。</summary>
    public bool ExceedsWarnThreshold { get; init; }
}

/// <summary>
/// 仕様書10章のコンテキスト収集を担う。ScanAsyncで除外規則を反映したツリーを、CollectAsyncで
/// 10.3の出力形式のテキストを生成する。BuildTreeTextAsync・BuildFilesTextAsyncは
/// <see cref="PromptTemplateRenderer"/> の {{tree}}・{{files}} 展開と処理を共有し、
/// 4.8.4「コンテキスト収集とは同一の出力パイプラインを共有する」を満たす。
/// </summary>
public sealed class ContextCollector
{
    /// <summary>
    /// 既定の除外パターン（仕様書10.2）。エクスプローラ（4.2）・横断検索（4.4）と
    /// 同一の規則を使うため、唯一の定義としてここに置き公開する。
    /// </summary>
    public static IReadOnlyList<string> DefaultExcludePatterns { get; } = new[]
    {
        "node_modules/", "bin/", "obj/", ".venv/", "dist/", ".git/", "*.min.js",
    };

    /// <summary>
    /// バイナリとみなす拡張子（仕様書10.2）。エクスプローラ・横断検索と共有する。
    /// </summary>
    public static IReadOnlySet<string> BinaryFileExtensions => BinaryExtensions;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".so", ".dylib", ".bin", ".dat", ".db", ".sqlite", ".sqlite3",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff",
        ".pdf", ".zip", ".7z", ".rar", ".tar", ".gz", ".xz",
        ".mp3", ".mp4", ".avi", ".mov", ".wav", ".flac", ".ogg",
        ".ttf", ".otf", ".woff", ".woff2", ".class", ".pyc", ".jar", ".war",
        ".o", ".a", ".lib", ".msi", ".iso", ".node",
    };

    private const long MaxFileSizeBytes = 1024 * 1024;

    private readonly AppPaths _paths;
    private readonly JsonFileStore _jsonStore = new();

    public ContextCollector(AppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>対象フォルダを走査して選択用のツリーを返す（除外規則を反映済み）。</summary>
    public async Task<GraftResult<IReadOnlyList<ContextFileNode>>> ScanAsync(Project project, Settings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(project.Root) || !Directory.Exists(project.Root))
        {
            return GraftResult<IReadOnlyList<ContextFileNode>>.Fail(ErrorCode.E201, "プロジェクトルートが存在しません", path: project.Root);
        }
        var filter = await BuildFilterAsync(project, settings, ct).ConfigureAwait(false);
        var nodes = new List<ContextFileNode>();
        WalkDirectory(project.Root, project.Root, filter, nodes, ct);
        return GraftResult<IReadOnlyList<ContextFileNode>>.Ok(nodes);
    }

    /// <summary>10.3の出力形式でテキストを生成する。</summary>
    public async Task<GraftResult<ContextResult>> CollectAsync(ContextRequest request, CancellationToken ct = default)
    {
        var scan = await ScanAsync(request.Project, request.Settings, ct).ConfigureAwait(false);
        if (!scan.IsSuccess) return GraftResult<ContextResult>.Fail(scan.Issues);
        var files = scan.Value;

        var sb = new StringBuilder();
        AppendStandingContext(sb, request.Project.StandingContext);
        if (IncludesTree(request.Mode)) AppendTree(sb, files);

        var issues = new List<GraftIssue>();
        if (IncludesFiles(request.Mode))
        {
            var filesText = await CollectFilesTextAsync(request, files, ct).ConfigureAwait(false);
            if (!filesText.IsSuccess) return GraftResult<ContextResult>.Fail(filesText.Issues);
            sb.Append(filesText.Value);
            issues.AddRange(filesText.Issues);
        }

        var text = sb.ToString();
        var tokens = TokenEstimator.Estimate(text, request.Settings.Context.TokenRatio);
        var exceeds = tokens > request.Settings.Context.TokenWarnThreshold;
        var result = new ContextResult { Text = text, EstimatedTokens = tokens, Files = files, ExceedsWarnThreshold = exceeds };
        return GraftResult<ContextResult>.Ok(result, issues);
    }

    /// <summary>{{tree}} 展開用に、見出しなしのツリー本文のみを返す。</summary>
    public async Task<GraftResult<string>> BuildTreeTextAsync(Project project, Settings settings, CancellationToken ct = default)
    {
        var scan = await ScanAsync(project, settings, ct).ConfigureAwait(false);
        if (!scan.IsSuccess) return GraftResult<string>.Fail(scan.Issues);
        return GraftResult<string>.Ok(BuildTreeText(scan.Value));
    }

    /// <summary>{{files}} 展開用に、request のモードに従って選択されたファイルの全文のみを返す。</summary>
    public async Task<GraftResult<string>> BuildFilesTextAsync(ContextRequest request, CancellationToken ct = default)
    {
        var scan = await ScanAsync(request.Project, request.Settings, ct).ConfigureAwait(false);
        if (!scan.IsSuccess) return GraftResult<string>.Fail(scan.Issues);
        return await CollectFilesTextAsync(request, scan.Value, ct).ConfigureAwait(false);
    }

    /// <summary>選択対象ファイルを解決し、本文セクションを連結して返す（CollectAsyncと共有）。</summary>
    private async Task<GraftResult<string>> CollectFilesTextAsync(ContextRequest request, IReadOnlyList<ContextFileNode> files, CancellationToken ct)
    {
        var targets = await ResolveTargetsAsync(request, files, ct).ConfigureAwait(false);
        if (!targets.IsSuccess) return GraftResult<string>.Fail(targets.Issues);

        var sb = new StringBuilder();
        var issues = new List<GraftIssue>(targets.Issues);
        foreach (var node in targets.Value)
        {
            var issue = await AppendFileSectionAsync(sb, request.Project.Root, node, ct).ConfigureAwait(false);
            if (issue is not null) issues.Add(issue);
        }
        return GraftResult<string>.Ok(sb.ToString(), issues);
    }

    private static bool IncludesTree(ContextMode mode) => mode is ContextMode.TreeOnly or ContextMode.TreeAndSelected;

    private static bool IncludesFiles(ContextMode mode)
        => mode is ContextMode.SelectedFiles or ContextMode.TreeAndSelected or ContextMode.ChangedSince;

    private static void AppendStandingContext(StringBuilder sb, string? standingContext)
    {
        if (string.IsNullOrWhiteSpace(standingContext)) return;
        sb.AppendLine("# 前提");
        sb.AppendLine(standingContext.Trim());
        sb.AppendLine();
    }

    private static void AppendTree(StringBuilder sb, IReadOnlyList<ContextFileNode> files)
    {
        sb.AppendLine("# プロジェクト構成");
        sb.Append(BuildTreeText(files));
        sb.AppendLine();
    }

    private static string BuildTreeText(IReadOnlyList<ContextFileNode> files)
    {
        var sb = new StringBuilder();
        foreach (var node in files)
        {
            if (node.IsExcluded) continue;
            var depth = node.RelativePath.Count(c => c == '/');
            var nameStart = node.RelativePath.LastIndexOf('/') + 1;
            var name = node.RelativePath[nameStart..];
            sb.Append(' ', depth * 2).Append(name);
            if (node.IsDirectory) sb.Append('/');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task<GraftIssue?> AppendFileSectionAsync(StringBuilder sb, string root, ContextFileNode node, CancellationToken ct)
    {
        var fullPath = Path.Combine(root, node.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var read = await FileTextIO.ReadAsync(fullPath, ct).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return GraftIssue.Of(ErrorCode.E204, "コンテキスト収集時にファイルを読み込めませんでした", path: node.RelativePath, severity: Severity.Warning);
        }
        var (text, _) = read.Value;
        var hash = FileTextIO.ShortHash(FileTextIO.ComputeHash(text));
        sb.AppendLine($"# {node.RelativePath}  ({hash})");
        sb.AppendLine(text);
        sb.AppendLine();
        return null;
    }

    private async Task<GraftResult<IReadOnlyList<ContextFileNode>>> ResolveTargetsAsync(
        ContextRequest request, IReadOnlyList<ContextFileNode> files, CancellationToken ct)
    {
        if (request.Mode == ContextMode.TreeOnly)
        {
            return GraftResult<IReadOnlyList<ContextFileNode>>.Ok(Array.Empty<ContextFileNode>());
        }

        if (request.Mode == ContextMode.ChangedSince)
        {
            if (request.SinceRevision is null) return GraftResult<IReadOnlyList<ContextFileNode>>.Ok(Array.Empty<ContextFileNode>());
            var changed = await LoadChangedPathsAsync(request.Project, request.SinceRevision.Value, ct).ConfigureAwait(false);
            if (!changed.IsSuccess) return GraftResult<IReadOnlyList<ContextFileNode>>.Fail(changed.Issues);
            return GraftResult<IReadOnlyList<ContextFileNode>>.Ok(ResolvePaths(files, changed.Value));
        }

        return GraftResult<IReadOnlyList<ContextFileNode>>.Ok(ResolvePaths(files, request.SelectedPaths));
    }

    private static IReadOnlyList<ContextFileNode> ResolvePaths(IReadOnlyList<ContextFileNode> files, IReadOnlyList<string> paths)
    {
        var byPath = files.Where(f => !f.IsDirectory && !f.IsExcluded)
            .ToDictionary(f => Normalize(f.RelativePath), StringComparer.OrdinalIgnoreCase);
        var result = new List<ContextFileNode>();
        foreach (var raw in paths)
        {
            if (byPath.TryGetValue(Normalize(raw), out var node)) result.Add(node);
        }
        return result;
    }

    /// <summary>
    /// 指定リビジョン番号より新しいリビジョンのmanifest.jsonをAppPathsから直接読み、変更された
    /// ファイルのパス一覧を集める。RevisionStoreへの依存を避けるための実装（10.1差分モード）。
    /// </summary>
    private async Task<GraftResult<IReadOnlyList<string>>> LoadChangedPathsAsync(Project project, int sinceRevision, CancellationToken ct)
    {
        var backupDir = _paths.GetProjectBackupDirectory(project.Id);
        if (!Directory.Exists(backupDir)) return GraftResult<IReadOnlyList<string>>.Ok(Array.Empty<string>());

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(backupDir))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            if (!TryParseRevisionNumber(name, out var revision) || revision <= sinceRevision) continue;

            var manifestPath = Path.Combine(dir, "manifest.json");
            var result = await _jsonStore.ValidateJsonAsync<RevisionManifest>(manifestPath, ct: ct).ConfigureAwait(false);
            if (!result.IsSuccess) continue;
            foreach (var entry in result.Value.Entries) changed.Add(entry.Path);
        }
        return GraftResult<IReadOnlyList<string>>.Ok(changed.ToArray());
    }

    private static bool TryParseRevisionNumber(string folderName, out int revision)
    {
        revision = 0;
        if (!folderName.StartsWith('r')) return false;
        var underscoreIndex = folderName.IndexOf('_');
        var numPart = underscoreIndex > 0 ? folderName[1..underscoreIndex] : folderName[1..];
        return int.TryParse(numPart, out revision);
    }

    private static async Task<GitignoreFilter> BuildFilterAsync(Project project, Settings settings, CancellationToken ct)
    {
        var defaultFilter = GitignoreFilter.FromPatterns(DefaultExcludePatterns, "既定除外");
        var gitignoreFilter = settings.Context.RespectGitignore
            ? await GitignoreFilter.LoadAsync(project.Root, ct).ConfigureAwait(false)
            : GitignoreFilter.Empty;
        var overrideFilter = GitignoreFilter.FromPatterns(project.Overrides.Excludes, "プロジェクト設定");
        return defaultFilter.Merge(gitignoreFilter).Merge(overrideFilter);
    }

    private static void WalkDirectory(string root, string currentDir, GitignoreFilter filter, List<ContextFileNode> nodes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        List<string> dirEntries;
        List<string> fileEntries;
        try
        {
            dirEntries = Directory.EnumerateDirectories(currentDir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
            fileEntries = Directory.EnumerateFiles(currentDir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var dir in dirEntries)
        {
            var rel = ToRelative(root, dir);
            var (ignored, label) = filter.Evaluate(rel, true);
            nodes.Add(new ContextFileNode
            {
                RelativePath = rel,
                IsDirectory = true,
                SizeBytes = 0,
                IsExcluded = ignored,
                ExcludeReason = ignored ? ReasonOf(label) : null,
            });
            if (!ignored) WalkDirectory(root, dir, filter, nodes, ct);
        }

        foreach (var file in fileEntries) nodes.Add(BuildFileNode(root, file, filter));
    }

    private static ContextFileNode BuildFileNode(string root, string fullPath, GitignoreFilter filter)
    {
        var rel = ToRelative(root, fullPath);
        var (ignored, label) = filter.Evaluate(rel, false);
        long size = 0;
        try
        {
            size = new FileInfo(fullPath).Length;
        }
        catch (IOException)
        {
            // サイズ取得に失敗しても走査自体は続行する
        }

        if (ignored) return new ContextFileNode { RelativePath = rel, SizeBytes = size, IsExcluded = true, ExcludeReason = ReasonOf(label) };
        if (BinaryExtensions.Contains(Path.GetExtension(fullPath)))
        {
            return new ContextFileNode { RelativePath = rel, SizeBytes = size, IsExcluded = true, ExcludeReason = "バイナリファイルのため除外" };
        }
        if (size > MaxFileSizeBytes)
        {
            return new ContextFileNode { RelativePath = rel, SizeBytes = size, IsExcluded = true, ExcludeReason = "サイズが1MBを超過" };
        }
        return new ContextFileNode { RelativePath = rel, SizeBytes = size, IsExcluded = false, ExcludeReason = null };
    }

    private static string? ReasonOf(string? label) => label switch
    {
        "既定除外" => "既定の除外パターンに一致",
        ".gitignore" => ".gitignoreに一致",
        "プロジェクト設定" => "プロジェクト設定の除外パターンに一致",
        _ => "除外パターンに一致",
    };

    private static string ToRelative(string root, string fullPath) => Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
}
