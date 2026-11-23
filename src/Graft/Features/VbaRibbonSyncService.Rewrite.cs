using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;

namespace Graft.Features;

/// <summary>「リボンの XML を検査」（16.8 の手動の検査）の、ミラーのファイル 1 つ分の結果。</summary>
/// <param name="MirrorPath">ミラーの相対パス。</param>
/// <param name="Validation">検査の結果（読めなかったときは null）。</param>
/// <param name="Problem">ファイルを読めなかった理由（読めたときは null）。</param>
public sealed record VbaRibbonInspection(string MirrorPath, VbaRibbonValidation? Validation, string? Problem);

/// <summary>
/// <see cref="VbaRibbonSyncService"/> の分割ファイル。ブックのファイルの書き換え（仕様書_ブックの情報と依頼の観点 16.5。段階 F3）。
/// <para>
/// 【段階 F3 の範囲】 COM を使わない経路だけ、つまり <b>Excel で開いていないブック</b>のファイルを書き換える。開いているブック
/// （.xlam のアドインを含む）は書き換えず、「Excel でブックを閉じてから反映してください」と案内して止める（E819）。閉じ開きの
/// 自動化（16.6）は段階 F4 で、この流れの前後に足す。
/// </para>
/// <para>
/// 【流れ】 準備（<see cref="PrepareRewriteAsync"/>。段階 1〜3: 対象の確定・XML の検査・ブックの状態の検査とパッケージの予行）→
/// 呼び出し側が確認ダイアログ（16.9）を出す → 書き換え（<see cref="RewriteAsync"/>。段階 5・7〜12・14: 作業の記録 → 排他で開く →
/// 控え → 新しいファイル → 照合 → 置き換え → 読み直して照合 → 基準の更新 → 後始末）。準備と書き換えのあいだで確認を挟むので、
/// 書き換えの最初に「確認のあいだにミラー・ブックが変わっていないか」を比べ直す（確認したのと違う内容を黙って書かない）。
/// </para>
/// <para>
/// 【利用者のブックを壊さない】 元のブックは、置き換えの瞬間まで 1 バイトも変えない。書き込みは必ず一時ファイル（ブックと同じフォルダ）
/// → 全項目の CRC-32 の照合 → <c>File.Replace</c> の順（<see cref="VbaRibbonPackageWriter.RewriteFile"/>）。その前に、ブック全体の控えを
/// 書いて SHA-256 で照合し、控えを書けなければ書き換えない（E816）。置き換えたあとに読み直してミラーと違えば、控えから戻す（16.11）。
/// どの段階で落ちても半端な状態にならないよう、作業の記録（<c>.ribbon-job.json</c>）を先に書き、次の起動・次のリボンの操作で後始末する。
/// </para>
/// </summary>
public sealed partial class VbaRibbonSyncService
{
    private readonly JsonFileStore _jobStore = new();

    /// <summary>ファイルの操作の差し替え口（失敗の注入。本番は既定のまま）。</summary>
    internal VbaRibbonRewriteIo Io { get; set; } = new();

    /// <summary>
    /// 紐付けたブック（Excel の <c>FullName</c>）から、書き換えるファイルのパスを決める。本番はそのまま（同じもの）。画面のテストは、
    /// Excel の代役が開いている「ブック」に実ファイルが無いので、一時ファイルの zip へ向ける（<see cref="PackageReader"/> と同じ事情）。
    /// </summary>
    public Func<string, string> WorkbookFileOf { get; set; } = static path => path;

    /// <summary>
    /// 書き換えの前に、リボンのミラーのファイルの未保存の編集を確認して保存させる（16.5 の段階 1）。引数は既にあるリボンのファイルの
    /// フルパス。断られたら中止（何も変えない）。未設定なら確認なしで続ける。
    /// </summary>
    public Func<IReadOnlyList<string>, Task<VbaPullSaveOutcome>>? BeforeRewriteAsync { get; set; }

    /// <summary>ブックの丸ごとの控えの世代数（設定 <c>Vba.WorkbookBackupMaxGenerations</c>。呼び出しのたびに読む）。</summary>
    public Func<int> MaxBackupGenerations { get; set; } = static () => 5;

    /// <summary>ブックの丸ごとの控えの合計の容量（MB。設定 <c>Vba.WorkbookBackupMaxTotalMB</c>）。</summary>
    public Func<int> MaxBackupTotalMB { get; set; } = static () => 500;

    // ------------------------------------------------------------------
    // 手動の「リボンの XML を検査」（16.8）
    // ------------------------------------------------------------------

    /// <summary>
    /// ミラーのリボンのファイルを検査する（結果の一覧を出すだけで、何も書かない）。書き換えの直前の検査（段階 2）と同じ材料
    /// （VBA のミラーのモジュール・画像の関係）を渡すので、確認ダイアログに出る警告と同じものが出る。ファイルが 1 つも無ければ空。
    /// </summary>
    public async Task<IReadOnlyList<VbaRibbonInspection>> InspectMirrorAsync(Project project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Vba is not { } link) return Array.Empty<VbaRibbonInspection>();

        var mirror = await ReadMirrorAsync(project.Root, ct).ConfigureAwait(true);
        if (mirror.Count == 0) return Array.Empty<VbaRibbonInspection>();

        // 画像の関係（#13）の材料に、ブックの部品名を読む（読めなければ #13 を省く）。読み取りの処理が無い（同期していない）ときも省く。
        VbaRibbonReadResult? book = null;
        if (PackageReader is { } reader)
        {
            book = await Task.Run(() => CheckParts(reader(link.WorkbookFullName)), ct).ConfigureAwait(true);
        }

        var modules = await VbaRibbonCollector.ReadMirrorModulesAsync(project.Root, ct).ConfigureAwait(true);
        var file = WorkbookFileOf(link.WorkbookFullName);
        var results = new List<VbaRibbonInspection>();
        foreach (var entry in mirror.Values.OrderBy(e => e.Format))
        {
            if (!entry.File.IsReadable)
            {
                results.Add(new VbaRibbonInspection(entry.RelativePath, null, entry.File.Problem));
                continue;
            }

            var imageIds = book is null ? null : ImageIdsFor(book, file, entry.Format);
            var validation = await Task.Run(
                () => VbaRibbonXmlValidator.Validate(entry.File.Text!, entry.Format, new VbaRibbonValidationContext(modules, imageIds)), ct)
                .ConfigureAwait(true);
            results.Add(new VbaRibbonInspection(entry.RelativePath, validation, null));
        }

