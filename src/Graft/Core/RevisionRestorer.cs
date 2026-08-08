using System.IO;
using Graft.Infra;

namespace Graft.Core;

/// <summary>
/// 指定リビジョン直前の状態への復元処理。仕様書7.3。
/// 復元操作自体を新規リビジョンとして記録するのは呼び出し側の責務であり、ここでは
/// 元のファイル内容を書き戻し、書き戻した相対パスの一覧を返すところまでを行う。
/// </summary>
public sealed class RevisionRestorer
{
    private readonly AppPaths _paths;

    public RevisionRestorer(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>
    /// リビジョン適用直前の状態へ復元する。復元前に entries の <c>hashAfter</c> と現在の
    /// ファイル内容を照合し、変更があれば E301（Warning）を issues に含める。
    /// <paramref name="force"/> が false のとき警告が1件でもあれば復元を行わず失敗を返す。
    /// </summary>
    public async Task<GraftResult<IReadOnlyList<string>>> RestoreAsync(
        string projectId, string projectRoot, RevisionSummary revision, bool force, CancellationToken ct = default)
    {
        if (!revision.IsRestorable || !Directory.Exists(revision.FolderPath))
        {
            var expectedDir = _paths.GetProjectBackupDirectory(projectId);
            return GraftResult<IReadOnlyList<string>>.Fail(
                ErrorCode.E405, $"バックアップの実体が見つからないため復元できません（{expectedDir} 配下）", path: revision.FolderPath);
        }

        var warnings = await CheckHashesAsync(projectRoot, revision.Manifest.Entries, ct).ConfigureAwait(false);
        if (warnings.Count > 0 && !force)
        {
            return GraftResult<IReadOnlyList<string>>.Fail(warnings);
        }

        var restored = new List<string>();
        var issues = new List<GraftIssue>(warnings);

        foreach (var entry in OrderForUndo(revision.Manifest.Entries))
        {
            var result = await UndoEntryAsync(revision.FolderPath, projectRoot, entry, ct).ConfigureAwait(false);
            issues.AddRange(result.Issues);
            if (result.IsSuccess && result.Value is not null)
            {
                restored.Add(result.Value);
            }
        }

        return issues.Any(i => i.Severity == Severity.Error)
            ? GraftResult<IReadOnlyList<string>>.Fail(issues)
            : GraftResult<IReadOnlyList<string>>.Ok(restored, issues);
    }

    private static async Task<List<GraftIssue>> CheckHashesAsync(
        string projectRoot, IReadOnlyList<RevisionEntry> entries, CancellationToken ct)
    {
        var issues = new List<GraftIssue>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.HashAfter)) continue;

            var normalized = BackupPathUtil.NormalizeRelativePath(entry.Path);
            if (!normalized.IsSuccess) continue; // パス不正は復元処理側の書き込みで検出する

            var full = Path.GetFullPath(Path.Combine(projectRoot, normalized.Value));
            if (!File.Exists(LongPath.Extended(full)))
            {
                issues.Add(GraftIssue.Of(ErrorCode.E301, "適用後にファイルが削除されています", path: entry.Path, severity: Severity.Warning));
                continue;
            }

            var readResult = await FileTextIO.ReadAsync(full, ct).ConfigureAwait(false);
            if (!readResult.IsSuccess) continue;

