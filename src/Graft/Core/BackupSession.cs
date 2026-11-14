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
    // 適用が何をしたかの記録。メモリ上のこの一覧がロールバックの元で、同じ内容をバックアップフォルダの
    // journal.jsonl へも都度追記する（BackupJournal参照）。落ちたあとの起動時ロールバックは後者から戻す。
    private readonly List<JournalOp> _ops = new();
    private readonly RevisionManifest _beganManifest;

    internal BackupSession(
        JsonFileStore jsonStore, RevisionIndex revisionIndex, string projectId,
        string projectRoot, string folderPath, string manifestPath, int revision,
        RevisionManifest beganManifest)
    {
        _beganManifest = beganManifest;
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

        // 新規に作成するバックアップは必ずfiles/サブフォルダ配下へ書く（BackupPathUtil.
        // FilesSubfolderNameのコメント参照）。リビジョンフォルダ直下にはメタデータ
        // （manifest.json）だけを置き、退避したプロジェクトファイルとは物理的に分離する。
        var destFull = BackupPathUtil.GetBackupFilePathForWrite(FolderPath, normalized.Value);
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
            return GraftResult<bool>.Fail(ErrorCode.E401, $"退避元の読み取りに失敗しました: {ExceptionMessages.Describe(ex)}", path: relativePath);
        }

        var writeResult = await SafeFileWriter.ReplaceAsync(destFull, bytes, ct).ConfigureAwait(false);
        if (!writeResult.IsSuccess)
        {
            var remapped = writeResult.Issues.Select(i => i with { Code = ErrorCode.E401 });
            return GraftResult<bool>.Fail(remapped);
        }

        Record(new JournalOp(BackupJournal.Stored, normalized.Value));
        return GraftResult<bool>.Ok(true);
    }

    /// <summary>
    /// manifest.json を最終状態（success または rolled_back）で確定させ、あわせて
    /// history.jsonl（仕様書13.1）へも1行追記する。history.jsonl はフォルダが後で外部から
    /// 削除・移動されても履歴一覧（summaryとstats）を参照できるようにするための補助索引であり、
    /// manifest.json が正本である。追記に失敗しても確定自体は成功として扱い、Warningで通知する。
    /// </summary>
    public async Task<GraftResult<bool>> CompleteAsync(RevisionManifest manifest, CancellationToken ct = default)
    {
        try
        {
            await _jsonStore.WriteAsync(_manifestPath, manifest, JsonFileStore.DefaultOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<bool>.Fail(ErrorCode.E401, $"履歴の記録ファイル（manifest.json）を確定できませんでした: {ExceptionMessages.Describe(ex)}", path: _manifestPath);
        }

        return await AppendToIndexAsync(manifest, ct).ConfigureAwait(false);
    }

    private async Task<GraftResult<bool>> AppendToIndexAsync(RevisionManifest manifest, CancellationToken ct)
    {
        var indexEntry = new RevisionIndexEntry
        {
            Revision = manifest.Revision,
            Summary = manifest.Summary,
            Type = manifest.Type,
            AppliedAt = manifest.AppliedAt,
            Status = manifest.Status,
            PatchHash = manifest.PatchHash,
            Stats = manifest.Stats,
            FolderName = Path.GetFileName(FolderPath),
        };

        try
        {
            await _revisionIndex.AppendAsync(_projectId, indexEntry, ct).ConfigureAwait(false);
            return GraftResult<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var warning = GraftIssue.Of(
                ErrorCode.E404, $"history.jsonl への追記に失敗しました: {ExceptionMessages.Describe(ex)}", path: _projectId, severity: Severity.Warning);
            return GraftResult<bool>.Ok(true, new[] { warning });
        }
    }

    /// <summary>
    /// 退避済みファイルを元の場所へ書き戻し、適用中に新規作成されたファイルを削除する。
    ///
    /// 【戻しきれたときは manifest.json を rolled_back で確定する】 BeginAsyncが書く
    /// status: in_progress は「処理中」の印で、確定（CompleteAsync）されないまま残ると
    /// 起動時の中断検出（RevisionStore.FindInProgressAsync）が「中断された適用」として拾い、
    /// 取り消しを勧めるダイアログを毎回出す。ところが適用の失敗→ここでのロールバックは
    /// 「失敗したが元に戻した」正常な終わり方で、中断ではない。以前はここで印を更新しなかったため、
    /// 失敗した適用（実機のMAI-Historyではr4・r5）が起動のたびに「未完了」として残り続け、
    /// OKを押すと後から成功した適用(r7・r8)の変更まで古いバックアップで上書きする危険があった。
    ///
    /// 戻しきれなかったとき（退避の読み取りや書き戻し、新規ファイルの削除が1件でも失敗）は
    /// 一部のファイルだけが書き換わったままの可能性があるため、in_progress のまま残して
    /// 起動時の検出で利用者に知らせる（これは本来の用途）。
    ///
    /// history.jsonl へは追記しない。失敗して元どおりになった適用は履歴に残すべき変更ではなく、
    /// 番号は projects.json の nextRevision で消費済みなので再利用の心配も無い（フォルダが
    /// 残るかぎりDetectMaxRevisionAsyncもフォルダから最大番号を拾う）。履歴ペインへは
    /// フォルダ（manifest.json）側から従来どおり一覧に出るため、内容は確認できる。
    /// </summary>
    public async Task<GraftResult<bool>> RollbackAsync(CancellationToken ct = default)
    {
        // 新規作成ファイルの削除 → 改名を戻す → 退避の書き戻し → 作ったフォルダの削除、の順で、
        // 新しい記録から古い記録へたどる（BackupJournal.RollbackAsync参照）。
        var issues = await BackupJournal.RollbackAsync(FolderPath, _projectRoot, _ops, ct).ConfigureAwait(false);

        if (issues.Any(i => i.Severity == Severity.Error))
        {
            return GraftResult<bool>.Fail(issues);
        }

        try
        {
            var rolledBack = _beganManifest with { Status = RevisionStatus.RolledBack };
            await _jsonStore.WriteAsync(_manifestPath, rolledBack, JsonFileStore.DefaultOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ファイルは戻せているので失敗にはしない。印だけが in_progress で残り、
            // 起動時の検出が（最新の場合に限り）知らせる。
            issues.Add(GraftIssue.Of(
                ErrorCode.E401, $"元に戻した印（manifest.json）を書けませんでした: {ExceptionMessages.Describe(ex)}",
                path: _manifestPath, severity: Severity.Warning));
        }

        return GraftResult<bool>.Ok(true, issues);
    }

    /// <summary>
    /// 適用中に新規作成するファイルを記録する（ロールバック時に削除するため）。
    /// 不正な相対パスは黙って無視する（呼び出し側は事前に検証済みである前提）。
    /// <b>ファイルを書く前</b>に呼ぶこと。記録を先にしておけば、書いた直後に落ちても起動時のロールバックが
    /// 削除できる。実在しないファイルの削除は何もしないので、書くのに失敗しても害は無い。
    /// </summary>
    public void TrackCreated(string relativePath)
    {
        var normalized = BackupPathUtil.NormalizeRelativePath(relativePath);
        if (normalized.IsSuccess)
        {
            Record(new JournalOp(BackupJournal.Created, normalized.Value));
        }
    }

    /// <summary>
    /// 改名（移動）が成功した直後に呼ぶ。ロールバックで移動先を移動元へ戻す。
    /// 不正な相対パスは黙って無視する。
    /// </summary>
    public void TrackRenamed(string fromRelativePath, string toRelativePath)
    {
        var from = BackupPathUtil.NormalizeRelativePath(fromRelativePath);
        var to = BackupPathUtil.NormalizeRelativePath(toRelativePath);
        if (from.IsSuccess && to.IsSuccess)
        {
            Record(new JournalOp(BackupJournal.Renamed, From: from.Value, To: to.Value));
        }
    }

    /// <summary>
    /// フォルダを作成する。この呼び出しで<b>新しく作られた</b>フォルダ（<paramref name="fullDirectoryPath"/>と、
    /// それまで無かった親フォルダ）だけを記録し、ロールバックで空のときに限り消す。もともとあったフォルダは
    /// 記録しない。プロジェクトルートの外側のパスや、相対パスにできないパスは作るだけで記録しない。
    /// </summary>
    public void CreateDirectoryTracked(string fullDirectoryPath)
    {
        // まだ無い祖先を、浅い方から深い方へ集める。
        var missing = new Stack<string>();
        for (var dir = fullDirectoryPath; !string.IsNullOrEmpty(dir) && !Directory.Exists(LongPath.Extended(dir));
             dir = Path.GetDirectoryName(dir))
        {
            missing.Push(dir);
        }

        Directory.CreateDirectory(LongPath.Extended(fullDirectoryPath));

        foreach (var dir in missing)
        {
            var relative = Path.GetRelativePath(_projectRoot, dir);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) continue;
            Record(new JournalOp(BackupJournal.Mkdir, relative));
        }
    }

    private void Record(JournalOp op)
    {
        _ops.Add(op);
        BackupJournal.TryAppend(FolderPath, op);
    }
}
