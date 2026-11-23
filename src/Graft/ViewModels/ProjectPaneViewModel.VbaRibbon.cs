using System.IO;
using System.Text;
using System.Windows.Input;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="ProjectPaneViewModel"/> の分割ファイル。リボン（customUI）の書き換えの手動の入口（仕様書_ブックの情報と依頼の観点 16.10・
/// 16.13 の UI。段階 F3）: 右クリックとコマンドパレットの「リボンをブックへ反映…」「リボンの XML を検査」と、途中で終わった書き換えの
/// 起動時の後始末の知らせ。
/// <para>
/// 【段階 F4 の範囲】 開いているブックは、サービスが保存して閉じ、書き換えたあとで開き直す（16.6）。ここでは確認の文面（開いている・
/// 未保存の有無）を出し分け、処理中は段階の名前を出し、失敗の知らせに「もう一度試す」（一時的な理由）と「Excel で開く」（開き直せなかった・
/// 時間切れのあとで閉じられていた）を添える。
/// </para>
/// <para>
/// 【段階 F5 の範囲】 適用・取り消し・復元・手動の「Excel へ反映」のあとの自動の確認（16.10）は、<c>MainViewModel</c> が VBA の反映を済ませたあとで、
/// <see cref="PushRibbonAsync(Project, RibbonPushOptions?)"/> を呼んで行う（準備・確認・書き換えの流れを 1 か所に保つ。呼ぶ時点で VBA の反映の
/// 処理中の表示は終わっているので、書き換え自身の処理中の表示と二重にならない）。ここには、リボンの衝突（ブックのリボンとミラーの両方が変わっている）を
/// 衝突ダイアログで解決する入口（採用・上書き・反映しない）がある。
/// </para>
/// </summary>
public sealed partial class ProjectPaneViewModel
{
    // 起動してから、途中で終わった書き換えの後始末を確かめたプロジェクト（1 回の起動につき 1 回だけ知らせる）。
    private readonly HashSet<string> _ribbonRecoveryChecked = new(StringComparer.Ordinal);

    /// <summary>「リボンをブックへ反映…」（16.13）。リボンが未反映のときだけ有効。</summary>
    public ICommand PushRibbonCommand { get; private set; } = null!;

    /// <summary>「リボンの XML を検査」（16.8 の手動の検査）。</summary>
    public ICommand InspectRibbonCommand { get; private set; } = null!;

    /// <summary>
    /// 確認ダイアログ（<c>VbaRibbonRewriteWindow</c>。16.9）を出して、「書き換える」を選んだかを返す（View が配線する）。
    /// 未設定なら「中止」として扱う（確認を出せないときに書き換えない）。
    /// </summary>
    public Func<VbaRibbonRewriteViewModel, Task<bool>>? ShowRibbonRewriteAsync { get; set; }

    /// <summary>確認ダイアログの ViewModel を作る（<c>MainViewModel</c> が設定と UI 機能を渡して配線する）。</summary>
    public Func<VbaRibbonRewritePlan, VbaRibbonRewriteViewModel?>? CreateRibbonRewriteViewModel { get; set; }

    /// <summary>
    /// 数秒で消える通知に、押せる操作を 1 つ添えて出す（<c>MainViewModel</c> が <c>StatusNoticeViewModel.Show</c> を渡す）。
    /// 完了の通知の「Excel で開く」に使う。未設定なら、操作の無い通知（<see cref="ProjectPaneViewModel.Notify"/>）にする。
    /// </summary>
    public Action<string, string, Func<Task>>? NotifyWithAction { get; set; }

    /// <summary>書き換えの段階の名前を処理中の表示に出す（16.5。<c>MainViewModel</c> が配線する）。</summary>
    public Action<string>? ShowRibbonProgress { get; set; }

    /// <summary>
    /// 書き換え（保存して閉じる〜開き直す）のあいだ、中央ペインを処理中の表示にして、取り消し・貼り付けなどを塞ぐ（16.5）。
    /// 引数は始めるとき true・終わったとき false。<c>MainViewModel</c> が配線する。
    /// </summary>
    public Action<bool>? SetRibbonRewriteBusy { get; set; }

