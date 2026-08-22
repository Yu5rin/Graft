using System.IO;
using System.Text;

namespace Graft.Core;

/// <summary>
/// 仕様書6章の適用エンジン。6.1の二段階実行のうち、ドライラン（<see cref="DryRunAsync"/>）は
/// <see cref="DryRunPlanner"/> に委譲する。本適用（<see cref="ApplyAsync"/>）はバックアップ取得
/// 後に書き込みを行い、manifest を確定する。
/// </summary>
public sealed partial class ApplyEngine
{
    private readonly BackupManager _backup;
    private readonly RevisionStore _revisions;
    // 課題1: マッチング設定（類似度しきい値・あいまい一致の可否・範囲警告行数）は設定画面の
    // 変更を実行中に反映できるよう、readonlyにせず差し替え可能にしておく
    // （UpdateMatchOptions参照）。BackupやSafety等、他の設定はApplyContext.Settings経由で
    // ドライラン・適用のたびに渡されるため差し替え不要（呼び出し元で都度最新値を積める）だが、
    // マッチングだけはMatchEngineインスタンスに固定で焼き込む設計のため、この対応が要る。
    private MatchEngine _matcher;
    private DryRunPlanner _planner;

    public ApplyEngine(BackupManager backup, RevisionStore revisions, MatchEngine matcher)
    {
        _backup = backup;
        _revisions = revisions;
        _matcher = matcher;
        _planner = new DryRunPlanner(matcher, revisions);
    }

    /// <summary>6.1 ドライラン。ファイルへは一切書き込まない。</summary>
    public Task<GraftResult<DryRunResult>> DryRunAsync(Patch patch, ApplyContext ctx, CancellationToken ct = default)
        => _planner.PlanAsync(patch, ctx, ct);

    /// <summary>
    /// 課題1: 設定画面でのマッチング設定変更を実行中のアプリへ反映する。<see cref="MatchEngine"/>は
    /// コンストラクタで受け取ったオプションをフィールドへ固定で保持する不変な設計のため、値を
    /// 差し替えるにはインスタンスごと作り直す必要がある。それに依存する<see cref="DryRunPlanner"/>
    /// も同様に作り直す（RevisionStoreは使い回す。ドライラン計画自体は状態を持たないため、
    /// 作り直しても進行中の処理には影響しない）。
    ///
    /// 呼び出し元の責務: 適用処理（ドライラン確定〜書き込み〜適用後フック）の実行中には
    /// 呼ばないこと。書き込み中（<see cref="ApplyFileGroupAsync"/>）はこのインスタンスを
    /// フィールド経由で直接参照するため、途中で差し替わると同一リビジョン内のファイルが
    /// 前半と後半で異なるしきい値で処理されてしまう。呼び出し元（MainViewModel）は
    /// 適用処理中の反映を保留する設計になっている前提のため、本メソッド自体は排他制御を持たない。
    /// </summary>
    public void UpdateMatchOptions(MatchOptions options)
    {
        _matcher = new MatchEngine(options);
        _planner = new DryRunPlanner(_matcher, _revisions);
    }