            var currentHash = FileTextIO.ComputeHash(readResult.Value.Text);
            if (!string.Equals(currentHash, entry.HashAfter, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(GraftIssue.Of(ErrorCode.E301, "適用後にさらに変更されています", path: entry.Path, severity: Severity.Warning));
            }
        }
        return issues;
    }

    /// <summary>6.6の適用順序（MKDIR→RENAME→FULL→SR等→DELETE）を逆順に辿る並びへ整列する。</summary>
    private static IEnumerable<RevisionEntry> OrderForUndo(IReadOnlyList<RevisionEntry> entries)
        => entries.OrderBy(UndoPriority);

    private static int UndoPriority(RevisionEntry entry) => entry.Operation switch
    {
        EntryOperation.Delete => 0,
        EntryOperation.Modify => 1,
        EntryOperation.Create => 1,
        EntryOperation.Rename => 2,
        EntryOperation.Mkdir => 3,
        _ => 4,
    };

    private static async Task<GraftResult<string?>> UndoEntryAsync(
        string backupFolder, string projectRoot, RevisionEntry entry, CancellationToken ct)
    {
        switch (entry.Operation)
        {
            case EntryOperation.Create:
                return UndoCreate(projectRoot, entry.Path);
            case EntryOperation.Mkdir:
                return UndoMkdir(projectRoot, entry.Path);
            case EntryOperation.Rename:
                return await UndoRenameAsync(backupFolder, projectRoot, entry, ct).ConfigureAwait(false);
            default:
                return await UndoContentAsync(backupFolder, projectRoot, entry.Path, ct).ConfigureAwait(false);
        }
    }

    /// <summary>CREATE操作の取り消し。新規作成されたファイルを削除する。</summary>
    private static GraftResult<string?> UndoCreate(string projectRoot, string relativePath)
    {
        var normalized = BackupPathUtil.NormalizeRelativePath(relativePath);
        if (!normalized.IsSuccess)
        {
            return GraftResult<string?>.Fail(normalized.Issues);
        }

        var ioPath = LongPath.Extended(Path.GetFullPath(Path.Combine(projectRoot, normalized.Value)));
        try
        {
            if (File.Exists(ioPath))
            {
                File.Delete(ioPath);
            }
            return GraftResult<string?>.Ok(relativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<string?>.Fail(ErrorCode.E402, $"新規作成ファイルの削除に失敗しました: {ExceptionMessages.Describe(ex)}", path: relativePath);
        }
    }

    /// <summary>MKDIR操作の取り消し。作成後に何も追加されていなければフォルダを削除する。</summary>
    private static GraftResult<string?> UndoMkdir(string projectRoot, string relativePath)
    {
        var normalized = BackupPathUtil.NormalizeRelativePath(relativePath);
        if (!normalized.IsSuccess)
        {
            return GraftResult<string?>.Fail(normalized.Issues);
        }

        var ioPath = LongPath.Extended(Path.GetFullPath(Path.Combine(projectRoot, normalized.Value)));
        try
        {
            if (!Directory.Exists(ioPath))
            {
                return GraftResult<string?>.Ok(relativePath);
            }
            if (Directory.EnumerateFileSystemEntries(ioPath).Any())
            {
                var warning = GraftIssue.Of(
                    ErrorCode.E402, "作成後にファイルが追加されたためフォルダを削除できません", path: relativePath, severity: Severity.Warning);
                return GraftResult<string?>.Ok(null, new[] { warning });
            }
            Directory.Delete(ioPath);
            return GraftResult<string?>.Ok(relativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<string?>.Fail(ErrorCode.E402, $"作成フォルダの削除に失敗しました: {ExceptionMessages.Describe(ex)}", path: relativePath);
        }
    }

    /// <summary>RENAME操作の取り消し。退避内容を移動元パスへ復元し、移動先ファイルを削除する。</summary>
    private static async Task<GraftResult<string?>> UndoRenameAsync(
        string backupFolder, string projectRoot, RevisionEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entry.RenamedFrom))
        {
            return GraftResult<string?>.Fail(ErrorCode.E405, "移動元のパスが記録されていません", path: entry.Path);
        }

        var restoreResult = await UndoContentAsync(backupFolder, projectRoot, entry.RenamedFrom, ct).ConfigureAwait(false);
        if (!restoreResult.IsSuccess)
        {
            return restoreResult;
        }

        var deleteResult = DeleteRenamedTarget(projectRoot, entry.Path, entry.RenamedFrom);
        if (!deleteResult.IsSuccess)
        {
            return deleteResult;
        }

        return GraftResult<string?>.Ok(restoreResult.Value, restoreResult.Issues.Concat(deleteResult.Issues));
    }

    private static GraftResult<string?> DeleteRenamedTarget(string projectRoot, string newRelativePath, string originalRelativePath)
    {
        var normalizedNew = BackupPathUtil.NormalizeRelativePath(newRelativePath);
        var normalizedOriginal = BackupPathUtil.NormalizeRelativePath(originalRelativePath);
        if (!normalizedNew.IsSuccess || !normalizedOriginal.IsSuccess)
        {
            return GraftResult<string?>.Ok(null); // パス不正は退避側の正規化で既に検出済み
        }

        var newFull = Path.GetFullPath(Path.Combine(projectRoot, normalizedNew.Value));
        var originalFull = Path.GetFullPath(Path.Combine(projectRoot, normalizedOriginal.Value));
        if (string.Equals(newFull, originalFull, StringComparison.OrdinalIgnoreCase))
        {
            return GraftResult<string?>.Ok(null); // 移動先と移動元が同一なら削除不要
        }

        var newIoPath = LongPath.Extended(newFull);
        try
        {
            if (File.Exists(newIoPath))
            {
                File.Delete(newIoPath);
            }
            return GraftResult<string?>.Ok(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<string?>.Fail(ErrorCode.E402, $"移動先ファイルの削除に失敗しました: {ExceptionMessages.Describe(ex)}", path: newRelativePath);
        }
    }

    /// <summary>MODIFY/DELETE系操作の取り消し。バックアップフォルダの退避内容を元の場所へ書き戻す。</summary>
    private static async Task<GraftResult<string?>> UndoContentAsync(
        string backupFolder, string projectRoot, string relativePath, CancellationToken ct)
    {
        var normalized = BackupPathUtil.NormalizeRelativePath(relativePath);
        if (!normalized.IsSuccess)
        {
            return GraftResult<string?>.Fail(normalized.Issues);
        }

        var backupFull = Path.Combine(backupFolder, normalized.Value);
        var backupIo = LongPath.Extended(backupFull);
        if (!File.Exists(backupIo))
        {
            return GraftResult<string?>.Fail(ErrorCode.E405, "退避ファイルが見つかりません", path: relativePath);
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(backupIo, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<string?>.Fail(ErrorCode.E402, $"退避ファイルの読み取りに失敗しました: {ExceptionMessages.Describe(ex)}", path: relativePath);
        }

        var targetFull = Path.GetFullPath(Path.Combine(projectRoot, normalized.Value));
        var writeResult = await SafeFileWriter.ReplaceAsync(targetFull, bytes, ct).ConfigureAwait(false);
        return writeResult.IsSuccess
            ? GraftResult<string?>.Ok(relativePath, writeResult.Issues)
            : GraftResult<string?>.Fail(writeResult.Issues);
    }
}
