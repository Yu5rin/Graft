using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Graft.Infra;

namespace Graft.Core;

/// <summary>振り直し1件分の対応（旧番号・旧フォルダ名 → 新番号・新フォルダ名）。</summary>
/// <param name="HasFolder">実体のバックアップフォルダがあったか。falseならhistory.jsonlにだけ残っていた記録。</param>
public sealed record RevisionRenumbering(
    int OldRevision, int NewRevision, string OldFolderName, string NewFolderName, bool HasFolder);

/// <summary>
/// 同じ番号のバックアップフォルダが2つ以上ある状態（<c>r36_20261009_133000</c> と
/// <c>r36_20261009_155900</c> のような、番号が同じで時刻だけ違うフォルダ）を検出し、
/// 適用日時の順に番号を振り直して解消する。
///
/// 【なぜ起きたか】 コンテキスト収集画面の保存が古いスナップショットでprojects.jsonを丸ごと上書きし、
/// 適用済みの履歴番号を巻き戻した。フォルダ名には時刻が入る（<see cref="AppPaths.BuildRevisionFolderName"/>）
/// ので名前は衝突せず、実機ではr36〜r42が2つずつでき、さらにr37…r44と続いた。
/// <see cref="RevisionStore.ListAsync"/>は番号ごとに1件へまとめるため片方が履歴から見えなくなり、
/// 「ここまで戻す」・「元に戻す」は番号で対象を選ぶので別のリビジョンを取り違えるおそれがある。
/// 原因の書き戻しは直したが、既に重複してしまった利用者のデータは起動時に自動で直す必要がある。
///
/// 【振り直しの規則】 重複している最小の番号を M とし、<b>M以上のリビジョンすべて</b>を適用日時
/// （フォルダ名のタイムスタンプ。同時刻なら元の番号、それでも同じならフォルダ名）の順に並べて
/// M から連番を振る。重複した側（新しい側）だけを末尾へ足すやり方にしないのは、
/// 「ここまで戻す」が番号の降順＝新しい順に取り消す前提で作られているため、番号の順序が
/// 適用の時間順と食い違うと取り消しの順序が壊れるから（例: r36〜r42が2組とr43・r44の16件は、
/// 時刻順にr36〜r51になる）。M未満のリビジョンには触れない。
///
/// 【更新するもの】 フォルダ名、manifest.jsonのrevision、history.jsonlの該当行（revisionとfolderName。
/// 同じ番号の行が2つあるので、行の folderName または「番号＋適用日時」の組で対応づける）。
/// フォルダが既に無くhistory.jsonlにだけ残っている記録も、並び順が崩れないよう同じ連番に含める。
/// ほかにリビジョン番号を保持する永続データは無い（projects.jsonのnextRevisionは呼び出し側が
/// 修復後に実体の最大番号+1へ補正する。logs/の過去の記録とGitのコミットメッセージは当時の
/// 事実の記録なので書き換えない）。manifestのsummaryに文章として入っている番号
/// （「r3まで戻す（r5、r4を取り消し）」など）は文面の解釈になるため書き換えない。
///
/// 【途中で落ちても既存のフォルダを失わない作り】
/// ・名前の衝突を避けるため、まず全フォルダを一時名（<c>最終のフォルダ名.repairing</c>）へ移し、
///   manifestとhistoryを直してから最終名へ移す2段階にする。一時名は命名規則に合わないので
///   履歴には現れないが、フォルダは消えず、最終名を名前に含んでいる。
/// ・開始前にhistory.jsonlを複製し（<c>history.jsonl.before-renumber-…</c>）、対応表を
///   <c>repair-journal.json</c> に書く。途中で落ちたら次回の起動で、この対応表から続きを行う。
/// ・一時名への移動中に失敗したら元の名前へ戻して終える。
/// 重複が無いときはフォルダの一覧を読むだけで、ディスクへは一切書かない。
/// </summary>
public sealed class RevisionDuplicateRepairer
{
    private const string TempSuffix = ".repairing";
    private const string JournalFileName = "repair-journal.json";
    private const string StampFormat = "yyyyMMdd_HHmmss";

    private static readonly Regex TempFolderPattern = new(@"^(r\d+_\d{8}_\d{6})\.repairing$", RegexOptions.Compiled);

    private const string PhasePlanned = "planned";
    private const string PhaseMovedToTemp = "movedToTemp";
    private const string PhaseManifestsFixed = "manifestsFixed";
    private const string PhaseHistoryFixed = "historyFixed";