    /// <summary>フォルダを開く（E823 の「控えのフォルダを開く」）。既定はファイルマネージャ。テストで差し替える。</summary>
    public Action<string> RevealFolder { get; set; } = static path => PlatformServices.Current.FileManager.Reveal(path);

    private void InitializeVbaRibbonCommands()
    {
        PushRibbonCommand = new AsyncRelayCommand(PushRibbonSelectedAsync, CanPushRibbon, context: "リボンのブックへの反映");
        // 検査は読むだけなので、反映の最中でもよい（ブックにも Excel にも書かない）。
        InspectRibbonCommand = new AsyncRelayCommand(InspectRibbonSelectedAsync, CanInspectRibbon, context: "リボンの XML の検査");
    }

    /// <summary>
    /// 選択中の VBA プロジェクトでリボンを同期していて、未反映がある（またはまだ数えていない）とき。未反映が無いと分かっていれば
    /// 無効にする（16.13）。数える前に無効にすると、起動直後に押せない時間ができるので、分からないときは有効にして、押したときに
    /// 「反映するものはありません」と伝える。
    /// </summary>
    private bool CanPushRibbon()
    {
        if (!CanOperateOnSelectedVbaProject() || _vbaSync?.Ribbon.IsActive != true || SelectedItem is not { } item) return false;
        return !_vbaBadgeCache.TryGetValue(item.Project.Id, out var entry) || entry.RibbonUnpushed;
    }

    private bool CanInspectRibbon()
        => IsVbaFeatureAvailable && _vbaSync?.Ribbon.IsActive == true && SelectedItem is { IsDisconnected: false } item && item.Project.Vba is not null;

    private async Task PushRibbonSelectedAsync()
    {
        if (_vbaSync is null || SelectedItem is not { } item || item.Project.Vba is null) return;
        if (RejectWhileVbaBusy()) return;
        await PushRibbonAsync(item.Project, null).ConfigureAwait(true);
    }

    /// <summary>
    /// リボンをブックへ反映する（16.5）: 準備 → 〔衝突があれば衝突ダイアログ〕→ 確認（毎回）→ 書き換え → 結果の知らせ。失敗のうち、
    /// 利用者が直してすぐ押し直せるもの（使用中・Excel の応答なし・マクロの実行中）は「もう一度試す」を出し、選ばれたら準備からやり直す
    /// （確認も、もう一度出す）。手動の入口と、適用・取り消し・復元・手動の「Excel へ反映」のあとの自動の確認が、すべてここを通る。
    /// </summary>
    internal Task<RibbonPushEnd> PushRibbonAsync(Project project) => PushRibbonAsync(project, null);

