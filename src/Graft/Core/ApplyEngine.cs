using System.IO;
using System.Text;

namespace Graft.Core;

/// <summary>
/// 仕様書6章の適用エンジン。6.1の二段階実行のうち、ドライラン（<see cref="DryRunAsync"/>）は
/// <see cref="DryRunPlanner"/> に委譲する。本適用（<see cref="ApplyAsync"/>）はバックアップ取得
/// 後に書き込みを行い、manifest を確定する。
/// </summary>
public sealed class ApplyEngine
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

    /// <summary>
    /// 6.4「改行コードの混在は可能な限り維持する」への対応。未変更行（OriginalIndexが非null）は
    /// 元ファイルの改行文字をそのまま使い、新規生成行（置換後・追記・先頭挿入・FULL全文）は
    /// TextShape.NewLineを使う。末尾改行の有無は行の由来によらずTextShape.EndsWithNewLineに従う。
    /// </summary>
    private static string ComposeFinalText(
        IReadOnlyList<ResolvedLine> lines, IReadOnlyList<(string Text, string Terminator)>? original, TextShape shape)
    {
        if (lines.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i].Text);
            if (i < lines.Count - 1)
            {
                sb.Append(OriginalTerminatorOrDefault(lines[i], original, shape.NewLine));
            }
            else if (shape.EndsWithNewLine)
            {
                sb.Append(shape.NewLine);
            }
        }
        return sb.ToString();
    }

    private static string OriginalTerminatorOrDefault(
        ResolvedLine line, IReadOnlyList<(string Text, string Terminator)>? original, string fallback)
    {
        if (original is null || line.OriginalIndex is not int idx || idx >= original.Count) return fallback;
        var terminator = original[idx].Terminator;
        return terminator.Length > 0 ? terminator : fallback;
    }

    /// <summary>
    /// <see cref="TextNormalizer.SplitLines"/> と同じ行区切り規則（CRLF/LF/CRいずれも境界として扱う）
    /// で分割しつつ、各行の元の改行文字列も保持する。未変更行の改行コードを書き込み時に維持するために
    /// のみ使う（比較・マッチングには関与しない）。
    /// </summary>
    private static List<(string Text, string Terminator)> SplitLinesWithTerminators(string text)
    {
        var result = new List<(string, string)>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c != '\r' && c != '\n') { i++; continue; }

            var content = text.Substring(start, i - start);
            string terminator;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { terminator = "\r\n"; i++; }
            else terminator = c.ToString();
            i++;
            result.Add((content, terminator));
            start = i;
        }

        if (start < text.Length) result.Add((text.Substring(start), string.Empty));
        return result;
    }

    private static bool ClearReadOnlyIfNeeded(string fullPath, ApplyContext ctx)
    {
        if (!ctx.AllowReadOnlyOverride) return false;
        var ioPath = LongPath.Extended(fullPath);
        if (!File.Exists(ioPath)) return false;

        var info = new FileInfo(ioPath);
        if (!info.IsReadOnly) return false;
        info.IsReadOnly = false;
        return true;
    }

    private static void RestoreReadOnlyIfNeeded(string fullPath, bool wasCleared)
    {
        if (!wasCleared) return;
        var ioPath = LongPath.Extended(fullPath);
        if (!File.Exists(ioPath)) return;
        new FileInfo(ioPath).IsReadOnly = true;
    }

    // ------------------------------------------------------------------
    // DELETE（バックアップは BackupTargetsAsync で既に退避済み）
    // ------------------------------------------------------------------

    private static GraftResult<bool> ExecuteDeletes(List<BlockPlan> eligible, ApplyContext ctx, List<RevisionEntry> entries)
    {
        foreach (var p in eligible.Where(p => p.Operation == EntryOperation.Delete))
        {
            var resolved = ctx.Guard.Resolve(p.Path);
            if (!resolved.IsSuccess) return GraftResult<bool>.Fail(resolved.Issues);

            var ioPath = LongPath.Extended(resolved.Value);
            if (!File.Exists(ioPath))
            {
                entries.Add(new RevisionEntry { Path = p.Path, Operation = EntryOperation.Delete, Desc = p.Description });
                continue;
            }

            try
            {
                ClearReadOnlyIfNeeded(resolved.Value, ctx);
                File.Delete(ioPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return GraftResult<bool>.Fail(ErrorCode.E402, ex.Message, path: p.Path);
            }

            entries.Add(new RevisionEntry { Path = p.Path, Operation = EntryOperation.Delete, Desc = p.Description });
        }
        return GraftResult<bool>.Ok(true);
    }
}