        return results;
    }

    /// <summary>
    /// 画像の関係の <c>Id</c>（16.8 の #13 の材料）。ブックにその形式の部品が無い（新しく足す）なら空（画像の関係も無いので、
    /// <c>image="…"</c> はどれもブックに無い）。ブックが読めないときは null（#13 を省く）。
    /// </summary>
    private static IReadOnlyCollection<string>? ImageIdsFor(VbaRibbonReadResult book, string workbookFile, VbaRibbonFormat format)
    {
        if (book.Status is not (VbaRibbonReadStatus.Ok or VbaRibbonReadStatus.NoRibbon or VbaRibbonReadStatus.PartsUnreadable)) return null;
        var part = book.Parts.FirstOrDefault(p => VbaRibbonMirrorLayout.FormatOf(p) == format);
        if (part is null) return Array.Empty<string>();
        return File.Exists(workbookFile) ? VbaRibbonImageRelations.ReadIds(workbookFile, part.PartName) : null;
    }

    // ------------------------------------------------------------------
    // 準備（16.5 の段階 1〜3）
    // ------------------------------------------------------------------

    /// <summary>
    /// 書き換えの準備（16.5 の段階 1〜3）。何も書かない。止めるべき問題（XML のエラー E818、対象外のブック E819、予行の失敗 E821・
    /// 控えの保存先の空き不足 E816）は失敗で返す。反映するものが無ければ <see cref="VbaRibbonRewritePreparationKind.NothingToDo"/>。
    /// <para>
    /// 「閉じる前にできる検査は、すべて閉じる前に済ませる」（16.5 の原則）。F3 では閉じないが、同じ順序にしておくと、F4 で閉じ開きを
    /// 足したときに、閉じたあとで止まる経路が増えない。
    /// </para>
    /// </summary>
    public async Task<GraftResult<VbaRibbonRewritePreparation>> PrepareRewriteAsync(Project project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var link = project.Vba ?? throw new ArgumentException("VBA プロジェクトではありません。", nameof(project));
        if (!IsActive)
        {
            return GraftResult<VbaRibbonRewritePreparation>.Fail(GraftIssue.Of(
                ErrorCode.E810, "リボン（customUI）の同期が設定でオフのため、ブックへ反映しません（設定「リボンをミラーのファイルとして扱う」）。",
                path: VbaRibbonMirrorLayout.FolderName + "/"));
        }

        // 段階 1: CustomUI/*.xml の未保存のタブがあれば保存の確認（断られたら中止）。ダイアログを出すので、錠を取る前に行う。
        var files = MirrorFiles(project.Root);
        if (files.Count > 0 && BeforeRewriteAsync is { } before)
        {
            var saved = await before(files).ConfigureAwait(true);
            if (saved.Declined.Count > 0)
            {
                return GraftResult<VbaRibbonRewritePreparation>.Ok(
                    new VbaRibbonRewritePreparation(VbaRibbonRewritePreparationKind.Declined, null, Array.Empty<GraftIssue>()));
            }
        }

        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            return await PrepareCoreAsync(project, link, ct).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GraftResult<VbaRibbonRewritePreparation>> PrepareCoreAsync(Project project, VbaLink link, CancellationToken ct)
    {
        // 前回の書き換えが途中で終わっていれば、先に片付ける（16.11。「次のリボンの操作で」）。片付ける前に次を始めると、残った
        // 一時ファイルや記録を、今回のものと取り違える。
        var recovery = await RecoverInterruptedCoreAsync(project.Id).ConfigureAwait(true);

        var work = await BuildWorkAsync(project.Root, project.Id, link.WorkbookFullName, null, null, ct).ConfigureAwait(true);
        if (work is null)
        {
            return GraftResult<VbaRibbonRewritePreparation>.Fail(GraftIssue.Of(
                ErrorCode.E810, "リボン（customUI）を同期していないため、ブックへ反映しません。", path: VbaRibbonMirrorLayout.FolderName + "/"));
        }

        var workbookName = VbaRibbonRewriteText.FileNameOf(link.WorkbookFullName);
        if (BookStatusIssue(work.Book, workbookName) is { } bookIssue) return GraftResult<VbaRibbonRewritePreparation>.Fail(bookIssue);

        // 反映しない衝突・読めない形式は、止める理由ではなく知らせ（反映できる形式だけを書く）。
        var notes = new List<GraftIssue>(work.LoadIssues);
        notes.AddRange(VbaRibbonSyncIssues.Collect(work.Plan, null, "ブックへは反映しませんでした"));

        var pushes = work.Plan.Where(VbaRibbonSyncAction.Push).OrderBy(i => i.Format).ToList();
        if (pushes.Count == 0)
        {
            return GraftResult<VbaRibbonRewritePreparation>.Ok(
                new VbaRibbonRewritePreparation(VbaRibbonRewritePreparationKind.NothingToDo, null, notes, recovery));
        }

        var targets = new List<VbaRibbonRewriteTarget>();
        foreach (var item in pushes)
        {
            // 計画の Push は「ミラーが読めた」ものだけ（読めないものは Skip）なので、本文は必ずある。
            var text = work.Mirror[item.Format].File.Text!;
            var bookPart = work.Book.Parts.FirstOrDefault(p => VbaRibbonMirrorLayout.FormatOf(p) == item.Format && p.IsReadable);
            targets.Add(new VbaRibbonRewriteTarget(
                item.Format, item.MirrorPath, text, VbaTextNormalizer.Hash(text), bookPart?.Xml, item.IsNewPart));
        }

        // 段階 2: XML の検査。エラーが 1 件でもあれば止める（E818）。警告は確認ダイアログに出す。
        var file = WorkbookFileOf(link.WorkbookFullName);
        var modules = await VbaRibbonCollector.ReadMirrorModulesAsync(project.Root, ct).ConfigureAwait(true);
        var errors = new List<GraftIssue>();
        var warnings = new List<VbaRibbonRewriteWarning>();
        foreach (var target in targets)
        {
            var imageIds = target.IsNewPart ? Array.Empty<string>() : ImageIdsFor(work.Book, file, target.Format);
            var validation = await Task.Run(
                () => VbaRibbonXmlValidator.Validate(target.Xml, target.Format, new VbaRibbonValidationContext(modules, imageIds)), ct)
                .ConfigureAwait(true);
            errors.AddRange(validation.Errors.Select(f => GraftIssue.Of(
                ErrorCode.E818, $"{target.MirrorPath} {f.ToDisplayText()}", line: f.Line > 0 ? f.Line : null, path: target.MirrorPath)));
            warnings.AddRange(validation.Warnings.Select(f => new VbaRibbonRewriteWarning(target.MirrorPath, f)));
        }

        if (errors.Count > 0)
        {
            Logger?.Warn("vba-ribbon", $"リボンの XML にエラーが {errors.Count} 件あるため、ブックへ書きませんでした（警告 {warnings.Count} 件）。", targetPath: project.Root);
            return GraftResult<VbaRibbonRewritePreparation>.Fail(errors);
        }

        // 段階 3: ブックの状態の検査とパッケージの予行。
        if (await CheckOpenAsync(link.WorkbookFullName, ct).ConfigureAwait(true) is { } openIssue)
            return GraftResult<VbaRibbonRewritePreparation>.Fail(openIssue);

        var preflight = await Task.Run(() => Preflight(file, project.Id), ct).ConfigureAwait(true);
        if (!preflight.IsSuccess) return GraftResult<VbaRibbonRewritePreparation>.Fail(preflight.Issues);

        var plan = new VbaRibbonRewritePlan(
            project.Id, file, workbookName, targets, warnings, _paths.GetVbaWorkbookBackupDirectory(project.Id),
            preflight.Value.Size, preflight.Value.ModifiedUtc);
        return GraftResult<VbaRibbonRewritePreparation>.Ok(
            new VbaRibbonRewritePreparation(VbaRibbonRewritePreparationKind.Ready, plan, notes, recovery));
    }

    /// <summary>
    /// ブック全体を読めなかったとき、書き換えを止める問題（読み取りの結果の種類から写す。16.7 の結果の写し方と同じ: 対象外のブックは
    /// E819、そのほかは E821）。読めたとき（customUI が無い・一部の部品だけ読めないを含む）は null。
    /// </summary>
    private static GraftIssue? BookStatusIssue(VbaRibbonReadResult book, string workbookName) => book.Status switch
    {
        VbaRibbonReadStatus.Ok or VbaRibbonReadStatus.NoRibbon or VbaRibbonReadStatus.PartsUnreadable => null,
        VbaRibbonReadStatus.Encrypted => GraftIssue.Of(ErrorCode.E819, $"「{workbookName}」はパスワードで暗号化されているため、リボンを書き換えられません。"),
        VbaRibbonReadStatus.LegacyFormat => GraftIssue.Of(ErrorCode.E819, $"「{workbookName}」は古い形式（.xls）のため、リボンを書き換えられません（.xlsm で保存し直してください）。"),
        VbaRibbonReadStatus.FileNotFound => GraftIssue.Of(ErrorCode.E821, $"「{workbookName}」のファイルが見つかりません（{book.Reason}）。ブックは変えていません。"),
        VbaRibbonReadStatus.Locked => GraftIssue.Of(ErrorCode.E821, $"「{workbookName}」をほかのプログラムが使っているため読めません（{book.Reason}）。ブックは変えていません。"),
        _ => GraftIssue.Of(ErrorCode.E821, $"「{workbookName}」をブックとして読めません（{book.Reason}）。ブックは変えていません。"),
    };

    /// <summary>
    /// Excel で開いているか（16.6）。開いていれば、段階 F3 では書き換えずに止める（E819）。開いているかは既存の
    /// <see cref="IVbaBridge.ListOpenWorkbooksAsync"/>（ROT）で調べる。調べられないとき（この環境で使えない・Excel が応答しない）は止めず、
    /// 段階 7 の「排他で開く」に任せる（Excel が開いているブックは排他で開けないので、そこで E821 として止まる）。
    /// </summary>
    private async Task<GraftIssue?> CheckOpenAsync(string workbookFullName, CancellationToken ct)
    {
        if (!_bridge.IsSupported) return null;
        var listed = await _bridge.ListOpenWorkbooksAsync(ct).ConfigureAwait(true);
        if (!listed.IsSuccess)
        {
            Logger?.Info("vba-ribbon", "Excel で開いているブックの一覧を取れなかったため、排他で開けるかで確かめます。", targetPath: workbookFullName);
            return null;
        }

        var hit = listed.Value.FirstOrDefault(w => VbaRotNames.SamePath(w.FullName, workbookFullName));
        if (hit is null) return null;
        var name = VbaRibbonRewriteText.FileNameOf(workbookFullName);
        var reason = VbaRibbonRewriteText.IsAddinPath(workbookFullName)
            ? VbaRibbonRewriteText.OpenAddinReason(name)
            : VbaRibbonRewriteText.OpenWorkbookReason(name);
        return GraftIssue.Of(ErrorCode.E819, reason, path: workbookFullName);
    }

    private sealed record PreflightResult(long Size, DateTime ModifiedUtc);

    /// <summary>
    /// パッケージの予行（16.5 の段階 3）: zip の項目を列挙でき、暗号化・電子署名・読めない圧縮方式が無いこと、ブックのフォルダに
    /// 一時ファイルを作れること、空き容量（ブックのフォルダにブックの 1.2 倍、控えの保存先にブックの大きさ）。何も残さない。
    /// </summary>
    private GraftResult<PreflightResult> Preflight(string file, string projectId)
    {
        var name = VbaRibbonRewriteText.FileNameOf(file);
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists)
                return GraftResult<PreflightResult>.Fail(ErrorCode.E821, $"「{name}」のファイルが見つかりません。ブックは変えていません。", path: file);

            // 読み取り（15.2）と同じ共有の指定で開く。予行は中身を変えないので、ほかが読んでいても構わない。
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var inspected = VbaRibbonPackageWriter.Inspect(stream);
            if (!inspected.Succeeded) return GraftResult<PreflightResult>.Fail(WriteStatusIssue(inspected, name, file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<PreflightResult>.Fail(ErrorCode.E821,
                $"「{name}」を読めません（{ExceptionMessages.Describe(ex)}）。ブックは変えていません。", path: file);
        }

        // ブックのフォルダに一時ファイルを作れること（1 バイト書いて消す）。作れなければ、置き換えの段階で必ず止まる。
        var probe = VbaRibbonPackageWriter.NewTempPath(file, ".probe");
        try
        {
            using (var s = Io.CreateNew(probe))
            {
                s.WriteByte(0);
            }

            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(probe);
            return GraftResult<PreflightResult>.Fail(ErrorCode.E821,
                $"ブックのフォルダに一時ファイルを作れません（{ExceptionMessages.Describe(ex)}）。書き込みの権限と空き容量を確かめてください。ブックは変えていません。",
                path: file);
        }

        // 空き容量。取れない保存先（UNC など）では省く（予行で止めなくても、書き込みの IOException で止まり、元のブックは無傷）。
        var size = info.Length;
        var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        if (Io.FreeSpace(folder) is { } freeHere && freeHere < size + size / 5)
        {
            return GraftResult<PreflightResult>.Fail(ErrorCode.E821,
                $"ブックのフォルダの空き容量が足りません（必要 {Megabytes(size + size / 5)}・空き {Megabytes(freeHere)}）。ブックは変えていません。",
                path: file);
        }

        var backupDir = _paths.GetVbaWorkbookBackupDirectory(projectId);
        if (Io.FreeSpace(backupDir) is { } freeBackup && freeBackup < size)
        {
            return GraftResult<PreflightResult>.Fail(ErrorCode.E816,
                $"控えの保存先の空き容量が足りないため、ブックの控えを残せません（必要 {Megabytes(size)}・空き {Megabytes(freeBackup)}。保存先: {backupDir}）。ブックは変えていません。",
                path: backupDir);
        }

        return GraftResult<PreflightResult>.Ok(new PreflightResult(size, info.LastWriteTimeUtc));
    }

    // ------------------------------------------------------------------
    // 書き換え（16.5 の段階 5・7〜12・14）
    // ------------------------------------------------------------------

    /// <summary>
    /// 確認ダイアログで「書き換える」を選んだ計画を実行する（Excel で開いていないブックのファイルを書き換える。段階 F3）。
    /// 失敗は問題（E816・E819・E821・E823）で返し、<b>E823 以外ではブックは元の内容のまま</b>。成功したら同期基準を更新し、控えを掃除する。
    /// </summary>
    public async Task<GraftResult<VbaRibbonRewriteOutcome>> RewriteAsync(Project project, VbaRibbonRewritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        var link = project.Vba ?? throw new ArgumentException("VBA プロジェクトではありません。", nameof(project));

        using var writing = _beginWriting();
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            return await RewriteCoreAsync(project, link, plan, ct).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GraftResult<VbaRibbonRewriteOutcome>> RewriteCoreAsync(Project project, VbaLink link, VbaRibbonRewritePlan plan, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var file = plan.WorkbookPath;
        var name = plan.WorkbookName;

        // 確認のあいだの変化（このクラスの冒頭の説明）。ここで止まるときは、何も書いていない。
        if (await CheckUnchangedSincePlanAsync(project, link, plan, ct).ConfigureAwait(true) is { } changed)
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(changed);

        // 段階 5: 作業の記録。控えと一時ファイルの場所を先に決めて書く（途中で落ちたとき、どこを片付けるかを分かるように）。
        string backupPath;
        try
        {
            Directory.CreateDirectory(plan.BackupDirectory);
            backupPath = UniqueBackupPath(plan.BackupDirectory, name, _clock());
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
            BackupPath = backupPath,
            TempPath = VbaRibbonPackageWriter.NewTempPath(file),
            Stage = VbaRibbonRewriteStage.Started,
            ExpectedHashes = plan.Targets.ToDictionary(t => t.MirrorPath, t => t.MirrorHash, StringComparer.OrdinalIgnoreCase),
            StartedAt = _clock(),
        };
        if (!await TryWriteJobAsync(job).ConfigureAwait(true))
        {
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                $"作業の記録（{_paths.GetVbaRibbonJobFilePath(project.Id)}）を書けないため、書き換えを始めませんでした。データ保存先の空き容量と権限を確かめてください。ブックは変えていません。",
                path: file);
        }

        Logger?.Info("vba-ribbon",
            $"リボンの書き換えを始めます（{name}・{Megabytes(plan.BookSize)}・{string.Join("、", plan.Targets.Select(t => $"{t.MirrorPath} {Short(t.MirrorHash)}"))}）。",
            targetPath: file);
        try
        {
            return await RewriteStagesAsync(project, link, plan, job, watch).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 段階ごとに捕まえていない所での入出力の失敗（作業の記録の書き直しなど）。途中で落ちたときと同じ後始末をして、ブックの
            // いまの状態を知らせる（元のまま／書き換え済み）。後始末も記録に従うので、ここで新しい判断はしない。
            Logger?.Error("vba-ribbon", $"リボンの書き換えの途中で失敗しました（{ex.GetType().Name}）。", targetPath: file);
            var recovered = await RecoverInterruptedCoreAsync(project.Id).ConfigureAwait(true);
            var detail = recovered?.Message ?? "ブックの状態を確かめられませんでした。";
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                $"リボンの書き換えの途中で失敗しました（{ExceptionMessages.Describe(ex)}）。{detail}", path: file);
        }
    }

    private async Task<GraftResult<VbaRibbonRewriteOutcome>> RewriteStagesAsync(
        Project project, VbaLink link, VbaRibbonRewritePlan plan, VbaRibbonRewriteJob job, Stopwatch watch)
    {
        var file = plan.WorkbookPath;
        var name = plan.WorkbookName;
        var backupPath = job.BackupPath!;

        // 段階 7・8: 排他で開き、そのストリームから控えを写して照合する。
        var backup = await BackupAsync(file, backupPath).ConfigureAwait(true);
        if (!backup.IsSuccess)
        {
            DeleteJob(project.Id);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(backup.Issues);
        }

        job = job with { BackupSha256 = backup.Value.Sha256, Stage = VbaRibbonRewriteStage.BackedUp };
        if (!await TryWriteJobAsync(job).ConfigureAwait(true))
        {
            // 記録に控えのハッシュを残せないと、途中で落ちたときに「書き換え済みか」を判定できない。何も変えていないうちに止める。
            TryDelete(backupPath);
            DeleteJob(project.Id);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                "作業の記録を更新できないため、書き換えを止めました。ブックは変えていません。", path: file);
        }

        // 段階 9〜11: 控えを取ったときから変わっていないことを確かめ、一時ファイルへ書いて照合し、置き換える。
        var now = new FileInfo(file);
        if (!now.Exists || now.Length != backup.Value.Size || now.LastWriteTimeUtc != backup.Value.ModifiedUtc)
        {
            TryDelete(backupPath);
            DeleteJob(project.Id);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                $"控えを取ったあとに「{name}」がほかのプログラムに変えられたため、上書きしないよう止めました。ブックは変えていません。", path: file);
        }

        job = job with { Stage = VbaRibbonRewriteStage.Replacing };
        await WriteJobOrThrowAsync(job).ConfigureAwait(true);

        var requests = plan.Targets.Select(t => new VbaRibbonWriteRequest(t.Format, t.Xml)).ToList();
        var tempPath = job.TempPath!;
        var replace = Io.Replace;
        // 書き込みと照合は大きなブックで時間がかかるので、UI スレッドを塞がないようスレッドプールで行う。取り消しは受け付けない
        // （置き換えの途中で止めると、一時ファイルの後始末が呼び出し側に漏れる。RewriteFile は自分で最後まで片付ける）。
        var written = await Task.Run(() => VbaRibbonPackageWriter.RewriteFile(file, requests, 0, null, tempPath, replace)).ConfigureAwait(true);
        if (!written.Succeeded)
        {
            // 置き換えの前（または置き換えそのもの）で止まった。ブックは 1 バイトも変わっていないので、いま作った控えは消す
            // （何も変えていないのに世代だけが進むのを避ける。16.11）。
            TryDelete(backupPath);
            DeleteJob(project.Id);
            Logger?.Warn("vba-ribbon", $"リボンを書き換えられませんでした（{written.Status}）。ブックは元のままです。", targetPath: file, durationMs: watch.ElapsedMilliseconds);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(WriteStatusIssue(written, name, file));
        }

        job = job with { Stage = VbaRibbonRewriteStage.Replaced };
        await WriteJobOrThrowAsync(job).ConfigureAwait(true);

        // 段階 12: 置き換えたブックを読み直し、ミラーと同じかを確かめる。違えば控えから戻す（16.11）。
        var mismatch = await Task.Run(() => VerifyRewritten(link, plan.Targets)).ConfigureAwait(true);
        if (mismatch is not null)
        {
            return await RestoreAfterMismatchAsync(project, link, plan, job, mismatch, written).ConfigureAwait(true);
        }

        // 同期基準の更新（Push の基準は、実行が成功してから作る。モジュールと同じ約束）。部品名は書いた先（関係の指す部品）に直す。
        var issues = new List<GraftIssue>();
        try
        {
            var loaded = await _states.LoadAsync(project.Id, link.WorkbookFullName).ConfigureAwait(true);
            var before = loaded.Value;
            var state = before;
            foreach (var target in plan.Targets)
            {
                var part = written.Parts.First(p => p.Format == target.Format);
                state = state.WithRibbon(target.MirrorPath, new VbaRibbonSyncRecord(part.PartName, target.MirrorHash, target.MirrorHash, SyncedAt: _clock()));
            }

            if (!VbaSyncService.StatesEqual(before, state)) await _states.SaveAsync(project.Id, state).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ブックは書き換え済み。基準を書けないと「リボン未反映」のままに見えるが、次の取込で両側が同じ本文なので基準を作り直す（16.4）。
            issues.Add(GraftIssue.Of(ErrorCode.E821,
                $"リボンは書き換えましたが、同期の記録を保存できませんでした（{ExceptionMessages.Describe(ex)}）。次の「Excel から取込」で記録を作り直します。",
                path: file, severity: Severity.Warning));
        }

        // 段階 14: 後始末（作業の記録を消す・控えの掃除）。掃除の失敗は警告だけ。
        DeleteJob(project.Id);
        if (PruneBackups(plan.BackupDirectory) is { } pruneIssue) issues.Add(pruneIssue);

        Logger?.Info("vba-ribbon",
            $"リボンをブックへ書き換えました（{name}・{string.Join("、", written.Parts.Select(p => p.PartName + (p.AddedRelationship ? "（新規）" : string.Empty)))}・控え {VbaRibbonRewriteText.FileNameOf(backupPath)}）。",
            targetPath: file, durationMs: watch.ElapsedMilliseconds);
        var outcome = new VbaRibbonRewriteOutcome(file, backupPath, plan.Targets.Select(t => t.MirrorPath).ToList(), written.Parts.Any(p => p.AddedRelationship));
        return GraftResult<VbaRibbonRewriteOutcome>.Ok(outcome, issues);
    }

    /// <summary>
    /// 確認のあいだに、ミラーのファイル・ブックが変わっていないか・Excel で開かれていないかを確かめる（変わっていれば止める問題）。
    /// </summary>
    private async Task<GraftIssue?> CheckUnchangedSincePlanAsync(Project project, VbaLink link, VbaRibbonRewritePlan plan, CancellationToken ct)
    {
        var mirror = await ReadMirrorAsync(project.Root, ct).ConfigureAwait(true);
        foreach (var target in plan.Targets)
        {
            if (!mirror.TryGetValue(target.Format, out var entry) || !entry.File.IsReadable
                || !string.Equals(VbaTextNormalizer.Hash(entry.File.Text!), target.MirrorHash, StringComparison.Ordinal))
            {
                return GraftIssue.Of(ErrorCode.E821,
                    $"確認のあいだに {target.MirrorPath} が変わったため、書き換えませんでした。ブックは変えていません。もう一度「リボンをブックへ反映…」を選んでください。",
                    path: target.MirrorPath);
            }
        }

        if (await CheckOpenAsync(link.WorkbookFullName, ct).ConfigureAwait(true) is { } openIssue) return openIssue;

        var info = new FileInfo(plan.WorkbookPath);
        if (!info.Exists)
            return GraftIssue.Of(ErrorCode.E821, $"「{plan.WorkbookName}」のファイルが見つかりません。ブックは変えていません。", path: plan.WorkbookPath);
        if (info.Length != plan.BookSize || info.LastWriteTimeUtc != plan.BookModifiedUtc)
        {
            return GraftIssue.Of(ErrorCode.E821,
                $"確認のあいだに「{plan.WorkbookName}」のファイルが変わった（Excel などで保存された見込み）ため、書き換えませんでした。ブックは変えていません。もう一度「リボンをブックへ反映…」を選んでください。",
                path: plan.WorkbookPath);
        }

        return null;
    }

    private sealed record BackupInfo(string Sha256, long Size, DateTime ModifiedUtc);

    /// <summary>
    /// 段階 7（排他で開く）と段階 8（控え）。排他で開いたストリームから控えを写すので、写しているあいだにほかが書き込むことは無い。
    /// 写しながら元の SHA-256 を取り、書いた控えを読み直した SHA-256 と比べる。控えを書けない・照合が合わないときは、作りかけを消して
    /// E816（書き換えない）。
    /// </summary>
    private async Task<GraftResult<BackupInfo>> BackupAsync(string file, string backupPath)
    {
        var name = VbaRibbonRewriteText.FileNameOf(file);
        var source = await OpenExclusiveAsync(file).ConfigureAwait(true);
        if (!source.IsSuccess) return GraftResult<BackupInfo>.Fail(source.Issues);

        string sourceHash;
        long size;
        DateTime modified;
        using (var input = source.Value)
        {
            size = input.Length;
            modified = File.GetLastWriteTimeUtc(file);
            try
            {
                using var output = Io.CreateNew(backupPath);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int n;
                while ((n = await input.ReadAsync(buffer).ConfigureAwait(true)) > 0)
                {
                    sha.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(true);
                }

                output.Flush(flushToDisk: true);
                sourceHash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(backupPath);
                var why = ex is IOException io && IsDiskFull(io) ? "ディスクの空きが足りません" : ExceptionMessages.Describe(ex);
                Logger?.Warn("vba-ribbon", $"ブックの控えを書けませんでした（{ex.GetType().Name}）。書き換えません。", targetPath: file);
                return GraftResult<BackupInfo>.Fail(ErrorCode.E816,
                    $"「{name}」の控えを保存できないため、書き換えませんでした（{why}。保存先: {Path.GetDirectoryName(backupPath)}）。ブックは変えていません。",
                    path: backupPath);
            }
        }

        string? backupHash;
        try
        {
            backupHash = Sha256OfFile(backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            backupHash = null;
        }

        if (!string.Equals(backupHash, sourceHash, StringComparison.Ordinal))
        {
            TryDelete(backupPath);
            return GraftResult<BackupInfo>.Fail(ErrorCode.E816,
                $"「{name}」の控えを読み直したら元と一致しなかったため、書き換えませんでした（保存先: {Path.GetDirectoryName(backupPath)}）。ブックは変えていません。",
                path: backupPath);
        }

        Logger?.Info("vba-ribbon", $"ブックの控えを残しました（{VbaRibbonRewriteText.FileNameOf(backupPath)}・{Megabytes(size)}・{Short(sourceHash)}）。", targetPath: file);
        return GraftResult<BackupInfo>.Ok(new BackupInfo(sourceHash, size, modified));
    }

    /// <summary>
    /// 段階 7: ブックを <see cref="FileShare.None"/> で開けるまで待つ（最大 <see cref="VbaRibbonRewriteIo.ExclusiveTimeout"/>、
    /// <see cref="VbaRibbonRewriteIo.ExclusiveInterval"/> 間隔）。開けなければ E821（使用中。<c>~$</c> の所有者ファイルがあれば添える）。
    /// </summary>
    private async Task<GraftResult<FileStream>> OpenExclusiveAsync(string file)
    {
        var name = VbaRibbonRewriteText.FileNameOf(file);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return GraftResult<FileStream>.Ok(Io.OpenExclusive(file));
            }
            catch (FileNotFoundException)
            {
                return GraftResult<FileStream>.Fail(ErrorCode.E821, $"「{name}」のファイルが見つかりません。ブックは変えていません。", path: file);
            }
            catch (DirectoryNotFoundException)
            {
                return GraftResult<FileStream>.Fail(ErrorCode.E821, $"「{name}」のフォルダが見つかりません。ブックは変えていません。", path: file);
            }
            catch (UnauthorizedAccessException ex)
            {
                return GraftResult<FileStream>.Fail(ErrorCode.E821,
                    $"「{name}」を開く権限がありません（{ExceptionMessages.Describe(ex)}）。ブックは変えていません。", path: file);
            }
            catch (IOException) when (deadline.Elapsed < Io.ExclusiveTimeout)
            {
                await Task.Delay(Io.ExclusiveInterval).ConfigureAwait(true);
            }
            catch (IOException)
            {
                var owner = OwnerFileExists(file)
                    ? $"（同じフォルダに Excel の所有者ファイル「~${name}」があります。別の Excel や別の PC で開いていないか確かめてください）"
                    : string.Empty;
                Logger?.Warn("vba-ribbon", $"ブックを排他で開けませんでした（{Io.ExclusiveTimeout.TotalSeconds:0} 秒待ちました）。", targetPath: file);
                return GraftResult<FileStream>.Fail(ErrorCode.E821,
                    $"「{name}」をほかのプログラムが使っているため、書き換えませんでした{owner}。使っているプログラム（別の Excel・同期ソフト・ウイルス対策ソフトなど）を閉じてから、もう一度お試しください。ブックは変えていません。",
                    path: file);
            }
        }
    }

    /// <summary>
    /// Excel の所有者ファイル（<c>~$&lt;ブック名&gt;</c>。長い名前では先頭 2 文字が置き換わる）があるか。古いものが残っていることもあるので、
    /// これだけで「開いている」とは決めない（案内に添えるだけ）。
    /// </summary>
    private static bool OwnerFileExists(string file)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
            var name = Path.GetFileName(file);
            return File.Exists(Path.Combine(folder, "~$" + name))
                   || (name.Length > 2 && File.Exists(Path.Combine(folder, "~$" + name[2..])));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 段階 12: 置き換えたブックを読み取り（取込と同じ <see cref="PackageReader"/>）で読み直し、書いた形式の部品の本文がミラーと同じかを
    /// 確かめる。合えば null、合わなければ理由。
    /// </summary>
    private string? VerifyRewritten(VbaLink link, IReadOnlyList<VbaRibbonRewriteTarget> targets)
    {
        var reader = PackageReader ?? (path => VbaRibbonPackageReader.Read(path));
        var read = CheckParts(reader(link.WorkbookFullName));
        if (read.Status != VbaRibbonReadStatus.Ok) return $"書き換えたブックのリボンを読めません（{read.Reason ?? read.Status.ToString()}）";
        foreach (var target in targets)
        {
            var part = read.Parts.FirstOrDefault(p => VbaRibbonMirrorLayout.FormatOf(p) == target.Format && p.IsReadable);
            var expected = target.Xml.Length > 0 && target.Xml[0] == '﻿' ? target.Xml[1..] : target.Xml;
            if (part?.Xml is null) return $"書き換えたブックに {VbaRibbonMirrorLayout.FileName(target.Format)} の部品が見つかりません";
            if (!string.Equals(part.Xml, expected, StringComparison.Ordinal)) return $"{target.MirrorPath} の本文がミラーと違います";
        }

        return null;
    }

    /// <summary>
    /// 仕上げの照合が合わなかったとき（16.11）: 控えを一時ファイルへ写し、SHA-256 を確かめてから置き換える。戻せたら E821（ブックは
    /// 元の内容）、戻せなければ E823（同期状態に要確認の印を立て、控えの場所を示す）。
    /// </summary>
    private async Task<GraftResult<VbaRibbonRewriteOutcome>> RestoreAfterMismatchAsync(
        Project project, VbaLink link, VbaRibbonRewritePlan plan, VbaRibbonRewriteJob job, string mismatch, VbaRibbonWriteResult written)
    {
        var file = plan.WorkbookPath;
        var backupPath = job.BackupPath!;
        Logger?.Error("vba-ribbon", $"書き換えたブックの照合が合いませんでした。控えから戻します（{mismatch}）。", targetPath: file);

        job = job with { Stage = VbaRibbonRewriteStage.Restoring, RestoreTempPath = VbaRibbonPackageWriter.NewTempPath(file, ".restore") };
        await WriteJobOrThrowAsync(job).ConfigureAwait(true);
        var restoreTemp = job.RestoreTempPath!;
        var backupSha = job.BackupSha256!;
        var restored = await Task.Run(() => RestoreFromBackup(backupPath, backupSha, file, restoreTemp)).ConfigureAwait(true);
        if (restored)
        {
            DeleteJob(project.Id);
            Logger?.Warn("vba-ribbon", "控えからブックを元の内容に戻しました。", targetPath: file);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                $"書き換えたブックを読み直したら、期待と違っていました（{mismatch}）。控えから元の内容に戻しました。ブックは元の内容です（控え: {backupPath}）。",
                path: file);
        }

        // 戻せなかった。ブックは期待と違う内容のまま。同期状態に要確認の印を立て、次の同期で衝突として扱わせる（黙って上書きしない）。
        try
        {
            var loaded = await _states.LoadAsync(project.Id, link.WorkbookFullName).ConfigureAwait(true);
            var state = loaded.Value;
            var records = state.RibbonRecordsByFormat();
            foreach (var target in plan.Targets)
            {
                var partName = written.Parts.FirstOrDefault(p => p.Format == target.Format)?.PartName ?? VbaRibbonMirrorLayout.DefaultPartName(target.Format);
                var record = records.TryGetValue(target.Format, out var existing)
                    ? existing with { NeedsReview = true }
                    : new VbaRibbonSyncRecord(partName, target.MirrorHash, string.Empty, NeedsReview: true, SyncedAt: _clock());
                state = state.WithRibbon(target.MirrorPath, record);
            }

            await _states.SaveAsync(project.Id, state).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.Error("vba-ribbon", $"要確認の印を同期状態に書けませんでした（{ex.GetType().Name}）。", targetPath: file);
        }

        DeleteJob(project.Id);
        Logger?.Error("vba-ribbon", "控えからブックを戻せませんでした（要確認）。", targetPath: file);
        return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E823,
            $"書き換えたブックを読み直したら期待と違っていて（{mismatch}）、控えから元の内容へ戻すこともできませんでした。控え: {backupPath}（フォルダ: {plan.BackupDirectory}）",
            path: file);
    }

    /// <summary>控えでブックを置き換える（控え → 一時ファイル → SHA-256 の照合 → 置き換え → ブックの SHA-256 の照合）。</summary>
    private bool RestoreFromBackup(string backupPath, string backupSha256, string file, string restoreTemp)
    {
        try
        {
            using (var input = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = Io.CreateNew(restoreTemp))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            if (!string.Equals(Sha256OfFile(restoreTemp), backupSha256, StringComparison.Ordinal)) return false;
            Io.Replace(restoreTemp, file);
            return string.Equals(Sha256OfFile(file), backupSha256, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.Error("vba-ribbon", $"控えからの復元に失敗しました（{ex.GetType().Name}）。", targetPath: file);
            return false;
        }
        finally
        {
            TryDelete(restoreTemp);
        }
    }

    // ------------------------------------------------------------------
    // 途中で落ちたときの後始末（16.11）
    // ------------------------------------------------------------------

    /// <summary>このプロジェクトに、途中で終わった書き換えの記録が残っているか（ファイルがあるかだけを見る。錠を取らない）。</summary>
    public bool HasInterruptedRewrite(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return File.Exists(_paths.GetVbaRibbonJobFilePath(project.Id));
    }

    /// <summary>
    /// 途中で終わった書き換えの後始末（16.11 の「Graft が途中で落ちた」の行）。作業の記録が無ければ null。あれば、一時ファイルを消し、
    /// ブックのハッシュを控えと比べて「元のまま／書き換え済み」を決め、記録を消して、知らせの文を返す。次の起動（プロジェクトペインの
    /// バッジの調べ直し）と、次のリボンの操作（<see cref="PrepareRewriteAsync"/>）から呼ぶ。
    /// </summary>
    public async Task<VbaRibbonRecovery?> RecoverInterruptedAsync(Project project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!HasInterruptedRewrite(project)) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            return await RecoverInterruptedCoreAsync(project.Id).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<VbaRibbonRecovery?> RecoverInterruptedCoreAsync(string projectId)
    {
        var path = _paths.GetVbaRibbonJobFilePath(projectId);
        if (!File.Exists(path)) return null;

        VbaRibbonRewriteJob? job;
        try
        {
            var text = await File.ReadAllTextAsync(path).ConfigureAwait(true);
            job = JsonSerializer.Deserialize<VbaRibbonRewriteJob>(text, JsonFileStore.DefaultOptions);
            if (job is null || job.Version != 1 || string.IsNullOrEmpty(job.WorkbookPath)) job = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            job = null;
        }

        var removed = 0;
        var state = VbaRibbonInterruptedState.Unknown;
        if (job is not null)
        {
            // 一時ファイル（~graft-*.tmp）を消す。記録にある名前でも、形が違うものは消さない（記録が書き換えられていても、利用者の
            // ファイルを消さないため）。
            foreach (var temp in new[] { job.TempPath, job.RestoreTempPath })
            {
                if (temp is null || !IsOurTempFile(temp, job.WorkbookPath)) continue;
                if (TryDelete(temp)) removed++;
            }

            // 控えを書き終える前に落ちたなら、作りかけの控えを消す（中身が途中までのファイルを「控え」として残さない）。
            if (job.BackupSha256 is null && job.BackupPath is { } partial
                && VbaWorkbookBackupNaming.Parse(Path.GetFileName(partial)) is not null)
            {
                TryDelete(partial);
            }

            var bookSha = await Task.Run(() => TrySha256(job.WorkbookPath)).ConfigureAwait(true);
            IReadOnlyDictionary<string, string>? ribbonHashes = null;
            if (bookSha is not null && job.BackupSha256 is not null && !string.Equals(bookSha, job.BackupSha256, StringComparison.OrdinalIgnoreCase))
            {
                var read = await Task.Run(() => CheckParts(VbaRibbonPackageReader.Read(job.WorkbookPath))).ConfigureAwait(true);
                if (read.Status == VbaRibbonReadStatus.Ok)
                {
                    ribbonHashes = read.Parts.Where(p => p.IsReadable)
                        .GroupBy(p => VbaRibbonMirrorLayout.ToRelativePath(VbaRibbonMirrorLayout.FormatOf(p)), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => VbaTextNormalizer.Hash(g.First().Xml!), StringComparer.OrdinalIgnoreCase);
                }
            }

            state = VbaRibbonRecoveryLogic.Judge(job, bookSha, ribbonHashes);
        }

        DeleteJob(projectId);
        var message = VbaRibbonRecoveryLogic.Message(job, state);
        Logger?.Warn("vba-ribbon", $"途中で終わったリボンの書き換えを片付けました（ブック: {state}・一時ファイル {removed} 件）。", targetPath: job?.WorkbookPath);
        return new VbaRibbonRecovery(job, state, message, removed);
    }

    private static bool IsOurTempFile(string temp, string workbookPath)
    {
        try
        {
            var name = Path.GetFileName(temp);
            return name.StartsWith("~graft-", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Path.GetDirectoryName(Path.GetFullPath(temp)), Path.GetDirectoryName(Path.GetFullPath(workbookPath)), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 作業の記録・控え・小道具
    // ------------------------------------------------------------------

    private async Task<bool> TryWriteJobAsync(VbaRibbonRewriteJob job)
    {
        try
        {
            await WriteJobOrThrowAsync(job).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.Warn("vba-ribbon", $"作業の記録を書けませんでした（{ex.GetType().Name}）。", targetPath: job.WorkbookPath);
            return false;
        }
    }

    private async Task WriteJobOrThrowAsync(VbaRibbonRewriteJob job)
    {
        var path = _paths.GetVbaRibbonJobFilePath(job.ProjectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 途中で取り消させない（記録が書きかけになると、後始末の判断を誤る）。JsonFileStore は一時ファイル経由で原子的に書く。
        await _jobStore.WriteAsync(path, job, JsonFileStore.DefaultOptions, CancellationToken.None).ConfigureAwait(true);
    }

    private void DeleteJob(string projectId) => TryDelete(_paths.GetVbaRibbonJobFilePath(projectId));

    /// <summary>控えのファイル名（同じ秒に既にあれば連番。<see cref="VbaWorkbookBackupNaming"/>）。</summary>
    private static string UniqueBackupPath(string directory, string workbookName, DateTimeOffset at)
    {
        var fileName = VbaWorkbookBackupNaming.BuildFileName(workbookName, at);
        var path = Path.Combine(directory, fileName);
        for (var sequence = 2; File.Exists(path); sequence++)
        {
            path = Path.Combine(directory, VbaWorkbookBackupNaming.WithSequence(fileName, sequence));
        }

        return path;
    }

    /// <summary>
    /// 控えの掃除（16.11。書き換えが成功したあとだけ）。世代数も容量もプロジェクトのフォルダ全体で数え、最新の 1 件は残す。
    /// 消せなかったときは警告を返す（書き換えは成功している）。
    /// </summary>
    private GraftIssue? PruneBackups(string directory)
    {
        try
        {
            var entries = Directory.EnumerateFiles(directory)
                .Select(f => (Path: f, Name: VbaWorkbookBackupNaming.Parse(Path.GetFileName(f))))
                .Where(x => x.Name is not null)
                .Select(x => new VbaWorkbookBackupEntry(Path.GetFileName(x.Path), x.Name!, new FileInfo(x.Path).Length))
                .ToList();
            var plan = VbaWorkbookBackupPruner.Plan(entries, MaxBackupGenerations(), MaxBackupTotalMB() * 1024L * 1024L);
            foreach (var entry in plan.Delete) File.Delete(Path.Combine(directory, entry.FileName));
            if (plan.Delete.Count > 0) Logger?.Info("vba-ribbon", $"ブックの控えを {plan.Delete.Count} 件掃除しました。", targetPath: directory);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftIssue.Of(ErrorCode.E816,
                $"古いブックの控えを掃除できませんでした（{ExceptionMessages.Describe(ex)}。保存先: {directory}）。リボンの書き換えは済んでいます。",
                path: directory, severity: Severity.Warning);
        }
    }

    /// <summary>書き換えの結果の種類を、利用者向けの問題に写す（16.7: 対象外のブックは E819、そのほかは E821）。</summary>
    private static GraftIssue WriteStatusIssue(VbaRibbonWriteResult result, string workbookName, string file)
    {
        var code = result.Status is VbaRibbonWriteStatus.Encrypted or VbaRibbonWriteStatus.Signed or VbaRibbonWriteStatus.LegacyFormat
            ? ErrorCode.E819
            : ErrorCode.E821;
        var tail = code == ErrorCode.E821 && !(result.Problem ?? string.Empty).Contains("変えていません", StringComparison.Ordinal)
            ? "ブックは変えていません。"
            : string.Empty;
        return GraftIssue.Of(code, $"「{workbookName}」: {result.Problem}{tail}", path: file);
    }

    /// <summary>ディスクがいっぱいの入出力の失敗か（Windows の ERROR_DISK_FULL・ERROR_HANDLE_DISK_FULL、Unix の ENOSPC）。</summary>
    internal static bool IsDiskFull(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 0x70 or 0x27 || ex.HResult == 28;
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string? TrySha256(string path)
    {
        try
        {
            return File.Exists(path) ? Sha256OfFile(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Megabytes(long bytes)
        => (bytes / 1024d / 1024d).ToString(bytes < 10 * 1024 * 1024 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>ハッシュの先頭 8 桁（ログ用。16.12: 本文は書かない）。</summary>
    private static string Short(string hash) => hash.Length > 8 ? hash[..8] : hash;
}
