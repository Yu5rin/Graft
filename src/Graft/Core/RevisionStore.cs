using System.IO;
using System.Security.Cryptography;
using System.Text;
using Graft.Infra;

namespace Graft.Core;

/// <summary>
/// リビジョン履歴（back/&lt;プロジェクトID&gt;/ 配下）の読み取り・検索・世代管理を担う（仕様書7.1〜7.4）。
/// manifest.json とバックアップ実体が正本であり、<see cref="RevisionIndex"/>（history.jsonl）は
/// フォルダが外部から削除・移動された場合にも履歴の記録自体を残すための補助索引（仕様書13.1）。
/// 7.2「追加のデータ保持は不要」と13.1は仕様書内で競合するため、実害の大きい13.1を優先する。
/// </summary>
public sealed class RevisionStore
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _jsonStore = new();
    private readonly RevisionIndex _revisionIndex;

    public RevisionStore(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _revisionIndex = new RevisionIndex(paths);
    }

    /// <summary>
    /// プロジェクトのリビジョン一覧を降順（新しい順）で返す。実体フォルダが存在するリビジョンと
    /// history.jsonl のみに残るリビジョン（フォルダが外部から削除・移動された）をマージする。
    /// 両方に存在する場合は実体フォルダ側の情報を優先する。
    /// </summary>
    public async Task<GraftResult<IReadOnlyList<RevisionSummary>>> ListAsync(string projectId, CancellationToken ct = default)
    {
        var byRevision = new Dictionary<int, RevisionSummary>();
        var issues = new List<GraftIssue>();

        await CollectFromFoldersAsync(projectId, byRevision, issues, ct).ConfigureAwait(false);
        await CollectFromIndexAsync(projectId, byRevision, issues, ct).ConfigureAwait(false);

        var sorted = byRevision.Values.OrderByDescending(s => s.Manifest.Revision).ToList();
        return GraftResult<IReadOnlyList<RevisionSummary>>.Ok(sorted, issues);
    }

    /// <summary>
    /// 指定リビジョンを1件読み取る。実体フォルダがあればそちらを、無ければhistory.jsonlの記録から
    /// 復元不可（IsRestorable=false）のRevisionSummaryを返す。どちらにも無ければ E405。
    /// </summary>
    public async Task<GraftResult<RevisionSummary>> ReadAsync(string projectId, int revision, CancellationToken ct = default)
    {
        var projectDir = _paths.GetProjectBackupDirectory(projectId);
        var folder = FindRevisionFolder(projectDir, revision);
        if (folder is not null)
        {
            var parsed = BackupPathUtil.TryParseFolderName(Path.GetFileName(folder));
            var revisionInfo = parsed ?? (Revision: revision, AppliedAt: DateTimeOffset.Now);
            return await ReadFolderAsync(projectId, folder, revisionInfo.Revision, revisionInfo.AppliedAt, ct).ConfigureAwait(false);
        }

        var indexResult = await _revisionIndex.ReadAllAsync(projectId, ct).ConfigureAwait(false);
        var indexEntry = indexResult.Value.FirstOrDefault(e => e.Revision == revision);
        if (indexEntry is not null)
        {
            var summary = BuildMissingSummary(projectId, indexEntry);
            var issues = new List<GraftIssue>(indexResult.Issues)
            {
                GraftIssue.Of(ErrorCode.E405, "バックアップフォルダが見つかりません（外部から削除・移動された可能性があります）",
                    path: summary.FolderPath, severity: Severity.Warning),
            };
            return GraftResult<RevisionSummary>.Ok(summary, issues);
        }

        return GraftResult<RevisionSummary>.Fail(ErrorCode.E405, $"リビジョン {revision} の実体が見つかりません", path: projectDir);
    }

    /// <summary>
    /// back/配下の実体とhistory.jsonlの双方から最大リビジョン番号を検出する。
    /// projects.json のnextRevision補正に使う（フォルダが後で削除されても番号の再利用を防ぐため
    /// history.jsonl側も参照する）。
    /// </summary>
    public async Task<GraftResult<int>> DetectMaxRevisionAsync(string projectId, CancellationToken ct = default)
    {
        var projectDir = _paths.GetProjectBackupDirectory(projectId);
        var max = 0;

        if (Directory.Exists(projectDir))
        {
            foreach (var folder in Directory.EnumerateDirectories(projectDir))
            {
                var parsed = BackupPathUtil.TryParseFolderName(Path.GetFileName(folder));
                if (parsed is not null && parsed.Value.Revision > max)
                {
                    max = parsed.Value.Revision;
                }
            }
        }

        var indexResult = await _revisionIndex.ReadAllAsync(projectId, ct).ConfigureAwait(false);
        foreach (var entry in indexResult.Value)
        {
            if (entry.Revision > max)
            {
                max = entry.Revision;
            }
        }

        return GraftResult<int>.Ok(max, indexResult.Issues);
    }

    /// <summary>status が in_progress のまま残るリビジョンを探す（仕様書6.3）。</summary>
    public async Task<GraftResult<IReadOnlyList<RevisionSummary>>> FindInProgressAsync(string projectId, CancellationToken ct = default)
    {
        var listResult = await ListAsync(projectId, ct).ConfigureAwait(false);
        if (!listResult.IsSuccess)
        {
            return listResult;
        }

        var inProgress = listResult.Value.Where(s => s.Manifest.Status == RevisionStatus.InProgress).ToList();
        return GraftResult<IReadOnlyList<RevisionSummary>>.Ok(inProgress, listResult.Issues);
    }

    /// <summary>7.4の世代管理。上限超過分を古い順にゴミ箱（非Windowsでは通常削除）へ送る。</summary>
    public async Task<GraftResult<int>> EnforceRetentionAsync(string projectId, BackupSettings settings, CancellationToken ct = default)
    {
        var listResult = await ListAsync(projectId, ct).ConfigureAwait(false);
        if (!listResult.IsSuccess)
        {
            return GraftResult<int>.Fail(listResult.Issues);
        }

        var oldestFirst = listResult.Value.OrderBy(s => s.Manifest.Revision).ToList();

        // 仕様書14章「設定で無制限にできる」の規約: 0以下は無制限（BackupSettingsは非nullableの
        // intのため、null等の別表現は使わない）。設定UI側もこの規約に合わせること。
        var maxCount = settings.MaxRevisions > 0 ? settings.MaxRevisions : int.MaxValue;
        var maxBytes = settings.MaxTotalMB > 0 ? (long)settings.MaxTotalMB * 1024 * 1024 : long.MaxValue;

        return await RemoveUntilWithinLimitsAsync(oldestFirst, maxCount, maxBytes, settings.UseRecycleBin, ct).ConfigureAwait(false);
    }

    /// <summary>同じ patchHash を持つ成功済みリビジョンを探す（仕様書6.2）。見つからなければ null。</summary>
    public async Task<GraftResult<RevisionSummary?>> FindByPatchHashAsync(string projectId, string patchHash, CancellationToken ct = default)
    {
        var listResult = await ListAsync(projectId, ct).ConfigureAwait(false);
        if (!listResult.IsSuccess)
        {
            return GraftResult<RevisionSummary?>.Fail(listResult.Issues);
        }

        var found = listResult.Value.FirstOrDefault(s =>
            s.Manifest.Status == RevisionStatus.Success &&
            string.Equals(s.Manifest.PatchHash, patchHash, StringComparison.OrdinalIgnoreCase));

        return GraftResult<RevisionSummary?>.Ok(found, listResult.Issues);
    }

    /// <summary>パッチ本文の正規化ハッシュを算出する（改行をLFへ、行末空白を除去してからSHA-256）。</summary>
    public static string ComputePatchHash(string patchText)
    {
        var normalized = NormalizeForHash(patchText);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>summaryの全文検索・type絞り込み・日付範囲でフィルタする（仕様書7.2）。</summary>
    public static IEnumerable<RevisionSummary> Filter(
        IEnumerable<RevisionSummary> source, string? keyword, string? type, DateTimeOffset? from, DateTimeOffset? to)
    {
        var query = source;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(r => r.Manifest.Summary is not null
                && r.Manifest.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(type))
        {
            query = query.Where(r => string.Equals(r.Manifest.Type, type, StringComparison.OrdinalIgnoreCase));
        }
        if (from is not null)
        {
            query = query.Where(r => r.Manifest.AppliedAt >= from.Value);
        }
        if (to is not null)
        {
            query = query.Where(r => r.Manifest.AppliedAt <= to.Value);
        }
        return query;
    }

    private static string NormalizeForHash(string text)
    {
        var unified = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = unified.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd(' ', '\t');
        }
        return string.Join('\n', lines);
    }

    /// <summary>back/配下の実体フォルダをすべて読み取り、リビジョン番号をキーに集約する。</summary>
    private async Task CollectFromFoldersAsync(
        string projectId, Dictionary<int, RevisionSummary> byRevision, List<GraftIssue> issues, CancellationToken ct)
    {
        var projectDir = _paths.GetProjectBackupDirectory(projectId);
        if (!Directory.Exists(projectDir)) return;

        foreach (var folder in Directory.EnumerateDirectories(projectDir))
        {
            var parsed = BackupPathUtil.TryParseFolderName(Path.GetFileName(folder));
            if (parsed is null) continue; // 命名規則に合わないフォルダは無視する

            var result = await ReadFolderAsync(projectId, folder, parsed.Value.Revision, parsed.Value.AppliedAt, ct)
                .ConfigureAwait(false);
            byRevision[result.Value.Manifest.Revision] = result.Value;
            issues.AddRange(result.Issues);
        }
    }

    /// <summary>
    /// history.jsonl を読み取り、実体フォルダに存在しないリビジョンだけを補って集約する
    /// （仕様書13.1）。実体フォルダ側が既に登録済みのリビジョンは上書きしない。
    /// </summary>
    private async Task CollectFromIndexAsync(
        string projectId, Dictionary<int, RevisionSummary> byRevision, List<GraftIssue> issues, CancellationToken ct)
    {
        var indexResult = await _revisionIndex.ReadAllAsync(projectId, ct).ConfigureAwait(false);
        issues.AddRange(indexResult.Issues);

        foreach (var entry in indexResult.Value)
        {
            if (byRevision.ContainsKey(entry.Revision)) continue; // 実体フォルダ側を優先する

            var summary = BuildMissingSummary(projectId, entry);
            byRevision[entry.Revision] = summary;
            issues.Add(GraftIssue.Of(ErrorCode.E405, "バックアップフォルダが見つかりません（外部から削除・移動された可能性があります）",
                path: summary.FolderPath, severity: Severity.Warning));
        }
    }

    /// <summary>
    /// history.jsonl のみに残るリビジョンから、復元不可（IsRestorable=false）の
    /// RevisionSummary を組み立てる。Entries/Hooksは記録していないため空のままとなる。
    /// </summary>
    private RevisionSummary BuildMissingSummary(string projectId, RevisionIndexEntry entry)
    {
        var manifest = new RevisionManifest
        {
            Revision = entry.Revision,
            ProjectId = projectId,
            Summary = entry.Summary,
            Type = entry.Type,
            AppliedAt = entry.AppliedAt,
            PatchHash = entry.PatchHash,
            Status = entry.Status,
            Stats = entry.Stats,
        };

        return new RevisionSummary
        {
            Manifest = manifest,
            FolderPath = _paths.GetRevisionDirectory(projectId, entry.FolderName),
            IsRestorable = false,
            SizeBytes = 0,
        };
    }

    private async Task<GraftResult<RevisionSummary>> ReadFolderAsync(
        string projectId, string folder, int revision, DateTimeOffset appliedAt, CancellationToken ct)
    {
        var manifestPath = Path.Combine(folder, "manifest.json");
        RevisionManifest Fallback() => CreateFallbackManifest(projectId, revision, appliedAt);
        var readResult = await _jsonStore
            .ReadWithRecoveryAsync<RevisionManifest>(manifestPath, Fallback, JsonFileStore.DefaultOptions, ct)
            .ConfigureAwait(false);

        var manifest = readResult.Value;
        var sizeBytes = BackupPathUtil.ComputeDirectorySize(folder);
        var isRestorable = AreBackupFilesPresent(folder, manifest);

        var summary = new RevisionSummary
        {
            Manifest = manifest,
            FolderPath = folder,
            IsRestorable = isRestorable,
            SizeBytes = sizeBytes,
        };

        var issues = new List<GraftIssue>(readResult.Issues);
        if (!isRestorable)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E405, "バックアップの実体の一部が見つかりません", path: folder, severity: Severity.Warning));
        }

        return GraftResult<RevisionSummary>.Ok(summary, issues);
    }

    private static RevisionManifest CreateFallbackManifest(string projectId, int revision, DateTimeOffset appliedAt)
        => new()
        {
            Revision = revision,
            ProjectId = projectId,
            AppliedAt = appliedAt,
            Status = RevisionStatus.InProgress,
            Summary = "(manifest.json が破損していたため再生成されました)",
        };

    /// <summary>
    /// manifestのentriesが要求する退避ファイルが実際に存在するかを検証する（仕様書13.1）。
    /// バックアップフォルダの一部が外部から削除・移動された場合に false を返す。
    /// </summary>
    private static bool AreBackupFilesPresent(string folder, RevisionManifest manifest)
    {
        foreach (var entry in manifest.Entries)
        {
            if (entry.Operation is EntryOperation.Create or EntryOperation.Mkdir) continue; // 退避対象なし

            var backupRelative = entry.Operation == EntryOperation.Rename ? entry.RenamedFrom : entry.Path;
            if (string.IsNullOrEmpty(backupRelative)) continue;

            var normalized = BackupPathUtil.NormalizeRelativePath(backupRelative);
            if (!normalized.IsSuccess) continue;

            var backupFull = Path.Combine(folder, normalized.Value);
            if (!File.Exists(LongPath.Extended(backupFull)))
            {
                return false;
            }
        }
        return true;
    }

    private static string? FindRevisionFolder(string projectDir, int revision)
    {
        if (!Directory.Exists(projectDir)) return null;
        foreach (var folder in Directory.EnumerateDirectories(projectDir))
        {
            var parsed = BackupPathUtil.TryParseFolderName(Path.GetFileName(folder));
            if (parsed is not null && parsed.Value.Revision == revision)
            {
                return folder;
            }
        }
        return null;
    }

    private static async Task<GraftResult<int>> RemoveUntilWithinLimitsAsync(
        IReadOnlyList<RevisionSummary> oldestFirst, int maxCount, long maxBytes, bool useRecycleBin, CancellationToken ct)
    {
        var totalBytes = oldestFirst.Sum(s => s.SizeBytes);
        var removed = 0;
        var issues = new List<GraftIssue>();
        var index = 0;

        while (index < oldestFirst.Count && (oldestFirst.Count - index > maxCount || totalBytes > maxBytes))
        {
            var target = oldestFirst[index];
            var ok = await RemoveRevisionFolderAsync(target.FolderPath, useRecycleBin, ct).ConfigureAwait(false);
            if (ok)
            {
                totalBytes -= target.SizeBytes;
                removed++;
            }
            else
            {
                issues.Add(GraftIssue.Of(ErrorCode.E402, "世代整理での削除に失敗しました", path: target.FolderPath, severity: Severity.Warning));
            }
            index++;
        }

        return GraftResult<int>.Ok(removed, issues);
    }

    private static Task<bool> RemoveRevisionFolderAsync(string folderPath, bool useRecycleBin, CancellationToken ct)
        => Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(LongPath.Extended(folderPath)))
                {
                    // 仕様書13.1: history.jsonlのみに残るリビジョン（フォルダが既に外部から
                    // 削除・移動済み）は削除対象が無いため、削除済み扱いとして扱う。
                    return true;
                }

                if (useRecycleBin && OperatingSystem.IsWindows())
                {
                    return RecycleBin.Send(folderPath);
                }

                // 非Windows環境（Linux/macOS。テスト実行環境含む）にはごみ箱APIが存在しないため
                // 通常削除にフォールバックする。Windows実行時に useRecycleBin=false の場合も同様。
                Directory.Delete(LongPath.Extended(folderPath), recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }, ct);
}
