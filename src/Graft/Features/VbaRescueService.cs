using System.IO;
using System.Text;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;

namespace Graft.Features;

/// <summary>控えを書いた結果。</summary>
public enum VbaRescueSaveStatus
{
    /// <summary>書いた。</summary>
    Saved,

    /// <summary>最新の自動控えと本文が同じなので書かなかった（同じ内容の世代が積み上がらないように）。</summary>
    SkippedSame,

    /// <summary>書けなかった（ディスクの空き不足・権限など）。</summary>
    Failed,
}

/// <param name="Status">結果。</param>
/// <param name="Path">書いたファイルのフルパス（<see cref="VbaRescueSaveStatus.Saved"/> のとき）。</param>
/// <param name="Failure">書けなかった理由（<see cref="VbaRescueSaveStatus.Failed"/> のとき）。</param>
public sealed record VbaRescueSaveResult(VbaRescueSaveStatus Status, string? Path = null, string? Failure = null)
{
    /// <summary>書けなかったのではない（書いた、または同じ内容なので書かなかった）。</summary>
    public bool IsOk => Status != VbaRescueSaveStatus.Failed;
}

/// <summary>控えの一覧の1件。</summary>
/// <param name="FileName">ファイル名。</param>
/// <param name="FullPath">フルパス。</param>
/// <param name="Name">解析したファイル名。</param>
/// <param name="Size">バイト数。</param>
/// <param name="Lines">行数。</param>
public sealed record VbaRescueItem(string FileName, string FullPath, VbaRescueFileName Name, long Size, int Lines)
{
    /// <summary>モジュール名。</summary>
    public string Module => Name.Module;

    /// <summary>種別。</summary>
    public VbaRescueKind Kind => Name.Kind;

    /// <summary>控えを残した日時。</summary>
    public DateTime At => Name.At;

    /// <summary>一覧に出す種別の名前（仕様書_ブックの情報と依頼の観点 7.4）。<c>Legacy</c> は E809 の書き戻し失敗も同じ表示。</summary>
    public string KindLabel => Kind switch
    {
        VbaRescueKind.Auto => "反映前の自動控え",
        VbaRescueKind.Mirror => "戻す前のミラー",
        _ => "上書き前",
    };
}

/// <summary>控えの一覧。</summary>
/// <param name="Items">新しい順。</param>
/// <param name="TotalBytes">控え全体（解析できないファイルを除く）の容量。</param>
/// <param name="OverLimit">容量の上限を超えている（<c>Legacy</c> は自動では消さないため、掃除しても収まらない）。</param>
/// <param name="Pruned">この呼び出しで掃除したファイルの数。</param>
public sealed record VbaRescueListing(IReadOnlyList<VbaRescueItem> Items, long TotalBytes, bool OverLimit, int Pruned)
{
    /// <summary>何も無い。</summary>
    public static VbaRescueListing Empty { get; } = new(Array.Empty<VbaRescueItem>(), 0, false, 0);
}

/// <summary>「ミラーへ戻す」の結果の区分。</summary>
public enum VbaRescueRestoreStatus
{
    /// <summary>ミラーへ書いた。</summary>
    Restored,

    /// <summary>ミラーが既にこの控えと同じ内容だった。何も書いていない。</summary>
    AlreadySame,

    /// <summary>対象のファイルに未保存の編集があり、保存を断られた。何も書いていない。</summary>
    Declined,

    /// <summary>戻せなかった。何も書いていない（ミラーはそのまま）。</summary>
    Failed,
}

/// <param name="Status">結果。</param>
/// <param name="RelativePath">戻し先のミラーの相対パス（特定できたとき）。</param>
/// <param name="MirrorBackupPath">戻す前のミラーを控えたファイルのフルパス（控えたとき）。</param>
/// <param name="Message">利用者に伝える一文。</param>
/// <param name="Issues">失敗の理由（<see cref="VbaRescueRestoreStatus.Failed"/> のとき）。</param>
public sealed record VbaRescueRestoreOutcome(
    VbaRescueRestoreStatus Status, string? RelativePath, string? MirrorBackupPath, string Message, IReadOnlyList<GraftIssue> Issues);