    /// <summary>6.1 本適用。バックアップ取得後に書き込み、manifest を確定する。</summary>
    public async Task<GraftResult<RevisionManifest>> ApplyAsync(DryRunResult plan, ApplyContext ctx, CancellationToken ct = default)
    {
        var dupIssues = await CheckDuplicateAsync(ctx, plan.PatchHash, ct).ConfigureAwait(false);
        if (dupIssues.HardBlock) return GraftResult<RevisionManifest>.Fail(dupIssues.Issues);

        // 実機不具合対応: 適用モードを問わず、実際に書き込むブロック（チェック済みかつ適用可能）が
        // 1件も無いなら、ここで必ず失敗として返す。これが無いと、部分適用モードでは
        // 「全ブロック失敗」や「チェックを全部外した」状態でもExecuteAsyncが素通りし、
        // 何も書き換えていないのに成功扱いの空のリビジョンが記録されてしまう
        // （MainViewModel.ApplyCoreAsync側にも同種のガードを置いているが、ApplyEngineは
        // UIを経由しない呼び出し元からも直接使われうるため、ここでも独立して防ぐ）。
        var eligiblePlans = plan.Plans.Where(p => p.IsSelected && p.CanApply).ToList();
        if (eligiblePlans.Count == 0)
        {
            var noneIssues = plan.Plans.Where(p => !p.CanApply).SelectMany(p => p.Issues).ToList();
            if (noneIssues.Count == 0)
            {
                noneIssues.Add(GraftIssue.Of(ErrorCode.E101, "チェックが付いている、適用可能な変更がありません"));
            }
            return GraftResult<RevisionManifest>.Fail(noneIssues);
        }

        if (ctx.Settings.ApplyMode != "partial")
        {
            // 「全件適用（All or Nothing）」: 選択状態を問わず、パッチ全体に1件でも適用できない
            // ブロックがあれば何も書き込まず中止する（取扱説明書6.4のとおり）。DryRunPlannerが
            // 失敗ブロックを自動的にIsSelected=falseへ倒し、UI側でも再選択できない
            // （BlockItemViewModel.CanToggle）ため、ここでIsSelectedも条件に加えてしまうと
            // 「選択されている失敗ブロック」は事実上存在しなくなり、allOrNothingが名前だけ残って
            // 何も中止しないモードになってしまう。「1件でも当てはまらなければ何も書き込まない」
            // という設定どおりの安全側の既定を守るのが目的のため、あえてIsSelectedは見ない。
            var fatal = plan.Plans.Where(p => !p.CanApply).SelectMany(p => p.Issues).ToList();
            if (fatal.Count > 0)
            {
                // 実機不具合対応: 以前はここでE101等の個別ブロックのエラーだけがそのまま
                // 利用者へ表示され、「全件適用の設定のせいで中止された」ことが伝わらなかった
                // （個々のブロックのエラーだけを見ると、そのブロック単体の問題に見えてしまう）。
                // E304を先頭に加え、設定が原因であることと対処法（部分適用可への切り替え）を
                // 明示する。個々のブロックのエラー（E101等）はfatalに含めたまま従来どおり併記する。
                var failedBlockCount = plan.Plans.Count(p => !p.CanApply);
                var blockDetail = string.Join(" / ", fatal.Take(3).Select(i => i.ToDisplayText()));
                if (fatal.Count > 3) blockDetail += $" ほか{fatal.Count - 3}件";
                var modeNotice = GraftIssue.Of(ErrorCode.E304,
                    detail: $"「全件適用」の設定のため、適用できない変更が{failedBlockCount}件あった時点で中止しました。" +
                        $"設定の「適用モード」で「部分適用可」に切り替えると、適用できる変更だけを書き込めます。（{blockDetail}）");
                return GraftResult<RevisionManifest>.Fail(new[] { modeNotice }.Concat(fatal).ToList());
            }
        }

        var initial = BuildInitialManifest(plan, ctx);
        var began = await _backup.BeginAsync(ctx.ProjectId, ctx.ProjectRoot, initial, ct).ConfigureAwait(false);
        if (!began.IsSuccess) return GraftResult<RevisionManifest>.Fail(began.Issues);
        var session = began.Value;

        var backedUp = await BackupTargetsAsync(session, plan.Plans, ctx, ct).ConfigureAwait(false);
        if (!backedUp.IsSuccess)
        {
            await session.RollbackAsync(ct).ConfigureAwait(false);
            return GraftResult<RevisionManifest>.Fail(backedUp.Issues);
        }

        var executed = await ExecuteAsync(plan.Plans, ctx, session, ct).ConfigureAwait(false);
        if (!executed.IsSuccess)
        {
            await session.RollbackAsync(ct).ConfigureAwait(false);
            return GraftResult<RevisionManifest>.Fail(executed.Issues);
        }

        var finalManifest = initial with { Status = RevisionStatus.Success, Entries = executed.Value };
        var completed = await session.CompleteAsync(finalManifest, ct).ConfigureAwait(false);
        if (!completed.IsSuccess) return GraftResult<RevisionManifest>.Fail(completed.Issues);

        // 不具合1対応: 7.4の世代管理（RevisionStore.EnforceRetentionAsync）は実装済みだったが
        // 呼び出し元が存在せず、設定画面の「最大保持リビジョン数」「バックアップ合計上限」が
        // 一切効いていなかった。リビジョンが確定した直後（=これ以上このリビジョンの実体を
        // 参照する処理が無くなった時点）に実行するのが最も安全なタイミングのため、ここで行う。
        // 失敗時の扱い: 適用そのものは直前のCompleteAsyncで既に確定済みであり、世代整理
        // （古いフォルダの削除）が失敗したからといって「適用が失敗した」と利用者に見せると
        // 実際には成功しているのに誤解を招く。そのためEnforceRetentionAsyncの失敗は
        // 適用結果をFailへ倒さず、Warningのissueとして合流させるだけにとどめる。
        // 通知の要否: 削除件数を適用のたびにダイアログで知らせると、通常運用時（上限超過は
        // 稀ではなく毎回発生しうる）はポップアップが頻発してうるさくなる。呼び出し元
        // （MainViewModel.ApplyAsync）は現状issuesを成功時ダイアログへ反映していないため、
        // ここではissuesに合流させるだけにとどめ、ログへ出すかどうかは呼び出し側の判断に委ねる。
        var retention = await _revisions.EnforceRetentionAsync(ctx.ProjectId, ctx.Settings.Backup, ct).ConfigureAwait(false);

        // ついでの修正: CompleteAsync自身が返すissues（history.jsonl追記失敗時のWarning等）が
        // これまで呼び出し元へ一切伝わっていなかった（このメソッドの戻り値に含めていなかった）
        // ため、あわせて合流させる。適用が成功したこと自体には影響しない付随情報。
        var mergedIssues = dupIssues.Issues.Concat(executed.Issues).Concat(completed.Issues).Concat(retention.Issues).ToList();
        return GraftResult<RevisionManifest>.Ok(finalManifest, mergedIssues);
    }

