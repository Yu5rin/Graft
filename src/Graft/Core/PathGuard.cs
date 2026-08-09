using System.IO;

namespace Graft.Core;

/// <summary>
/// PathGuardの動作設定。既定値は仕様書14章 <c>safety</c> セクションに対応する。
/// </summary>
public sealed record PathGuardOptions
{
    private static readonly IReadOnlyList<string> DefaultAllowedExtensions = new[]
    {
        ".py", ".js", ".ts", ".tsx", ".cs", ".java", ".go",
        ".rs", ".html", ".css", ".json", ".yaml", ".yml",
        ".md", ".sql", ".xml", ".txt",
    };

    /// <summary>許可する拡張子（先頭ドット付き）。既定は.exe/.dll/.bat/.ps1等を含まないテキスト系のみ。</summary>
    public IReadOnlyList<string> AllowedExtensions { get; init; } = DefaultAllowedExtensions;

    /// <summary>1ファイルあたりの最大サイズ（MB）。</summary>
    public int MaxFileSizeMB { get; init; } = 10;

    /// <summary>1リビジョンあたりの最大ファイル数。</summary>
    public int MaxFilesPerRevision { get; init; } = 200;

    /// <summary>仕様書14章どおりの既定設定。</summary>
    public static PathGuardOptions Default { get; } = new();
}

/// <summary>既存ファイルに対する追加検証結果。</summary>
public sealed record FileCheck
{
    /// <summary>解決済みの絶対パス。</summary>
    public required string FullPath { get; init; }

    /// <summary>ファイルが既に存在するか。</summary>
    public bool Exists { get; init; }

    /// <summary>読み取り専用属性が付いているか。</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>排他ロック中か。</summary>
    public bool IsLocked { get; init; }

    /// <summary>ファイルサイズ（バイト）。存在しない場合は0。</summary>
    public long SizeBytes { get; init; }
}

/// <summary>
/// プロジェクトルート外への書き込みを防ぐ経路検証機構（4.7節/13章）。
/// 正規化後の絶対パスとシンボリックリンク解決後の実パスの両方でルート内判定を行う。
/// </summary>
public sealed class PathGuard
{
    private readonly string _root;
    private readonly PathGuardOptions _options;

    public PathGuard(string projectRoot, PathGuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        ArgumentNullException.ThrowIfNull(options);

        var normalizedRoot = NormalizeTrailingSeparator(Path.GetFullPath(projectRoot));
        _root = NormalizeTrailingSeparator(ResolveRealPath(normalizedRoot));
        _options = options;
    }

    /// <summary>相対パスを検証し、ルート内の絶対パスへ解決する。E201/E202/E206を返しうる。</summary>
    public GraftResult<string> Resolve(string relativePath) => Resolve(relativePath, checkExtension: true);

    /// <summary>
    /// フォルダの相対パスを検証し、ルート内の絶対パスへ解決する。
    /// 拡張子ホワイトリスト（13章）はファイルに対する規則のため、フォルダには適用しない。
    /// </summary>
    public GraftResult<string> ResolveDirectory(string relativePath) => Resolve(relativePath, checkExtension: false);

