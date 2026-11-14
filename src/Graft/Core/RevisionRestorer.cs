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

        // 取り消し済み(rolled_back)のリビジョンを重ねて取り消さない。手元の一覧が古い（別の操作で既に取り消された）
        // 場合に備え、ディスク上の最新の状態も確かめる。
        var store = new RevisionStore(_paths);
        // 読むだけで書かない: RevisionStore.ReadAsyncは壊れた/形の違うmanifest.jsonを再生成して上書きするため、
        // 旧レイアウトで退避コピーがmanifest.jsonの位置にあるフォルダの退避を壊してしまう。
        if (ReadStatusFromDisk(revision.FolderPath) == RevisionStatus.RolledBack
            || revision.Manifest.Status == RevisionStatus.RolledBack)
        {
            return GraftResult<IReadOnlyList<string>>.Fail(
                ErrorCode.E219, $"r{revision.Manifest.Revision} は既に取り消し済みです", path: revision.FolderPath);
        }

        var warnings = await CheckHashesAsync(projectRoot, revision.FolderPath, revision.Manifest.Entries, ct).ConfigureAwait(false);
        if (warnings.Count > 0 && !force)
        {
            return GraftResult<IReadOnlyList<string>>.Fail(warnings);
        }

        // 適用が確定しないまま止まったリビジョン（in_progress）は、entries が空のことが多い。entries を
        // 順に戻すだけの下の経路では何も戻らず成功を返してしまうので、適用の途中経過の記録から戻す。
        if (revision.Manifest.Status == RevisionStatus.InProgress)
        {
            return await RestoreInterruptedAsync(projectId, projectRoot, revision, warnings, ct).ConfigureAwait(false);
        }

        var entriesResult = await UndoRevisionEntriesAsync(revision, projectRoot, ct).ConfigureAwait(false);
        var issues = warnings.Concat(entriesResult.Issues).ToList();
        if (issues.Any(i => i.Severity == Severity.Error))
        {
            return GraftResult<IReadOnlyList<string>>.Fail(issues);
        }

        // 取り消せたので、取り消し済みの印を付ける。以前は success のまま残り、同じパッチを貼り直すと
        // 「適用済み」(E302)で適用できず、「直前を取り消す」を続けると同じリビジョンをまた取り消そうとした。
        issues.AddRange(await MarkUndoneAsync(store, projectId, revision.Manifest, ct).ConfigureAwait(false));
        return GraftResult<IReadOnlyList<string>>.Ok(entriesResult.Value, issues);
    }

    /// <summary>
    /// 取り消したリビジョンを rolled_back にする。そのリビジョンが「ここまで戻す」で、それが取り消していた
    /// リビジョン（<see cref="RevisionManifest.RolledBackRevisions"/>）があれば、それらは再び有効になるので
    /// success へ戻す。印を付けられなくても、ファイルは既に戻せているので失敗にはせず Warning にとどめる
    /// （印だけが古いまま残る）。
    ///
    /// 戻す側の条件: いま rolled_back で、かつ戻す側のリビジョンより前に適用されたもの。番号だけで引くと、
    /// 履歴番号の重複を直した（<see cref="RevisionDuplicateRepairer"/>）あとで番号がずれた記録から、
    /// 無関係なリビジョンを success に戻しかねないため、適用日時でも確かめる。
    /// </summary>
    private static async Task<List<GraftIssue>> MarkUndoneAsync(
        RevisionStore store, string projectId, RevisionManifest undone, CancellationToken ct)
    {
        var issues = new List<GraftIssue>();

        var marked = await store.SetStatusAsync(projectId, undone.Revision, RevisionStatus.RolledBack, ct).ConfigureAwait(false);
        AddAsWarnings(marked, $"r{undone.Revision} に取り消し済みの印を付けられませんでした", issues);

        foreach (var number in undone.RolledBackRevisions ?? Array.Empty<int>())
        {
            var read = await store.ReadAsync(projectId, number, ct).ConfigureAwait(false);
            if (!read.IsSuccess || read.Value.Manifest.Status != RevisionStatus.RolledBack) continue;
            if (read.Value.Manifest.AppliedAt > undone.AppliedAt) continue;

            var revived = await store.SetStatusAsync(projectId, number, RevisionStatus.Success, ct).ConfigureAwait(false);
            AddAsWarnings(revived, $"r{number} を適用済みの状態へ戻す印を付けられませんでした", issues);
        }
        return issues;
    }

    private static string? ReadStatusFromDisk(string folder)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(LongPath.Extended(Path.Combine(folder, "manifest.json"))));
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("status", out var status)
                   && status.ValueKind == System.Text.Json.JsonValueKind.String
                ? status.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static void AddAsWarnings(GraftResult<bool> result, string message, List<GraftIssue> issues)
    {
        if (!result.IsSuccess)
        {
            issues.Add(GraftIssue.Of(ErrorCode.E401, $"{message}: {string.Join(" / ", result.Errors.Select(i => i.ToDisplayText()))}",
                severity: Severity.Warning));
        }
        else
        {
            issues.AddRange(result.Issues);
        }
    }

    /// <summary>
    /// 適用が途中で止まった（status が in_progress のまま残った）リビジョンを、元の状態へ戻す。
    ///
    /// 【戻す元】 優先順に、(1) バックアップフォルダの journal.jsonl（適用が退避・新規作成・改名・フォルダ作成を
    /// 1件ずつ書き残した記録。<see cref="BackupJournal"/>参照）、(2) 記録が無い古い版のフォルダで entries が
    /// ある場合（「ここまで戻す」が途中で失敗して in_progress で確定したもの）は entries、(3) どちらも無ければ
    /// フォルダに退避されているファイルを手がかりにそれを書き戻す。(3)は新しく作ったファイルが分からないので、
    /// その旨を Warning で伝える。
    ///
    /// 【なぜ rolled_back の印は戻せたときだけ付けるか】 以前は、何も戻していなくても成功を返し、呼び出し側が
    /// rolled_back の印を付けていた。以後は検出もされず、書き換わったファイルが残った。戻せなかったとき
    /// （退避の読み取りや書き戻し、削除に1件でも失敗）は失敗を返し、印は付けない（in_progress のまま残して
    /// 次回の起動時に再び知らせる）。
    /// </summary>
    private async Task<GraftResult<IReadOnlyList<string>>> RestoreInterruptedAsync(
        string projectId, string projectRoot, RevisionSummary revision, List<GraftIssue> warnings, CancellationToken ct)
    {
        var issues = new List<GraftIssue>(warnings);
        IReadOnlyList<string> restoredPaths;

        var journal = BackupJournal.TryRead(revision.FolderPath);
        if (journal is null && revision.Manifest.Entries.Count > 0)
        {
            var viaEntries = await UndoRevisionEntriesAsync(revision, projectRoot, ct).ConfigureAwait(false);
            issues.AddRange(viaEntries.Issues);
            if (viaEntries.IsSuccess is false) return GraftResult<IReadOnlyList<string>>.Fail(issues);
            restoredPaths = viaEntries.Value;
        }
        else
        {
            IReadOnlyList<JournalOp> ops;
            if (journal is not null)
            {
                ops = journal;
            }
            else
            {
                ops = BackupJournal.InferStoredFromFolder(revision.FolderPath);
                issues.Add(GraftIssue.Of(
                    ErrorCode.E405,
                    ops.Count == 0
                        ? "この適用には途中経過の記録が無く、退避されたファイルも見つかりませんでした。戻すものはありませんでした"
                        : "この適用には途中経過の記録が無い古い形式のため、退避されているファイルだけを書き戻しました。" +
                          "適用中に新しく作られたファイルがあっても削除していません",
                    path: revision.FolderPath, severity: Severity.Warning));
            }

            issues.AddRange(await BackupJournal.RollbackAsync(revision.FolderPath, projectRoot, ops, ct).ConfigureAwait(false));
            if (issues.Any(i => i.Severity == Severity.Error)) return GraftResult<IReadOnlyList<string>>.Fail(issues);

            restoredPaths = ops.Where(o => o.Op == BackupJournal.Stored && o.Path is not null).Select(o => o.Path!).Distinct().ToList();
        }

        var marked = await new RevisionStore(_paths)
            .SetStatusAsync(projectId, revision.Manifest.Revision, RevisionStatus.RolledBack, ct).ConfigureAwait(false);
        issues.AddRange(marked.IsSuccess
            ? marked.Issues
            : marked.Issues.Select(i => i with { Severity = Severity.Warning }));

        return GraftResult<IReadOnlyList<string>>.Ok(restoredPaths, issues);
    }

    /// <summary>
    /// 「ここまで戻す」の事前確認情報を組み立てる（実行前チェック専用。ファイルへは一切触れない）。
    /// <paramref name="targetRevision"/>より新しいリビジョンをすべて対象とする。
    /// </summary>
    public static RestoreThroughPreview BuildRestoreThroughPreview(
        IReadOnlyList<RevisionSummary> allRevisions, int targetRevision)
    {
        ArgumentNullException.ThrowIfNull(allRevisions);
        // 取り消し済み(rolled_back)のリビジョンは対象にしない。既に戻っているので、もう一度取り消すと、
        // 取り消したあとの変更を古い内容で上書きしてしまう。適用に失敗して戻したリビジョンも同じ
        // （entries が空で、取り消すものが無い）。
        var toUndo = allRevisions
            .Where(r => r.Manifest.Revision > targetRevision && r.Manifest.Status != RevisionStatus.RolledBack)
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
    ///
    /// 4番目の要件: 全ての取り消しが成功した（<c>failedAt</c>がnull）にもかかわらず、
    /// 結果としてentriesが1件も無い（＝どのファイルも実質的には変わらなかった。既にその状態
    /// だった場合）は、空のリビジョンとして記録せず<see cref="TryDeleteAbandonedSessionFolder"/>
    /// でバックアップフォルダごと破棄する。戻り値は成功（IsSuccess=true）のまま、
    /// <c>Value.Entries.Count == 0</c>で呼び出し側が「記録しなかった」ことを判別できるようにする。
    /// </summary>
    public async Task<GraftResult<RevisionManifest>> RestoreThroughAsync(
        string projectId, string projectRoot, int targetRevision, IReadOnlyList<RevisionSummary> revisionsToUndo,
        int newRevisionNumber, bool force, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(revisionsToUndo);
        // 呼び出し元が BuildRestoreThroughPreview を通さずに渡しても、取り消し済みは重ねて取り消さない。
        revisionsToUndo = revisionsToUndo.Where(r => r.Manifest.Status != RevisionStatus.RolledBack).ToList();
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
        var warnings = await CheckHashesAsync(projectRoot, newest.FolderPath, newest.Manifest.Entries, ct).ConfigureAwait(false);
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

        // 4番目の要件: 取り消し処理の結果、ファイルが1つも変わらなかった場合（既にその状態
        // だった場合）は空リビジョンとして記録しない。BeginAsyncが既に作成済みのバックアップ
        // フォルダ（status: in_progress）をそのまま残すと、次回起動時の6.3中断検出
        // （StartupCoordinator）に「未完了の適用」として引っかかってしまうため、
        // CompleteAsyncで確定させず、フォルダごと削除して安全に中断する
        // （history.jsonlへはまだ何も書いていないため、フォルダを消せば記録は残らない）。
        if (failedAt is null && entries.Count == 0)
        {
            TryDeleteAbandonedSessionFolder(session.FolderPath);
            var noopManifest = initial with { Status = RevisionStatus.Success, Entries = entries, Stats = initial.Stats with { Files = 0 } };
            return GraftResult<RevisionManifest>.Ok(noopManifest, issues);
        }

        var status = failedAt is null ? RevisionStatus.Success : RevisionStatus.InProgress;
        var finalManifest = initial with
        {
            Status = status, Entries = entries, Stats = initial.Stats with { Files = entries.Count },
            // 全部取り消せたときだけ、取り消したリビジョンを記録する（このリビジョンを取り消すと戻せるように。
            // RevisionManifest.RolledBackRevisions参照）。途中で失敗した（in_progress）ときは、取り消しの
            // 範囲が確定しないので記録しない。
            RolledBackRevisions = failedAt is null ? revisionsToUndo.Select(r => r.Manifest.Revision).ToList() : null,
        };
        var completed = await session.CompleteAsync(finalManifest, ct).ConfigureAwait(false);
        issues.AddRange(completed.Issues);

        if (failedAt is null)
        {
            // 取り消したリビジョンを rolled_back にする。「ここまで戻す」は新しいリビジョンとして記録する設計で、
            // 戻しすぎたときはそれを取り消して元へ戻せる。そのとき上で記録した番号から success へ戻す。
            // 以前は印を付けなかったため、取り消したリビジョンと同じパッチを貼り直しても「適用済み」で
            // 適用できなかった。
            var store = new RevisionStore(_paths);
            foreach (var undoneRevision in revisionsToUndo)
            {
                var marked = await store
                    .SetStatusAsync(projectId, undoneRevision.Manifest.Revision, RevisionStatus.RolledBack, ct)
                    .ConfigureAwait(false);
                AddAsWarnings(marked, $"r{undoneRevision.Manifest.Revision} に取り消し済みの印を付けられませんでした", issues);
            }
        }

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

    /// <summary>
    /// 4番目の要件: 実質的な変更が無かった「ここまで戻す」操作で、BeginAsyncが既に作成した
    /// バックアップフォルダ（status: in_progressのmanifest.json含む）を破棄する。
    /// 削除に失敗しても致命的扱いにはしない（次回起動時の6.3中断検出に引っかかるだけの
    /// ベストエフォートな後始末のため）。
    /// </summary>
    private static void TryDeleteAbandonedSessionFolder(string folderPath)
    {
        try
        {
            var ioPath = LongPath.Extended(folderPath);
            if (Directory.Exists(ioPath))
            {
                Directory.Delete(ioPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 削除できなくても、次回起動時の6.3中断検出でロールバック提案の対象になるだけで
            // 実害は無いため、ここで例外を上位へ伝播させる必要は無い。
        }
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
            // RENAMEの移動先を同じパッチで編集したmodifyは、移動先をそのまま元の内容へ書き戻してはならない
            // （移動先のバックアップは存在せず、元の内容は移動元のバックアップにある）。移動元への復元と
            // 移動先の削除は同じパッチのrenameのentryが行うので、こちらは何もしない。
            if (entry.Operation == EntryOperation.Modify
                && RevisionEntryLinks.ResolveRenamedFrom(revision.Manifest.Entries, entry) is not null)
            {
                continue;
            }

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
                // 移動元は、renameのentryだけでなく、移動先を同じパッチで編集したentryにも持たせている
                // （古いmanifestでは持たないので補う。RevisionEntryLinks参照）。
                var renamedFrom = RevisionEntryLinks.ResolveRenamedFrom(revision.Manifest.Entries, entry);
                if (!string.IsNullOrEmpty(renamedFrom))
                {
                    set.Add(renamedFrom);
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
            // ここで参照するbackupFolderは、この「ここまで戻す」操作自身のためにBeginAsyncが
            // 直前に新規作成し、session.StoreAsyncで退避を終えたばかりのフォルダである
            // （呼び出し元RestoreThroughAsync参照）。旧レイアウトのバックアップを読むことは
            // 無いため、後退（ResolveBackupFilePathForRead）は使わず新レイアウトのパスを
            // 直接組み立ててよい。
            string? hashBefore = before
                ? await ReadHashIfExistsAsync(BackupPathUtil.GetBackupFilePathForWrite(backupFolder, normalized.Value), ct).ConfigureAwait(false)
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

    /// <summary>
    /// 取り消しの前に、適用後に利用者が手を入れたファイルを探して E301（Warning）にする。
    /// 取り消しはバックアップの内容でファイルを書き戻す／削除するので、適用後の変更は黙って失われる。
    ///
    /// 【RENAME・DELETE も見る理由】 以前は <c>HashAfter</c> を持つ entry（modify・create）しか見なかった。
    /// そのため、RENAME だけのリビジョンの後に移動先を手で編集して取り消すと、警告なしで移動先が削除され、
    /// DELETE のリビジョンの後に同じパスを別の内容で作り直して取り消すと、警告なしで古い内容に
    /// 上書きされた。次を確かめる。
    /// ・RENAME: 移動先が適用直後の内容のままか（適用時に記録した <c>HashAfter</c>。古い manifest には
    ///   無いので、移動先を編集した modify の entry が無ければ、移動元のバックアップと中身を比べる）。
    ///   移動元のパスに適用後ファイルが作られていないか（取り消しは移動元へ書き戻すため上書きになる）。
    /// ・DELETE: 同じパスにファイルが作られていないか（取り消しで上書きになる）。削除前の内容と
    ///   同じなら失われるものが無いので警告しない。
    /// どちらも、退避した内容と比べる経路があるので、古い形式の manifest でも分かる範囲で警告できる。
    /// 同じファイルについて複数の entry が同じ警告を出す（renameとmodifyの両方）ときは1件にまとめる。
    /// </summary>
    private static async Task<List<GraftIssue>> CheckHashesAsync(
        string projectRoot, string revisionFolder, IReadOnlyList<RevisionEntry> entries, CancellationToken ct)
    {
        var issues = new List<GraftIssue>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Warn(string path, string message)
        {
            if (reported.Add($"{path}\n{message}"))
            {
                issues.Add(GraftIssue.Of(ErrorCode.E301, message, path: path, severity: Severity.Warning));
            }
        }

        foreach (var entry in entries)
        {
            var normalized = BackupPathUtil.NormalizeRelativePath(entry.Path);
            if (!normalized.IsSuccess) continue; // パス不正は復元処理側の書き込みで検出する

            var full = Path.GetFullPath(Path.Combine(projectRoot, normalized.Value));
            var exists = File.Exists(LongPath.Extended(full));

            if (entry.Operation == EntryOperation.Delete)
            {
                if (exists && !await SameAsBackupAsync(revisionFolder, normalized.Value, full, ct).ConfigureAwait(false))
                {
                    Warn(entry.Path, "削除したあとに、同じパスへ別の内容のファイルが作られています");
                }
                continue;
            }

            if (entry.Operation == EntryOperation.Rename)
            {
                await CheckRenameEntryAsync(projectRoot, revisionFolder, entries, entry, full, exists, Warn, ct).ConfigureAwait(false);
                continue;
            }

            if (string.IsNullOrEmpty(entry.HashAfter)) continue;

            if (!exists)
            {
                Warn(entry.Path, "適用後にファイルが削除されています");
                continue;
            }

            var readResult = await FileTextIO.ReadAsync(full, ct).ConfigureAwait(false);
            if (!readResult.IsSuccess) continue;

            var currentHash = FileTextIO.ComputeHash(readResult.Value.Text);
            if (!string.Equals(currentHash, entry.HashAfter, StringComparison.OrdinalIgnoreCase))
            {
                Warn(entry.Path, "適用後にさらに変更されています");
            }
        }
        return issues;
    }

    private static async Task CheckRenameEntryAsync(
        string projectRoot, string revisionFolder, IReadOnlyList<RevisionEntry> entries, RevisionEntry entry,
        string destFull, bool destExists, Action<string, string> warn, CancellationToken ct)
    {
        // 移動元のパスに、適用後にファイルが作られていないか。取り消しはそこへバックアップを書き戻す。
        // 大文字小文字だけが違う改名（a.txt -> A.txt）は同じファイルを指しうるので対象外にする。
        if (!string.IsNullOrEmpty(entry.RenamedFrom)
            && !string.Equals(entry.RenamedFrom, entry.Path, StringComparison.OrdinalIgnoreCase)
            && BackupPathUtil.NormalizeRelativePath(entry.RenamedFrom) is { IsSuccess: true } fromNormalized)
        {
            var fromFull = Path.GetFullPath(Path.Combine(projectRoot, fromNormalized.Value));
            if (File.Exists(LongPath.Extended(fromFull)))
            {
                warn(entry.RenamedFrom, "改名したあとに、改名前のパスへ別のファイルが作られています");
            }
        }

        if (!destExists)
        {
            warn(entry.Path, "適用後にファイルが削除されています");
            return;
        }

        if (!string.IsNullOrEmpty(entry.HashAfter))
        {
            var readResult = await FileTextIO.ReadAsync(destFull, ct).ConfigureAwait(false);
            if (readResult.IsSuccess
                && !string.Equals(FileTextIO.ComputeHash(readResult.Value.Text), entry.HashAfter, StringComparison.OrdinalIgnoreCase))
            {
                warn(entry.Path, "適用後にさらに変更されています");
            }
            return;
        }

        // 古い形式のmanifest（renameにHashAfterが無い）。移動先を編集したmodifyがHashAfterを持っていれば
        // そちらの照合に任せる。無ければ、改名しただけのはずなので、移動元のバックアップと中身を比べる。
        var coveredByModify = entries.Any(e =>
            !ReferenceEquals(e, entry) && e.Operation != EntryOperation.Rename && !string.IsNullOrEmpty(e.HashAfter)
            && string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
        if (coveredByModify || string.IsNullOrEmpty(entry.RenamedFrom)) return;

        if (BackupPathUtil.NormalizeRelativePath(entry.RenamedFrom) is { IsSuccess: true } backupPath
            && !await SameAsBackupAsync(revisionFolder, backupPath.Value, destFull, ct).ConfigureAwait(false))
        {
            warn(entry.Path, "適用後にさらに変更されています");
        }
    }

    /// <summary>
    /// 現在のファイルが、バックアップ（適用前に退避した内容）とバイト単位で同じか。バックアップを
    /// 読めないときは、失われるものを確かめられないので「同じではない」（警告する）側に倒す。
    /// </summary>
    private static async Task<bool> SameAsBackupAsync(
        string revisionFolder, string normalizedBackupRelative, string currentFull, CancellationToken ct)
    {
        var backup = await BackupPathUtil.ReadBackupFileAsync(revisionFolder, normalizedBackupRelative, ct).ConfigureAwait(false);
        if (!backup.IsSuccess) return false;
        try
        {
            var current = await File.ReadAllBytesAsync(LongPath.Extended(currentFull), ct).ConfigureAwait(false);
            return current.AsSpan().SequenceEqual(backup.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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

        // 新旧レイアウトの解決・存在確認・旧レイアウト特有の破損検出（E216）まで含めて
        // BackupPathUtil.ReadBackupFileAsyncへ委譲する（BackupSession.RestoreOneAsyncと共通）。
        var readResult = await BackupPathUtil.ReadBackupFileAsync(backupFolder, normalized.Value, ct).ConfigureAwait(false);
        if (!readResult.IsSuccess)
        {
            return GraftResult<string?>.Fail(readResult.Issues);
        }

        var targetFull = Path.GetFullPath(Path.Combine(projectRoot, normalized.Value));
        var writeResult = await SafeFileWriter.ReplaceAsync(targetFull, readResult.Value, ct).ConfigureAwait(false);
        return writeResult.IsSuccess
            ? GraftResult<string?>.Ok(relativePath, writeResult.Issues)
            : GraftResult<string?>.Fail(writeResult.Issues);
    }
}
