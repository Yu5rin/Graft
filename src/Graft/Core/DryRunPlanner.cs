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

        var dupIssues = await CheckDuplicateAsync(ctx, patchHash, ct).ConfigureAwait(false);
        var stats = ComputeStats(patch, plans, ctx);
        var result = new DryRunResult
        {
            Patch = patch, Plans = plans, PatchHash = patchHash, Stats = stats, FileProbes = fileProbes,
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

    private async Task<IReadOnlyList<GraftIssue>> CheckDuplicateAsync(ApplyContext ctx, string patchHash, CancellationToken ct)
    {
        var found = await _revisions.FindByPatchHashAsync(ctx.ProjectId, patchHash, ct).ConfigureAwait(false);
        if (!found.IsSuccess || found.Value is null) return Array.Empty<GraftIssue>();

        var revisionNo = found.Value.Manifest.Revision;
        var severity = ctx.ForceReapply ? Severity.Warning : Severity.Error;
        var issue = GraftIssue.Of(ErrorCode.E302, $"このパッチはr{revisionNo}で適用済みです", severity: severity);
        return new[] { issue };
    }

    // ------------------------------------------------------------------
    // 12章 トークン統計
    // ------------------------------------------------------------------

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