    private readonly AppPaths _paths;
    private readonly RevisionIndex _revisionIndex;

    public RevisionDuplicateRepairer(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _revisionIndex = new RevisionIndex(paths);
    }

    /// <summary>
    /// 指定プロジェクトの重複を検出して直す。振り直した対応を返す（重複が無ければ空）。
    /// 問題はWarningとして<see cref="GraftResult{T}.Issues"/>に載せ、失敗（Fail）にはしない。
    /// 呼び出し元（起動時検証）が失敗で止まらないようにするため。
    /// </summary>
    public async Task<GraftResult<IReadOnlyList<RevisionRenumbering>>> RepairAsync(
        string projectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var projectDir = _paths.GetProjectBackupDirectory(projectId);
        var applied = new List<RevisionRenumbering>();
        var issues = new List<GraftIssue>();
        if (!Directory.Exists(projectDir))
        {
            return GraftResult<IReadOnlyList<RevisionRenumbering>>.Ok(applied, issues);
        }

        try
        {
            // 前回途中で落ちていたら、まずその続きを行う。
            var journalPath = Path.Combine(projectDir, JournalFileName);
            if (File.Exists(journalPath))
            {
                var resumed = await ResumeAsync(projectId, projectDir, journalPath, issues, ct).ConfigureAwait(false);
                applied.AddRange(resumed);
            }
            FinishOrphanTempFolders(projectDir, issues);

            var folders = EnumerateRevisionFolders(projectDir);
            var duplicated = folders.GroupBy(f => f.Revision).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicated.Count == 0)
            {
                return GraftResult<IReadOnlyList<RevisionRenumbering>>.Ok(applied, issues);
            }

            var firstDuplicate = duplicated.Min();
            var historyEntries = await ReadHistoryEntriesAsync(projectId, ct).ConfigureAwait(false);
            var plan = BuildPlan(folders, historyEntries, firstDuplicate);
            var changes = plan.Where(i => i.IsChanged).ToList();
            if (changes.Count > 0)
            {
                applied.AddRange(await ExecuteAsync(projectId, projectDir, changes, issues, ct).ConfigureAwait(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            issues.Add(GraftIssue.Of(
                ErrorCode.E401, $"履歴番号の重複の修復に失敗しました: {ExceptionMessages.Describe(ex)}",
                path: projectDir, severity: Severity.Warning));
        }

        return GraftResult<IReadOnlyList<RevisionRenumbering>>.Ok(applied, issues);
    }

    // ------------------------------------------------------------------
    // 計画
    // ------------------------------------------------------------------

    private sealed class PlanItem
    {
        public required string Stamp { get; init; }
        public required int OldRevision { get; init; }
        public required string OldFolderName { get; init; }
        public required bool HasFolder { get; init; }
        public int NewRevision { get; set; }
        public string NewFolderName => $"r{NewRevision}_{Stamp}";
        public bool IsChanged => NewRevision != OldRevision;
    }

    private sealed record FolderInfo(string Name, int Revision, string Stamp);

    private static List<FolderInfo> EnumerateRevisionFolders(string projectDir)
    {
        var result = new List<FolderInfo>();
        foreach (var folder in Directory.EnumerateDirectories(projectDir))
        {
            var name = Path.GetFileName(folder);
            var parsed = BackupPathUtil.TryParseFolderName(name);
            if (parsed is null) continue;
            result.Add(new FolderInfo(name, parsed.Value.Revision, name[(name.IndexOf('_') + 1)..]));
        }
        return result;
    }

    private async Task<IReadOnlyList<RevisionIndexEntry>> ReadHistoryEntriesAsync(string projectId, CancellationToken ct)
    {
        var read = await _revisionIndex.ReadAllAsync(projectId, ct).ConfigureAwait(false);
        return read.Value;
    }

    private static string StampOf(DateTimeOffset appliedAt)
        => appliedAt.ToString(StampFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// 振り直しの計画を立てる。<paramref name="firstDuplicate"/>以上のフォルダと、同じ範囲で
    /// フォルダが無いhistory.jsonlだけの記録を、適用日時・元の番号・フォルダ名の順に並べて連番を振る。
    /// </summary>
    private static List<PlanItem> BuildPlan(
        IReadOnlyList<FolderInfo> folders, IReadOnlyList<RevisionIndexEntry> history, int firstDuplicate)
    {
        var items = folders
            .Where(f => f.Revision >= firstDuplicate)
            .Select(f => new PlanItem { Stamp = f.Stamp, OldRevision = f.Revision, OldFolderName = f.Name, HasFolder = true })
            .ToList();

        // フォルダの無い記録（世代整理や外部操作でフォルダだけ消えたもの）。フォルダのある記録と
        // 同じ行（folderName、または番号＋適用日時）に当たるものは除く。
        var knownNames = new HashSet<string>(items.Select(i => i.OldFolderName), StringComparer.OrdinalIgnoreCase);
        var knownKeys = new HashSet<(int, string)>(items.Select(i => (i.OldRevision, i.Stamp)));
        var allFolderNames = new HashSet<string>(folders.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in history)
        {
            if (entry.Revision < firstDuplicate) continue;
            var stamp = StampOf(entry.AppliedAt);
            if (knownNames.Contains(entry.FolderName) || knownKeys.Contains((entry.Revision, stamp))) continue;
            // フォルダ名が実在する別のフォルダ（firstDuplicate未満など）を指す行は対象外。
            if (allFolderNames.Contains(entry.FolderName)) continue;

            items.Add(new PlanItem
            {
                Stamp = stamp, OldRevision = entry.Revision, OldFolderName = entry.FolderName, HasFolder = false,
            });
            knownNames.Add(entry.FolderName);
            knownKeys.Add((entry.Revision, stamp));
        }

        var sorted = items
            .OrderBy(i => i.Stamp, StringComparer.Ordinal)
            .ThenBy(i => i.OldRevision)
            .ThenBy(i => i.OldFolderName, StringComparer.Ordinal)
            .ToList();
        for (var i = 0; i < sorted.Count; i++) sorted[i].NewRevision = firstDuplicate + i;
        return sorted;
    }

    // ------------------------------------------------------------------
    // 実行（フェーズごとにjournalへ進み具合を残す）
    // ------------------------------------------------------------------

    private sealed record JournalItem(
        int OldRevision, int NewRevision, string OldFolderName, string NewFolderName, bool HasFolder);

    private sealed record Journal
    {
        public string Phase { get; init; } = PhasePlanned;
        public string? HistoryBackupFile { get; init; }
        public IReadOnlyList<JournalItem> Items { get; init; } = Array.Empty<JournalItem>();
    }

    private async Task<IReadOnlyList<RevisionRenumbering>> ExecuteAsync(
        string projectId, string projectDir, IReadOnlyList<PlanItem> changes, List<GraftIssue> issues, CancellationToken ct)
    {
        // 履歴の複製。重複の修復で書き換える前の姿を残しておく（万一の手戻し用）。
        string? backupName = null;
        var historyPath = _revisionIndex.GetIndexPath(projectId);
        if (File.Exists(historyPath))
        {
            backupName = $"history.jsonl.before-renumber-{DateTime.Now.ToString(StampFormat, CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..8]}";
            File.Copy(historyPath, Path.Combine(projectDir, backupName), overwrite: false);
        }

        var journal = new Journal
        {
            Phase = PhasePlanned,
            HistoryBackupFile = backupName,
            Items = changes
                .Select(c => new JournalItem(c.OldRevision, c.NewRevision, c.OldFolderName, c.NewFolderName, c.HasFolder))
                .ToList(),
        };
        var journalPath = Path.Combine(projectDir, JournalFileName);
        await WriteJournalAsync(journalPath, journal, ct).ConfigureAwait(false);

        return await RunPhasesAsync(projectId, projectDir, journalPath, journal, issues, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RevisionRenumbering>> ResumeAsync(
        string projectId, string projectDir, string journalPath, List<GraftIssue> issues, CancellationToken ct)
    {
        Journal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<Journal>(
                await File.ReadAllTextAsync(journalPath, ct).ConfigureAwait(false), JsonFileStore.DefaultOptions);
        }
        catch (JsonException)
        {
            journal = null;
        }

        if (journal is null || journal.Items.Count == 0)
        {
            // 読めない対応表は続きに使えない。残しておくと毎回この警告になるので脇へ退避し、
            // 一時名のまま残ったフォルダは FinishOrphanTempFolders が最終名へ戻す。
            File.Move(journalPath, journalPath + ".corrupt-" + Guid.NewGuid().ToString("N")[..8], overwrite: false);
            issues.Add(GraftIssue.Of(
                ErrorCode.E404, "履歴番号の修復の途中経過（repair-journal.json）を読み取れませんでした",
                path: journalPath, severity: Severity.Warning));
            return Array.Empty<RevisionRenumbering>();
        }

        return await RunPhasesAsync(projectId, projectDir, journalPath, journal, issues, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RevisionRenumbering>> RunPhasesAsync(
        string projectId, string projectDir, string journalPath, Journal journal, List<GraftIssue> issues, CancellationToken ct)
    {
        var folderItems = journal.Items.Where(i => i.HasFolder).ToList();
        var result = journal.Items
            .Select(i => new RevisionRenumbering(i.OldRevision, i.NewRevision, i.OldFolderName, i.NewFolderName, i.HasFolder))
            .ToList();

        // 段階1: 全フォルダを一時名へ。ここで失敗したら元の名前へ戻して終える（何も変わらなかったことにする）。
        if (journal.Phase == PhasePlanned)
        {
            var moved = new List<JournalItem>();
            try
            {
                foreach (var item in folderItems)
                {
                    var oldPath = Path.Combine(projectDir, item.OldFolderName);
                    var tempPath = Path.Combine(projectDir, item.NewFolderName + TempSuffix);
                    if (Directory.Exists(LongPath.Extended(oldPath)) && !Directory.Exists(LongPath.Extended(tempPath)))
                    {
                        Directory.Move(LongPath.Extended(oldPath), LongPath.Extended(tempPath));
                        moved.Add(item);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foreach (var item in moved)
                {
                    TryMove(Path.Combine(projectDir, item.NewFolderName + TempSuffix), Path.Combine(projectDir, item.OldFolderName));
                }
                File.Delete(journalPath);
                issues.Add(GraftIssue.Of(
                    ErrorCode.E401, $"履歴番号の重複を直せませんでした（フォルダ名を元に戻しました）: {ExceptionMessages.Describe(ex)}",
                    path: projectDir, severity: Severity.Warning));
                return Array.Empty<RevisionRenumbering>();
            }

            journal = journal with { Phase = PhaseMovedToTemp };
            await WriteJournalAsync(journalPath, journal, ct).ConfigureAwait(false);
        }

        // 段階2: manifest.jsonのrevisionを新しい番号へ。
        if (journal.Phase == PhaseMovedToTemp)
        {
            foreach (var item in folderItems)
            {
                var folder = Path.Combine(projectDir, item.NewFolderName + TempSuffix);
                if (!Directory.Exists(LongPath.Extended(folder))) continue; // 既に最終名（再開時の二重実行）
                await FixManifestRevisionAsync(folder, item.NewRevision, issues, ct).ConfigureAwait(false);
            }

            journal = journal with { Phase = PhaseManifestsFixed };
            await WriteJournalAsync(journalPath, journal, ct).ConfigureAwait(false);
        }

        // 段階3: history.jsonlの該当行。
        if (journal.Phase == PhaseManifestsFixed)
        {
            await FixHistoryAsync(projectId, journal, ct).ConfigureAwait(false);
            journal = journal with { Phase = PhaseHistoryFixed };
            await WriteJournalAsync(journalPath, journal, ct).ConfigureAwait(false);
        }

        // 段階4: 一時名から最終名へ。
        if (journal.Phase == PhaseHistoryFixed)
        {
            var failed = false;
            foreach (var item in folderItems)
            {
                var tempPath = Path.Combine(projectDir, item.NewFolderName + TempSuffix);
                var finalPath = Path.Combine(projectDir, item.NewFolderName);
                if (!Directory.Exists(LongPath.Extended(tempPath))) continue;
                if (Directory.Exists(LongPath.Extended(finalPath)))
                {
                    failed = true;
                    issues.Add(GraftIssue.Of(
                        ErrorCode.E401, $"{item.NewFolderName} が既に存在するため、一時名のフォルダを戻せませんでした",
                        path: tempPath, severity: Severity.Warning));
                    continue;
                }
                try
                {
                    Directory.Move(LongPath.Extended(tempPath), LongPath.Extended(finalPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed = true;
                    issues.Add(GraftIssue.Of(
                        ErrorCode.E401, $"フォルダ名を確定できませんでした: {ExceptionMessages.Describe(ex)}",
                        path: tempPath, severity: Severity.Warning));
                }
            }

            // 全部確定できたときだけ対応表を片付ける。残した場合は次回の起動で続きを行う。
            if (!failed) File.Delete(journalPath);
        }

        return result;
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            if (Directory.Exists(LongPath.Extended(from)) && !Directory.Exists(LongPath.Extended(to)))
            {
                Directory.Move(LongPath.Extended(from), LongPath.Extended(to));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 戻せなかったフォルダは一時名のまま残る（最終名を含むので消えてはいない）。
            // 次回起動時の FinishOrphanTempFolders が最終名へ戻す。
        }
    }

    private static async Task WriteJournalAsync(string journalPath, Journal journal, CancellationToken ct)
        => await new JsonFileStore().WriteAsync(journalPath, journal, JsonFileStore.DefaultOptions, ct).ConfigureAwait(false);

    /// <summary>
    /// manifest.jsonのrevisionだけを書き換える。他の項目は触らず、読み込みと同じ形のまま残すため
    /// 型付きのモデルではなくJSONの木のまま編集する（将来の版が足した項目も失わない）。
    /// manifest.jsonが無い・壊れている場合は触らない（履歴一覧はフォルダ名の番号で補う。
    /// RevisionStore.ReadFolderAsyncのフォールバック参照）。
    /// </summary>
    private async Task FixManifestRevisionAsync(string folder, int newRevision, List<GraftIssue> issues, CancellationToken ct)
    {
        var manifestPath = Path.Combine(folder, "manifest.json");
        if (!File.Exists(LongPath.Extended(manifestPath))) return;

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(await File.ReadAllTextAsync(LongPath.Extended(manifestPath), ct).ConfigureAwait(false)) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is null)
        {
            issues.Add(GraftIssue.Of(
                ErrorCode.E404, "manifest.json を読み取れないため、リビジョン番号を書き換えませんでした",
                path: manifestPath, severity: Severity.Warning));
            return;
        }

        var key = root.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, "revision", StringComparison.OrdinalIgnoreCase))
                  ?? "revision";
        root[key] = newRevision;
        await new JsonFileStore().WriteAsync(manifestPath, root, JsonFileStore.DefaultOptions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// history.jsonlの該当行のrevisionとfolderNameを書き換える。行の対応づけは、まずfolderName
    /// （旧フォルダ名。時刻を含むので一意）、無ければ「旧番号＋適用日時（フォルダ名と同じ書式）」の組。
    /// 同じ番号の行が2つあっても、時刻が違うので取り違えない。
    /// 既に反映済み（旧フォルダ名に当たる行がもう無い）なら何もしない。
    /// </summary>
    private async Task FixHistoryAsync(string projectId, Journal journal, CancellationToken ct)
    {
        var historyPath = _revisionIndex.GetIndexPath(projectId);
        if (!File.Exists(historyPath)) return;

        var byName = new Dictionary<string, JournalItem>(StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<(int, string), JournalItem>();
        foreach (var item in journal.Items)
        {
            byName[item.OldFolderName] = item;
            byKey[(item.OldRevision, item.OldFolderName[(item.OldFolderName.IndexOf('_') + 1)..])] = item;
        }

        JournalItem? Find(RevisionIndexEntry entry)
        {
            if (byName.TryGetValue(entry.FolderName, out var named)) return named;
            return byKey.TryGetValue((entry.Revision, StampOf(entry.AppliedAt)), out var keyed) ? keyed : null;
        }

        await _revisionIndex.RewriteAsync(projectId, entry =>
        {
            var item = Find(entry);
            return item is null ? entry : entry with { Revision = item.NewRevision, FolderName = item.NewFolderName };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 一時名（<c>….repairing</c>）のまま残っているフォルダを最終名へ戻す。対応表が使えなかった場合や、
    /// 戻す途中で落ちた場合の後始末。一時名には最終名が入っているので、そこへ戻すだけで済む。
    /// 戻せない（同名が既にある）場合は触らず警告する。
    /// </summary>
    private static void FinishOrphanTempFolders(string projectDir, List<GraftIssue> issues)
    {
        foreach (var folder in Directory.EnumerateDirectories(projectDir))
        {
            var match = TempFolderPattern.Match(Path.GetFileName(folder));
            if (!match.Success) continue;

            var finalPath = Path.Combine(projectDir, match.Groups[1].Value);
            if (Directory.Exists(LongPath.Extended(finalPath)))
            {
                issues.Add(GraftIssue.Of(
                    ErrorCode.E401, $"{match.Groups[1].Value} が既に存在するため、修復途中のフォルダ {Path.GetFileName(folder)} を戻せません",
                    path: folder, severity: Severity.Warning));
                continue;
            }
            TryMove(folder, finalPath);
        }
    }
}
