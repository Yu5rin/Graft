using System.Text;

namespace Graft.Core;

/// <summary>
/// 仕様書6.1のドライランを計画する。ファイルへは一切書き込まない。
/// 6.6（ブロックの適用順序）・5.3（同一ファイル内の適用順序）・6.2（二重適用検知）・
/// 6.4（ロック・読み取り専用検出）・13章（安全機構）を担当する。
/// </summary>
public sealed class DryRunPlanner
{
    private readonly MatchEngine _matcher;
    private readonly RevisionStore _revisions;

    public DryRunPlanner(MatchEngine matcher, RevisionStore revisions)
    {
        _matcher = matcher;
        _revisions = revisions;
    }

    /// <summary>パッチ全体のドライラン計画を作成する。</summary>
    public async Task<GraftResult<DryRunResult>> PlanAsync(Patch patch, ApplyContext ctx, CancellationToken ct)
    {
        var patchHash = RevisionStore.ComputePatchHash(patch.RawText);
        var renamedFrom = CollectRenamedFromPaths(patch);
        var renameSourceFor = patch.Blocks.OfType<RenameBlock>()
            .ToDictionary(r => NormalizeKey(r.ToPath), r => r.FromPath, StringComparer.OrdinalIgnoreCase);

        // 依頼4対応（診断ログ用）: ドライラン中に実際に存在確認・読み取りを行った対象ファイルを
        // 記録する。MainViewModel側がドライラン完了後にLoggerへ1ファイル1行で書き出す。
        // ドライランのときだけ集める（適用時やUI再描画のたびには集めない）ことで、
        // 過剰なログにならないようにする。
        var fileProbes = new List<DryRunFileProbe>();

        var plans = new List<BlockPlan>();
        plans.AddRange(patch.Blocks.OfType<MkdirBlock>().Select(b => PlanMkdir(b, ctx)));
        plans.AddRange(patch.Blocks.OfType<RenameBlock>().Select(b => PlanRename(b, ctx, fileProbes)));

        var textGroups = patch.Blocks
            .Where(b => b is FullContentBlock or SearchReplaceBlock or AppendBlock or PrependBlock)
            .GroupBy(b => b.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var group in textGroups)
        {
            var units = await PlanFileTextBlocksAsync(group.Key, group.ToList(), ctx, renamedFrom, renameSourceFor, fileProbes, ct)
                .ConfigureAwait(false);
            plans.AddRange(units);
        }

        foreach (var block in patch.Blocks.OfType<DeleteBlock>())
        {
            plans.Add(await PlanDeleteAsync(block, ctx, renamedFrom, fileProbes, ct).ConfigureAwait(false));
        }

        // E302は結果レベルのissues、E305は該当プランのIssuesへ付く（理由はCheckDuplicateAsync参照）。
        // そのためplansを渡し、E305の付与でplansの要素が差し替わる。ComputeStatsより前に呼ぶこと。
        var (dupIssues, alreadyApplied) = await CheckDuplicateAsync(ctx, patchHash, plans, ct).ConfigureAwait(false);
        var stats = ComputeStats(patch, plans, ctx);
        var result = new DryRunResult
        {
            Patch = patch, Plans = plans, PatchHash = patchHash, Stats = stats, FileProbes = fileProbes,
            AlreadyAppliedRevision = alreadyApplied,
        };
        return GraftResult<DryRunResult>.Ok(result, dupIssues);
    }

    // ------------------------------------------------------------------
    // MKDIR / RENAME（テキスト変換を伴わない単純な操作）
    // ------------------------------------------------------------------

    private static BlockPlan PlanMkdir(MkdirBlock block, ApplyContext ctx)
    {
        var resolved = ctx.Guard.ResolveDirectory(block.Path);
        return new BlockPlan
        {
            Block = block, Path = block.Path, Operation = EntryOperation.Mkdir, Stage = MatchStage.None,
            CanApply = resolved.IsSuccess, NeedsConfirmation = false, IsSelected = resolved.IsSuccess,
            Issues = resolved.Issues, Description = block.Description,
        };
    }

