using System.IO;
using Graft.Infra;

namespace Graft.Core;

/// <summary>
/// 1回の適用処理に対応するバックアップの作業単位。仕様書2.2・6.3。
/// <see cref="BackupManager.BeginAsync"/> が生成し、適用エンジンが対象ファイルごとに
/// <see cref="StoreAsync"/> または <see cref="TrackCreated"/> を呼び出す。
/// </summary>
public sealed class BackupSession
{
    private readonly JsonFileStore _jsonStore;
    private readonly RevisionIndex _revisionIndex;
    private readonly string _projectId;
    private readonly string _projectRoot;
    private readonly string _manifestPath;
    private readonly List<string> _storedPaths = new();
    private readonly List<string> _createdPaths = new();

    internal BackupSession(
        JsonFileStore jsonStore, RevisionIndex revisionIndex, string projectId,
        string projectRoot, string folderPath, string manifestPath, int revision)
    {
        _jsonStore = jsonStore;
        _revisionIndex = revisionIndex;
        _projectId = projectId;
        _projectRoot = projectRoot;
        FolderPath = folderPath;
        _manifestPath = manifestPath;
        Revision = revision;
    }

    /// <summary>このリビジョンのバックアップフォルダの絶対パス。</summary>
    public string FolderPath { get; }

    /// <summary>リビジョン番号。</summary>
    public int Revision { get; }

    /// <summary>
    /// 元ファイルを相対パス構造を保ったままバックアップフォルダへ退避する。
    /// 元ファイルが存在しない場合（新規作成対象）は何もせず <c>false</c> を返す。
    /// </summary>
    public async Task<GraftResult<bool>> StoreAsync(string relativePath, CancellationToken ct = default)
    {
        var normalized = BackupPathUtil.NormalizeRelativePath(relativePath);
        if (!normalized.IsSuccess)
        {
            return GraftResult<bool>.Fail(normalized.Issues);
        }

        var sourceFull = Path.GetFullPath(Path.Combine(_projectRoot, normalized.Value));
        var sourceIo = LongPath.Extended(sourceFull);
        if (!File.Exists(sourceIo))
        {
            return GraftResult<bool>.Ok(false);
        }

        var destFull = Path.Combine(FolderPath, normalized.Value);
        var destDir = Path.GetDirectoryName(destFull);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(LongPath.Extended(destDir));
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(sourceIo, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<bool>.Fail(ErrorCode.E401, $"退避元の読み取りに失敗しました: {ex.Message}", path: relativePath);
        }

        var writeResult = await SafeFileWriter.ReplaceAsync(destFull, bytes, ct).ConfigureAwait(false);
        if (!writeResult.IsSuccess)
        {
            var remapped = writeResult.Issues.Select(i => i with { Code = ErrorCode.E401 });
            return GraftResult<bool>.Fail(remapped);
        }

        _storedPaths.Add(normalized.Value);
        return GraftResult<bool>.Ok(true);
    }

    /// <summary>manifest.json を最終状態（success または rolled_back）で確定させる。</summary>
    public async Task<GraftResult<bool>> CompleteAsync(RevisionManifest manifest, CancellationToken ct = default)
    {
        try
        {
            await _jsonStore.WriteAsync(_manifestPath, manifest, JsonFileStore.DefaultOptions, ct).ConfigureAwait(false);
            return GraftResult<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<bool>.Fail(ErrorCode.E401, $"manifest.json の確定に失敗しました: {ex.Message}", path: _manifestPath);
        }
    }

    /// <summary>退避済みファイルを元の場所へ書き戻し、適用中に新規作成されたファイルを削除する。</summary>
    public async Task<GraftResult<bool>> RollbackAsync(CancellationToken ct = default)
    {
        var issues = new List<GraftIssue>();

        foreach (var relativePath in _storedPaths)
        {
            var restoreResult = await RestoreOneAsync(relativePath, ct).ConfigureAwait(false);
            issues.AddRange(restoreResult.Issues);
        }

        foreach (var relativePath in _createdPaths)
        {
            DeleteCreatedFile(relativePath, issues);
        }

        return issues.Any(i => i.Severity == Severity.Error)
            ? GraftResult<bool>.Fail(issues)
            : GraftResult<bool>.Ok(true, issues);
    }

    /// <summary>
    /// 適用中に新規作成したファイルを記録する（ロールバック時に削除するため）。
    /// 不正な相対パスは黙って無視する（呼び出し側は事前に検証済みである前提）。
    /// </summary>
    public void TrackCreated(string relativePath)
    {
        var normalized = BackupPathUtil.NormalizeRelativePath(relativePath);
        if (normalized.IsSuccess)
        {
            _createdPaths.Add(normalized.Value);
        }
    }

    private async Task<GraftResult<bool>> RestoreOneAsync(string relativePath, CancellationToken ct)
    {
        var backupFull = Path.Combine(FolderPath, relativePath);
        var backupIo = LongPath.Extended(backupFull);
        if (!File.Exists(backupIo))
        {
            return GraftResult<bool>.Fail(ErrorCode.E405, "退避ファイルが見つかりません", path: relativePath);
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(backupIo, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<bool>.Fail(ErrorCode.E402, $"退避ファイルの読み取りに失敗しました: {ex.Message}", path: relativePath);
        }

        var targetFull = Path.GetFullPath(Path.Combine(_projectRoot, relativePath));
        return await SafeFileWriter.ReplaceAsync(targetFull, bytes, ct).ConfigureAwait(false);
    }

    private void DeleteCreatedFile(string relativePath, List<GraftIssue> issues)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_projectRoot, relativePath));
        var ioPath = LongPath.Extended(fullPath);
        try
        {
            if (File.Exists(ioPath))
            {
                File.Delete(ioPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E402, $"新規作成ファイルの削除に失敗しました: {ex.Message}", path: relativePath));
        }
    }
}
