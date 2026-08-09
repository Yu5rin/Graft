using System.IO;
using Graft.Infra;

namespace Graft.Core;

/// <summary>
/// 「ここまで戻す」（まとめ戻し）の事前確認情報。実行前に対象リビジョン数・影響ファイルを
/// UIへ提示するために使う（仕様: 確認なしに実行しないこと）。
/// </summary>
public sealed record RestoreThroughPreview
{
    /// <summary>取り消し対象のリビジョン（新しい順）。復元不可のものも含む。</summary>
    public required IReadOnlyList<RevisionSummary> RevisionsToUndo { get; init; }

    /// <summary>取り消し対象全体が影響するプロジェクト相対パスの一覧（重複除去・昇順）。</summary>
    public required IReadOnlyList<string> AffectedPaths { get; init; }

    /// <summary>取り消し対象に含まれる復元不可（<see cref="RevisionSummary.IsRestorable"/>=false）のリビジョン。</summary>
    public required IReadOnlyList<RevisionSummary> NotRestorable { get; init; }

    /// <summary>実行可能かどうか。取り消し対象が無い（最新リビジョンを選んだ）場合、
    /// または復元不可のリビジョンを含む場合は false。</summary>
    public bool CanExecute => RevisionsToUndo.Count > 0 && NotRestorable.Count == 0;
}

/// <summary>
/// 指定リビジョン直前の状態への復元処理（仕様書7.3）と、選択リビジョンを適用した直後の状態まで
/// 一括で戻す「ここまで戻す」処理を担う。
/// 単発復元（<see cref="RestoreAsync"/>）は復元操作自体を新規リビジョンとして記録するのを
/// 呼び出し側の責務としているのに対し、まとめ戻し（<see cref="RestoreThroughAsync"/>）は
/// 複数リビジョンにまたがる取り消しを「戻しすぎた」場合にも戻せるようにする必要があるため、
/// この操作自体の記録（<see cref="BackupManager"/>/<see cref="BackupSession"/>の再利用）まで
/// ここで完結させる。
/// </summary>
public sealed class RevisionRestorer
{
    private readonly AppPaths _paths;
    private readonly BackupManager _backup;

    public RevisionRestorer(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _backup = new BackupManager(paths);
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

        var entriesResult = await UndoRevisionEntriesAsync(revision, projectRoot, ct).ConfigureAwait(false);
        var issues = warnings.Concat(entriesResult.Issues).ToList();

        return issues.Any(i => i.Severity == Severity.Error)
            ? GraftResult<IReadOnlyList<string>>.Fail(issues)
            : GraftResult<IReadOnlyList<string>>.Ok(entriesResult.Value, issues);
    }

    /// <summary>
    /// 「ここまで戻す」の事前確認情報を組み立てる（実行前チェック専用。ファイルへは一切触れない）。
    /// <paramref name="targetRevision"/>より新しいリビジョンをすべて対象とする。
    /// </summary>
    public static RestoreThroughPreview BuildRestoreThroughPreview(
        IReadOnlyList<RevisionSummary> allRevisions, int targetRevision)
    {
        ArgumentNullException.ThrowIfNull(allRevisions);
        var toUndo = allRevisions
            .Where(r => r.Manifest.Revision > targetRevision)
            .OrderByDescending(r => r.Manifest.Revision)
            .ToList();
        var notRestorable = toUndo.Where(r => !r.IsRestorable).ToList();

        return new RestoreThroughPreview
        {
            RevisionsToUndo = toUndo,
            AffectedPaths = CollectAffectedPaths(toUndo),
            NotRestorable = notRestorable,
        };
    }