    private static BlockPlan PlanRename(RenameBlock block, ApplyContext ctx, List<DryRunFileProbe> probes)
    {
        var issues = new List<GraftIssue>();
        var fromCheck = ctx.Guard.Inspect(block.FromPath);
        if (!fromCheck.IsSuccess)
        {
            issues.AddRange(fromCheck.Issues);
        }
        else
        {
            issues.AddRange(UpgradeReadOnlyIfBlocking(fromCheck.Issues, fromCheck.Value, ctx));
            if (!fromCheck.Value.Exists)
                issues.Add(GraftIssue.Of(ErrorCode.E201, "移動元のファイルが存在しません", path: block.FromPath));

            probes.Add(new DryRunFileProbe
            {
                Path = block.FromPath, FullPath = fromCheck.Value.FullPath, Exists = fromCheck.Value.Exists,
                SizeBytes = fromCheck.Value.Exists ? fromCheck.Value.SizeBytes : null,
            });
        }

        var toResolved = ctx.Guard.Resolve(block.ToPath);
        if (!toResolved.IsSuccess) issues.AddRange(toResolved.Issues);

        var canApply = issues.All(i => i.Severity != Severity.Error);
        return new BlockPlan
        {
            Block = block, Path = block.ToPath, Operation = EntryOperation.Rename, Stage = MatchStage.None,
            CanApply = canApply, NeedsConfirmation = false, IsSelected = canApply,
            Issues = issues, Description = block.Description,
        };
    }

    // ------------------------------------------------------------------
    // FULL / SR / APPEND / PREPEND（ファイル単位でまとめて解決する）
    // ------------------------------------------------------------------

    private async Task<IReadOnlyList<BlockPlan>> PlanFileTextBlocksAsync(
        string path, IReadOnlyList<PatchBlock> blocksForFile, ApplyContext ctx,
        HashSet<string> renamedFrom, IReadOnlyDictionary<string, string> renameSourceFor,
        List<DryRunFileProbe> probes, CancellationToken ct)
    {
        if (renamedFrom.Contains(NormalizeKey(path)))
        {
            var issue = GraftIssue.Of(ErrorCode.E207, "リネームされた旧パスを参照しています", path: path);
            return FailPlansForFile(blocksForFile, path, new[] { issue });
        }

        var targetResolved = ctx.Guard.Resolve(path);
        if (!targetResolved.IsSuccess) return FailPlansForFile(blocksForFile, path, targetResolved.Issues);

        // 6.6: パッチ内でリネームされた先のパスは、リネーム済みの状態として旧パスの内容を読む。
        var readPath = renameSourceFor.TryGetValue(NormalizeKey(path), out var src) ? src : path;
        var inspect = ctx.Guard.Inspect(readPath);
        if (!inspect.IsSuccess) return FailPlansForFile(blocksForFile, path, inspect.Issues);

        var check = inspect.Value;

        // 実機不具合対応: ファイルが存在しない（または読み取れない）のに、SEARCH/REPLACEの
        // 照合結果として「SEARCH部が見つからない（E101）」と表示されるのは誤解を招く
        // （「ファイルは読めたが中身が一致しない」という意味に読めてしまう）。ここで
        // ファイルの有無を先に確認し、無ければE210で明確に報告する。
        // ただしFULL形式が同じファイルに含まれる場合は対象外にする。FULLは新規作成が正規の
        // 用途で（EntryOperation.Create）、BlockResolver.ResolveFileはFULLを先に適用した
        // 「これから書き込む内容」に対してSEARCH/REPLACEを解決する（E208混在警告と同じ経路）。
        // つまりファイルが未作成でもSEARCH/REPLACEが正しくマッチしうる正規のケースであり、
        // これをE210で止めてしまうと既存の「FULL/SR混在」機能を壊すため除外する。
        if (!check.Exists
            && blocksForFile.Any(b => b is SearchReplaceBlock)
            && !blocksForFile.Any(b => b is FullContentBlock))
        {
            // 依頼4対応: 読み取りに進まず打ち切る場合も、確認した内容（存在しなかったこと）を
            // 診断ログ用に記録しておく。
            probes.Add(new DryRunFileProbe { Path = readPath, FullPath = check.FullPath, Exists = false });

            // 依頼2対応: Graftが実際に存在確認を行った絶対パス（check.FullPath）を必ず
            // メッセージへ含める。利用者がログを掘らなくても、画面を見た瞬間に
            // 「Graftが見に行った場所が正しいか」を判断できるようにするため。
            var notFoundIssue = GraftIssue.Of(ErrorCode.E210,
                $"確認した絶対パス: {check.FullPath}", path: path);
            return FailPlansForFile(blocksForFile, path, new[] { notFoundIssue });
        }

        var fileIssues = UpgradeReadOnlyIfBlocking(inspect.Issues, check, ctx).ToList();
        if (blocksForFile.Any(b => b is FullContentBlock) && blocksForFile.Any(b => b is SearchReplaceBlock))
        {
            // 6.6・13章: 同一ファイルにFULL形式とSR形式が混在する場合は警告する
            // （実際の適用順序はBlockResolver.ResolveFileがFULLを先に解決する）。
            fileIssues.Add(GraftIssue.Of(ErrorCode.E208, path: path, severity: Severity.Warning));
        }

        var loaded = await LoadCurrentLinesAsync(check, ctx, ct).ConfigureAwait(false);

        // 依頼4対応: 「解決した絶対パス」「存在するか」「読み取った行数」を1ファイル1行で記録する。
        // 読み取りに失敗した場合（E204等）は行数が取れないためnullのままにする。
        probes.Add(new DryRunFileProbe
        {
            Path = readPath,
            FullPath = check.FullPath,
            Exists = check.Exists,
            SizeBytes = check.Exists ? check.SizeBytes : null,
            LineCount = loaded.IsSuccess ? loaded.Value.Lines.Count : null,
        });

        if (!loaded.IsSuccess) return FailPlansForFile(blocksForFile, path, loaded.Issues);

        var (originalLines, shape) = loaded.Value;
        var resolution = BlockResolver.ResolveFile(originalLines, blocksForFile, _matcher);
        return resolution.Units.Select(u => BuildBlockPlan(u, path, check.Exists, shape, ctx, fileIssues)).ToList();
    }

