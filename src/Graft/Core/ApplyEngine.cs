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

    /// <summary>
    /// テスト専用のフック（修正6の回帰テスト用）。非nullの場合、書き込み直前の <c>finalText</c>
    /// をこの関数の戻り値へ差し替える。既定はnullで、本番のふるまいには一切影響しない。
    ///
    /// 【なぜ必要か】 修正6（<see cref="VerifyWrittenContentMatchesPatch"/>）は「実際に書き込んだ
    /// 内容がパッチの指示と食い違っていないか」を検証するが、正常なGraftの内部処理では
    /// <c>finalText</c> は常にパッチの指示どおりに組み立てられるため、通常のパッチ適用では
    /// この不一致を意図的に発生させる経路が無い（修正3のE217も同様に、通常経路では
    /// 発生させられない）。「検証が実際に機能し、E217で中止したうえでロールバックが正しく
    /// 動くこと」を検証するには、書き込み直前の内容を意図的に壊す手段が要る。
    ///
    /// 【なぜ本番コードに分岐を増やさずに実現できるか】 <see cref="SafeFileWriter"/> の
    /// <c>IPrimaryReplaceOp</c>/<c>IMoveOp</c>（<see cref="SafeFileWriterTests"/>参照）と同じ
    /// 考え方で、判定ロジックそのものは一切変えず、「値の差し替え口」だけをinternalに公開する。
    /// <c>Graft.Core</c> は <c>tests/Graft.Tests</c> へソースごと取り込まれる構成のため
    /// （Graft.Tests.csproj参照）、internalのままテストから直接設定できる。
    /// </summary>
    internal Func<string, string>? DebugCorruptFinalTextForTests { get; set; }

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

        // 実機不具合対応（履歴の「Nファイル」が実際の変更件数と食い違う）:
        // BuildInitialManifestが載せるplan.Stats（DryRunPlanner.ComputeStats）は、ドライラン時点の
        // 全ブロックを対象にした見積もりであり、「適用できなかったブロック」「利用者がチェックを
        // 外したブロック」まで数に入れている。1ファイルだけ適用したリビジョンのmanifest.jsonが
        // "stats": { "files": 2 } なのにentriesは1件、履歴ペインは「2ファイル +1 -1」と表示する、
        // という食い違いが実機で確認された。履歴を後から見た利用者は「2ファイル変えた」と誤解し、
        // 復元の影響範囲の判断まで誤る。
        //
        // そこで確定時に、実際に書き込んだ結果から数え直す。
        // ・Files: 実際に記録されたentries（＝本当に書き換えた対象）の相異なるパス数。
        // ・Added/Removed: entriesは行数を持たないため、実行対象そのものであるeligiblePlans
        //   （IsSelected && CanApply。ExecuteAsyncが実行する集合と同一の条件）から数える。
        //   ここまで到達している時点でExecuteAsyncは成功しており（失敗時は上でロールバックして
        //   returnする）、eligiblePlansと実際に適用された内容は一致する。
        // ・EstimatedTokens/EstimatedSavedTokens はパッチ本文そのものの見積もりで、
        //   どのブロックを適用したかとは無関係のため、plan.Statsの値をそのまま引き継ぐ。
        var appliedStats = initial.Stats with
        {
            Files = executed.Value.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Added = eligiblePlans.Sum(p => p.Added),
            Removed = eligiblePlans.Sum(p => p.Removed),
        };
        var finalManifest = initial with { Status = RevisionStatus.Success, Entries = executed.Value, Stats = appliedStats };
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
            var written = await ApplyFileGroupAsync(group.Key, plansForFile, ctx, session, ct).ConfigureAwait(false);
            if (!written.IsSuccess) return GraftResult<bool>.Fail(written.Issues);

            // SafeFileWriterが検出した警告・情報（退避方式を使った／書き込み直後の検証で
            // やり直した等）は、以前は捨てられて呼び出し元へ一切伝わっていなかった。
            // ApplyAsyncの戻り値まで合流させ、ログや画面へ出せるようにする。
            writeIssues.AddRange(written.Issues);

            var (existedBefore, hashBefore, hashAfter) = written.Value;
            // 修正6: session.TrackCreated は ApplyFileGroupAsync 内で「実際にディスクへ書き込んだ
            // 直後」に呼ぶよう移した（このメソッドの旧実装ではここで呼んでいた）。理由は、
            // 書き込み後のバイト検証（VerifyWrittenContentMatchesPatch）に失敗してE217を返す
            // 経路がApplyFileGroupAsync内に増えたため。検証失敗時もファイルは既に物理的に
            // 作成済みであり、ここまで到達する前にreturnで抜けてしまうと、新規作成ファイルが
            // TrackCreatedされないままロールバック（BackupSession.RollbackAsync）を迎え、
            // 壊れた内容のファイルが削除されずディスクに残ってしまう。書き込み成否の直後という
            // 最も早いタイミングで記録することで、その後どの経路で失敗してもロールバックが
            // 確実にこのファイルを削除できるようにしている。

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
        string path, List<BlockPlan> plansForFile, ApplyContext ctx, BackupSession session, CancellationToken ct)
    {
        var resolved = ctx.Guard.Resolve(path);
        if (!resolved.IsSuccess) return GraftResult<(bool, string?, string)>.Fail(resolved.Issues);
        var fullPath = resolved.Value;

        var existed = File.Exists(LongPath.Extended(fullPath));
        var shape = plansForFile[0].Shape ?? new TextShape { Encoding = new UTF8Encoding(false), NewLine = "\r\n", EndsWithNewLine = true };
        IReadOnlyList<string> originalLines = Array.Empty<string>();
        IReadOnlyList<(string Text, string Terminator)>? originalWithTerminators = null;
        string? hashBefore = null;
        // 修正6: 書き込み後のバイト検証（VerifyWrittenContentMatchesPatch）で、SR形式が絡まない
        // ファイル（MODE=FULL・APPEND・PREPENDのみ）の期待値を組み立てる材料として使う
        // 「変更前の生テキスト」。既存のoriginalLines/originalWithTerminatorsは改行コードの
        // 復元用に行単位へ分解済みのため、それとは別に生の文字列のまま保持しておく。
        string? originalTextForVerify = null;

        if (existed)
        {
            var read = await FileTextIO.ReadAsync(fullPath, ct).ConfigureAwait(false);
            if (!read.IsSuccess) return GraftResult<(bool, string?, string)>.Fail(read.Issues);
            originalWithTerminators = SplitLinesWithTerminators(read.Value.Text);
            originalLines = originalWithTerminators.Select(l => l.Text).ToList();
            shape = read.Value.Shape;
            hashBefore = FileTextIO.ComputeHash(read.Value.Text);
            originalTextForVerify = read.Value.Text;
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
        // テスト専用フック（DebugCorruptFinalTextForTestsのコメント参照）。本番では常にnullのため
        // 素通りする。
        if (DebugCorruptFinalTextForTests is not null) finalText = DebugCorruptFinalTextForTests(finalText);

        var clearedReadOnly = ClearReadOnlyIfNeeded(fullPath, ctx);
        var written = await FileTextIO.WriteAsync(fullPath, finalText, shape, ct).ConfigureAwait(false);
        RestoreReadOnlyIfNeeded(fullPath, clearedReadOnly);
        if (!written.IsSuccess) return GraftResult<(bool, string?, string)>.Fail(written.Issues);

        // 修正6: TrackCreatedは「ディスクへの書き込みが成功した直後」に呼ぶ。この後の
        // バイト検証（VerifyWrittenContentMatchesPatch）で不一致を検出して失敗を返す経路が
        // あるが、その時点で既にファイルは物理的に作成済みのため、記録を後回しにすると
        // ロールバック（BackupSession.RollbackAsync）がこの新規作成ファイルを削除対象として
        // 認識できず、壊れた内容のファイルが削除されずに残ってしまう
        // （ExecuteTextFilesAsyncの呼び出し元コメントも参照）。
        if (!existed) session.TrackCreated(path);

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

        // 修正6: MODE=FULL/APPEND/PREPENDについても、SR形式のE217（MatchEngine.BuildResult）と
        // 同じ発想で「実際にディスクへ書かれた内容」を「パッチが指示した内容」と突き合わせる。
        // SafeFileWriter.ReplaceAsyncが既に持つ長さ検証・ハッシュ再計算は「渡された内容
        // （finalText）どおりに書けたか」の検証であり、その渡された内容自体がパッチの指示と
        // 一致しているかは検証していない（層が異なる）。ここではその後者を、finalTextや
        // BlockResolverの中間状態を経由せず、パッチのContent（生の指示）と読み戻した実ファイルの
        // 内容だけを突き合わせることで独立に検証する。
        if (!VerifyWrittenContentMatchesPatch(blocks, originalTextForVerify, verifyRead.Value.Text, shape))
        {
            return GraftResult<(bool, string?, string)>.Fail(ErrorCode.E217,
                "MODE=FULL/APPEND/PREPENDの書き込み内容がパッチの本文と一致しません", path: path);
        }

        return GraftResult<(bool, string?, string)>.Ok(
            (existed, hashBefore, FileTextIO.ComputeHash(verifyRead.Value.Text)), written.Issues);
    }

    // ------------------------------------------------------------------
    // 修正6: 書き込み後のバイト検証をFULL/APPEND/PREPENDへ広げる
    // ------------------------------------------------------------------

    /// <summary>
    /// 実機不具合対応（修正6）: SR形式の不変条件検証（<see cref="MatchEngine.BuildResult"/>の
    /// E217）はマッチ結果にのみ効き、MODE=FULL・APPEND・PREPENDの書き込み結果は検証されない
    /// ままだった。利用者から「MODE=FULLで新規作成したファイルが、インデントのある行だけ
    /// 行頭空白1個分減った状態でディスクに書かれていた」という実物の証拠が届いたが、
    /// 同じパッチ本文をGraft自身に通した再現実験では、パーサ出力・書き込み結果ともに
    /// パッチ本文とバイト単位で完全一致しており、原因はGraftの外（AIの出力やコピー経路）か、
    /// まだ辿れていない経路にある可能性が高い。原因の所在によらず、Graftが「パッチの指示」と
    /// 食い違う内容を黙って書き込んでしまう状態は避けたいため、書き込み直後に独立して検証する。
    ///
    /// 検証対象を「SR形式が混在しないファイル」「PREPEND/APPENDはそれぞれ0〜1個」に限定する
    /// 理由: SR形式はマッチ結果（どこに・どう当たったか）に依存するため、その結果を使わずに
    /// 期待値を組み立てることはMatchEngineの判定そのものを二重実装することになり、独立検証の
    /// 意味が薄れる（SR形式の不変条件はMatchEngine.BuildResult側のE217が別途担う）。
    /// PREPEND/APPENDが複数個ある場合の最終的な行順序はBlockResolver.ApplyBlockEditsInOrderの
    /// 並び替え規則（同一開始行は文書内で後方のブロックを先に適用）に依存し、単純な文字列連結
    /// として再現するのは複雑になりすぎるため対象外とする（実務上、1ファイルにAPPEND・PREPENDを
    /// 複数積む使い方は稀）。対象外の場合はtrue（=検証をパス）を返す。
    /// </summary>
    private static bool VerifyWrittenContentMatchesPatch(
        IReadOnlyList<PatchBlock> blocks, string? originalText, string actualDiskText, TextShape shape)
    {
        if (!TryBuildIndependentExpectedText(blocks, originalText, out var expectedRaw)) return true;

        // エンコーディングが表現できない文字（例: Shift-JISに存在しない文字）がある場合、
        // 既存のFileTextIO.WriteAsync（shape.Encoding.GetBytes）は例外を投げず、既定の
        // 置換フォールバックで書き込む（EncodingRoundTripTests参照）。この往復を期待値側にも
        // 同じエンコーディングで通しておかないと、エンコーディングの表現限界による正当な差異
        // まで「内容が壊れた」と誤検知し、正しく書けているファイルをロールバックしてしまう。
        var expectedRoundTripped = shape.Encoding.GetString(shape.Encoding.GetBytes(expectedRaw));
        var expected = CanonicalizeLines(expectedRoundTripped);
        var actual = CanonicalizeLines(actualDiskText);
        return expected == actual;
    }

    /// <summary>
    /// SR形式が混在しない場合に限り、パッチのContent（生の指示）だけから期待される全文を
    /// 独立に組み立てる。組み立てられない（対象外の）場合はfalseを返す。
    /// </summary>
    private static bool TryBuildIndependentExpectedText(
        IReadOnlyList<PatchBlock> blocks, string? originalText, out string expected)
    {
        expected = string.Empty;
        if (blocks.Any(b => b is SearchReplaceBlock)) return false;

        var fulls = blocks.OfType<FullContentBlock>().ToList();
        var prepends = blocks.OfType<PrependBlock>().ToList();
        var appends = blocks.OfType<AppendBlock>().ToList();
        if (fulls.Count == 0 && prepends.Count == 0 && appends.Count == 0) return false;
        if (prepends.Count > 1 || appends.Count > 1) return false;

        // BlockResolver.ResolveFileと同じく、MODE=FULLが複数あれば最後のものが勝つ
        // （foreachで順に baseLines を上書きしていくため）。
        var baseContent = fulls.Count > 0 ? fulls[^1].Content : originalText ?? string.Empty;

        var parts = new List<string>();
        if (prepends.Count == 1) parts.Add(CanonicalizeLines(prepends[0].Content));
        var baseCanon = CanonicalizeLines(baseContent);
        if (baseCanon.Length > 0) parts.Add(baseCanon);
        if (appends.Count == 1) parts.Add(CanonicalizeLines(appends[0].Content));

        expected = string.Join("\n", parts);
        return true;
    }

    /// <summary>
    /// 改行コード（CRLF/CR/LF）の違いと末尾改行の有無を正規化した、行ベースの比較用文字列を
    /// 返す。<see cref="TextNormalizer.SplitLines"/>は3種の改行いずれも境界として扱い、末尾の
    /// 改行1個分は「末尾に空行がある」とはみなさない（同メソッドの実装参照）ため、
    /// これで改行コードと末尾改行の有無の両方が吸収された比較ができる。
    /// </summary>
    private static string CanonicalizeLines(string text) => string.Join("\n", TextNormalizer.SplitLines(text));
}