    /// <summary>
    /// 「ここまで戻す」本体。<paramref name="revisionsToUndo"/>（新しい順。必ず
    /// <see cref="BuildRestoreThroughPreview"/>が返した順序のまま渡すこと）を先頭から順に
    /// 1件ずつ取り消し、<paramref name="targetRevision"/>を適用した直後の状態を再現する。
    /// 逆順（新しい順）で処理しないと、同一ファイルが複数リビジョンで変更されている場合に
    /// 内容が壊れる。
    ///
    /// この操作自体を r<paramref name="newRevisionNumber"/> として新規リビジョンに記録する
    /// （「戻しすぎた」場合にも通常の復元で元へ戻せるようにするため）。そのため、取り消しを
    /// 始める前に影響を受ける全ファイルの「操作前」の内容を <see cref="BackupSession"/> へ
    /// 退避してから取り消しを行う。
    ///
    /// 途中（SafeFileWriterの検証失敗等）で取り消しが失敗した場合は、それ以降の（より古い）
    /// リビジョンの取り消しは行わずそこで打ち切る。取り消し順序を保てなくなった状態で
    /// 続行すると内容が壊れるため。それまでに実際に変更されたファイルだけを新規リビジョンの
    /// entriesとして記録し、status は success にせず in_progress のまま確定する
    /// （仕様書6.3の中断復帰の仕組みと同じ扱い。次回起動時に「未完了の適用」として検出され、
    /// ロールバックを提案できる）。この場合は E403 を伴う失敗として返し、成功したとは報告しない。
    /// </summary>
    public async Task<GraftResult<RevisionManifest>> RestoreThroughAsync(
        string projectId, string projectRoot, int targetRevision, IReadOnlyList<RevisionSummary> revisionsToUndo,
        int newRevisionNumber, bool force, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(revisionsToUndo);
        if (revisionsToUndo.Count == 0)
        {
            return GraftResult<RevisionManifest>.Fail(ErrorCode.E201, "取り消し対象のリビジョンがありません");
        }

        var notRestorable = revisionsToUndo.Where(r => !r.IsRestorable).ToList();
        if (notRestorable.Count > 0)
        {
            var names = string.Join("、", notRestorable.Select(r => $"r{r.Manifest.Revision}"));
            return GraftResult<RevisionManifest>.Fail(
                ErrorCode.E405,
                $"バックアップの実体が失われているリビジョンが含まれるため中止しました（{names}）。" +
                "取り消し順序を保てないリビジョンを飛ばして続行すると内容が壊れるため、ここまで戻す操作自体を行いません。");
        }

        // 単発復元と同じく、取り消しを始める最初（＝最新）のリビジョンについてのみ、適用後に
        // 外部からさらに変更されていないかを確認する。それより古い段階は、この一連の取り消し
        // 処理自身が作る中間状態であり、外部変更の検知としては意味を持たないため確認しない。
        var newest = revisionsToUndo[0];
        var warnings = await CheckHashesAsync(projectRoot, newest.Manifest.Entries, ct).ConfigureAwait(false);
        if (warnings.Count > 0 && !force)
        {
            return GraftResult<RevisionManifest>.Fail(warnings);
        }

        var affectedPaths = CollectAffectedPaths(revisionsToUndo);
        var initial = BuildRestoreThroughInitialManifest(projectId, targetRevision, newRevisionNumber, revisionsToUndo, affectedPaths.Count);
        var began = await _backup.BeginAsync(projectId, projectRoot, initial, ct).ConfigureAwait(false);
        if (!began.IsSuccess)
        {
            return GraftResult<RevisionManifest>.Fail(began.Issues);
        }
        var session = began.Value;

        var existedBefore = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in affectedPaths)
        {
            var stored = await session.StoreAsync(path, ct).ConfigureAwait(false);
            if (!stored.IsSuccess)
            {
                // まだプロジェクト側のファイルには一切書き込んでいないため、ロールバックする
                // 対象が無い。BeginAsyncが作ったin_progressの空フォルダは6.3の中断検出に委ねる。
                return GraftResult<RevisionManifest>.Fail(stored.Issues);
            }
            existedBefore[path] = stored.Value;
            if (!stored.Value)
            {
                session.TrackCreated(path); // 現在存在しない = 戻すと新規作成扱いになる
            }
        }

        var issues = new List<GraftIssue>(warnings);
        var undone = new List<int>();
        RevisionSummary? failedAt = null;

        foreach (var revision in revisionsToUndo)
        {
            var result = await UndoRevisionEntriesAsync(revision, projectRoot, ct).ConfigureAwait(false);
            issues.AddRange(result.Issues);
            if (!result.IsSuccess)
            {
                failedAt = revision;
                break;
            }
            undone.Add(revision.Manifest.Revision);
        }

        var entries = await BuildRestoreThroughEntriesAsync(session.FolderPath, projectRoot, affectedPaths, existedBefore, ct)
            .ConfigureAwait(false);
        var status = failedAt is null ? RevisionStatus.Success : RevisionStatus.InProgress;
        var finalManifest = initial with { Status = status, Entries = entries, Stats = initial.Stats with { Files = entries.Count } };
        var completed = await session.CompleteAsync(finalManifest, ct).ConfigureAwait(false);
        issues.AddRange(completed.Issues);

        if (failedAt is not null)
        {
            var progressText = undone.Count == 0
                ? "1件も取り消せませんでした"
                : $"{string.Join("、", undone.Select(r => $"r{r}"))} は取り消せましたが";
            issues.Insert(0, GraftIssue.Of(
                ErrorCode.E403,
                $"{progressText}、r{failedAt.Manifest.Revision} の取り消し中に失敗したため中断しました。" +
                "一部のファイルだけが書き換わった中途半端な状態になっている可能性があります。" +
                $"今回の変更はr{newRevisionNumber}として記録したので、履歴からr{newRevisionNumber}を選び" +
                "「このリビジョンを取り消す」を実行すれば、この「ここまで戻す」操作を行う前の状態へ戻せます。" +
                "解決したら、あらためて「ここまで戻す」をやり直してください。"));
            return GraftResult<RevisionManifest>.Fail(issues);
        }