    private static async Task<GraftResult<(IReadOnlyList<string> Lines, TextShape Shape)>> LoadCurrentLinesAsync(
        FileCheck check, ApplyContext ctx, CancellationToken ct)
    {
        if (!check.Exists)
            return GraftResult<(IReadOnlyList<string>, TextShape)>.Ok((Array.Empty<string>(), DefaultShapeFor(ctx)));

        var read = await FileTextIO.ReadAsync(check.FullPath, ct).ConfigureAwait(false);
        if (!read.IsSuccess) return GraftResult<(IReadOnlyList<string>, TextShape)>.Fail(read.Issues);

        return GraftResult<(IReadOnlyList<string>, TextShape)>.Ok(
            (TextNormalizer.SplitLines(read.Value.Text), read.Value.Shape));
    }

    private static BlockPlan BuildBlockPlan(ChangeUnitResult unit, string path, bool fileExisted,
        TextShape shape, ApplyContext ctx, IReadOnlyList<GraftIssue> fileIssues)
    {
        var blockingReadOnly = fileIssues.Any(i => i.Severity == Severity.Error);
        var canApply = unit.CanApply && !blockingReadOnly;
        var diff = canApply ? DiffBuilder.Build(path, unit.BeforeText, unit.AfterText, ctx.Settings.Diff.ContextLines) : null;
        var operation = !fileExisted && canApply ? EntryOperation.Create : EntryOperation.Modify;
        var issues = unit.CanApply ? MergeIssues(fileIssues, unit.Issues) : unit.Issues;

        return new BlockPlan
        {
            Block = unit.SourceBlock, Pair = unit.SourcePair, Path = path, Operation = operation, Stage = unit.Stage,
            CanApply = canApply, NeedsConfirmation = unit.NeedsConfirmation, IsSelected = canApply,
            Issues = issues, BeforeText = unit.BeforeText, AfterText = unit.AfterText, Shape = shape,
            Diff = diff, Description = unit.Description, Added = diff?.Added ?? 0, Removed = diff?.Removed ?? 0,
            IndentCorrectionChars = unit.IndentCorrectionChars,
        };
    }

    // ------------------------------------------------------------------
    // DELETE
    // ------------------------------------------------------------------

