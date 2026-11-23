using System.Diagnostics;
using System.IO;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Platform;

namespace Graft.Features;

/// <summary>
/// <see cref="VbaRibbonSyncService"/> の分割ファイル。ブックの丸ごとの控えの一覧と、「この控えでブックを置き換える…」（丸ごと戻す操作。
/// 仕様書_ブックの情報と依頼の観点 16.11。段階 F5）。
/// <para>
/// 【リボンの書き換えの部品を使い回す】 手順は 16.5 と同じ「保存して閉じる → いまのブックを控える → 置き換え → 開き直す」。保存して閉じる
/// （<c>SaveAndCloseForRewriteAsync</c>）・排他で開いて控える（<c>BackupAsync</c>）・控えで置き換えて SHA-256 を照合する
/// （<c>RestoreFromBackup</c>。リボンの書き換えの照合が合わなかったときの「控えから戻す」と同じ部品）・開き直す（<c>ReopenAsync</c>）・
/// 失敗のあとの後始末（<c>ReopenAfterFailureAsync</c>）・作業の記録（<c>.ribbon-job.json</c>。途中で落ちたときの後始末も同じ）は、書き換えと
/// 同じものを通す。違うのは、新しいブックを作る代わりに<b>選んだ控えの中身</b>を書くことと、終わったあとにリボンの同期基準を捨てること。
/// </para>
/// <para>
/// 【利用者のブックを失わない】 置き換える前に、いまのブックを丸ごと控える（控えを書けなければ置き換えない）。置き換えは一時ファイル
/// （ブックと同じフォルダ）→ SHA-256 の照合 → <c>File.Replace</c>。置き換えたあとの照合が合わなければ、いま取った控えから戻す
/// （戻せなければ E823。開き直さない）。控えを取ったあとのシートの編集・VBA の変更もすべて戻る強い操作なので、確認は呼び出し側が毎回出す。
/// </para>
/// </summary>
public sealed partial class VbaRibbonSyncService
{
    // ------------------------------------------------------------------
    // 控えの一覧・取り出し・削除
    // ------------------------------------------------------------------