    /// <inheritdoc cref="PushRibbonAsync(Project)"/>
    internal async Task<RibbonPushEnd> PushRibbonAsync(Project project, RibbonPushOptions? options)
    {
        options ??= new RibbonPushOptions();
        if (_vbaSync is null) return RibbonPushEnd.NothingToDo;
        var ribbon = _vbaSync.Ribbon;
        var overwrite = new HashSet<string>(options.OverwriteConflicts ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        // 衝突ダイアログで選んだ（採用・上書き・反映しない）パス。同じ衝突を聞き直さない。
        var decided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            // 前回の書き換えが途中で終わっていれば、先に片付けて知らせる（16.11「次のリボンの操作で」）。
            if (await ribbon.RecoverInterruptedAsync(project).ConfigureAwait(true) is { } recovery)
            {
                await _dialogs.ShowMessageAsync(RecoveryTitle, recovery.Message).ConfigureAwait(true);
            }

            var prepared = await ribbon.PrepareRewriteAsync(project, overwriteConflicts: overwrite.ToList()).ConfigureAwait(true);
            if (!prepared.IsSuccess)
            {
                if (!await ShowRibbonFailureAsync("リボンをブックへ反映できません", project, prepared.Issues).ConfigureAwait(true)) return RibbonPushEnd.Failed;
                continue;
            }

            var preparation = prepared.Value;

            // 衝突（ブックのリボンとミラーの両方が変わっている）は、衝突ダイアログで利用者に選んでもらう（16.4）。聞くのは 1 つの衝突につき 1 回。
            var undecided = preparation.Conflicts.Where(c => !decided.Contains(c.RelativePath)).ToList();
            if (undecided.Count > 0 && ShowConflictAsync is not null)
            {
                var request = new VbaConflictRequest(options.ConflictScene, undecided);
                var decision = await ShowConflictAsync(request).ConfigureAwait(true) ?? request.DefaultDecision;
                if (decision.Aborted)
                {
                    Notify?.Invoke("リボンの反映を中止しました（ブックのリボンとファイルの衝突が解決されていません）。リボンは未反映のままです。");
                    return RibbonPushEnd.Cancelled;
                }

                var adopt = decision.PathsWith(VbaConflictChoice.AdoptExcel);
                foreach (var path in undecided.Select(c => c.RelativePath)) decided.Add(path);
                foreach (var path in decision.PathsWith(VbaConflictChoice.OverwriteExcel)) overwrite.Add(path);
                if (adopt.Count > 0)
                {
                    // ブックのリボンでミラーを置き換える（ファイルを読むだけ。Excel は要らない）。
                    var pulled = await _vbaSync.PullAsync(project, adopt, adoptConflicts: adopt).ConfigureAwait(true);
                    if (!pulled.IsSuccess) await ShowVbaFailureAsync("ブックのリボンを取り込めませんでした", project, pulled.Issues).ConfigureAwait(true);
                }

                continue; // 選んだ結果で、準備からやり直す（上書きは反映の対象になる）。
            }

            switch (preparation.Kind)
            {
                case VbaRibbonRewritePreparationKind.Declined:
                    Notify?.Invoke("リボンの反映を中止しました（未保存の変更を保存しなかったため）。リボンは未反映のままです。");
                    return RibbonPushEnd.Cancelled;
                case VbaRibbonRewritePreparationKind.NothingToDo:
                    // 利用者が衝突ダイアログで「反映しない」を選んだものは、もう知っているので、注意に出さない。
                    await ShowWarningsAsync("リボンの反映の注意",
                        preparation.Notes.Where(n => !(n.Code == ErrorCode.E812 && n.Path is not null && decided.Contains(n.Path)))).ConfigureAwait(true);
                    if (!options.Automatic) Notify?.Invoke("ブックへ反映するリボンの変更はありません。");
                    return RibbonPushEnd.NothingToDo;
            }

            // VBA の反映に続けて出すときは、確認の冒頭に「VBA のモジュール N 件は Excel へ反映しました」を添える（16.9）。
            var plan = preparation.Plan! with { PrecedingVbaModules = options.PrecedingVbaModules };
            var viewModel = CreateRibbonRewriteViewModel?.Invoke(plan);
            var confirmed = viewModel is not null && ShowRibbonRewriteAsync is not null
                            && await ShowRibbonRewriteAsync(viewModel).ConfigureAwait(true);
            if (!confirmed)
            {
                // 「中止」は失敗ではない（未反映のまま残す。適用の取り消しは提案しない。16.10）。
                Notify?.Invoke("リボンの反映を中止しました。リボンは未反映のままです。");
                return RibbonPushEnd.Cancelled;
            }

            // 処理中の表示（段階の名前）。ダイアログ（確認・失敗の知らせ）を出しているあいだは出さない。
            ribbon.Progress = ShowRibbonProgress;
            SetRibbonRewriteBusy?.Invoke(true);
            GraftResult<VbaRibbonRewriteOutcome> result;
            try
            {
                result = await ribbon.RewriteAsync(project, plan).ConfigureAwait(true);
            }
            finally
            {
                SetRibbonRewriteBusy?.Invoke(false);
                ribbon.Progress = null;
            }

            await RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                var outcome = result.Value;
                var reopenFailure = result.Issues.Where(i => i.Code == ErrorCode.E822).ToList();
                // 適用のあとは、適用の通知（「rN として適用しました（…、リボンも反映済み）」）にまとめるので、ここでは出さない（二重に出すと、
                // あとから出した方が前の通知を押しのける）。それ以外は「控えを見る」（開き直したとき）か「Excel で開く」（開いていなかったブック）を添える。
                if (!options.QuietSuccess)
                {
                    // 開いていなかったブックは Graft が開かない。完了の通知に「Excel で開く」を添える（16.6）。開き直せなかったときは、
                    // 通知ではなく下のダイアログで案内する（見逃さないように）。
                    if (!outcome.WasOpen && reopenFailure.Count == 0) NotifyOpenInExcel(project, VbaRibbonRewriteText.SucceededNotice(outcome));
                    else NotifyRevealBackup(project, VbaRibbonRewriteText.SucceededNotice(outcome));
                }

                await ShowWarningsAsync("リボンの反映の注意", preparation.Notes.Concat(result.Issues.Where(i => i.Code != ErrorCode.E822))).ConfigureAwait(true);
                if (reopenFailure.Count > 0)
                {
                    // 書き換えは済んでいる。開き直せなかった理由と「Excel で開く」（16.11 の表「開き直し」）。
                    var text = string.Join(Environment.NewLine + Environment.NewLine, reopenFailure.Select(FormatIssue));
                    if (await _dialogs.ShowActionMessageAsync("ブックを開き直せませんでした", text, VbaRibbonRewriteText.OpenInExcelButton).ConfigureAwait(true))
                    {
                        OpenWorkbookInExcel(project.Vba!.WorkbookFullName);
                    }
                }

                // リボンを新しく足したとき（そのブックへの初めての反映）だけ、Excel が読み込めたかを確かめる方法を案内する（16.8）。
                // エラーコードの付く問題ではないので、知らせの窓で出す。
                if (outcome.AddedNew) await _dialogs.ShowMessageAsync("リボンを新しく足しました", VbaRibbonRewriteText.FirstAddHint).ConfigureAwait(true);
                return RibbonPushEnd.Rewritten;
            }

            // 失敗（「ファイルの変更は残す（リボンは未反映）」が既定。適用の取り消しは提案しない。16.10・16.16-12）。
            if (!await ShowRibbonFailureAsync("リボンをブックへ反映できませんでした", project, result.Issues).ConfigureAwait(true)) return RibbonPushEnd.Failed;
        }
    }

    /// <summary>VBA の反映がすべて済んでいないときの、リボンの扱いの知らせ（16.10）。</summary>
    internal const string RibbonHeldNotice =
        "リボンは未反映のままです（VBA の反映が終わってから、もう一度「Excel へ反映」を押してください）";

    /// <summary>
    /// <b>VBA の反映のあと</b>に、リボンを続けて反映する（適用・取り消し・復元・手動の「Excel へ反映」の共通の入口。16.10）。
    /// <list type="bullet">
    /// <item>リボンの同期をしていない・未反映が無い（ミラーと基準だけで見る。Excel には触れない）: 何もしない（<see cref="RibbonFollow.NotNeeded"/>）。
    /// リボンの無いプロジェクトでは、通知も確認も一切出ない。</item>
    /// <item>VBA の反映が一部でも失敗・未解決: 準備を始めず、「リボンは未反映のままです」と通知する（<see cref="RibbonFollow.Held"/>）。
    /// 閉じ開きの前に、リボンの呼び先の VBA が Excel に入っていなければならないため（16.6）。</item>
    /// <item>それ以外: 確認（毎回。既定は「中止」）→ 書き換え。「中止」は失敗ではない。</item>
    /// </list>
    /// </summary>
    /// <param name="vbaComplete">VBA の反映が（反映するものが無かった場合を含め）すべて済んだか。</param>
    /// <param name="vbaModules">いま Excel へ反映した VBA のモジュールの数（確認の冒頭に添える）。</param>
    /// <param name="scene">リボンの衝突を衝突ダイアログに出すときの場面。</param>
    /// <param name="quietSuccess">成功の通知を出さない（適用の通知にまとめる）。</param>
    internal async Task<RibbonFollow> FollowWithRibbonAsync(
        Project project, bool vbaComplete, int vbaModules, VbaConflictScene scene, bool quietSuccess)
    {
        if (_vbaSync is null || project.Vba is null || !_vbaSync.Ribbon.IsActive) return RibbonFollow.NotNeeded;

        // 未反映の有無は、ミラーと同期基準だけで見る（Excel にもブックのファイルにも触れない）。
        var status = await _vbaSync.GetLocalStatusAsync(project).ConfigureAwait(true);
        if (!status.RibbonUnpushed) return RibbonFollow.NotNeeded;

        if (!vbaComplete)
        {
            Notify?.Invoke(RibbonHeldNotice);
            return RibbonFollow.Held;
        }

        var end = await PushRibbonAsync(project, new RibbonPushOptions
        {
            PrecedingVbaModules = vbaModules,
            QuietSuccess = quietSuccess,
            Automatic = true,
            ConflictScene = scene,
        }).ConfigureAwait(true);
        return end switch
        {
            RibbonPushEnd.Rewritten => RibbonFollow.Rewritten,
            RibbonPushEnd.Cancelled => RibbonFollow.Cancelled,
            RibbonPushEnd.Failed => RibbonFollow.Failed,
            _ => RibbonFollow.NotNeeded,
        };
    }

    // ------------------------------------------------------------------
    // ブックの控えの一覧と、丸ごと戻す操作（16.11。段階 F5）
    // ------------------------------------------------------------------

    /// <summary>
    /// 「Excel 側の控えを見る」窓に、「ブックの控え」（ブック全体の写し）の一覧・取り出し・削除・「この控えでブックを置き換える…」を足す。
    /// リボンを同期しているプロジェクト、または控えが既にあるプロジェクトだけ（リボンを使わない VBA プロジェクトの窓は従来のまま）。
    /// </summary>
    private void AttachWorkbookBackups(VbaRescueViewModel viewModel, Project project)
    {
        if (_vbaSync is null || project.Vba is null) return;
        var ribbon = _vbaSync.Ribbon;
        if (!ribbon.IsActive && ribbon.ListWorkbookBackups(project).Count == 0) return;

        viewModel.ListWorkbookBackups = prune => ribbon.ListWorkbookBackups(project, prune);
        viewModel.DeleteWorkbookBackup = backup => ribbon.DeleteWorkbookBackup(project, backup);
        viewModel.ExportWorkbookBackup = (backup, destination) => ribbon.ExportWorkbookBackup(project, backup, destination);
        viewModel.PickExportFolderAsync = () => _dialogs.PickFolderAsync("控えの取り出し先のフォルダを選択");
        viewModel.WorkbookBackupDirectory = VbaWorkbookBackupDirectoryOf?.Invoke(project);
        viewModel.ReplaceWorkbookFromBackupAsync = (backup, choose) => ReplaceWorkbookFromBackupAsync(project, backup, choose);
    }

    /// <summary>
    /// 「この控えでブックを置き換える…」（16.11）: 準備 → <b>強い確認</b>（既定は「やめる」）→ 置き換え → 知らせ。リボンの書き換えと同じ手順
    /// （保存して閉じる → いまのブックを控える → 置き換え → 開き直す）で、置き換えたあとはリボンの同期基準を捨てる。
    /// </summary>
    /// <param name="choose">確認ダイアログを出す関数（控えの窓を親にして出す）。</param>
    /// <returns>控えの窓に出す、結果の一文。</returns>
    internal async Task<string> ReplaceWorkbookFromBackupAsync(
        Project project, VbaWorkbookBackupItem backup, Func<VbaChoiceRequest, Task<string>> choose)
    {
        if (_vbaSync is null || project.Vba is null) return string.Empty;
        var ribbon = _vbaSync.Ribbon;

        var prepared = await ribbon.PrepareRestoreFromBackupAsync(project, backup.FileName).ConfigureAwait(true);
        if (!prepared.IsSuccess)
        {
            await ShowRibbonFailureAsync("控えでブックを置き換えられません", project, prepared.Issues).ConfigureAwait(true);
            return "置き換えませんでした（ブックは変えていません）。";
        }

        var plan = prepared.Value;
        var choice = await choose(new VbaChoiceRequest(
            VbaWorkbookRestoreText.Title, VbaWorkbookRestoreText.Message(plan),
            new[]
            {
                new VbaChoiceOption("replace", VbaWorkbookRestoreText.ConfirmButton),
                new VbaChoiceOption("cancel", VbaWorkbookRestoreText.CancelButton, IsDefault: true),
            })).ConfigureAwait(true);
        if (choice != "replace") return "置き換えをやめました。";

        ribbon.Progress = ShowRibbonProgress;
        SetRibbonRewriteBusy?.Invoke(true);
        GraftResult<VbaRibbonRewriteOutcome> result;
        try
        {
            result = await ribbon.RestoreFromBackupAsync(project, plan).ConfigureAwait(true);
        }
        finally
        {
            SetRibbonRewriteBusy?.Invoke(false);
            ribbon.Progress = null;
        }

        await RefreshVbaBadgesAsync(checkWorkbookOpen: false).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            var outcome = result.Value;
            var text = VbaWorkbookRestoreText.SucceededNotice(plan.WorkbookName, plan.BackupFileName, System.IO.Path.GetFileName(outcome.BackupPath));
            var reopenFailure = result.Issues.Where(i => i.Code == ErrorCode.E822).ToList();
            if (!outcome.WasOpen && reopenFailure.Count == 0) NotifyOpenInExcel(project, text);
            else NotifyRevealBackup(project, text);
            await ShowWarningsAsync("控えでの置き換えの注意", result.Issues.Where(i => i.Code != ErrorCode.E822)).ConfigureAwait(true);
            if (reopenFailure.Count > 0)
            {
                var detail = string.Join(Environment.NewLine + Environment.NewLine, reopenFailure.Select(FormatIssue));
                if (await _dialogs.ShowActionMessageAsync("ブックを開き直せませんでした", detail, VbaRibbonRewriteText.OpenInExcelButton).ConfigureAwait(true))
                {
                    OpenWorkbookInExcel(project.Vba.WorkbookFullName);
                }
            }

            return text;
        }

        await ShowRibbonFailureAsync("控えでブックを置き換えられませんでした", project, result.Issues).ConfigureAwait(true);
        return "置き換えられませんでした（失敗の理由は、表示した知らせを見てください）。";
    }

    /// <summary>完了の通知に「控えを見る」を添える（書き換える前のブックの控えのフォルダを開く。16.10）。</summary>
    private void NotifyRevealBackup(Project project, string text)
    {
        if (NotifyWithAction is { } notify && VbaWorkbookBackupDirectoryOf is not null)
        {
            notify(text, "控えを見る", () =>
            {
                RevealBackupFolder(project);
                return Task.CompletedTask;
            });
            return;
        }

        Notify?.Invoke(text);
    }

    private const string RecoveryTitle = "前回のリボンの書き換えについて";

    /// <summary>
    /// 失敗の知らせ（16.10「書き換えが失敗したとき」）。E823 は「控えのフォルダを開く」、一時的な理由（使用中・Excel で開いている）は
    /// 「もう一度試す」を添える。既定（閉じる・Esc）は「ファイルの変更は残す（リボンは未反映）」＝何もしない。戻り値は「もう一度試す」を選んだか。
    /// 適用の取り消しは提案しない（16.16-12）。
    /// </summary>
    private async Task<bool> ShowRibbonFailureAsync(string title, Project project, IReadOnlyList<GraftIssue> issues)
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, issues.Select(FormatIssue));
        if (issues.Any(i => i.Code == ErrorCode.E823))
        {
            if (await _dialogs.ShowActionMessageAsync(title, text, "控えのフォルダを開く").ConfigureAwait(true) && _vbaSync is not null)
            {
                RevealBackupFolder(project);
            }

            return false;
        }

        if (issues.Any(IsRetryable))
        {
            return await _dialogs.ShowActionMessageAsync(title, text, "もう一度試す").ConfigureAwait(true);
        }

        // 閉じたあとに時間切れになり、Excel 側で閉じられていた（開き直す券が無い）・開き直せなかった: 「Excel で開く」。
        if (issues.Any(VbaRibbonRewriteText.OffersOpenInExcel))
        {
            if (await _dialogs.ShowActionMessageAsync(title, text, VbaRibbonRewriteText.OpenInExcelButton).ConfigureAwait(true) && project.Vba is { } link)
            {
                OpenWorkbookInExcel(link.WorkbookFullName);
            }

            return false;
        }

        await _dialogs.ShowMessageAsync(title, text).ConfigureAwait(true);
        return false;
    }

    internal static bool IsRetryable(GraftIssue issue) => VbaRibbonRewriteText.IsRetryable(issue);

    /// <summary>完了の通知に「Excel で開く」を添える（開いていなかったブックを、続けて Excel で見られるように。16.6）。</summary>
    private void NotifyOpenInExcel(Project project, string text)
    {
        if (NotifyWithAction is { } notify && project.Vba is { } link)
        {
            notify(text, VbaRibbonRewriteText.OpenInExcelButton, () =>
            {
                OpenWorkbookInExcel(link.WorkbookFullName);
                return Task.CompletedTask;
            });
            return;
        }

        Notify?.Invoke(text);
    }

    private void RevealBackupFolder(Project project)
    {
        if (VbaWorkbookBackupDirectoryOf?.Invoke(project) is not { } directory) return;
        try
        {
            Directory.CreateDirectory(directory);
            RevealFolder(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify?.Invoke($"控えのフォルダを開けませんでした: {ExceptionMessages.Describe(ex)}");
        }
    }

    /// <summary>ブックの丸ごとの控えのフォルダ（<c>MainViewModel</c> が <c>AppPaths.GetVbaWorkbookBackupDirectory</c> を渡す）。</summary>
    public Func<Project, string>? VbaWorkbookBackupDirectoryOf { get; set; }

    private async Task InspectRibbonSelectedAsync()
    {
        if (_vbaSync is null || SelectedItem is not { } item || item.Project.Vba is null) return;
        var results = await _vbaSync.Ribbon.InspectMirrorAsync(item.Project).ConfigureAwait(true);
        if (results.Count == 0)
        {
            Notify?.Invoke("検査するリボンのファイル（CustomUI/customUI14.xml・CustomUI/customUI.xml）がありません。");
            return;
        }

        await _dialogs.ShowMessageAsync("リボンの XML の検査", FormatInspection(results)).ConfigureAwait(true);
    }

    /// <summary>
    /// 「リボンの XML を検査」の結果の文（ファイルごとに件数の見出しと、行・列つきの一覧）。エラーは「［エラー］」、警告は「［警告］」を
    /// 頭に付ける（重さが一目で分かるように。エラーがあるうちはブックへ書けないことも添える）。
    /// </summary>
    internal static string FormatInspection(IReadOnlyList<VbaRibbonInspection> results)
    {
        var text = new StringBuilder();
        foreach (var result in results)
        {
            if (text.Length > 0) text.AppendLine().AppendLine();
            if (result.Validation is not { } validation)
            {
                text.Append(result.MirrorPath).Append(": 読めません（").Append((result.Problem ?? "理由は不明です").TrimEnd('。')).Append("）。");
                continue;
            }

            var errors = validation.Errors.Count();
            var warnings = validation.Warnings.Count();
            if (errors == 0 && warnings == 0)
            {
                text.Append(result.MirrorPath).Append(": 問題はありません。");
                continue;
            }

            text.Append(result.MirrorPath).Append(": エラー ").Append(errors).Append(" 件・警告 ").Append(warnings).Append(" 件");
            text.Append(errors > 0 ? "（エラーがあるうちは、ブックへ書きません）" : "（警告だけなら、ブックへ書けます）");
            foreach (var finding in validation.Findings)
            {
                text.AppendLine().Append(finding.Severity == Severity.Error ? "  ［エラー］" : "  ［警告］").Append(finding.ToDisplayText());
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// 途中で終わった書き換えの後始末（16.11「次の起動」）。バッジの調べ直しのついでに、起動してから初めて見るプロジェクトだけ確かめる。
    /// 知らせ（ダイアログ）は、呼び出し側がバッジの錠を放してから出す（ダイアログを出しているあいだ、バッジの調べ直しを止めないため）。
    /// </summary>
    private async Task<List<VbaRibbonRecovery>> RecoverInterruptedRibbonRewritesAsync(VbaSyncService service, IReadOnlyList<Project> targets)
    {
        var found = new List<VbaRibbonRecovery>();
        foreach (var project in targets)
        {
            if (!_ribbonRecoveryChecked.Add(project.Id) || !service.Ribbon.HasInterruptedRewrite(project)) continue;
            if (await service.Ribbon.RecoverInterruptedAsync(project).ConfigureAwait(true) is { } recovery) found.Add(recovery);
        }

        return found;
    }

    private async Task ShowRibbonRecoveriesAsync(IReadOnlyList<VbaRibbonRecovery> recoveries)
    {
        foreach (var recovery in recoveries)
        {
            await _dialogs.ShowMessageAsync(RecoveryTitle, recovery.Message).ConfigureAwait(true);
        }
    }
}

/// <summary>リボンの反映（<see cref="ProjectPaneViewModel.PushRibbonAsync(Project, RibbonPushOptions?)"/>）の結末。</summary>
internal enum RibbonPushEnd
{
    /// <summary>反映するリボンの変更が無かった。</summary>
    NothingToDo,

    /// <summary>利用者が確認・衝突の解決・未保存の保存を断った（未反映のまま。失敗ではない）。</summary>
    Cancelled,

    /// <summary>ブックのリボンを書き換えた。</summary>
    Rewritten,

    /// <summary>書き換えられなかった（失敗の知らせを出した。リボンは未反映のまま）。</summary>
    Failed,
}

/// <summary>リボンの反映の呼び方の指定（適用・取り消し・復元・手動の反映のあとの自動の確認が使う）。</summary>
internal sealed record RibbonPushOptions
{
    /// <summary>確認の冒頭に添える、直前に Excel へ反映した VBA のモジュールの数（0 なら添えない。16.9）。</summary>
    public int PrecedingVbaModules { get; init; }

    /// <summary>
    /// 成功の通知を出さない（適用のあとは、適用の通知「rN として適用しました（…、リボンも反映済み）」にまとめる。16.10）。
    /// </summary>
    public bool QuietSuccess { get; init; }

    /// <summary>自動の確認（適用・取り消し・復元・手動の反映のあと）。反映するものが無いときの「ありません」の通知を出さない。</summary>
    public bool Automatic { get; init; }

    /// <summary>「Graft 側で上書き」と選んだ衝突（ミラーのパス）。反映の対象に入れる（確認は省かない）。</summary>
    public IReadOnlyCollection<string>? OverwriteConflicts { get; init; }

    /// <summary>リボンの衝突を衝突ダイアログに出すときの場面（選べる項目が変わる）。</summary>
    public VbaConflictScene ConflictScene { get; init; } = VbaConflictScene.ManualPush;
}

/// <summary>VBA の反映のあとにリボンを続けて反映した結果（<see cref="ProjectPaneViewModel.FollowWithRibbonAsync"/>）。</summary>
internal enum RibbonFollow
{
    /// <summary>リボンの反映は要らなかった（同期していない・未反映が無い・反映するものが無い）。何も出していない。</summary>
    NotNeeded,

    /// <summary>VBA の反映が済んでいないため、リボンの準備を始めなかった（通知した）。</summary>
    Held,

    /// <summary>リボンを書き換えた。</summary>
    Rewritten,

    /// <summary>利用者が確認などを断った（未反映のまま）。</summary>
    Cancelled,

    /// <summary>書き換えられなかった（失敗の知らせを出した。未反映のまま）。</summary>
    Failed,
}