    private async Task<BlockPlan> PlanDeleteAsync(
        DeleteBlock block, ApplyContext ctx, HashSet<string> renamedFrom, List<DryRunFileProbe> probes, CancellationToken ct)
    {
        if (renamedFrom.Contains(NormalizeKey(block.Path)))
        {
            var issue = GraftIssue.Of(ErrorCode.E207, "リネームされた旧パスを参照しています", path: block.Path);
            return FailedDeletePlan(block, new[] { issue });
        }

        var inspect = ctx.Guard.Inspect(block.Path);
        if (!inspect.IsSuccess) return FailedDeletePlan(block, inspect.Issues);

        var check = inspect.Value;
        var issues = UpgradeReadOnlyIfBlocking(inspect.Issues, check, ctx).ToList();
        string? beforeText = null;
        if (check.Exists)
        {
            var read = await FileTextIO.ReadAsync(check.FullPath, ct).ConfigureAwait(false);
            // 依頼4対応: 読み取り成否に関わらず1件記録する。読み取りに失敗した場合は行数が
            // 取れないためnullのままにする。
            probes.Add(new DryRunFileProbe
            {
                Path = block.Path, FullPath = check.FullPath, Exists = true, SizeBytes = check.SizeBytes,
                LineCount = read.IsSuccess ? TextNormalizer.SplitLines(read.Value.Text).Count : null,
            });
            if (!read.IsSuccess) return FailedDeletePlan(block, read.Issues);
            beforeText = read.Value.Text;
        }
        else
        {
            probes.Add(new DryRunFileProbe { Path = block.Path, FullPath = check.FullPath, Exists = false });
        }

        var canApply = check.Exists && issues.All(i => i.Severity != Severity.Error);
        var diff = canApply ? DiffBuilder.Build(block.Path, beforeText, null, ctx.Settings.Diff.ContextLines) : null;
        return new BlockPlan
        {
            Block = block, Path = block.Path, Operation = EntryOperation.Delete, Stage = MatchStage.None,
            CanApply = canApply, IsSelected = canApply, Issues = issues, BeforeText = beforeText, Diff = diff,
            Description = block.Description, Added = diff?.Added ?? 0, Removed = diff?.Removed ?? 0,
        };
    }

    private static BlockPlan FailedDeletePlan(DeleteBlock block, IReadOnlyList<GraftIssue> issues) => new()
    {
        Block = block, Path = block.Path, Operation = EntryOperation.Delete, Stage = MatchStage.None,
        CanApply = false, IsSelected = false, Issues = issues, Description = block.Description,
    };

    // ------------------------------------------------------------------
    // 共通ヘルパ
    // ------------------------------------------------------------------