/// <summary>
/// Excel 側の控え（<c>vba-sync/&lt;プロジェクトId&gt;.rescue/</c>）の読み書き・掃除・ミラーへの取り込み
/// （仕様書_ブックの情報と依頼の観点 7章。機能 C）。
/// <para>
/// 【「戻す」は Excel へ直接書かず、ミラーへ取り込む】 直接書くには、E813（実行中）・CP932・衝突検査・同期基準の更新といった
/// Push の安全装置を別の経路で作り直す必要がある。ミラーへ書けば、同期基準は触らないので <see cref="VbaSyncPlanner"/> が
/// 自然に「未反映」と判定し、送る操作は既存の Push が担う（付録A-4）。
/// </para>
/// <para>
/// 【履歴（リビジョン）とは混ぜない】 履歴は Graft が適用した変更だけを持つ設計（VBA連携仕様 13章）。戻した結果は
/// 履歴に記録せず、<see cref="Logger"/> に <c>vba-rescue</c> として残す（付録A-8）。
/// </para>
/// </summary>
public sealed class VbaRescueService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly AppPaths _paths;
    private readonly VbaSyncStateStore _states;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim? _gate;

    /// <param name="paths">データ保存先。</param>
    /// <param name="states">同期状態（ミラーのファイルが無いモジュールの種別を知るために読むだけ。書かない）。</param>
    /// <param name="clock">現在時刻（テストで固定するため）。</param>
    /// <param name="gate">
    /// ミラーへの書き込みを <see cref="VbaSyncService"/> の呼び出しと直列にするためのゲート。控えを書く側
    /// （Push の最中）は呼び出し元がすでに持っているので、ここでは取らない。
    /// </param>
    public VbaRescueService(AppPaths paths, VbaSyncStateStore states, Func<DateTimeOffset>? clock = null, SemaphoreSlim? gate = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _clock = clock ?? (() => DateTimeOffset.Now);
        _gate = gate;
    }

    /// <summary>記録の窓口。</summary>
    public Logger? Logger { get; set; }

    /// <summary>1 モジュールあたりの <c>Auto</c>＋<c>Mirror</c> の世代数（<c>Vba.RescueMaxGenerations</c>）。</summary>
    public int MaxGenerations { get; set; } = 20;

    /// <summary>控え全体の容量（MB。<c>Vba.RescueMaxTotalMB</c>）。</summary>
    public int MaxTotalMB { get; set; } = 50;

    /// <summary>
    /// 「ミラーへ戻す」の前の、未保存の編集の確認。引数は戻し先のうち既に存在するファイルのフルパス。
    /// <c>MainViewModel</c> が Pull の前の確認（<see cref="VbaSyncService.BeforePullAsync"/>）と同じ作りで配線する。
    /// 未設定なら確認なしで続ける。
    /// </summary>
    public Func<IReadOnlyList<string>, Task<VbaPullSaveOutcome>>? BeforeRestoreAsync { get; set; }

    /// <summary>プロジェクトの控えのフォルダ（「保存先を開く」・エラーの案内に使う）。</summary>
    public string DirectoryOf(Project project) => _paths.GetVbaRescueDirectory(project.Id);

    // ------------------------------------------------------------------
    // 書く
    // ------------------------------------------------------------------

    /// <summary>
    /// 控えを 1 つ書く。モジュール本文を正規化せずそのまま書く（戻したときに行末・空白が変わらないように）。
    /// 同じ秒に同じ名前があれば連番を付ける。
    /// </summary>
    /// <param name="skipIfSameAsLatestAuto">
    /// true なら、同じモジュールの最新の <c>Auto</c> と本文のハッシュ（<see cref="VbaTextNormalizer.Hash"/>）が同じときは書かない。
    /// </param>
    public async Task<VbaRescueSaveResult> SaveAsync(
        Project project, string component, string code, VbaRescueKind kind, bool skipIfSameAsLatestAuto = false)
    {
        try
        {
            var directory = DirectoryOf(project);
            var now = _clock();
            var fileName = VbaRescueNaming.BuildFileName(component, now, kind);

            // 【フォルダの列挙は、UI スレッドで同期的に行わない】 保存時の自動反映（機能 D）は、保存のたびにここを通る。
            // 控え（Legacy は自動では消えない）が数千件になったフォルダを、await のあとの継続（UI スレッド）で
            // 列挙して名前を解析すると、保存のたびに入力が止まる。列挙と名前の解析はスレッドプールで行い、
            // 戻るのは結果の小さな値（最新の自動控えのパス・次のファイル名）だけにする。
            var latestAutoPath = await Task.Run(() =>
            {
                Directory.CreateDirectory(directory);
                return skipIfSameAsLatestAuto ? FindLatestAuto(directory, fileName) : null;
            }).ConfigureAwait(true);

            if (latestAutoPath is not null && await IsSameAsLatestAutoAsync(latestAutoPath, code).ConfigureAwait(true))
            {
                return new VbaRescueSaveResult(VbaRescueSaveStatus.SkippedSame);
            }

            var nextName = await Task.Run(() => NextFileName(directory, fileName)).ConfigureAwait(true);
            var path = Path.Combine(directory, nextName);
            await File.WriteAllTextAsync(path, code, Utf8NoBom).ConfigureAwait(true);
            return new VbaRescueSaveResult(VbaRescueSaveStatus.Saved, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VbaRescueSaveResult(VbaRescueSaveStatus.Failed, Failure: ExceptionMessages.Describe(ex));
        }
    }

    /// <summary>
    /// 同じ秒・同じ種別の控えが既にあれば、<b>いちばん大きい連番の次</b>の連番を付けた名前にする。
    /// 空いている番号を拾う（<c>_2</c> が無ければ <c>_2</c>）のではなく最大値の次にするのは、掃除で古い連番が消えたあとに、
    /// 新しい控えが「最も古い連番」になって、書いた直後の掃除で消されてしまうのを防ぐため（同じ秒に何度も反映したとき）。
    /// </summary>
    private static string NextFileName(string directory, string fileName)
    {
        var wanted = VbaRescueNaming.Parse(fileName)!;
        var highest = 0;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var other = VbaRescueNaming.Parse(Path.GetFileName(file));
            if (other is not null && other.Kind == wanted.Kind && other.At == wanted.At
                && string.Equals(other.Module, wanted.Module, StringComparison.OrdinalIgnoreCase))
            {
                highest = Math.Max(highest, other.Sequence);
            }
        }

        return highest == 0 ? fileName : VbaRescueNaming.WithSequence(fileName, highest + 1);
    }

    /// <summary>同じモジュールの最新の <c>Auto</c> のフルパス（無ければ null）。フォルダを列挙するので、呼び出し側はスレッドプールで呼ぶ。</summary>
    private static string? FindLatestAuto(string directory, string newFileName)
    {
        var module = VbaRescueNaming.Parse(newFileName)?.Module;
        if (module is null || !Directory.Exists(directory)) return null;

        var latest = Directory.EnumerateFiles(directory)
            .Select(f => (Path: f, Name: VbaRescueNaming.Parse(Path.GetFileName(f))))
            .Where(x => x.Name is { Kind: VbaRescueKind.Auto } n && string.Equals(n.Module, module, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Name!.At).ThenByDescending(x => x.Name!.Sequence)
            .ThenByDescending(x => Path.GetFileName(x.Path), StringComparer.Ordinal)
            .FirstOrDefault();
        return latest.Path;
    }

    private static async Task<bool> IsSameAsLatestAutoAsync(string latestPath, string code)
    {
        try
        {
            var existing = await File.ReadAllTextAsync(latestPath, Utf8NoBom).ConfigureAwait(true);
            return string.Equals(VbaTextNormalizer.Hash(existing), VbaTextNormalizer.Hash(code), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // 読めないなら、同じとは言えない。新しく書く。
        }
    }

    // ------------------------------------------------------------------
    // 一覧・読み取り・掃除・削除
    // ------------------------------------------------------------------

    /// <summary>
    /// 控えの一覧（新しい順）。<paramref name="prune"/> が true なら先に掃除する（「Excel 側の控えを見る」を開いたとき。
    /// 起動時や定期の掃除はしない。仕様書 7.3-5）。解析できないファイルは載せず、消さない。
    /// </summary>
    public async Task<VbaRescueListing> ListAsync(Project project, bool prune = false, CancellationToken ct = default)
    {
        var pruned = 0;
        var overLimit = false;
        if (prune)
        {
            if (_gate is not null) await _gate.WaitAsync(ct).ConfigureAwait(true);
            try
            {
                (pruned, overLimit) = await PruneCoreAsync(project).ConfigureAwait(true);
            }
            finally
            {
                _gate?.Release();
            }
        }

        var directory = DirectoryOf(project);
        if (!Directory.Exists(directory)) return VbaRescueListing.Empty;

        var items = await Task.Run(() => ScanItems(directory, countLines: true), ct).ConfigureAwait(true);
        var total = items.Sum(i => i.Size);
        return new VbaRescueListing(
            items.OrderByDescending(i => i.Name.At).ThenByDescending(i => i.Name.Sequence)
                .ThenBy(i => i.Module, StringComparer.OrdinalIgnoreCase).ThenByDescending(i => i.FileName, StringComparer.Ordinal).ToList(),
            total,
            prune ? overLimit : total > MaxTotalBytes,
            pruned);
    }

    private long MaxTotalBytes => (long)Math.Max(1, MaxTotalMB) * 1024 * 1024;

    /// <param name="directory">控えのフォルダ。</param>
    /// <param name="countLines">
    /// 行数を数えるか。数えるには全ファイルの中身を読む（最大 50MB）。<b>掃除（<see cref="PruneCoreAsync"/>）は
    /// ファイル名と大きさだけを使う</b>ので、読まずに 0 とする。保存時の自動反映は 30 秒に 1 回掃除に入るため、
    /// 掃除のたびに全控えを読み直すと、保存のたびに数十 MB の読み取りが走りうる（<c>FileInfo.Length</c> はディレクトリの
    /// 情報だけで済む）。行数は、一覧を開くとき（<see cref="ListAsync"/>）にだけ数える。
    /// </param>
    private static List<VbaRescueItem> ScanItems(string directory, bool countLines)
    {
        var items = new List<VbaRescueItem>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = VbaRescueNaming.Parse(Path.GetFileName(file));
                if (name is null) continue;
                try
                {
                    items.Add(new VbaRescueItem(
                        Path.GetFileName(file), file, name, new FileInfo(file).Length, countLines ? CountLines(file) : 0));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 読めない 1 件のために一覧全体を失わない。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // フォルダを列挙できなければ、空の一覧にする。
        }

        return items;
    }

    // 行数（改行の数。最後が改行でなければ 1 行足す）。本文を文字列として読まず、バイトで数える
    // （一覧に出すだけのために、最大 50MB 分の文字列を作らないため）。
    private static int CountLines(string path)
    {
        var lines = 0;
        var lastWasNewline = true;
        var any = false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192);
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            any = true;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n') lines++;
            }

            lastWasNewline = buffer[read - 1] == (byte)'\n';
        }

        return !any ? 0 : lastWasNewline ? lines : lines + 1;
    }

    /// <summary>控えの本文を読む。</summary>
    public async Task<string> ReadTextAsync(VbaRescueItem item, CancellationToken ct = default)
        => await File.ReadAllTextAsync(item.FullPath, Utf8NoBom, ct).ConfigureAwait(true);

    /// <summary>
    /// 控えを 1 つ消す（利用者が「控えを削除」を押したとき）。控えを消すだけで利用者の作成物ではないので、
    /// ごみ箱ではなく通常の削除。消せなければ false と理由。
    /// </summary>
    public (bool Deleted, string? Failure) Delete(VbaRescueItem item)
    {
        try
        {
            File.Delete(item.FullPath);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, ExceptionMessages.Describe(ex));
        }
    }

    /// <summary>
    /// 掃除する（<see cref="VbaRescuePruner"/> の計画どおりに消す）。呼び出し元が <see cref="VbaSyncService"/> のゲートを
    /// 持っているときに使う。削除に失敗したファイルは次回に回す。
    /// </summary>
    internal async Task<(int Deleted, bool OverLimit)> PruneCoreAsync(Project project)
    {
        var directory = DirectoryOf(project);
        if (!Directory.Exists(directory)) return (0, false);

        var scanned = await Task.Run(() => ScanItems(directory, countLines: false)).ConfigureAwait(true);
        var plan = VbaRescuePruner.Plan(
            scanned.Select(i => new VbaRescueEntry(i.FileName, i.Name, i.Size)).ToList(), Math.Max(1, MaxGenerations), MaxTotalBytes);

        var deleted = 0;
        var remaining = plan.TotalBytesAfter;
        foreach (var entry in plan.Delete)
        {
            try
            {
                File.Delete(Path.Combine(directory, entry.FileName));
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                remaining += entry.Size; // 消せなかった分は、まだ容量に残っている。次回の掃除に回す。
                Logger?.Warn("vba-rescue", $"控えを掃除できませんでした（次回に回します）: {entry.FileName}（{ExceptionMessages.Describe(ex)}）");
            }
        }

        if (deleted > 0) Logger?.Info("vba-rescue", $"古い控えを {deleted} 件掃除しました（{project.Name}）。", targetPath: directory);
        return (deleted, remaining > MaxTotalBytes);
    }

    /// <summary>
    /// 現在のミラーの本文（差分の右側）。ファイルが無い、または読めないときは Text が null
    /// （削除保留のモジュールなど。戻すとファイルが再び現れる）。
    /// </summary>
    public async Task<(string? RelativePath, string? Text)> ReadMirrorTextAsync(Project project, string module, CancellationToken ct = default)
    {
        var relative = await ResolveMirrorPathAsync(project, module, ct).ConfigureAwait(true);
        if (relative is null) return (null, null);

        var full = Path.Combine(project.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return (relative, null);

        var read = await FileTextIO.ReadAsync(full, ct).ConfigureAwait(true);
        return (relative, read.IsSuccess ? read.Value.Text : null);
    }

    // ------------------------------------------------------------------
    // 戻す（ミラーへ取り込む）
    // ------------------------------------------------------------------

    /// <summary>
    /// 控えの本文をミラーのファイルへ書く（Excel には書かない。仕様書 7.5）。
    /// <list type="number">
    /// <item>対象のミラーのファイルに未保存のタブがあれば、Pull と同じ確認を出す。断られたら戻さない。</item>
    /// <item>現在のミラーの本文が控えと違うなら、先に <c>Mirror</c> の控えとして残す。<b>残せなければ戻さない</b>（E816）。</item>
    /// <item>ミラーへ書く。既存のファイルは文字コード・改行・末尾改行を保ち、無ければ UTF-8（BOM なし）・CRLF。<b>同期基準は触らない</b>。</item>
    /// </list>
    /// </summary>
    public async Task<VbaRescueRestoreOutcome> RestoreAsync(Project project, VbaRescueItem item, CancellationToken ct = default)
    {
        string rescueText;
        try
        {
            rescueText = await File.ReadAllTextAsync(item.FullPath, Utf8NoBom, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(null, $"控えを読めませんでした: {ExceptionMessages.Describe(ex)}");
        }

        var relative = await ResolveMirrorPathAsync(project, item.Module, ct).ConfigureAwait(true);
        if (relative is null)
        {
            return Failed(null, $"戻し先のミラーのファイルを特定できませんでした（モジュール {item.Module}）。");
        }

        var full = Path.Combine(project.Root, relative.Replace('/', Path.DirectorySeparatorChar));

        // 未保存の編集の確認は、ゲートの外で行う（利用者が放置している間、ほかの取込・反映を待たせないため）。
        if (File.Exists(full) && BeforeRestoreAsync is not null)
        {
            var confirm = await BeforeRestoreAsync(new[] { full }).ConfigureAwait(true);
            if (confirm.Declined.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)))
            {
                return new VbaRescueRestoreOutcome(
                    VbaRescueRestoreStatus.Declined, relative, null,
                    "未保存の変更があるため、戻しませんでした。", Array.Empty<GraftIssue>());
            }
        }

        if (_gate is not null) await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            string? backupPath = null;
            if (File.Exists(full))
            {
                var current = await FileTextIO.ReadAsync(full, ct).ConfigureAwait(true);
                if (!current.IsSuccess)
                {
                    return new VbaRescueRestoreOutcome(
                        VbaRescueRestoreStatus.Failed, relative, null, "現在のミラーのファイルを読めなかったため、戻しませんでした。", current.Issues);
                }

                if (string.Equals(
                        VbaTextNormalizer.Hash(current.Value.Text), VbaTextNormalizer.Hash(rescueText), StringComparison.Ordinal))
                {
                    return new VbaRescueRestoreOutcome(
                        VbaRescueRestoreStatus.AlreadySame, relative, null, "ミラーは、すでにこの控えと同じ内容です。", Array.Empty<GraftIssue>());
                }

                // 戻す前のミラーを Mirror の控えとして残す。残せなければ戻さない（利用者が Graft で書いた内容が失われるため）。
                var kept = await SaveAsync(project, item.Module, current.Value.Text, VbaRescueKind.Mirror).ConfigureAwait(true);
                if (kept.Status == VbaRescueSaveStatus.Failed)
                {
                    var issue = GraftIssue.Of(
                        ErrorCode.E816,
                        $"{relative}: 戻す前のミラーの内容を控えに保存できなかったため、戻しませんでした。{kept.Failure} 控えの保存先: {DirectoryOf(project)}",
                        path: relative);
                    return new VbaRescueRestoreOutcome(
                        VbaRescueRestoreStatus.Failed, relative, null, "戻す前のミラーを控えに保存できなかったため、戻しませんでした。", new[] { issue });
                }

                backupPath = kept.Path;
            }

            var written = await VbaSyncService.WriteMirrorFileAsync(project.Root, relative, rescueText, ct).ConfigureAwait(true);
            if (!written.IsSuccess)
            {
                return new VbaRescueRestoreOutcome(
                    VbaRescueRestoreStatus.Failed, relative, backupPath, "ミラーのファイルへ書けませんでした。", written.Issues);
            }

            Logger?.Info(
                "vba-rescue",
                $"控え（{item.KindLabel} {item.At:yyyy-MM-dd HH:mm:ss}）をミラーへ戻しました" +
                (backupPath is null ? "" : $"（戻す前のミラーの控え: {Path.GetFileName(backupPath)}）") + $": {relative}",
                targetPath: full);

            // 戻す前のミラーの控えが増えたので、世代数・容量を守る。失敗しても戻した結果には影響させない。
            try
            {
                await PruneCoreAsync(project).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger?.Warn("vba-rescue", $"控えの掃除に失敗しました: {ExceptionMessages.Describe(ex)}");
            }

            return new VbaRescueRestoreOutcome(
                VbaRescueRestoreStatus.Restored, relative, backupPath,
                "戻しました。履歴には記録されません。Excel に送るには『Excel へ反映』を押してください。", Array.Empty<GraftIssue>());
        }
        finally
        {
            _gate?.Release();
        }
    }

    private static VbaRescueRestoreOutcome Failed(string? relative, string message)
        => new(VbaRescueRestoreStatus.Failed, relative, null, message, Array.Empty<GraftIssue>());

    /// <summary>
    /// モジュール名から、戻し先のミラーの相対パスを決める。まずミラーのフォルダの中のファイル、無ければ
    /// 同期状態の記録（ミラーのファイルを消して削除保留になっているモジュール。戻すとファイルが再び現れ、保留が解ける）。
    /// </summary>
    private async Task<string?> ResolveMirrorPathAsync(Project project, string module, CancellationToken ct)
    {
        if (Directory.Exists(project.Root))
        {
            foreach (var file in Directory.EnumerateFiles(project.Root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(project.Root, file).Replace('\\', '/');
                if (relative.Split('/').Any(segment => segment.StartsWith('.'))) continue;
                if (VbaMirrorLayout.TryParse(relative, out var parsed, out _) && parsed is not null
                    && string.Equals(parsed.ComponentName, module, StringComparison.OrdinalIgnoreCase))
                {
                    return parsed.RelativePath;
                }
            }
        }

        if (project.Vba is { } link)
        {
            var loaded = await _states.LoadAsync(project.Id, link.WorkbookFullName, ct).ConfigureAwait(true);
            if (loaded.IsSuccess)
            {
                foreach (var (key, record) in loaded.Value.Modules)
                {
                    if (string.Equals(record.Component, module, StringComparison.OrdinalIgnoreCase)) return key;
                }
            }
        }

        return null;
    }
}