    // ------------------------------------------------------------------
    // 6.2 二重適用検知（ドライラン時の判定を、書き込み直前にも再確認する）
    // ------------------------------------------------------------------

    private async Task<(bool HardBlock, IReadOnlyList<GraftIssue> Issues)> CheckDuplicateAsync(
        ApplyContext ctx, string patchHash, CancellationToken ct)
    {
        var found = await _revisions.FindByPatchHashAsync(ctx.ProjectId, patchHash, ct).ConfigureAwait(false);
        if (!found.IsSuccess || found.Value is null) return (false, Array.Empty<GraftIssue>());

        var revisionNo = found.Value.Manifest.Revision;
        if (!ctx.ForceReapply)
        {
            var error = GraftIssue.Of(ErrorCode.E302, $"このパッチはr{revisionNo}で適用済みです");
            return (true, new[] { error });
        }

        var warning = GraftIssue.Of(ErrorCode.E302, $"このパッチはr{revisionNo}で適用済みですが、強制的に再適用します",
            severity: Severity.Warning);
        return (false, new[] { warning });
    }

    private static RevisionManifest BuildInitialManifest(DryRunResult plan, ApplyContext ctx) => new()
    {
        Revision = ctx.Revision,
        ProjectId = ctx.ProjectId,
        Summary = plan.Patch.Meta.Summary,
        Type = plan.Patch.Meta.Type,
        AppliedAt = DateTimeOffset.Now,
        PatchHash = plan.PatchHash,
        Status = RevisionStatus.InProgress,
        Stats = plan.Stats,
        Entries = Array.Empty<RevisionEntry>(),
    };

    // ------------------------------------------------------------------
    // バックアップ（部分適用モードでも対象ファイル全件を対象にする）
    // ------------------------------------------------------------------

    private static async Task<GraftResult<bool>> BackupTargetsAsync(
        BackupSession session, IReadOnlyList<BlockPlan> plans, ApplyContext ctx, CancellationToken ct)
    {
        foreach (var relativePath in CollectBackupTargets(plans))
        {
            var resolved = ctx.Guard.Resolve(relativePath);
            if (!resolved.IsSuccess) continue;
            if (!File.Exists(LongPath.Extended(resolved.Value))) continue;

            var stored = await session.StoreAsync(relativePath, ct).ConfigureAwait(false);
            if (!stored.IsSuccess)
                return GraftResult<bool>.Fail(ErrorCode.E401, "バックアップの取得に失敗しました", path: relativePath);
        }
        return GraftResult<bool>.Ok(true);
    }