    private static HashSet<string> CollectRenamedFromPaths(Patch patch)
        => patch.Blocks.OfType<RenameBlock>().Select(r => NormalizeKey(r.FromPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeKey(string path) => path.Replace('\\', '/');

    private static IReadOnlyList<BlockPlan> FailPlansForFile(
        IReadOnlyList<PatchBlock> blocks, string path, IReadOnlyList<GraftIssue> issues)
        => blocks.Select(b => new BlockPlan
        {
            Block = b, Path = path, Operation = EntryOperation.Modify, Stage = MatchStage.Failed,
            CanApply = false, NeedsConfirmation = false, IsSelected = false,
            Issues = issues, Description = b.Description,
        }).ToList();

    private static IReadOnlyList<GraftIssue> MergeIssues(IReadOnlyList<GraftIssue> a, IReadOnlyList<GraftIssue> b)
        => a.Count == 0 ? b : b.Count == 0 ? a : a.Concat(b).ToArray();

    /// <summary>読み取り専用は既定でPathGuardからは警告として返るが、上書き許可がなければ書き込みを阻む致命的問題へ格上げする（13章）。</summary>
    private static IEnumerable<GraftIssue> UpgradeReadOnlyIfBlocking(IReadOnlyList<GraftIssue> issues, FileCheck check, ApplyContext ctx)
    {
        if (!check.IsReadOnly || ctx.AllowReadOnlyOverride) return issues;
        return issues.Select(i => i.Code == ErrorCode.E205 ? i with { Severity = Severity.Error } : i);
    }

    private static TextShape DefaultShapeFor(ApplyContext ctx)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var name = ctx.Settings.Encoding.NewFileEncoding;
        var encoding = string.Equals(name, "shift_jis", StringComparison.OrdinalIgnoreCase)
            ? Encoding.GetEncoding(932)
            : new UTF8Encoding(false);
        return new TextShape { Encoding = encoding, HasBom = ctx.Settings.Encoding.NewFileBom, NewLine = "\r\n", EndsWithNewLine = true };
    }

    // ------------------------------------------------------------------
    // 6.2 二重適用検知
    // ------------------------------------------------------------------

    /// <summary>
    /// 二重適用の検知。E302（パッチ本文のハッシュ一致）とE305（適用後の内容の一致）を、
    /// 過去リビジョンの一覧を<b>1回だけ</b>読んで両方判定する。
    /// <para>
    /// 【なぜ1回に寄せたか】 E302の判定に使っていた<see cref="RevisionStore.FindByPatchHashAsync"/>は
    /// 内部で<see cref="RevisionStore.ListAsync"/>を呼び、全リビジョンのmanifest.jsonを読み込む。
    /// E305のために別メソッドを足して同じ一覧をもう一度読むと、リビジョンが多い
    /// プロジェクト（世代管理の上限は設定で無制限にもできる）ではドライランのたびに
    /// manifestの読み込みが倍になる。ドライランはパッチを貼るたびに走るため、ここのI/Oは
    /// 増やさない。そこで一覧を1回だけ取り、その結果から両方を判定する。
    /// E302の判定条件（Status=success かつ patchHash一致、一覧の先頭側を採る）は
    /// <see cref="RevisionStore.FindByPatchHashAsync"/>と同一に保ち、挙動を変えない。
    /// </para>
    /// <para>
    /// 一覧の取得に失敗したときは、従来（E302のみ）と同じく何も出さない。二重適用の検知は
    /// あくまで補助情報であり、取得失敗でドライラン全体を止めてはならないため。
    /// </para>
    /// <para>
    /// 【E302とE305で付け先が違う理由】 E302は従来どおり結果レベルのissues（戻り値）に載せる。
    /// 適用時に<see cref="ApplyEngine"/>が同じ判定をやり直して止めるため、挙動を変えない。
    /// 一方E305は戻り値ではなく、該当ファイルの最終プランの<see cref="BlockPlan.Issues"/>へ付ける
    /// （<paramref name="plans"/>の要素を差し替える）。結果レベルのissuesは、MainViewModel.RunDryRunAsync
    /// が成功時に読んでおらず（失敗時に<c>Errors.FirstOrDefault()</c>を使うだけ）、載せても利用者の
    /// 画面に出ないため。ブロック行（BlockItemViewModel）は<c>Plan.Issues</c>を
    /// HasIssue/IssueLinesとして表示しているので、プランに付ければUIを変えずに該当の行へ出る。
    /// </para>
    /// <para>
    /// 【E302を<see cref="DryRunResult.AlreadyAppliedRevision"/>としても返す理由】 結果レベルのissuesは
    /// 成功時にMainViewModelが読まないため、E302だけでは利用者は要約入力と適用確認の窓を
    /// 通り抜けた後、適用時の再判定（ApplyEngine）で初めて止められていた（実機の指摘。しかも
    /// 止められた時点でリビジョン番号が1つ消費される）。そこで「止まる（Error）」場合に限り、
    /// 適用済みのリビジョン番号を構造化して返し、プレビューの時点で画面に出せるようにする。
    /// issues側のE302はこれまでどおり載せる（挙動を変えない）。ForceReapplyで警告に落としたときは
    /// 適用が止まらないので番号は返さない（利用者に「適用できない」と誤解させないため）。
    /// 全ブロックを失敗扱い（CanApply=false）にしないのは、そうすると「修正を依頼」が押せるように
    /// なってしまうため（適用済みのパッチをAIに直してもらう意味は無い）。
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyList<GraftIssue> Issues, int? AlreadyAppliedRevision)> CheckDuplicateAsync(
        ApplyContext ctx, string patchHash, List<BlockPlan> plans, CancellationToken ct)
    {
        var listed = await _revisions.ListAsync(ctx.ProjectId, ct).ConfigureAwait(false);
        if (!listed.IsSuccess) return (Array.Empty<GraftIssue>(), null);

        // 成功したリビジョンだけが対象。rolled_backやin_progressは「実際には反映されて
        // いない（または途中の）状態」であり、その内容と一致しても「適用済み」とは言えない。
        var successful = listed.Value.Where(s => s.Manifest.Status == RevisionStatus.Success).ToList();

        var sameBody = successful.FirstOrDefault(s =>
            string.Equals(s.Manifest.PatchHash, patchHash, StringComparison.OrdinalIgnoreCase));
        if (sameBody is not null)
        {
            var severity = ctx.ForceReapply ? Severity.Warning : Severity.Error;
            var issue = GraftIssue.Of(ErrorCode.E302, $"このパッチはr{sameBody.Manifest.Revision}で適用済みです", severity: severity);
            return (new[] { issue }, severity == Severity.Error ? sameBody.Manifest.Revision : null);
        }

        // E302が出ているときはE305を出さない。パッチ本文が完全に同じなら、適用後の内容が
        // 同じになるのは当然であり、同じことを2つのコードで二重に言うことになる。しかも
        // E302のほうが情報として強く（既定では適用を止める）、利用者が取るべき行動も
        // E302の表示だけで足りる。上のreturnで抜けているのはそのため。
        AttachSameResultIssues(plans, successful, ctx);
        return (Array.Empty<GraftIssue>(), null);
    }

