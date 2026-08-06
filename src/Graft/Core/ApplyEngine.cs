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
    private readonly MatchEngine _matcher;
    private readonly DryRunPlanner _planner;

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

    /// <summary>6.1 本適用。バックアップ取得後に書き込み、manifest を確定する。</summary>
    public async Task<GraftResult<RevisionManifest>> ApplyAsync(DryRunResult plan, ApplyContext ctx, CancellationToken ct = default)
    {
        var dupIssues = await CheckDuplicateAsync(ctx, plan.PatchHash, ct).ConfigureAwait(false);
        if (dupIssues.HardBlock) return GraftResult<RevisionManifest>.Fail(dupIssues.Issues);

        if (ctx.Settings.ApplyMode != "partial")
        {
            var fatal = plan.Plans.Where(p => !p.CanApply).SelectMany(p => p.Issues).ToList();
            if (fatal.Count > 0) return GraftResult<RevisionManifest>.Fail(fatal);
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

        return GraftResult<RevisionManifest>.Ok(finalManifest, dupIssues.Issues);
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

        return GraftResult<List<RevisionEntry>>.Ok(entries);
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
                return GraftResult<bool>.Fail(ErrorCode.E402, ex.Message, path: p.Path);
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
                return GraftResult<bool>.Fail(ErrorCode.E402, ex.Message, path: p.Path);
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

        foreach (var group in groups)
        {
            var plansForFile = group.ToList();
            var written = await ApplyFileGroupAsync(group.Key, plansForFile, ctx, ct).ConfigureAwait(false);
            if (!written.IsSuccess) return GraftResult<bool>.Fail(written.Issues);

            var (existedBefore, hashBefore, hashAfter) = written.Value;
            if (!existedBefore) session.TrackCreated(group.Key);

            var stage = plansForFile.Max(p => p.Stage);
            entries.Add(new RevisionEntry
            {
                Path = group.Key, Operation = existedBefore ? EntryOperation.Modify : EntryOperation.Create,
                Desc = plansForFile[0].Description, MatchStage = (int)stage, HashBefore = hashBefore, HashAfter = hashAfter,
            });
        }
        return GraftResult<bool>.Ok(true);
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
                        ErrorCode.E402, $"親フォルダを作成できませんでした: {ex.Message}", path: path);
                }
            }
        }

        var seen = new HashSet<PatchBlock>(ReferenceEqualityComparer.Instance);
        var blocks = plansForFile.Select(p => p.Block).Where(seen.Add).ToList();
        var resolution = BlockResolver.ResolveFile(originalLines, blocks, _matcher);
        var finalText = ComposeFinalText(resolution.FinalLines, originalWithTerminators, shape);

        var clearedReadOnly = ClearReadOnlyIfNeeded(fullPath, ctx);
        var written = await FileTextIO.WriteAsync(fullPath, finalText, shape, ct).ConfigureAwait(false);
        RestoreReadOnlyIfNeeded(fullPath, clearedReadOnly);
        if (!written.IsSuccess) return GraftResult<(bool, string?, string)>.Fail(written.Issues);

        return GraftResult<(bool, string?, string)>.Ok((existed, hashBefore, FileTextIO.ComputeHash(finalText)));
    }
}