    /// <summary>
    /// ブックの控え（<c>vba-sync/&lt;プロジェクトId&gt;.workbooks/</c>）を、新しい順に一覧する。<paramref name="prune"/> が true なら、先に掃除する
    /// （16.11「控えの一覧を開いたとき」）。形式に合わないファイル（利用者が置いたもの）は載せず、消さない。フォルダが無ければ空。
    /// </summary>
    public IReadOnlyList<VbaWorkbookBackupItem> ListWorkbookBackups(Project project, bool prune = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        var directory = _paths.GetVbaWorkbookBackupDirectory(project.Id);
        if (!Directory.Exists(directory)) return Array.Empty<VbaWorkbookBackupItem>();

        try
        {
            if (prune && PruneBackups(directory) is { } issue) Logger?.Warn("vba-ribbon", issue.ToDisplayText(), targetPath: directory);
            return Directory.EnumerateFiles(directory)
                .Select(f => (Path: f, Name: VbaWorkbookBackupNaming.Parse(System.IO.Path.GetFileName(f))))
                .Where(x => x.Name is not null)
                .Select(x => new VbaWorkbookBackupItem(System.IO.Path.GetFileName(x.Path), x.Path, x.Name!, new FileInfo(x.Path).Length))
                .OrderByDescending(i => i.At).ThenByDescending(i => i.Name.Sequence).ThenByDescending(i => i.FileName, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.Warn("vba-ribbon", $"ブックの控えの一覧を読めませんでした（{ex.GetType().Name}）。", targetPath: directory);
            return Array.Empty<VbaWorkbookBackupItem>();
        }
    }

    /// <summary>ブックの控えを 1 件削除する（控えのフォルダの中の、控えの形のファイルだけ。ごみ箱には送らない）。</summary>
    public (bool Deleted, string? Failure) DeleteWorkbookBackup(Project project, VbaWorkbookBackupItem item)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);
        if (!IsInsideBackupDirectory(project, item.FullPath)) return (false, "控えのフォルダの外のファイルは削除できません。");
        try
        {
            if (!File.Exists(item.FullPath)) return (true, null);
            File.Delete(item.FullPath);
            Logger?.Info("vba-ribbon", $"ブックの控えを削除しました（{item.FileName}）。", targetPath: item.FullPath);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, ExceptionMessages.Describe(ex));
        }
    }

    /// <summary>
    /// ブックの控えを、別の場所へ取り出す（コピー。控えは残す）。同名のファイルがあれば上書きしない。
    /// </summary>
    /// <param name="destination">取り出し先のファイルのフルパス。</param>
    public (bool Exported, string? Failure) ExportWorkbookBackup(Project project, VbaWorkbookBackupItem item, string destination)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (!IsInsideBackupDirectory(project, item.FullPath)) return (false, "控えのフォルダの外のファイルは取り出せません。");
        try
        {
            File.Copy(item.FullPath, destination, overwrite: false);
            Logger?.Info("vba-ribbon", $"ブックの控えを取り出しました（{item.FileName}）。", targetPath: destination);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, ExceptionMessages.Describe(ex));
        }
    }

    private bool IsInsideBackupDirectory(Project project, string path)
    {
        var directory = System.IO.Path.GetFullPath(_paths.GetVbaWorkbookBackupDirectory(project.Id)).TrimEnd('\\', '/');
        var full = System.IO.Path.GetFullPath(path);
        return string.Equals(System.IO.Path.GetDirectoryName(full), directory, StringComparison.OrdinalIgnoreCase)
               && VbaWorkbookBackupNaming.Parse(System.IO.Path.GetFileName(full)) is not null;
    }

    // ------------------------------------------------------------------
    // 準備（確認ダイアログの前。何も書かない）
    // ------------------------------------------------------------------

    /// <summary>
    /// 「この控えでブックを置き換える…」の準備。控えが同じブックのものか・読めるか・ブックを置き換えられる状態か（Excel で開いているなら、
    /// 止めるブックでないか）を確かめ、確認ダイアログの材料（<see cref="VbaWorkbookRestorePlan"/>）を返す。何も書かない。
    /// </summary>
    /// <param name="backupFileName">控えのファイル名（フォルダを含まない）。</param>
    public async Task<GraftResult<VbaWorkbookRestorePlan>> PrepareRestoreFromBackupAsync(
        Project project, string backupFileName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFileName);
        var link = project.Vba ?? throw new ArgumentException("VBA プロジェクトではありません。", nameof(project));

        var recovery = await RecoverInterruptedAsync(project, ct).ConfigureAwait(true);
        if (recovery is not null) Logger?.Info("vba-ribbon", "前回の書き換えの後始末をしてから、控えでの置き換えを準備します。", targetPath: link.WorkbookFullName);

        var file = WorkbookFileOf(link.WorkbookFullName);
        var name = VbaRibbonRewriteText.FileNameOf(link.WorkbookFullName);
        var directory = _paths.GetVbaWorkbookBackupDirectory(project.Id);
        var parsed = VbaWorkbookBackupNaming.Parse(System.IO.Path.GetFileName(backupFileName));
        var backupPath = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(backupFileName));
        if (parsed is null || !File.Exists(backupPath))
        {
            return GraftResult<VbaWorkbookRestorePlan>.Fail(ErrorCode.E821,
                $"控え「{backupFileName}」が見つかりません。ブックは変えていません。控えの一覧を開き直してください。", path: backupPath);
        }

        // 別のブックの控え（ブックを結び直す前のもの）で、いまのブックを置き換えてしまわない。
        var expected = VbaWorkbookBackupNaming.Parse(VbaWorkbookBackupNaming.BuildFileName(name, _clock()));
        if (expected is null
            || !string.Equals(parsed.WorkbookName, expected.WorkbookName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parsed.Extension, expected.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return GraftResult<VbaWorkbookRestorePlan>.Fail(ErrorCode.E821,
                $"控え「{backupFileName}」は、別のブック（「{parsed.WorkbookName}{parsed.Extension}」）の控えです。「{name}」は置き換えません。ブックは変えていません。",
                path: backupPath);
        }

        string sha;
        long size;
        try
        {
            size = new FileInfo(backupPath).Length;
            // 控えが zip（ブックのパッケージ）として読めること。読めない控えで置き換えて、ブックを壊さない。
            using (var stream = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var inspected = VbaRibbonPackageWriter.Inspect(stream);
                if (inspected.Status == VbaRibbonWriteStatus.Corrupt)
                {
                    return GraftResult<VbaWorkbookRestorePlan>.Fail(ErrorCode.E821,
                        $"控え「{backupFileName}」をブックとして読めません（{inspected.Problem}）。ブックは変えていません。", path: backupPath);
                }
            }

            sha = await Task.Run(() => Sha256OfFile(backupPath), ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<VbaWorkbookRestorePlan>.Fail(ErrorCode.E821,
                $"控え「{backupFileName}」を読めません（{ExceptionMessages.Describe(ex)}）。ブックは変えていません。", path: backupPath);
        }

        var info = new FileInfo(file);
        if (!info.Exists)
        {
            return GraftResult<VbaWorkbookRestorePlan>.Fail(ErrorCode.E821,
                $"「{name}」のファイルが見つかりません。控えでの置き換えは、ブックのファイルがあるときだけ行えます。ブックは変えていません。", path: file);
        }

        // 空き容量（取れない保存先は省く）。ブックのフォルダに控えの 1.2 倍、控えのフォルダにいまのブックの大きさ。
        if (Io.FreeSpace(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!) is { } freeHere && freeHere < size + size / 5)
        {
            return GraftResult<VbaWorkbookRestorePlan>.Fail(ErrorCode.E821,
                $"ブックのフォルダの空き容量が足りません（必要 {Megabytes(size + size / 5)}・空き {Megabytes(freeHere)}）。ブックは変えていません。", path: file);
        }

        if (Io.FreeSpace(directory) is { } freeBackup && freeBackup < info.Length)
        {
            return GraftResult<VbaWorkbookRestorePlan>.Fail(ErrorCode.E816,
                $"控えの保存先の空き容量が足りないため、いまのブックの控えを残せません（必要 {Megabytes(info.Length)}・空き {Megabytes(freeBackup)}。保存先: {directory}）。ブックは変えていません。",
                path: directory);
        }

        // Excel で開いているか・止めるブックか（16.6）。開いていても止めるブックでなければ、閉じ開きの経路で置き換える。
        var open = await InspectOpenStateAsync(link.WorkbookFullName, ct).ConfigureAwait(true);
        if (open.Stop is { } stop) return GraftResult<VbaWorkbookRestorePlan>.Fail(stop);

        return GraftResult<VbaWorkbookRestorePlan>.Ok(new VbaWorkbookRestorePlan(
            project.Id, file, name, backupPath, System.IO.Path.GetFileName(backupPath), parsed.At, size, sha, directory,
            info.Length, info.LastWriteTimeUtc, open.State.IsOpen ? open.State : null));
    }

    // ------------------------------------------------------------------
    // 実行
    // ------------------------------------------------------------------

    /// <summary>
    /// 確認ダイアログで「控えの内容で置き換える」を選んだ計画を実行する。成功の結果は <see cref="VbaRibbonRewriteOutcome"/>
    /// （<c>BackupPath</c> は<b>置き換える前のブックの控え</b>、<c>Written</c> は空）。失敗は問題（E816・E819・E820・E821・E823）で返し、
    /// <b>E823 以外ではブックは置き換える前の内容のまま</b>。閉じたあとの失敗は、E823 を除いて、開き直してから知らせる。
    /// </summary>
    public async Task<GraftResult<VbaRibbonRewriteOutcome>> RestoreFromBackupAsync(
        Project project, VbaWorkbookRestorePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        var link = project.Vba ?? throw new ArgumentException("VBA プロジェクトではありません。", nameof(project));

        using var writing = _beginWriting();
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            return await RestoreCoreAsync(project, link, plan, ct).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GraftResult<VbaRibbonRewriteOutcome>> RestoreCoreAsync(
        Project project, VbaLink link, VbaWorkbookRestorePlan plan, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var file = plan.WorkbookPath;
        var name = plan.WorkbookName;

        // 確認のあいだの変化（控え・ブック・Excel での状態）。ここで止まるときは、何も書いていない。
        if (await CheckRestoreUnchangedAsync(link, plan, ct).ConfigureAwait(true) is { } changed)
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(changed);

        // 作業の記録（途中で落ちたときの後始末のため。リボンの書き換えと同じ記録）。控えの場所と一時ファイルの場所を先に決める。
        string preRestorePath;
        try
        {
            Directory.CreateDirectory(plan.BackupDirectory);
            preRestorePath = UniqueBackupPath(plan.BackupDirectory, name, _clock());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E816,
                $"ブックの控えの保存先を作れません（{ExceptionMessages.Describe(ex)}。保存先: {plan.BackupDirectory}）。ブックは変えていません。",
                path: plan.BackupDirectory);
        }

        var job = new VbaRibbonRewriteJob
        {
            ProjectId = project.Id,
            WorkbookPath = file,
            BackupPath = preRestorePath,
            TempPath = VbaRibbonPackageWriter.NewTempPath(file),
            Stage = VbaRibbonRewriteStage.Started,
            StartedAt = _clock(),
        };
        if (!await TryWriteJobAsync(job).ConfigureAwait(true))
        {
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                $"作業の記録（{_paths.GetVbaRibbonJobFilePath(project.Id)}）を書けないため、置き換えを始めませんでした。データ保存先の空き容量と権限を確かめてください。ブックは変えていません。",
                path: file);
        }

        Logger?.Info("vba-ribbon",
            $"控えでブックを置き換えます（{name}・控え {plan.BackupFileName} {Short(plan.BackupSha256)}・{(plan.IsOpen ? "Excel で開いている" : "開いていない")}）。",
            targetPath: file);

        var held = new ReopenHolder(link.WorkbookFullName);
        try
        {
            GraftResult<VbaRibbonRewriteOutcome> result;
            try
            {
                if (plan.IsOpen)
                {
                    var closed = await SaveAndCloseForRewriteAsync(project, name, file, job, held, ct).ConfigureAwait(true);
                    if (closed.Failure is not null) return closed.Failure;
                    job = closed.Job!;
                }

                result = await RestoreStagesAsync(project, link, plan, job, preRestorePath, watch, held).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger?.Error("vba-ribbon", $"控えでの置き換えの途中で失敗しました（{ex.GetType().Name}）。", targetPath: file);
                var recovered = await RecoverInterruptedCoreAsync(project.Id).ConfigureAwait(true);
                result = GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                    $"控えでの置き換えの途中で失敗しました（{ExceptionMessages.Describe(ex)}）。{recovered?.Message ?? "ブックの状態を確かめられませんでした。"}", path: file);
            }

            // 閉じたあとの失敗は、E823 を除いて開き直してから知らせる。成功のときの開き直しは RestoreStagesAsync の中で済んでいる。
            return result.IsSuccess ? result : await ReopenAfterFailureAsync(result, held).ConfigureAwait(true);
        }
        finally
        {
            await held.DiscardAsync(_bridge).ConfigureAwait(true);
        }
    }

    /// <summary>確認のあいだに、控え・ブック・Excel での状態が変わっていないかを確かめる（変わっていれば止める問題）。</summary>
    private async Task<GraftIssue?> CheckRestoreUnchangedAsync(VbaLink link, VbaWorkbookRestorePlan plan, CancellationToken ct)
    {
        if (!File.Exists(plan.BackupPath) || new FileInfo(plan.BackupPath).Length != plan.BackupSize)
        {
            return GraftIssue.Of(ErrorCode.E821,
                $"確認のあいだに控え「{plan.BackupFileName}」が変わった（消された）ため、置き換えませんでした。ブックは変えていません。", path: plan.BackupPath);
        }

        var open = await InspectOpenStateAsync(link.WorkbookFullName, ct).ConfigureAwait(true);
        if (open.Stop is { } stop) return stop[0];
        if (open.State.IsOpen != plan.IsOpen)
        {
            return GraftIssue.Of(ErrorCode.E821,
                $"確認のあいだに「{plan.WorkbookName}」が Excel で{(open.State.IsOpen ? "開かれた" : "閉じられた")}ため、確認した内容と変わってしまうので、置き換えませんでした。ブックは変えていません。もう一度お試しください。",
                path: plan.WorkbookPath);
        }

        if (open.State.IsOpen) return null; // 開いているブックのファイルは、このあと Graft が保存するので、大きさ・更新日時は比べない（段階 9 で比べる）。

        var info = new FileInfo(plan.WorkbookPath);
        if (!info.Exists || info.Length != plan.BookSize || info.LastWriteTimeUtc != plan.BookModifiedUtc)
        {
            return GraftIssue.Of(ErrorCode.E821,
                $"確認のあいだに「{plan.WorkbookName}」のファイルが変わった（Excel などで保存された見込み）ため、置き換えませんでした。ブックは変えていません。もう一度お試しください。",
                path: plan.WorkbookPath);
        }

        return null;
    }

    /// <summary>
    /// 閉じたあと（または開いていないブック）の段階: いまのブックを控える → 控えの内容で置き換える → 照合 → 基準を捨てる → 開き直す → 後始末。
    /// </summary>
    private async Task<GraftResult<VbaRibbonRewriteOutcome>> RestoreStagesAsync(
        Project project, VbaLink link, VbaWorkbookRestorePlan plan, VbaRibbonRewriteJob job, string preRestorePath, Stopwatch watch, ReopenHolder held)
    {
        var file = plan.WorkbookPath;
        var name = plan.WorkbookName;

        // いまのブックを丸ごと控える（排他で開いたストリームから写す。控えを書けなければ置き換えない）。
        Progress?.Invoke(VbaRibbonRewriteText.ProgressBackup);
        var backup = await BackupAsync(file, preRestorePath, afterExcelClose: held.HasTicket).ConfigureAwait(true);
        if (!backup.IsSuccess)
        {
            DeleteJob(project.Id);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(backup.Issues);
        }

        job = job with { BackupSha256 = backup.Value.Sha256, Stage = VbaRibbonRewriteStage.BackedUp };
        if (!await TryWriteJobAsync(job).ConfigureAwait(true))
        {
            TryDelete(preRestorePath);
            DeleteJob(project.Id);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                "作業の記録を更新できないため、置き換えを止めました。ブックは変えていません。", path: file);
        }

        // 控えを取ったときから、ブックが変わっていないこと（ほかのプログラムに書かれていないこと）。
        var now = new FileInfo(file);
        if (!now.Exists || now.Length != backup.Value.Size || now.LastWriteTimeUtc != backup.Value.ModifiedUtc)
        {
            TryDelete(preRestorePath);
            DeleteJob(project.Id);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                $"控えを取ったあとに「{name}」がほかのプログラムに変えられたため、上書きしないよう止めました。ブックは変えていません。", path: file);
        }

        // 選んだ控えの内容で置き換える（一時ファイル → SHA-256 の照合 → 置き換え → ブックの SHA-256 の照合）。
        job = job with { Stage = VbaRibbonRewriteStage.Replacing, RestoreTempPath = VbaRibbonPackageWriter.NewTempPath(file, ".restore") };
        await WriteJobOrThrowAsync(job).ConfigureAwait(true);
        Progress?.Invoke(VbaWorkbookRestoreProgress);
        var restoreTemp = job.RestoreTempPath!;
        var replaced = await Task.Run(() => RestoreFromBackup(plan.BackupPath, plan.BackupSha256, file, restoreTemp)).ConfigureAwait(true);
        if (!replaced)
        {
            return await HandleRestoreMismatchAsync(project, plan, job, preRestorePath, backup.Value.Sha256).ConfigureAwait(true);
        }

        job = job with { Stage = VbaRibbonRewriteStage.Replaced };
        await WriteJobOrThrowAsync(job).ConfigureAwait(true);

        // リボンの同期基準を捨てて「記録なし」に戻す（16.11）。次の取込で、本文が同じなら基準を作り直し、違えば衝突になる。
        // 保存に失敗しても置き換えは済んでいるので、警告にとどめる（基準が古いまま残っても、次の取込で食い違いは衝突として見つかる）。
        var issues = new List<GraftIssue>();
        try
        {
            var loaded = await _states.LoadAsync(project.Id, link.WorkbookFullName).ConfigureAwait(true);
            var state = loaded.Value;
            foreach (var path in state.Ribbon?.Keys.ToList() ?? new List<string>()) state = state.WithoutRibbon(path);
            if (!VbaSyncService.StatesEqual(loaded.Value, state)) await _states.SaveAsync(project.Id, state).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.Warn("vba-ribbon", $"リボンの同期基準を捨てられませんでした（{ex.GetType().Name}）。", targetPath: file);
            issues.Add(GraftIssue.Of(ErrorCode.E821,
                $"ブックは控えの内容で置き換えましたが、同期状態を更新できませんでした（{ExceptionMessages.Describe(ex)}）。次の取込でリボンの食い違いを確かめます。",
                path: file, severity: Severity.Warning));
        }

        // 開き直し（成功のとき。段階 13 は後始末の前）。
        ReopenReport? report = null;
        if (held.HasTicket) report = await ReopenAsync(held).ConfigureAwait(true);
        if (report is { Reopened: false }) issues.AddRange(report.Issues);

        DeleteJob(project.Id);
        if (PruneBackups(plan.BackupDirectory) is { } pruneIssue) issues.Add(pruneIssue);
        Logger?.Info("vba-ribbon", $"控えでブックを置き換えました（{name}・控え {plan.BackupFileName}・置き換える前のブックの控え {System.IO.Path.GetFileName(preRestorePath)}）。",
            targetPath: file, durationMs: watch.ElapsedMilliseconds);
        return GraftResult<VbaRibbonRewriteOutcome>.Ok(
            new VbaRibbonRewriteOutcome(file, preRestorePath, Array.Empty<string>(), false, WasOpen: plan.IsOpen, Reopened: report?.Reopened == true),
            issues);
    }

    /// <summary>処理中の表示: 控えの内容でブックを置き換えている。</summary>
    public const string VbaWorkbookRestoreProgress = "控えの内容でブックを置き換えています";

    /// <summary>
    /// 置き換えが済まなかった（または、置き換えたあとの照合が合わなかった）とき。ブックがまだ元のまま（いま取った控えと同じ）なら、何も
    /// 変わっていないので、いま取った控えを消して E821。違う内容になっていれば、いま取った控えから戻す（戻せれば E821、戻せなければ E823。
    /// E823 のときは開き直さない）。
    /// </summary>
    private async Task<GraftResult<VbaRibbonRewriteOutcome>> HandleRestoreMismatchAsync(
        Project project, VbaWorkbookRestorePlan plan, VbaRibbonRewriteJob job, string preRestorePath, string preRestoreSha)
    {
        var file = plan.WorkbookPath;
        var current = await Task.Run(() => TrySha256(file)).ConfigureAwait(true);
        if (string.Equals(current, preRestoreSha, StringComparison.Ordinal))
        {
            // ブックは置き換える前のまま（置き換えの前に止まった・置き換えそのものが失敗した）。世代だけが進まないよう、控えは消す。
            TryDelete(preRestorePath);
            DeleteJob(project.Id);
            Logger?.Warn("vba-ribbon", "控えの内容で置き換えられませんでした。ブックは元のままです。", targetPath: file);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                $"控え「{plan.BackupFileName}」の内容で「{plan.WorkbookName}」を置き換えられませんでした（書き込みか置き換えの失敗、または照合の不一致）。ブックは元のままです。",
                path: file);
        }

        // 置き換えのあとで、期待と違う内容になっている。いま取った控えから戻す。
        Logger?.Error("vba-ribbon", "置き換えたブックの照合が合いませんでした。置き換える前の控えから戻します。", targetPath: file);
        var restoreTemp = VbaRibbonPackageWriter.NewTempPath(file, ".restore");
        var restored = await Task.Run(() => RestoreFromBackup(preRestorePath, preRestoreSha, file, restoreTemp)).ConfigureAwait(true);
        DeleteJob(project.Id);
        if (restored)
        {
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                $"控え「{plan.BackupFileName}」で置き換えたブックの照合が合いませんでした。置き換える前の内容に戻しました（控え: {preRestorePath}）。",
                path: file);
        }

        Logger?.Error("vba-ribbon", "置き換える前の控えからブックを戻せませんでした（要確認）。", targetPath: file);
        return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E823,
            $"控え「{plan.BackupFileName}」で置き換えたブックの照合が合わず、置き換える前の内容へ戻すこともできませんでした。置き換える前の控え: {preRestorePath}（フォルダ: {plan.BackupDirectory}）",
            path: file);
    }
}