    /// <summary>
    /// E305: 適用後のファイル内容が、過去の成功リビジョンの適用後の内容と同じになるファイルを探す。
    /// <para>
    /// パッチ本文が違っても（summaryの文言変更、SR形式からFULL形式への書き直し、ブロックの
    /// 順序変更など）、結果のファイル内容が同じなら、利用者にとっては「前に適用したのと
    /// 同じ変更」である。比較にはmanifestのHashAfter（<see cref="FileTextIO.ComputeHash"/>
    /// で作られる形式。"sha256:"の接頭辞は付かない）をそのまま使うため、過去の本文を
    /// 読み直す必要は無い。
    /// </para>
    /// <para>
    /// ファイルごとの最終状態: SR形式では同じパスに複数のBlockPlan（ペア単位）が並び、それぞれが
    /// 自分の時点の適用後の全文を持つ。途中の状態は最終結果ではないため、そのパスで
    /// AfterTextがnullでない<b>最後</b>のものだけを採る。削除・MKDIR・RENAMEはAfterTextが
    /// nullなので対象外（ファイルの内容が残らず、比べるものが無い）。CanApplyでないブロックは
    /// 実際には書き込まれないため、最終状態の判定から外す。
    /// </para>
    /// <para>
    /// 該当するリビジョンが複数あるときは、最も新しい1件を示す（利用者が見るべきは直近の
    /// 同じ状態であり、古いものを並べても判断材料が増えない）。複数ファイルが該当したら
    /// ファイルごとに1件ずつ、そのファイルの最終プランの<see cref="BlockPlan.Issues"/>へ付ける。
    /// Severityは常にWarning（巻き戻した内容をもう一度戻す等の正当な操作もあるため、適用は止めない）。
    /// </para>
    /// </summary>
    private static void AttachSameResultIssues(
        List<BlockPlan> plans, IReadOnlyList<RevisionSummary> successfulRevisions, ApplyContext ctx)
    {
        if (successfulRevisions.Count == 0) return;

        // パスごとの「最終プランの添字」。AfterTextを持つ最後のものを採るため、後のものが前のものを上書きする。
        var finalIndexByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < plans.Count; i++)
        {
            if (plans[i].CanApply && plans[i].AfterText is not null) finalIndexByPath[plans[i].Path] = i;
        }