    private static IReadOnlyList<string> CollectBackupTargets(IReadOnlyList<BlockPlan> plans)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in plans)
        {
            if (p.Operation == EntryOperation.Mkdir) continue;
            targets.Add(p.Block is RenameBlock rename ? rename.FromPath : p.Path);
        }
        return targets.ToList();
    }

    // ------------------------------------------------------------------
    // 実行（6.6の順序: MKDIR -> RENAME -> FULL/SR/APPEND/PREPEND -> DELETE）
    // ------------------------------------------------------------------

    private async Task<GraftResult<List<RevisionEntry>>> ExecuteAsync(
        IReadOnlyList<BlockPlan> plans, ApplyContext ctx, BackupSession session, CancellationToken ct)
    {
        var entries = new List<RevisionEntry>();
        var eligible = plans.Where(p => p.IsSelected && p.CanApply).ToList();

        var mkdirResult = ExecuteMkdirs(eligible, ctx, entries);
        if (!mkdirResult.IsSuccess) return GraftResult<List<RevisionEntry>>.Fail(mkdirResult.Issues);

        var renameResult = ExecuteRenames(eligible, ctx, entries);
        if (!renameResult.IsSuccess) return GraftResult<List<RevisionEntry>>.Fail(renameResult.Issues);

        var textResult = await ExecuteTextFilesAsync(eligible, ctx, session, entries, ct).ConfigureAwait(false);
        if (!textResult.IsSuccess) return GraftResult<List<RevisionEntry>>.Fail(textResult.Issues);

        var deleteResult = ExecuteDeletes(eligible, ctx, entries);
        if (!deleteResult.IsSuccess) return GraftResult<List<RevisionEntry>>.Fail(deleteResult.Issues);

        return GraftResult<List<RevisionEntry>>.Ok(entries, textResult.Issues);
    }

    private static GraftResult<bool> ExecuteMkdirs(List<BlockPlan> eligible, ApplyContext ctx, List<RevisionEntry> entries)
    {
        foreach (var p in eligible.Where(p => p.Operation == EntryOperation.Mkdir))
        {
            var resolved = ctx.Guard.ResolveDirectory(p.Path);
            if (!resolved.IsSuccess) return GraftResult<bool>.Fail(resolved.Issues);

            try
            {
                Directory.CreateDirectory(LongPath.Extended(resolved.Value));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return GraftResult<bool>.Fail(ErrorCode.E402, ExceptionMessages.Describe(ex), path: p.Path);
            }

            entries.Add(new RevisionEntry { Path = p.Path, Operation = EntryOperation.Mkdir, Desc = p.Description });
        }
        return GraftResult<bool>.Ok(true);
    }

    private static GraftResult<bool> ExecuteRenames(List<BlockPlan> eligible, ApplyContext ctx, List<RevisionEntry> entries)
    {
        foreach (var p in eligible.Where(p => p.Operation == EntryOperation.Rename))
        {
            if (p.Block is not RenameBlock rename) continue;
            var fromResolved = ctx.Guard.Resolve(rename.FromPath);
            var toResolved = ctx.Guard.Resolve(rename.ToPath);
            if (!fromResolved.IsSuccess) return GraftResult<bool>.Fail(fromResolved.Issues);
            if (!toResolved.IsSuccess) return GraftResult<bool>.Fail(toResolved.Issues);

            try
            {
                var toDir = Path.GetDirectoryName(toResolved.Value);
                if (!string.IsNullOrEmpty(toDir)) Directory.CreateDirectory(LongPath.Extended(toDir));
                File.Move(LongPath.Extended(fromResolved.Value), LongPath.Extended(toResolved.Value), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return GraftResult<bool>.Fail(ErrorCode.E402, ExceptionMessages.Describe(ex), path: p.Path);
            }

            entries.Add(new RevisionEntry
            {
                Path = rename.ToPath, Operation = EntryOperation.Rename, Desc = p.Description, RenamedFrom = rename.FromPath,
            });
        }
        return GraftResult<bool>.Ok(true);
    }

    // ------------------------------------------------------------------
    // FULL / SR / APPEND / PREPEND の書き込み
    // ------------------------------------------------------------------

    private async Task<GraftResult<bool>> ExecuteTextFilesAsync(
        List<BlockPlan> eligible, ApplyContext ctx, BackupSession session, List<RevisionEntry> entries, CancellationToken ct)
    {
        var groups = eligible
            .Where(p => p.Operation is EntryOperation.Modify or EntryOperation.Create)
            .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase);

        var writeIssues = new List<GraftIssue>();
        foreach (var group in groups)
        {
            var plansForFile = group.ToList();
            var written = await ApplyFileGroupAsync(group.Key, plansForFile, ctx, ct).ConfigureAwait(false);
            if (!written.IsSuccess) return GraftResult<bool>.Fail(written.Issues);

            // SafeFileWriterが検出した警告・情報（退避方式を使った／書き込み直後の検証で
            // やり直した等）は、以前は捨てられて呼び出し元へ一切伝わっていなかった。
            // ApplyAsyncの戻り値まで合流させ、ログや画面へ出せるようにする。
            writeIssues.AddRange(written.Issues);

            var (existedBefore, hashBefore, hashAfter) = written.Value;
            if (!existedBefore) session.TrackCreated(group.Key);

            var stage = plansForFile.Max(p => p.Stage);
            entries.Add(new RevisionEntry
            {
                Path = group.Key, Operation = existedBefore ? EntryOperation.Modify : EntryOperation.Create,
                Desc = plansForFile[0].Description, MatchStage = (int)stage, HashBefore = hashBefore, HashAfter = hashAfter,
            });
        }
        return GraftResult<bool>.Ok(true, writeIssues);
    }

    /// <summary>
    /// 選択されたブロックだけを対象に、現在のファイル内容を読み直したうえで再解決して書き込む。
    /// ドライラン時点のスナップショットではなく再解決する理由は、部分適用モードでユーザーが
    /// 一部ブロックの選択を外した場合にも正しい結果を書き込むため。
    /// </summary>
    private async Task<GraftResult<(bool ExistedBefore, string? HashBefore, string HashAfter)>> ApplyFileGroupAsync(
        string path, List<BlockPlan> plansForFile, ApplyContext ctx, CancellationToken ct)
    {
        var resolved = ctx.Guard.Resolve(path);
        if (!resolved.IsSuccess) return GraftResult<(bool, string?, string)>.Fail(resolved.Issues);
        var fullPath = resolved.Value;

        var existed = File.Exists(LongPath.Extended(fullPath));
        var shape = plansForFile[0].Shape ?? new TextShape { Encoding = new UTF8Encoding(false), NewLine = "\r\n", EndsWithNewLine = true };
        IReadOnlyList<string> originalLines = Array.Empty<string>();
        IReadOnlyList<(string Text, string Terminator)>? originalWithTerminators = null;
        string? hashBefore = null;

        if (existed)
        {
            var read = await FileTextIO.ReadAsync(fullPath, ct).ConfigureAwait(false);
            if (!read.IsSuccess) return GraftResult<(bool, string?, string)>.Fail(read.Issues);
            originalWithTerminators = SplitLinesWithTerminators(read.Value.Text);
            originalLines = originalWithTerminators.Select(l => l.Text).ToList();
            shape = read.Value.Shape;
            hashBefore = FileTextIO.ComputeHash(read.Value.Text);
        }
        else
        {
            // 4.5: FULL形式でファイルが存在しない場合は親フォルダごと作成する。
            // 同名のファイルが既にある等で作成できない場合、例外を投げず失敗として返す
            // （附録A: ユーザー操作起因の失敗はGraftResultで扱う）。
            var parentDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                try
                {
                    Directory.CreateDirectory(LongPath.Extended(parentDir));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    return GraftResult<(bool, string?, string)>.Fail(
                        ErrorCode.E402, $"親フォルダを作成できませんでした: {ExceptionMessages.Describe(ex)}", path: path);
                }
            }
        }

        var seen = new HashSet<PatchBlock>(ReferenceEqualityComparer.Instance);
        var blocks = plansForFile.Select(p => p.Block).Where(seen.Add).ToList();
        // SR形式は1ペア=1件のBlockPlanになる。同一ブロックの別ペアが選択されていても、このファイル
        // グループに含まれないペア（＝ユーザーが選択を外した成功ペア）は再解決の対象から除く。
        var includedPairs = new HashSet<SearchReplacePair>(
            plansForFile.Where(p => p.Pair is not null).Select(p => p.Pair!), ReferenceEqualityComparer.Instance);
        var resolution = BlockResolver.ResolveFile(originalLines, blocks, _matcher, includedPairs);
        var finalText = ComposeFinalText(resolution.FinalLines, originalWithTerminators, shape);

        var clearedReadOnly = ClearReadOnlyIfNeeded(fullPath, ctx);
        var written = await FileTextIO.WriteAsync(fullPath, finalText, shape, ct).ConfigureAwait(false);
        RestoreReadOnlyIfNeeded(fullPath, clearedReadOnly);
        if (!written.IsSuccess) return GraftResult<(bool, string?, string)>.Fail(written.Issues);

        // 実機不具合対応: hashAfterはメモリ上のfinalTextからではなく、書き込み直後にディスクを
        // 読み直した実測値から計算する。SafeFileWriterは既にバイト列レベルでの検証を済ませて
        // 「成功」を返しているが、manifest.jsonに記録するhashAfterは「本当にディスク上にある
        // 内容」を表すべきであり、メモリ上の値をそのまま信用しない。読み直しに失敗した場合
        // （検証をすり抜けたのちに何らかの理由で消えた等、極めて稀なケース）は書き込み自体を
        // 失敗として扱う。
        var verifyRead = await FileTextIO.ReadAsync(fullPath, ct).ConfigureAwait(false);
        if (!verifyRead.IsSuccess)
        {
            return GraftResult<(bool, string?, string)>.Fail(
                ErrorCode.E402, "書き込み後の確認読み込みに失敗しました。ファイルが見つからないか読み取れません", path: path);
        }

        return GraftResult<(bool, string?, string)>.Ok(
            (existed, hashBefore, FileTextIO.ComputeHash(verifyRead.Value.Text)), written.Issues);
    }
}