        return GraftResult<RevisionManifest>.Ok(finalManifest, issues);
    }

    /// <summary>単発復元の中核。指定リビジョンのentriesを新しい順の取り消し順序で書き戻す。
    /// hashAfterの照合は行わない（呼び出し側の責務）。</summary>
    private static async Task<GraftResult<IReadOnlyList<string>>> UndoRevisionEntriesAsync(
        RevisionSummary revision, string projectRoot, CancellationToken ct)
    {
        var restored = new List<string>();
        var issues = new List<GraftIssue>();

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

    /// <summary>取り消し対象リビジョン群が影響するプロジェクト相対パスを重複除去・昇順で集める。
    /// MKDIRはファイルではないため対象に含めない（ApplyEngine.CollectBackupTargetsと同じ扱い）。
    /// RENAMEは移動先（Path）だけでなく移動元（RenamedFrom）も戻す対象になる。</summary>
    private static IReadOnlyList<string> CollectAffectedPaths(IReadOnlyList<RevisionSummary> revisions)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var revision in revisions)
        {
            foreach (var entry in revision.Manifest.Entries)
            {
                if (entry.Operation == EntryOperation.Mkdir) continue;
                set.Add(entry.Path);
                if (entry.Operation == EntryOperation.Rename && !string.IsNullOrEmpty(entry.RenamedFrom))
                {
                    set.Add(entry.RenamedFrom);
                }
            }
        }
        return set.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static RevisionManifest BuildRestoreThroughInitialManifest(
        string projectId, int targetRevision, int newRevisionNumber,
        IReadOnlyList<RevisionSummary> revisionsToUndo, int affectedFileCount)
    {
        var undoneLabel = string.Join("、", revisionsToUndo.Select(r => $"r{r.Manifest.Revision}"));
        // 取り消し＝元のリビジョンの増減を反転させたものとみなし、大まかな目安として記録する
        // （行単位の厳密な差分再計算はしない）。
        var addedBack = revisionsToUndo.Sum(r => r.Manifest.Stats.Removed);
        var removedBack = revisionsToUndo.Sum(r => r.Manifest.Stats.Added);

        return new RevisionManifest
        {
            Revision = newRevisionNumber,
            ProjectId = projectId,
            Summary = $"r{targetRevision}まで戻す（{undoneLabel}を取り消し）",
            Type = "revert",
            AppliedAt = DateTimeOffset.Now,
            Status = RevisionStatus.InProgress,
            Stats = new RevisionStats { Files = affectedFileCount, Added = addedBack, Removed = removedBack },
            Entries = Array.Empty<RevisionEntry>(),
        };
    }

    /// <summary>
    /// まとめ戻し自身のmanifest.entriesを、操作前（バックアップ先に退避した内容）と操作後
    /// （現在のプロジェクトルートの内容）の実測比較から組み立てる。取り消し対象リビジョンの
    /// operation種別（rename等）をそのまま引き継がず、常にcreate/modify/deleteのいずれかに
    /// 単純化する。RENAMEの移動元・移動先を含め、実際にディスクへ起きた変化を素直に表現する
    /// ほうが、この合成リビジョン自身の取り消し（単発復元のUndoEntryAsync）にとって
    /// 確実に正しいため。
    /// </summary>
    private static async Task<List<RevisionEntry>> BuildRestoreThroughEntriesAsync(
        string backupFolder, string projectRoot, IReadOnlyList<string> affectedPaths,
        IReadOnlyDictionary<string, bool> existedBefore, CancellationToken ct)
    {
        var entries = new List<RevisionEntry>();
        foreach (var path in affectedPaths)
        {
            var normalized = BackupPathUtil.NormalizeRelativePath(path);
            if (!normalized.IsSuccess) continue;

            var before = existedBefore.TryGetValue(path, out var stored) && stored;
            string? hashBefore = before
                ? await ReadHashIfExistsAsync(Path.Combine(backupFolder, normalized.Value), ct).ConfigureAwait(false)
                : null;

            var currentFull = Path.GetFullPath(Path.Combine(projectRoot, normalized.Value));
            var existsAfter = File.Exists(LongPath.Extended(currentFull));
            var hashAfter = existsAfter ? await ReadHashIfExistsAsync(currentFull, ct).ConfigureAwait(false) : null;

            if (before && existsAfter && string.Equals(hashBefore, hashAfter, StringComparison.OrdinalIgnoreCase))
            {
                continue; // 結果としてこのファイルは変化していない
            }
            if (!before && !existsAfter)
            {
                continue; // 操作前後とも存在しない（対象リビジョン群には含まれるが実質無関係）
            }

            var operation = !before ? EntryOperation.Create : !existsAfter ? EntryOperation.Delete : EntryOperation.Modify;
            entries.Add(new RevisionEntry
            {
                Path = path,
                Operation = operation,
                Desc = "「ここまで戻す」による変更",
                HashBefore = hashBefore,
                HashAfter = hashAfter,
            });
        }
        return entries;
    }

    private static async Task<string?> ReadHashIfExistsAsync(string fullPath, CancellationToken ct)
    {
        if (!File.Exists(LongPath.Extended(fullPath))) return null;
        var read = await FileTextIO.ReadAsync(fullPath, ct).ConfigureAwait(false);
        return read.IsSuccess ? FileTextIO.ComputeHash(read.Value.Text) : null;
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