        foreach (var index in finalIndexByPath.Values)
        {
            var plan = plans[index];
            var hash = FileTextIO.ComputeHash(ReconstructWrittenText(plan, ctx));
            var latest = successfulRevisions
                .Where(r => r.Manifest.Entries.Any(e =>
                    e.HashAfter is not null
                    && RevisionStore.EntryPathEquals(e.Path, plan.Path)
                    && string.Equals(e.HashAfter, hash, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(r => r.Manifest.Revision)
                .FirstOrDefault();
            if (latest is null) continue;

            var issue = GraftIssue.Of(ErrorCode.E305,
                $"{plan.Path} は適用後の内容がr{latest.Manifest.Revision}と同じになります",
                severity: Severity.Warning, path: plan.Path);

            // 付けるのはIssuesだけ。CanApply・IsSelected・NeedsConfirmationには一切触れない。
            // E305は参考情報であり、確認を強制したりチェックを外したりしてはならない
            // （巻き戻した内容をもう一度戻すなど、結果が過去と同じになるのが正しい場合があるため）。
            // withで差し替えるので、DiffやAfterText等ほかの値はそのまま引き継がれる。
            plans[index] = plan with { Issues = plan.Issues.Append(issue).ToList() };
        }
    }

    /// <summary>
    /// ドライランの<see cref="BlockPlan.AfterText"/>から、<b>実際にディスクへ書かれる本文</b>を復元する。
    /// <para>
    /// 【なぜAfterTextをそのままハッシュしてはいけないか】 AfterTextは行を"\n"で連結しただけの
    /// 比較・差分表示用の文字列で、末尾改行も改行コードも持たない。一方manifestのHashAfterは、
    /// <see cref="ApplyEngine"/>が書き込んだあとに読み戻した本文のハッシュで、その本文は
    /// ComposeFinalText（ApplyEngine.Text.cs）が<see cref="TextShape"/>に従って組み立てたもの
    /// （新規行は<see cref="TextShape.NewLine"/>、最終行の後ろには
    /// <see cref="TextShape.EndsWithNewLine"/>が真のときだけ改行を付ける）。実測では、
    /// 「same result」という1行のFULLパッチは、AfterTextが<c>same result</c>、ディスク上の本文が
    /// 既定（CRLF・末尾改行あり）の<c>same result\r\n</c>になり、ハッシュが食い違って
    /// E305が1件も出なかった。そこで同じ規則でここで復元する。
    /// </para>
    /// <para>
    /// 割り切り: ComposeFinalTextは、変更しなかった行について元ファイルの改行文字を
    /// そのまま使う。1つのファイルにCRLFとLFが混在している場合、AfterTextからは行ごとの
    /// 元の改行を復元できないため、全行を<see cref="TextShape.NewLine"/>で組み立てる。
    /// その場合はハッシュが一致せずE305が出ない（見逃す側に倒れる）。混在ファイルは稀で、
    /// 誤って「同じ」と警告する（誤検知）ことは起きないため、この割り切りを選んだ。
    /// </para>
    /// <para>
    /// ComposeFinalTextの規則を変えるときは、ここも合わせること
    /// （SameResultDetectionTestsの改行・末尾改行の組み合わせのテストが食い違いを検出する）。
    /// </para>
    /// </summary>
    private static string ReconstructWrittenText(BlockPlan plan, ApplyContext ctx)
    {
        var after = plan.AfterText!;
        // 行が1本も無いファイル（空ファイル）はComposeFinalTextも空文字列を返す。
        if (after.Length == 0) return string.Empty;

        var shape = plan.Shape ?? DefaultShapeFor(ctx);
        var text = after.Replace("\n", shape.NewLine);
        return shape.EndsWithNewLine ? text + shape.NewLine : text;
    }

    // ------------------------------------------------------------------
    // 12章 トークン統計
    // ------------------------------------------------------------------

    /// <summary>
    /// ドライラン時点の見積もり統計。ここでの Files/Added/Removed は「パッチが対象にしている
    /// 範囲」を表し、適用できないブロックやチェックを外したブロックも含む。
    /// <para>
    /// 【履歴に残る値との違い】 リビジョンのmanifest.jsonへ最終的に残る Files/Added/Removed は、
    /// 実際に書き込んだ結果から<see cref="ApplyEngine.ApplyAsync"/>が数え直して上書きする
    /// （履歴ペインの「Nファイル +X -Y」が実際の変更件数と食い違っていた実機不具合への対応。
    /// 詳しい経緯は同メソッド内のコメント参照）。この見積もりの値がそのまま履歴に出ることは無い。
    /// </para>
    /// </summary>
    private static RevisionStats ComputeStats(Patch patch, IReadOnlyList<BlockPlan> plans, ApplyContext ctx)
    {
        var ratio = ctx.Settings.Context.TokenRatio;
        var files = plans.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var added = plans.Sum(p => p.Added);
        var removed = plans.Sum(p => p.Removed);
        var estimatedTokens = Features.TokenEstimator.Estimate(patch.RawText, ratio);

        var fullFileTokens = plans
            .Where(p => p.CanApply && p.Operation is EntryOperation.Modify or EntryOperation.Create)
            .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Last().AfterText is not null)
            .Sum(g => Features.TokenEstimator.Estimate(g.Last().AfterText!, ratio));

        var saved = Math.Max(0, fullFileTokens - estimatedTokens);
        return new RevisionStats
        {
            Files = files, Added = added, Removed = removed,
            EstimatedTokens = estimatedTokens, EstimatedSavedTokens = saved,
        };
    }
}