    private GraftResult<string> Resolve(string relativePath, bool checkExtension)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "パスが空です", path: relativePath);
        }

        if (IsAbsolutePath(relativePath))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "絶対パスは許可されていません", path: relativePath);
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "上位ディレクトリの参照(..)は許可されていません", path: relativePath);
        }

        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(_root, string.Join(Path.DirectorySeparatorChar, segments)));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return GraftResult<string>.Fail(ErrorCode.E201, $"不正なパスです: {ex.Message}", path: relativePath);
        }

        if (!IsWithinRoot(combined))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "パスがプロジェクトルート外です", path: relativePath);
        }

        var real = ResolveRealPath(combined);
        if (!IsWithinRoot(real))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "シンボリックリンク経由でルート外を参照しています", path: relativePath);
        }

        if (checkExtension)
        {
            var extension = Path.GetExtension(combined);
            // 不具合2対応: 拡張子ホワイトリストは「.exe/.bat等の危険な拡張子を遮断する」ことが
            // 目的であり、"Dockerfile"やLICENSEのような拡張子そのものが無いファイル名は
            // 遮断対象の想定外だった（エクスプローラで拡張子なしのファイルを新規作成できない
            // 不具合の原因）。拡張子が付いている場合のみホワイトリストで判定する。
            if (extension.Length > 0 &&
                !_options.AllowedExtensions.Any(a => string.Equals(a, extension, StringComparison.OrdinalIgnoreCase)))
            {
                return GraftResult<string>.Fail(ErrorCode.E202, $"拡張子 '{extension}' は許可されていません", path: relativePath);
            }
        }

        if (LongPath.ExceedsExtendedLimit(combined))
        {
            return GraftResult<string>.Fail(ErrorCode.E206, "長パス対応のプレフィックス込みでも上限を超えています", path: relativePath);
        }

        return GraftResult<string>.Ok(combined);
    }

    /// <summary>既存ファイルに対する追加検証（サイズE203、ロックE204、読み取り専用E205）。</summary>
    public GraftResult<FileCheck> Inspect(string relativePath)
    {
        var resolved = Resolve(relativePath);
        if (!resolved.IsSuccess)
        {
            return GraftResult<FileCheck>.Fail(resolved.Issues);
        }

        var fullPath = resolved.Value;
        var ioPath = LongPath.Extended(fullPath);
        if (!File.Exists(ioPath))
        {
            var absent = new FileCheck { FullPath = fullPath, Exists = false, IsReadOnly = false, IsLocked = false, SizeBytes = 0 };
            return GraftResult<FileCheck>.Ok(absent);
        }

        var info = new FileInfo(ioPath);
        var isReadOnly = info.IsReadOnly;
        var sizeBytes = info.Length;
        var isLocked = IsLocked(ioPath);

        var check = new FileCheck { FullPath = fullPath, Exists = true, IsReadOnly = isReadOnly, IsLocked = isLocked, SizeBytes = sizeBytes };

        var issues = new List<GraftIssue>();
        var maxBytes = (long)_options.MaxFileSizeMB * 1024 * 1024;
        if (sizeBytes > maxBytes)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E203, $"サイズが上限（{_options.MaxFileSizeMB}MB）を超えています", path: relativePath));
        }
        if (isLocked)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E204, "ファイルが排他ロック中です", path: relativePath));
        }
        if (isReadOnly)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E205, "読み取り専用属性です", path: relativePath, severity: Severity.Warning));
        }

        if (issues.Any(i => i.Severity == Severity.Error))
        {
            return GraftResult<FileCheck>.Fail(issues);
        }

        return GraftResult<FileCheck>.Ok(check, issues);
    }

    /// <summary>1リビジョンあたりのファイル数上限判定（E203相当）。</summary>
    public GraftResult<bool> CheckFileCount(int count)
    {
        if (count > _options.MaxFilesPerRevision)
        {
            return GraftResult<bool>.Fail(ErrorCode.E203,
                $"1リビジョンあたりのファイル数上限（{_options.MaxFilesPerRevision}）を超えています");
        }
        return GraftResult<bool>.Ok(true);
    }

    private bool IsWithinRoot(string candidate)
    {
        var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedCandidate, _root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return normalizedCandidate.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocked(string ioPath)
    {
        try
        {
            using var stream = new FileStream(ioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 読み取り専用等のアクセス拒否はE205側で検出するためロック扱いにしない
            return false;
        }
    }

    private static bool IsAbsolutePath(string path)
    {
        if (path.StartsWith('/') || path.StartsWith('\\')) return true;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') return true;
        return false;
    }

    private static string NormalizeTrailingSeparator(string fullPath)
    {
        var trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? fullPath : trimmed;
    }

    /// <summary>
    /// 既存のパス構成要素に含まれるシンボリックリンク／ジャンクションを最終的な実体まで
    /// 解決する。存在しない末尾部分（新規作成予定のファイル等）はそのまま維持する。
    /// </summary>
    private static string ResolveRealPath(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var rest = fullPath[root.Length..];
        var parts = rest.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        foreach (var part in parts)
        {
            current = string.IsNullOrEmpty(current) ? part : Path.Combine(current, part);
            current = ResolveIfLink(current) ?? current;
        }

        return current;
    }

    private static string? ResolveIfLink(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                if (info.LinkTarget is not null)
                {
                    return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
                }
            }
            else if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.LinkTarget is not null)
                {
                    return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // リンク解決に失敗した場合は元のパスを維持する（後続のルート内判定に委ねる）
        }

        return null;
    }
}
