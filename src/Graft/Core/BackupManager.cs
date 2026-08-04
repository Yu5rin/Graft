using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Graft.Infra;

namespace Graft.Core;

/// <summary>
/// リビジョン適用開始時のバックアップフォルダ作成を担う。仕様書2.2・6.3。
/// <c>back/&lt;プロジェクトID&gt;/r&lt;番号&gt;_&lt;yyyyMMdd_HHmmss&gt;/</c> フォルダを作成し、
/// <c>status: "in_progress"</c> の manifest.json を書いてから <see cref="BackupSession"/> を返す。
/// 個々のファイルの退避は <see cref="BackupSession"/> 側の責務とする。
/// </summary>
public sealed class BackupManager
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _jsonStore = new();

    public BackupManager(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>
    /// バックアップフォルダを作成し、<c>status: "in_progress"</c> の manifest.json を
    /// 書き込んでから <see cref="BackupSession"/> を返す。失敗時は E401。
    /// </summary>
    public async Task<GraftResult<BackupSession>> BeginAsync(
        string projectId, string projectRoot, RevisionManifest initial, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return GraftResult<BackupSession>.Fail(ErrorCode.E401, "プロジェクトIDが空です");
        }
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return GraftResult<BackupSession>.Fail(ErrorCode.E401, "プロジェクトルートが空です");
        }

        var appliedAt = initial.AppliedAt == default ? DateTimeOffset.Now : initial.AppliedAt;
        var folderName = AppPaths.BuildRevisionFolderName(initial.Revision, appliedAt);
        var folderPath = _paths.GetRevisionDirectory(projectId, folderName);

        try
        {
            Directory.CreateDirectory(LongPath.Extended(folderPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<BackupSession>.Fail(
                ErrorCode.E401, $"バックアップフォルダを作成できません: {ex.Message}", path: folderPath);
        }

        var manifest = initial with { Status = RevisionStatus.InProgress, AppliedAt = appliedAt };
        var manifestPath = _paths.GetManifestFilePath(projectId, folderName);

        try
        {
            await _jsonStore.WriteAsync(manifestPath, manifest, JsonFileStore.DefaultOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<BackupSession>.Fail(
                ErrorCode.E401, $"manifest.json の書き込みに失敗しました: {ex.Message}", path: manifestPath);
        }

        var session = new BackupSession(_jsonStore, projectRoot, folderPath, manifestPath, initial.Revision);
        return GraftResult<BackupSession>.Ok(session);
    }
}

/// <summary>
/// バックアップフォルダ・相対パス操作の共通処理。<see cref="BackupManager"/>・
/// <see cref="BackupSession"/>・<see cref="RevisionStore"/>・<see cref="RevisionRestorer"/> から使う。
/// </summary>
internal static class BackupPathUtil
{
    private static readonly Regex FolderNamePattern = new(@"^r(\d+)_(\d{8}_\d{6})$", RegexOptions.Compiled);

    /// <summary>フォルダ名（例: r24_20260804_143052）からリビジョン番号と適用日時を取り出す。</summary>
    public static (int Revision, DateTimeOffset AppliedAt)? TryParseFolderName(string folderName)
    {
        var match = FolderNamePattern.Match(folderName);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
        {
            return null;
        }
        if (!DateTime.TryParseExact(
                match.Groups[2].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return null;
        }
        return (revision, new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt)));
    }

    /// <summary>ディレクトリ配下の全ファイルサイズ合計をバイト単位で返す。</summary>
    public static long ComputeDirectorySize(string directoryPath)
    {
        if (!Directory.Exists(directoryPath)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // サイズ取得に失敗しても集計は継続する（世代管理の目安値のため）
            }
        }
        return total;
    }

    /// <summary>
    /// 相対パスを検証し、区切り文字をOS標準へ揃えて返す。絶対パスと上位参照(..)を拒否する。
    /// </summary>
    public static GraftResult<string> NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "パスが空です", path: relativePath);
        }
        if (IsAbsolute(relativePath))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "絶対パスは許可されていません", path: relativePath);
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s == ".."))
        {
            return GraftResult<string>.Fail(ErrorCode.E201, "不正な相対パスです", path: relativePath);
        }

        return GraftResult<string>.Ok(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private static bool IsAbsolute(string path)
    {
        if (path.StartsWith('/') || path.StartsWith('\\')) return true;
        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
    }
}
