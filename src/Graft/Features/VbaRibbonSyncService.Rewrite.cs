using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Platform;

namespace Graft.Features;

/// <summary>「リボンの XML を検査」（16.8 の手動の検査）の、ミラーのファイル 1 つ分の結果。</summary>
/// <param name="MirrorPath">ミラーの相対パス。</param>
/// <param name="Validation">検査の結果（読めなかったときは null）。</param>
/// <param name="Problem">ファイルを読めなかった理由（読めたときは null）。</param>
public sealed record VbaRibbonInspection(string MirrorPath, VbaRibbonValidation? Validation, string? Problem);

/// <summary>
/// <see cref="VbaRibbonSyncService"/> の分割ファイル。ブックのファイルの書き換え（仕様書_ブックの情報と依頼の観点 16.5。段階 F3）。
/// <para>
/// 【経路は 2 つ】 Excel で開いていないブックは、COM を使わずにファイルだけを書き換える（段階 F3）。<b>開いているブック</b>は、
/// 段階 5 と 7 のあいだに「保存して閉じる」（<see cref="IVbaBridge.SaveAndCloseWorkbookAsync"/>）、段階 14 の前に「同じ Excel で開き直す」
/// （<see cref="IVbaBridge.ReopenWorkbookAsync"/>）を足す（段階 F4。16.6）。止めるブック（読み取り専用・保護ビュー・共有・自動保存・
/// OneDrive・開いているアドイン・マクロの実行中）は、閉じる前に <see cref="VbaWorkbookRewriteGuard"/> で止める（E819・E813）。
/// <b>閉じたあとで止まった場合は、どの段階でも、すべて開き直してから知らせる</b>（利用者の Excel の画面を、失敗したまま閉じて放置しない）。
/// 例外は、控えから戻せなかった E823（壊れているかもしれないブックでマクロを動かさないため、開き直さずに券を捨てる）。
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
    /// 書き換えの段階の名前を知らせる口（16.5「ブックを保存して閉じています」「控えを残しています」「リボンを書き換えています」
    /// 「ブックを開き直しています」。<see cref="VbaRibbonRewriteText"/> の定数）。画面は中央ペインの処理中の表示に出す。未設定なら何もしない。
    /// </summary>
    public Action<string>? Progress { get; set; }

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
    /// <param name="project">VBA プロジェクト。</param>
    /// <param name="ct">取り消し。</param>
    /// <param name="overwriteConflicts">
    /// 衝突（ブックのリボンとミラーの両方が変わっている）のうち、利用者が衝突ダイアログで「Graft 側で上書き」を選んだもの（ミラーのパス。段階 F5）。
    /// 反映の対象に入れる（確認ダイアログ・控え・書き換えの手順は省かない。控えはブックの丸ごとの控えが兼ねる。16.4）。null なら、衝突は反映しない。
    /// </param>
    public async Task<GraftResult<VbaRibbonRewritePreparation>> PrepareRewriteAsync(
        Project project, CancellationToken ct = default, IReadOnlyCollection<string>? overwriteConflicts = null)
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
            return await PrepareCoreAsync(project, link, overwriteConflicts, ct).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GraftResult<VbaRibbonRewritePreparation>> PrepareCoreAsync(
        Project project, VbaLink link, IReadOnlyCollection<string>? overwriteConflicts, CancellationToken ct)
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
        // 衝突のうち「Graft 側で上書き」と言われたものは、反映の対象に入れる（残りの衝突だけを知らせる）。
        bool Overwrites(VbaRibbonSyncItem item)
            => item.Action == VbaRibbonSyncAction.Conflict && overwriteConflicts is not null
               && overwriteConflicts.Contains(item.MirrorPath, StringComparer.OrdinalIgnoreCase);
        var unresolved = work.Plan.Where(VbaRibbonSyncAction.Conflict).Where(i => !Overwrites(i)).Select(i => i.MirrorPath).ToList();
        var notes = new List<GraftIssue>(work.LoadIssues);
        notes.AddRange(VbaRibbonSyncIssues.Collect(work.Plan, unresolved, "ブックへは反映しませんでした"));
        var conflicts = ConflictItemsOf(work, unresolved);

        var pushes = work.Plan.Items.Where(i => i.Action == VbaRibbonSyncAction.Push || Overwrites(i)).OrderBy(i => i.Format).ToList();
        if (pushes.Count == 0)
        {
            return GraftResult<VbaRibbonRewritePreparation>.Ok(
                new VbaRibbonRewritePreparation(VbaRibbonRewritePreparationKind.NothingToDo, null, notes, recovery) { Conflicts = conflicts });
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

        // 段階 3: ブックの状態の検査（開いているか・止めるブックか。16.6）とパッケージの予行。
        // 開いていても止めるブックでなければ、閉じ開きの経路で書き換える（F4）。ここで止まるのは何も閉じる前なので、何も変えていない。
        var open = await InspectOpenStateAsync(link.WorkbookFullName, ct).ConfigureAwait(true);
        if (open.Stop is { } stop) return GraftResult<VbaRibbonRewritePreparation>.Fail(stop);

        var preflight = await Task.Run(() => Preflight(file, project.Id), ct).ConfigureAwait(true);
        if (!preflight.IsSuccess) return GraftResult<VbaRibbonRewritePreparation>.Fail(preflight.Issues);

        var plan = new VbaRibbonRewritePlan(
            project.Id, file, workbookName, targets, warnings, _paths.GetVbaWorkbookBackupDirectory(project.Id),
            preflight.Value.Size, preflight.Value.ModifiedUtc, open.State.IsOpen ? open.State : null,
            AutoMacros: open.State.IsOpen ? VbaRibbonCallbackMatcher.FindStandardProcedures(modules, "Auto_Open", "Auto_Close") : null);
        return GraftResult<VbaRibbonRewritePreparation>.Ok(
            new VbaRibbonRewritePreparation(VbaRibbonRewritePreparationKind.Ready, plan, notes, recovery) { Conflicts = conflicts });
    }

    /// <summary>
    /// 衝突したリボンの、両側の本文つきの一覧（衝突ダイアログの表示用。左: ブックの部品の XML、右: ミラーの本文）。
    /// どちらかが読めない形式は載せない（読めない形式は、衝突ではなく警告として扱われている）。
    /// </summary>
    internal static List<VbaConflictItem> ConflictItemsOf(VbaRibbonWork work, IEnumerable<string> paths)
    {
        var items = new List<VbaConflictItem>();
        foreach (var path in paths)
        {
            var item = work.Plan.Items.FirstOrDefault(i => string.Equals(i.MirrorPath, path, StringComparison.OrdinalIgnoreCase));
            if (item is null || item.Action != VbaRibbonSyncAction.Conflict) continue;
            var bookPart = work.Book.Parts.FirstOrDefault(p => VbaRibbonMirrorLayout.FormatOf(p) == item.Format && p.IsReadable);
            if (bookPart?.Xml is not { } bookXml) continue;
            if (!work.Mirror.TryGetValue(item.Format, out var mirror) || mirror.File.Text is not { } mirrorXml) continue;
            items.Add(VbaConflictItem.ForRibbon(item.MirrorPath, bookXml, mirrorXml, item.ConflictReason));
        }

        return items;
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
    /// ブックが Excel で開いているか・止めるブックか（16.6）を調べる（<see cref="IVbaBridge.InspectWorkbookForRewriteAsync"/>。何も変えない）。
    /// <list type="bullet">
    /// <item>開いていない・この環境で使えない: <see cref="VbaWorkbookRewriteState.NotOpen"/>（ファイルだけを書き換える経路）。</item>
    /// <item>開いていて、止めるブック（<see cref="VbaWorkbookRewriteGuard"/>）: 止める問題（E819・E813）。何も閉じない。</item>
    /// <item>開いていて応答しない（E805）: 止める。セル編集中・ダイアログ表示中の Excel を、閉じる方向へ進めない。</item>
    /// <item>そのほかの失敗（Excel と通信できない E801 など）: 止めず、開いていないものとして進める。段階 7 の排他で開く検査が、
    /// 本当に使われていれば E821 で止める（従来の F3 と同じ最後の砦）。</item>
    /// </list>
    /// </summary>
    private async Task<(VbaWorkbookRewriteState State, IReadOnlyList<GraftIssue>? Stop)> InspectOpenStateAsync(string workbookFullName, CancellationToken ct)
    {
        if (!_bridge.IsSupported) return (VbaWorkbookRewriteState.NotOpen, null);
        var inspected = await _bridge.InspectWorkbookForRewriteAsync(workbookFullName, ct).ConfigureAwait(true);
        if (!inspected.IsSuccess)
        {
            if (inspected.Issues.Any(i => i.Code == ErrorCode.E805)) return (VbaWorkbookRewriteState.NotOpen, inspected.Issues);
            Logger?.Info("vba-ribbon", "ブックの Excel での状態を調べられなかったため、排他で開けるかで確かめます。", targetPath: workbookFullName);
            return (VbaWorkbookRewriteState.NotOpen, null);
        }

        var state = inspected.Value;
        if (VbaWorkbookRewriteGuard.Check(state, workbookFullName) is { } stop) return (state, new[] { stop });
        return (state, null);
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
            WorkbookFullName = link.WorkbookFullName,
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
            $"リボンの書き換えを始めます（{name}・{Megabytes(plan.BookSize)}・{(plan.IsOpen ? "Excel で開いている" : "開いていない")}・{string.Join("、", plan.Targets.Select(t => $"{t.MirrorPath} {Short(t.MirrorHash)}"))}）。",
            targetPath: file);

        // 閉じたブックの券（段階 6 で受け取り、段階 13 か、失敗の後始末で必ず 1 回だけ使う）。
        var held = new ReopenHolder(link.WorkbookFullName);
        try
        {
            GraftResult<VbaRibbonRewriteOutcome> result;
            try
            {
                // 段階 6: 保存して閉じる（開いているときだけ）。
                if (plan.IsOpen)
                {
                    var closed = await SaveAndCloseForRewriteAsync(project, plan.WorkbookName, plan.WorkbookPath, job, held, ct).ConfigureAwait(true);
                    if (closed.Failure is not null) return closed.Failure;
                    job = closed.Job!;
                }

                result = await RewriteStagesAsync(project, link, plan, job, watch, held).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 段階ごとに捕まえていない所での入出力の失敗（作業の記録の書き直しなど）。途中で落ちたときと同じ後始末をして、ブックの
                // いまの状態を知らせる（元のまま／書き換え済み）。後始末も記録に従うので、ここで新しい判断はしない。
                // 知らせの文は「実行中の失敗用」（duringRun）: Graft はまだ動いているので「Graft が終了したため」「閉じたまま」は言わず、
                // このあと ReopenAfterFailureAsync が「開き直しました」を足す（不具合 4。一緒に出ると矛盾した案内になる）。
                Logger?.Error("vba-ribbon", $"リボンの書き換えの途中で失敗しました（{ex.GetType().Name}）。", targetPath: file);
                var recovered = await RecoverInterruptedCoreAsync(project.Id, duringRun: true).ConfigureAwait(true);
                var detail = recovered?.Message ?? "ブックの状態を確かめられませんでした。";
                result = GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                    $"リボンの書き換えの途中で失敗しました（{ExceptionMessages.Describe(ex)}）。{detail}", path: file);
            }

            // 段階 13（失敗のとき）: 閉じていたら、すべて開き直してから知らせる。成功のときは RewriteStagesAsync の中で開き直し済み
            // （段階 14 の前。16.5 の表の順）。
            return result.IsSuccess ? result : await ReopenAfterFailureAsync(result, held).ConfigureAwait(true);
        }
        finally
        {
            // 想定外の例外（IOException・UnauthorizedAccessException 以外。JsonException・ArgumentException・取り消しなど）で抜けたときは、
            // 保存して閉じたブックを開き直してから抜ける（利用者の Excel で、ブックが閉じたまま黙って残るのを防ぐ）。置き換えは原子的で、
            // Graft 自身は途中のブックを残さないので、開き直してよい。E823（控えから戻せなかった）は ReopenAfterFailureAsync が券を捨て済みで、
            // ここには券が残らない。開き直しの失敗は、すでに別の例外が飛んでいるので握りつぶさずログだけに残す。
            await ReopenAfterUnexpectedExceptionAsync(held, project.Id).ConfigureAwait(true);
            // 券を残さない（Excel のプロセスが Graft の終了まで残るのを防ぐ）。
            await held.DiscardAsync(_bridge).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 想定外の例外で抜けるときの後始末: 券が残っていれば（閉じたまま開き直していない）、開き直す。ふつうの経路（成功・失敗の返り値）では
    /// 券は既に使われているので何もしない。例外はここでは外へ出さない（元の例外を隠さないため）。
    /// 開き直せたら、作業の記録（まだ残っている）の「Graft が閉じた」印を下ろす: 残したままだと、次の後始末の知らせが、もう開き直したブックを
    /// 「Excel では閉じたままです」と案内してしまう。
    /// </summary>
    private async Task ReopenAfterUnexpectedExceptionAsync(ReopenHolder held, string projectId)
    {
        if (!held.HasTicket) return;
        try
        {
            Logger?.Warn("vba-ribbon", "想定外の例外で抜けるため、閉じたブックを開き直します。", targetPath: held.WorkbookFullName);
            var report = await ReopenAsync(held).ConfigureAwait(true);
            if (report is { Reopened: true }) await ClearClosedFlagInJobAsync(projectId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger?.Error("vba-ribbon", $"想定外の例外のあとの開き直しにも失敗しました（{ex.GetType().Name}）。", targetPath: held.WorkbookFullName);
        }
    }

    /// <summary>作業の記録が残っていれば、「Graft が Excel で閉じた」印を下ろして書き直す（読めない・書けないときは何もしない）。</summary>
    private async Task ClearClosedFlagInJobAsync(string projectId)
    {
        var path = _paths.GetVbaRibbonJobFilePath(projectId);
        try
        {
            if (!File.Exists(path)) return;
            var job = JsonSerializer.Deserialize<VbaRibbonRewriteJob>(await File.ReadAllTextAsync(path).ConfigureAwait(true), JsonFileStore.DefaultOptions);
            if (job is null || !job.ClosedWorkbook) return;
            await TryWriteJobAsync(job with { ClosedWorkbook = false }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Logger?.Warn("vba-ribbon", $"作業の記録の「閉じた」印を下ろせませんでした（{ex.GetType().Name}）。", targetPath: path);
        }
    }

    /// <summary>
    /// 閉じたブックの券の持ち主（1 回の書き換えにつき 1 つ）。券は「開き直す」か「捨てる」のどちらかで 1 回だけ使い、<see cref="Take"/> で
    /// 取り出した側が責任を持つ（二重に開き直さない・捨て忘れない）。
    /// </summary>
    private sealed class ReopenHolder
    {
        private VbaWorkbookReopenTicket? _ticket;

        public ReopenHolder(string workbookFullName) => WorkbookFullName = workbookFullName;

        public string WorkbookFullName { get; }

        /// <summary>券を持っているか（Excel でブックを閉じて、まだ開き直していない）。</summary>
        public bool HasTicket => Volatile.Read(ref _ticket) is not null;

        public void Hold(VbaWorkbookReopenTicket ticket) => Volatile.Write(ref _ticket, ticket);

        public VbaWorkbookReopenTicket? Take() => Interlocked.Exchange(ref _ticket, null);

        /// <summary>開き直さずに券を捨てる（持っていなければ何もしない）。</summary>
        public async Task DiscardAsync(IVbaBridge bridge)
        {
            if (Take() is { } ticket) await bridge.DiscardReopenTicketAsync(ticket).ConfigureAwait(true);
        }
    }

    /// <summary>開き直しの結果（<see cref="ReopenAsync"/>）。</summary>
    /// <param name="Reopened">ブックが Excel で開いた（時間切れのあとで開いていたのを確かめた場合を含む）。</param>
    /// <param name="Issues">開き直せなかったときの E822（警告）。</param>
    private sealed record ReopenReport(bool Reopened, IReadOnlyList<GraftIssue> Issues);

    /// <summary>
    /// 段階 6: ブックを保存して閉じる（16.6）。成功すれば作業の記録に <c>ClosedWorkbook</c> を書いて返す。失敗は、ブックのファイルを
    /// 変えていない問題（E820・E819・E813・E805）。時間切れ（E805）のときは、Excel 側で遅れて完了しうるので、状態を取り直して案内を決める。
    /// </summary>
    private async Task<(VbaRibbonRewriteJob? Job, GraftResult<VbaRibbonRewriteOutcome>? Failure)> SaveAndCloseForRewriteAsync(
        Project project, string workbookName, string workbookPath, VbaRibbonRewriteJob job, ReopenHolder held, CancellationToken ct)
    {
        Progress?.Invoke(VbaRibbonRewriteText.ProgressSaveAndClose);
        var watch = Stopwatch.StartNew();
        var saved = await _bridge.SaveAndCloseWorkbookAsync(held.WorkbookFullName, ct).ConfigureAwait(true);
        if (!saved.IsSuccess)
        {
            DeleteJob(project.Id);
            var issues = saved.Issues.ToList();
            if (issues.Any(i => i.Code == ErrorCode.E805)) issues = await ReconcileAfterSaveCloseTimeoutAsync(workbookName, workbookPath, held.WorkbookFullName, issues).ConfigureAwait(true);
            Logger?.Warn("vba-ribbon", $"ブックを保存して閉じられませんでした（{string.Join("・", issues.Select(i => i.Code.ToString()))}）。ファイルは変えていません。",
                targetPath: workbookPath, durationMs: watch.ElapsedMilliseconds);
            return (null, GraftResult<VbaRibbonRewriteOutcome>.Fail(issues));
        }

        held.Hold(saved.Value);
        Logger?.Info("vba-ribbon", $"ブックを保存して閉じました（{workbookName}）。", targetPath: workbookPath, durationMs: watch.ElapsedMilliseconds);

        // 「Graft が Excel で閉じた」印を、閉じたあとで書く（閉じていないのに印があると、後始末の知らせが嘘になる）。
        job = job with { ClosedWorkbook = true };
        if (!await TryWriteJobAsync(job).ConfigureAwait(true))
        {
            // 記録に印を残せないと、途中で落ちたときに「Excel では閉じたまま」と伝えられない。ブックはまだ何も変えていないので、
            // ここで止める（呼び出し側が開き直す）。
            DeleteJob(project.Id);
            return (null, GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                "作業の記録を更新できないため、書き換えを止めました。ブックのファイルは変えていません。", path: workbookPath));
        }

        return (job, null);
    }

    /// <summary>
    /// 保存して閉じるが時間切れ（E805）になったあと、Excel 側で遅れて完了していないかを取り直す（16.15 の申し送り。
    /// 「時間切れ ＝ 行われなかった」と決めつけない）。閉じていたら、開き直す券はもう無いので、書き換えずに「Excel で開いてください」と
    /// 案内する（利用者には時間切れと伝わっているので、ここから先へ黙って進めない）。まだ開いていれば、E805 に「開いたまま」を添える。
    /// </summary>
    private async Task<List<GraftIssue>> ReconcileAfterSaveCloseTimeoutAsync(
        string workbookName, string workbookPath, string workbookFullName, List<GraftIssue> timeoutIssues)
    {
        var state = await _bridge.InspectWorkbookForRewriteAsync(workbookFullName, CancellationToken.None).ConfigureAwait(true);
        if (state.IsSuccess && !state.Value.IsOpen)
        {
            return new List<GraftIssue>
            {
                GraftIssue.Of(ErrorCode.E820,
                    $"「{workbookName}」の保存と閉じる処理が時間切れになりましたが、そのあと Excel 側でブックが閉じられていました（保存も済んだ見込みです）。" +
                    "ブックのファイルは変えていません。リボンの書き換えは行っていません。" + VbaRibbonRewriteText.OpenInExcelSentence,
                    path: workbookPath),
            };
        }

        var open = state.IsSuccess ? "ブックは Excel で開いたままです。" : "ブックの状態を確かめられませんでした。";
        return timeoutIssues
            .Select(i => i.Code == ErrorCode.E805
                ? i with { Detail = (i.Detail ?? string.Empty) + " " + open + "ブックのファイルは変えていません。" }
                : i)
            .ToList();
    }

    /// <summary>
    /// 段階 13: 券の Excel でブックを開き直す（16.6）。成功・失敗・時間切れのどれでも券は捨てられる（ブリッジの責務）。開き直せなくても
    /// 書き換えは済んでいる（またはブックは元のまま）ので、失敗は E822（警告）にして返す。時間切れ・失敗のあとに、Excel 側で遅れて
    /// 開いていないかを取り直す（開いていれば成功として扱う）。券を持っていなければ何もしない（null）。
    /// </summary>
    private async Task<ReopenReport?> ReopenAsync(ReopenHolder held)
    {
        if (held.Take() is not { } ticket) return null;
        Progress?.Invoke(VbaRibbonRewriteText.ProgressReopen);
        var watch = Stopwatch.StartNew();
        // 取り消しは渡さない（閉じたあとの開き直しは、呼び出し側が取り消しても最後まで行う。利用者の Excel を閉じたままにしない）。
        var reopened = await _bridge.ReopenWorkbookAsync(ticket, CancellationToken.None).ConfigureAwait(true);
        if (reopened.IsSuccess)
        {
            Logger?.Info("vba-ribbon",
                reopened.Value.ViewRestored ? "ブックを開き直しました。" : $"ブックを開き直しました（見た目の一部は戻せませんでした: {string.Join(" / ", reopened.Value.Notes)}）。",
                targetPath: held.WorkbookFullName, durationMs: watch.ElapsedMilliseconds);
            return new ReopenReport(true, Array.Empty<GraftIssue>());
        }

        // 時間切れ・失敗のあとでも、Excel 側で開いているかもしれない（利用者がダイアログに答えた）。状態を取り直す。
        var state = await _bridge.InspectWorkbookForRewriteAsync(held.WorkbookFullName, CancellationToken.None).ConfigureAwait(true);
        if (state.IsSuccess && state.Value.IsOpen)
        {
            Logger?.Info("vba-ribbon", "開き直しは戻りませんでしたが、そのあとブックが開いていることを確かめました。", targetPath: held.WorkbookFullName);
            return new ReopenReport(true, Array.Empty<GraftIssue>());
        }

        Logger?.Warn("vba-ribbon", "ブックを開き直せませんでした。", targetPath: held.WorkbookFullName, durationMs: watch.ElapsedMilliseconds);
        return new ReopenReport(false, reopened.Issues.Select(i => i with { Severity = Severity.Warning }).ToList());
    }

    /// <summary>
    /// 閉じたあとの失敗（段階 7〜12）の後始末。E823（控えから戻せなかった）のときは、壊れているかもしれないブックでマクロを動かさないよう
    /// 開き直さずに券を捨て、「Excel では閉じたまま」を添える。それ以外は、開き直して「開き直しました」を添える（開き直せなければ E822 を足す）。
    /// </summary>
    private async Task<GraftResult<VbaRibbonRewriteOutcome>> ReopenAfterFailureAsync(
        GraftResult<VbaRibbonRewriteOutcome> failure, ReopenHolder held)
    {
        if (!held.HasTicket) return failure;
        var issues = failure.Issues.ToList();
        if (issues.Any(i => i.Code == ErrorCode.E823))
        {
            await held.DiscardAsync(_bridge).ConfigureAwait(true);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(issues.Select(i => i.Code == ErrorCode.E823
                ? i with { Detail = (i.Detail ?? string.Empty) + " " + VbaRibbonRewriteText.ClosedLeftNote }
                : i).ToList());
        }

        var report = await ReopenAsync(held).ConfigureAwait(true);
        if (report is null) return failure;
        if (report.Reopened)
        {
            issues = issues.Select((i, n) => n == 0 ? i with { Detail = (i.Detail ?? string.Empty) + " " + VbaRibbonRewriteText.ReopenedNote } : i).ToList();
        }
        else
        {
            issues.AddRange(report.Issues);
        }

        return GraftResult<VbaRibbonRewriteOutcome>.Fail(issues);
    }

    private async Task<GraftResult<VbaRibbonRewriteOutcome>> RewriteStagesAsync(
        Project project, VbaLink link, VbaRibbonRewritePlan plan, VbaRibbonRewriteJob job, Stopwatch watch, ReopenHolder held)
    {
        var file = plan.WorkbookPath;
        var name = plan.WorkbookName;
        var backupPath = job.BackupPath!;

        // 段階 7・8: 排他で開き、そのストリームから控えを写して照合する。
        Progress?.Invoke(VbaRibbonRewriteText.ProgressBackup);
        var backup = await BackupAsync(file, backupPath, afterExcelClose: held.HasTicket).ConfigureAwait(true);
        if (!backup.IsSuccess)
        {
            DeleteJob(project.Id);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(backup.Issues);
        }

        // 控えが「どの場所のブックの控えか」を残す（丸ごと戻す操作が、同じ名前の別の場所のブックの控えを取り違えないため。16.11）。
        await RecordBackupOriginAsync(backupPath, link.WorkbookFullName).ConfigureAwait(true);

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

        Progress?.Invoke(VbaRibbonRewriteText.ProgressRewrite);
        var requests = plan.Targets.Select(t => new VbaRibbonWriteRequest(t.Format, t.Xml)).ToList();
        var tempPath = job.TempPath!;
        var replace = Io.Replace;
        // 書き込みと照合は大きなブックで時間がかかるので、UI スレッドを塞がないようスレッドプールで行う。取り消しは受け付けない
        // （置き換えの途中で止めると、一時ファイルの後始末が呼び出し側に漏れる。RewriteFile は自分で最後まで片付ける）。
        var written = await Task.Run(() => VbaRibbonPackageWriter.RewriteFile(file, requests, 0, null, tempPath, replace)).ConfigureAwait(true);
        if (!written.Succeeded)
        {
            return await HandleWriteFailureAsync(project, link, plan, job, written, watch).ConfigureAwait(true);
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

        // 段階 13: 開き直す（閉じたときだけ。16.5 の表の順で、後始末の前）。開き直せなくても書き換えは済んでいるので、E822（警告）を
        // 添えた成功にする（「Excel で開く」を案内する）。
        var report = await ReopenAsync(held).ConfigureAwait(true);
        if (report is { Reopened: false }) issues.AddRange(report.Issues);

        // 段階 14: 後始末（作業の記録を消す・控えの掃除）。掃除の失敗は警告だけ。
        DeleteJob(project.Id);
        if (PruneBackups(plan.BackupDirectory, keep: backupPath) is { } pruneIssue) issues.Add(pruneIssue);

        Logger?.Info("vba-ribbon",
            $"リボンをブックへ書き換えました（{name}・{string.Join("、", written.Parts.Select(p => p.PartName + (p.AddedRelationship ? "（新規）" : string.Empty)))}・控え {VbaRibbonRewriteText.FileNameOf(backupPath)}）。",
            targetPath: file, durationMs: watch.ElapsedMilliseconds);
        var outcome = new VbaRibbonRewriteOutcome(
            file, backupPath, plan.Targets.Select(t => t.MirrorPath).ToList(), written.Parts.Any(p => p.AddedRelationship),
            WasOpen: plan.IsOpen, Reopened: report?.Reopened == true);
        return GraftResult<VbaRibbonRewriteOutcome>.Ok(outcome, issues);
    }

    /// <summary>
    /// 一時ファイルへの書き込み・照合・置き換えが成功しなかったとき（不具合 1）。
    /// <para>
    /// 【なぜ「ブックはそのまま」と決めつけないか】 以前は、失敗なら無条件で「元のまま」と見なして控えを消していた。ところが Win32 の
    /// <c>ReplaceFile</c> には、置き換えの途中で止まる失敗がある（1176: 置き換える側へ名前を移したあとで失敗すると、控えの名前を渡さない限り
    /// 「置き換えられる側が無い」状態で止まる。1177 も同様）。その状態で控えを消すと、ブック・一時ファイル・控えの 3 つがすべて無くなり、
    /// 利用者のブックを失う（失敗を差し込んだテストで再現済み）。そこで、置き換えを始めてから失敗したとき（
    /// <see cref="VbaRibbonWriteStatus.ReplaceFailed"/>）は、<b>ブックの SHA-256 を控え（＝元）と比べてから</b>扱いを決める。
    /// <list type="bullet">
    /// <item>一致: 元のまま。何も変わっていないので、控えと一時ファイルを消す（世代だけが進むのを避ける）。E821。</item>
    /// <item>違う・ブックが無い（読めない場合も、安全側で「違う」と見なす）: 控えは<b>消さず</b>、控えから戻す（16.11 の照合と同じ手順）。
    /// 戻せれば E821、戻せなければ E823（要確認）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 置き換えを始める前に止まった失敗（照合の不一致・ほかのプログラムがブックを変えた・一時ファイルを書けない、など）は、Graft はブックに
    /// 触れていない。ここで SHA-256 を比べて「違う」からと控えで上書きすると、<b>ほかのプログラムが書いた内容を消してしまう</b>
    /// （<see cref="VbaRibbonWriteStatus.SourceChanged"/> がその例）ので、従来どおり控えを消して、ブックには触れない。
    /// </para>
    /// </summary>
    private async Task<GraftResult<VbaRibbonRewriteOutcome>> HandleWriteFailureAsync(
        Project project, VbaLink link, VbaRibbonRewritePlan plan, VbaRibbonRewriteJob job, VbaRibbonWriteResult written, Stopwatch watch)
    {
        var file = plan.WorkbookPath;
        var name = plan.WorkbookName;
        var backupPath = job.BackupPath!;
        var tempPath = job.TempPath!;

        if (written.Status != VbaRibbonWriteStatus.ReplaceFailed)
        {
            // 置き換えの前で止まった。ブックは 1 バイトも変わっていないので、いま作った控えは消す（何も変えていないのに世代だけが進むのを避ける。16.11）。
            TryDelete(backupPath);
            DeleteJob(project.Id);
            Logger?.Warn("vba-ribbon", $"リボンを書き換えられませんでした（{written.Status}）。ブックは元のままです。", targetPath: file, durationMs: watch.ElapsedMilliseconds);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(WriteStatusIssue(written, name, file));
        }

        // 置き換えの最中の失敗: ブックの実際の状態を、控えのハッシュと比べて確かめる。
        var current = await Task.Run(() => TrySha256(file)).ConfigureAwait(true);
        if (current is not null && string.Equals(current, job.BackupSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(tempPath);
            TryDelete(backupPath);
            DeleteJob(project.Id);
            Logger?.Warn("vba-ribbon", "ブックの置き換えに失敗しました。ブックは元のままです。", targetPath: file, durationMs: watch.ElapsedMilliseconds);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821,
                $"「{name}」の置き換えに失敗しました（使用中・権限など）。ブックを控えと照合して、元のままであることを確かめました。ブックは変えていません。",
                path: file);
        }

        Logger?.Error("vba-ribbon",
            $"ブックの置き換えに失敗し、ブックが元の内容ではありません（{(current is null ? "ファイルが無い、または読めない" : "内容が違う")}）。控えから戻します。", targetPath: file);
        var detail = current is null ? "ブックのファイルが無くなっている（または読めない）" : "ブックが元の内容と違っている";
        return await RestoreFromBackupAfterFailureAsync(
            project, link, plan, job,
            restoredText: $"「{name}」の置き換えの途中で失敗し、{detail}ことを確かめたため、控えから元の内容に戻しました。ブックは元の内容です（控え: {backupPath}）。",
            failedText: $"「{name}」の置き換えの途中で失敗し、{detail}うえ、控えから元の内容へ戻すこともできませんでした。控え: {backupPath}（フォルダ: {plan.BackupDirectory}）",
            written).ConfigureAwait(true);
    }

    /// <summary>
    /// 確認のあいだに、ミラーのファイル・ブックが変わっていないか・Excel での状態が変わっていないかを確かめる（変わっていれば止める問題）。
    /// <list type="bullet">
    /// <item>Excel で開いている／いないが計画のときと違う（確認のあいだに開かれた・閉じられた）: 確認したのと違う経路になるので止める。</item>
    /// <item>止めるブックになった（読み取り専用で開き直された・マクロが走り始めた、など）: 止める（E819・E813）。</item>
    /// <item>計画のときは未保存なしだったのに、いまは未保存がある: 確認の文面に「未保存の変更も保存されます」が無かったので、
    /// 確認し直す（利用者の決定「未保存の変更があれば止める」）。</item>
    /// <item>開いているブックの大きさ・更新日時は見ない（このあと Graft が保存するので、変わって当然。控えを取る段階 9 で、控えを
    /// 取ったときから変わっていないことを確かめる）。</item>
    /// </list>
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

        var open = await InspectOpenStateAsync(link.WorkbookFullName, ct).ConfigureAwait(true);
        if (open.Stop is { } stop) return stop[0];
        if (open.State.IsOpen != plan.IsOpen)
        {
            return GraftIssue.Of(ErrorCode.E821,
                $"確認のあいだに「{plan.WorkbookName}」が Excel で{(open.State.IsOpen ? "開かれた" : "閉じられた")}ため、確認した内容と変わってしまうので、書き換えませんでした。ブックは変えていません。もう一度「リボンをブックへ反映…」を選んでください。",
                path: plan.WorkbookPath);
        }

        if (open.State.IsOpen)
        {
            if (!plan.HasUnsavedChanges && !open.State.Saved)
            {
                return GraftIssue.Of(ErrorCode.E821,
                    $"確認のあいだに「{plan.WorkbookName}」に保存していない変更ができたため、書き換えませんでした（確認では「未保存の変更なし」でした）。ブックは変えていません。もう一度「リボンをブックへ反映…」を選んでください。",
                    path: plan.WorkbookPath);
            }

            return File.Exists(plan.WorkbookPath)
                ? null
                : GraftIssue.Of(ErrorCode.E821, $"「{plan.WorkbookName}」のファイルが見つかりません。ブックは変えていません。", path: plan.WorkbookPath);
        }

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
    private async Task<GraftResult<BackupInfo>> BackupAsync(string file, string backupPath, bool afterExcelClose)
    {
        var name = VbaRibbonRewriteText.FileNameOf(file);
        var source = await OpenExclusiveAsync(file, afterExcelClose).ConfigureAwait(true);
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
    private async Task<GraftResult<FileStream>> OpenExclusiveAsync(string file, bool afterExcelClose)
    {
        var name = VbaRibbonRewriteText.FileNameOf(file);
        // Graft が Excel で閉じた直後は、別の上限（実機検証 12 で見直す。VbaRibbonRewriteTimings）。
        var limit = afterExcelClose ? Io.ExclusiveTimeoutAfterExcelClose : Io.ExclusiveTimeout;
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
            catch (IOException) when (deadline.Elapsed < limit)
            {
                await Task.Delay(Io.ExclusiveInterval).ConfigureAwait(true);
            }
            catch (IOException)
            {
                var owner = OwnerFileExists(file)
                    ? $"（同じフォルダに Excel の所有者ファイル「~${name}」があります。別の Excel や別の PC で開いていないか確かめてください）"
                    : string.Empty;
                Logger?.Warn("vba-ribbon", $"ブックを排他で開けませんでした（{limit.TotalSeconds:0} 秒待ちました。{(afterExcelClose ? "Excel で閉じた直後" : "Excel では開いていなかったブック")}）。", targetPath: file);
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
    private Task<GraftResult<VbaRibbonRewriteOutcome>> RestoreAfterMismatchAsync(
        Project project, VbaLink link, VbaRibbonRewritePlan plan, VbaRibbonRewriteJob job, string mismatch, VbaRibbonWriteResult written)
    {
        var backupPath = job.BackupPath!;
        Logger?.Error("vba-ribbon", $"書き換えたブックの照合が合いませんでした。控えから戻します（{mismatch}）。", targetPath: plan.WorkbookPath);
        return RestoreFromBackupAfterFailureAsync(
            project, link, plan, job,
            restoredText: $"書き換えたブックを読み直したら、期待と違っていました（{mismatch}）。控えから元の内容に戻しました。ブックは元の内容です（控え: {backupPath}）。",
            failedText: $"書き換えたブックを読み直したら期待と違っていて（{mismatch}）、控えから元の内容へ戻すこともできませんでした。控え: {backupPath}（フォルダ: {plan.BackupDirectory}）",
            written);
    }

    /// <summary>
    /// 控えからブックを戻す共通の手順（不具合 1 の置き換えの失敗と、仕上げの照合の不一致が使う）。戻す前に、一時ファイルの場所を
    /// <b>作業の記録へ先に書く</b>（一時ファイルを作ってから落ちても、後始末が消せるように。不具合 5 と同じ作法）。記録を書けなくても、
    /// 戻すのを止めない（戻さないほうが利用者のブックを失う。一時ファイルが 1 つ残るだけで済む）。戻せたら記録と一時ファイルを消して E821、
    /// 戻せなければ要確認の印を立てて E823（一時ファイルは、新しい内容の唯一の写しかもしれないので消さず、場所を知らせる）。
    /// </summary>
    private async Task<GraftResult<VbaRibbonRewriteOutcome>> RestoreFromBackupAfterFailureAsync(
        Project project, VbaLink link, VbaRibbonRewritePlan plan, VbaRibbonRewriteJob job, string restoredText, string failedText, VbaRibbonWriteResult written)
    {
        var file = plan.WorkbookPath;
        var backupPath = job.BackupPath!;
        var backupSha = job.BackupSha256!;
        var restored = await RestoreWithRecordAsync(job, backupPath, backupSha, file).ConfigureAwait(true);
        if (restored)
        {
            if (job.TempPath is { } leftover) TryDelete(leftover);
            DeleteJob(project.Id);
            Logger?.Warn("vba-ribbon", "控えからブックを元の内容に戻しました。", targetPath: file);
            return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E821, restoredText, path: file);
        }

        // 戻せなかった。ブックは期待と違う内容のまま（または無い）。同期状態に要確認の印を立て、次の同期で衝突として扱わせる（黙って上書きしない）。
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
        var tempNote = job.TempPath is { } temp && File.Exists(temp)
            ? $" 書き換え後の内容の一時ファイルを消さずに残しています: {temp}"
            : string.Empty;
        return GraftResult<VbaRibbonRewriteOutcome>.Fail(ErrorCode.E823, failedText + tempNote, path: file);
    }

    /// <summary>
    /// 戻す一時ファイル（<c>.restore</c>）の場所を作業の記録へ<b>先に書いてから</b>、控えでブックを置き換える。一時ファイルはブック全体の写しなので、
    /// 作ってから落ちて記録に無いと、後始末が消せずにブックの隣へ残り続ける（不具合 5）。
    /// </summary>
    private async Task<bool> RestoreWithRecordAsync(VbaRibbonRewriteJob job, string backupPath, string backupSha256, string file)
    {
        var restoreTemp = VbaRibbonPackageWriter.NewTempPath(file, ".restore");
        // 記録を書けなくても戻す（戻さないほうが、利用者のブックを失う）。
        await TryWriteJobAsync(job with { Stage = VbaRibbonRewriteStage.Restoring, RestoreTempPath = restoreTemp }).ConfigureAwait(true);
        return await Task.Run(() => RestoreFromBackup(backupPath, backupSha256, file, restoreTemp)).ConfigureAwait(true);
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

    /// <param name="projectId">プロジェクトの Id。</param>
    /// <param name="duringRun">
    /// Graft が動いているあいだの失敗（入出力の例外）の後始末か。true なら、知らせの文は状態だけ（「Graft が終了したため」「閉じたまま」を含めない。
    /// <see cref="VbaRibbonRecoveryLogic.Message"/>）。false は、Graft が終了したあとの後始末（次の起動・次のリボンの操作）。
    /// </param>
    private async Task<VbaRibbonRecovery?> RecoverInterruptedCoreAsync(string projectId, bool duringRun = false)
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
        var kept = new List<string>();
        var state = VbaRibbonInterruptedState.Unknown;
        if (job is not null)
        {
            // ブックの状態を先に見る（一時ファイルを消す前に。ブックが無いときは、一時ファイルが新しい内容の唯一の写しかもしれないため）。
            var bookSha = await Task.Run(() => TrySha256(job.WorkbookPath)).ConfigureAwait(true);

            // 一時ファイル（~graft-*.tmp）を消す。記録にある名前でも、形が違うものは消さない（記録が書き換えられていても、利用者の
            // ファイルを消さないため）。ブックが見つからないときは消さずに残して知らせる（控えから戻せないときの最後の手がかり）。
            foreach (var temp in new[] { job.TempPath, job.RestoreTempPath })
            {
                if (temp is null || !IsOurTempFile(temp, job.WorkbookPath) || !File.Exists(temp)) continue;
                if (bookSha is null) kept.Add(temp);
                else if (TryDelete(temp)) removed++;
            }

            // 控えを書き終える前に落ちたなら、作りかけの控えを消す（中身が途中までのファイルを「控え」として残さない）。
            if (job.BackupSha256 is null && job.BackupPath is { } partial
                && VbaWorkbookBackupNaming.Parse(Path.GetFileName(partial)) is not null)
            {
                TryDelete(partial);
            }

            IReadOnlyDictionary<string, string>? ribbonHashes = null;
            if (job.Kind == VbaRibbonJobKind.Rewrite && bookSha is not null && job.BackupSha256 is not null
                && !string.Equals(bookSha, job.BackupSha256, StringComparison.OrdinalIgnoreCase))
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

            // 丸ごと戻す操作で、選んだ控えの内容へ置き換え済みなら、リボンの同期基準を捨てる（成功したときと同じ後始末。落ちたせいで
            // 基準だけが古いまま残ると、控えより前のリボンの内容を指す基準で三方比較してしまう）。
            if (state == VbaRibbonInterruptedState.Replaced)
            {
                var issue = await DiscardRibbonBaselineAsync(projectId, job.WorkbookFullName ?? job.WorkbookPath, job.WorkbookPath).ConfigureAwait(true);
                if (issue is not null) Logger?.Warn("vba-ribbon", issue.ToDisplayText(), targetPath: job.WorkbookPath);
            }
        }

        DeleteJob(projectId);
        var message = VbaRibbonRecoveryLogic.Message(job, state, duringRun);
        if (kept.Count > 0)
        {
            message += $" ブックが見つからないため、一時ファイル（書き換え後の内容の写しかもしれません）は消さずに残しています: {string.Join("、", kept)}";
        }

        Logger?.Warn("vba-ribbon", $"途中で終わったリボンの書き換えを片付けました（ブック: {state}・一時ファイル {removed} 件）。", targetPath: job?.WorkbookPath);
        return new VbaRibbonRecovery(job, state, message, removed);
    }

    /// <summary>
    /// リボンの同期基準を捨てて「記録なし」に戻す（丸ごと戻した直後。16.11）。次の取込で、本文が同じなら基準を作り直し、違えば衝突になる。
    /// 保存に失敗しても置き換えは済んでいるので、警告の問題を返すだけにする（基準が古いまま残っても、次の取込で食い違いは衝突として見つかる）。
    /// </summary>
    private async Task<GraftIssue?> DiscardRibbonBaselineAsync(string projectId, string workbookFullName, string file)
    {
        try
        {
            var loaded = await _states.LoadAsync(projectId, workbookFullName).ConfigureAwait(true);
            var state = loaded.Value;
            foreach (var path in state.Ribbon?.Keys.ToList() ?? new List<string>()) state = state.WithoutRibbon(path);
            if (!VbaSyncService.StatesEqual(loaded.Value, state)) await _states.SaveAsync(projectId, state).ConfigureAwait(true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.Warn("vba-ribbon", $"リボンの同期基準を捨てられませんでした（{ex.GetType().Name}）。", targetPath: file);
            return GraftIssue.Of(ErrorCode.E821,
                $"ブックは控えの内容で置き換えましたが、同期状態を更新できませんでした（{ExceptionMessages.Describe(ex)}）。次の取込でリボンの食い違いを確かめます。",
                path: file, severity: Severity.Warning);
        }
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
    /// <para>
    /// 【<paramref name="keep"/> を必ず除く理由】 「最新」は控えのファイル名の日時で決まる。時計が戻った・夏時間が終わった・別の PC の
    /// 時計がずれていたとき、いま作ったばかりの控えが「古い」と数えられて消えうる。書き換えが成功した直後に、その書き換えを元へ戻せる
    /// 唯一の控えを消しては本末転倒なので、いま作った控え（<paramref name="keep"/>）は日時に依らず消さない
    /// （そのぶん、世代数・容量の上限を 1 件超えることがある）。
    /// </para>
    /// </summary>
    /// <param name="directory">控えのフォルダ。</param>
    /// <param name="keep">消してはならない控え（いま作った控えのフルパス）。null なら除かない（一覧を開いたときの掃除）。</param>
    private GraftIssue? PruneBackups(string directory, string? keep = null)
    {
        try
        {
            var entries = Directory.EnumerateFiles(directory)
                .Select(f => (Path: f, Name: VbaWorkbookBackupNaming.Parse(Path.GetFileName(f))))
                .Where(x => x.Name is not null)
                .Select(x => new VbaWorkbookBackupEntry(Path.GetFileName(x.Path), x.Name!, new FileInfo(x.Path).Length))
                .ToList();
            var plan = VbaWorkbookBackupPruner.Plan(entries, MaxBackupGenerations(), MaxBackupTotalMB() * 1024L * 1024L);
            var keepName = keep is null ? null : Path.GetFileName(keep);
            var deletions = plan.Delete
                .Where(e => keepName is null || !string.Equals(e.FileName, keepName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var entry in deletions) File.Delete(Path.Combine(directory, entry.FileName));
            if (deletions.Count > 0) Logger?.Info("vba-ribbon", $"ブックの控えを {deletions.Count} 件掃除しました。", targetPath: directory);
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
