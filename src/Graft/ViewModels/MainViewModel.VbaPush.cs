using System.IO;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル（1ファイル400行の目安のため <c>MainViewModel.VbaApply.cs</c> から分けた）。
/// Excel VBA 連携のうち、Excel へ書く側: 適用後の自動 Push と失敗時の選択・取り消し（仕様書_VBA連携 7.3-4）、
/// 取り消し・復元の後の Push（7.4）。ダイアログのデリゲート・旗・補助は <c>MainViewModel.VbaApply.cs</c> にある。
/// </summary>
public sealed partial class MainViewModel
{
    // ------------------------------------------------------------------
    // 適用後の自動 Push（7.3-4）
    // ------------------------------------------------------------------

    private enum VbaPushEnd
    {
        /// <summary>反映するものがすべて反映できた（反映するものが無かった場合を含む）。</summary>
        Completed,

        /// <summary>反映できないものがあり、ファイルの変更は残した（未反映の印）。</summary>
        KeptUnpushed,

        /// <summary>この適用を取り消した。</summary>
        Undone,
    }

    private sealed record VbaPushLoopResult(
        VbaPushEnd End, IReadOnlyList<string> Written, IReadOnlyList<string> Created, IReadOnlyList<string> DeletePending);

    /// <summary>
    /// 適用の成功後に、Excel へ反映する。ここまでの適用（ファイルの書き込み・履歴・一覧の読み込み）は済んでいる。
    /// 戻り値の <c>Undone</c> は、Push の失敗でこの適用を取り消したか（適用後フックと Git の自動コミットを行わない目印）。
    /// <c>Detail</c> は適用通知に添える一言。
    /// </summary>
    private async Task<(bool Undone, string? Detail)> PushVbaAfterApplyAsync(Project project, RevisionManifest manifest)
    {
        if (_vbaFileOnly) return (false, "Excel には未反映");

        var manifestPaths = new HashSet<string>(
            VbaScopeOf(manifest.Entries.Select(e => e.Path)), StringComparer.OrdinalIgnoreCase);

        // 処理中の表示。DiscardCurrentPatch／KeepOnlyFailedBlocks が済んだ後の状態を控え、終了時に戻す。
        var stateBefore = State;
        State = CenterPaneState.Loading;
        SetVbaPushing(true);
        try
        {
            var loop = await RunVbaPushLoopAsync(project, scope: null, VbaConflictScene.AfterApply, manifest.Revision, manifestPaths)
                .ConfigureAwait(true);
            if (loop.End == VbaPushEnd.Undone)
            {
                await NotifyVbaRemainingInExcelAsync(loop.DeletePending.Where(manifestPaths.Contains).ToList()).ConfigureAwait(true);
            }

            switch (loop.End)
            {
                case VbaPushEnd.Undone:
                    return (true, null);
                case VbaPushEnd.KeptUnpushed:
                    return (false, "Excel には未反映");
                default:
                    var written = loop.Written.Count + loop.Created.Count;
                    if (written == 0) return (false, null);
                    var earlier = loop.Written.Concat(loop.Created).Count(p => !manifestPaths.Contains(p));
                    return (false, earlier > 0
                        ? $"Excel へ反映済み: {written}件（うち以前からの未反映 {earlier}件）"
                        : "Excel へ反映済み");
            }
        }
        finally
        {
            SetVbaPushing(false);
            if (State == CenterPaneState.Loading) State = stateBefore;
        }
    }

    /// <summary>
    /// Push の本体。衝突の確認 → 反映 → 失敗時の選択、を終わるまで繰り返す。
    /// </summary>
    /// <param name="scope">対象を絞るパス。null なら Push 判定の全モジュール（適用で変わったものと以前からの未反映の両方）。</param>
    /// <param name="scene">衝突ダイアログを出す場面（適用の後か、復元の後か）。</param>
    /// <param name="undoRevision">取り消しの選択肢を出す適用のリビジョン。復元の後は null（出さない）。</param>
    /// <param name="manifestPaths">この適用で変わったモジュール（メッセージ用）。</param>
    private async Task<VbaPushLoopResult> RunVbaPushLoopAsync(
        Project project, IReadOnlyCollection<string>? scope, VbaConflictScene scene, int? undoRevision, HashSet<string>? manifestPaths)
    {
        var overwrite = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var written = new List<string>();
        var created = new List<string>();
        var deletePending = new List<string>();

        while (true)
        {
            var round = await VbaSync.PushAsync(project, scope, overwrite).ConfigureAwait(true);
            var errors = round.Issues.Where(i => i.Severity == Severity.Error && i.Code != ErrorCode.E812).ToList();
            var failedPaths = new List<string>();
            var wholeFailure = !round.IsSuccess;

            if (round.IsSuccess)
            {
                written.AddRange(round.Value.Pushed);
                created.AddRange(round.Value.Created);
                failedPaths.AddRange(round.Value.Failed);
                foreach (var path in round.Value.DeletePending)
                {
                    if (!deletePending.Contains(path, StringComparer.OrdinalIgnoreCase)) deletePending.Add(path);
                }

                foreach (var warning in round.Issues.Where(i => i.Severity == Severity.Warning))
                {
                    Logger?.Warn("vba-push", warning.ToDisplayText(), targetPath: warning.Path);
                }

                var open = round.Value.ConflictItems.Where(c => !skipped.Contains(c.RelativePath)).ToList();
                if (open.Count > 0)
                {
                    var grew = false;

                    // 解析後の再検査で承認した「上書き」は、Excel 側がそのときのままなら、聞き直さずに上書きする。
                    var approved = open.Where(c => _vbaApprovedOverwrites.TryGetValue(c.RelativePath, out var hash) && hash == c.ExcelHash).ToList();
                    foreach (var item in approved) grew |= overwrite.Add(item.RelativePath);

                    var undecided = open.Except(approved).ToList();
                    if (undecided.Count > 0)
                    {
                        var decision = await AskVbaConflictAsync(new VbaConflictRequest(scene, undecided)).ConfigureAwait(true);
                        if (decision.UndoApply && undoRevision is int revision)
                        {
                            return await UndoApplyAfterVbaPushFailureAsync(project, revision, written, created).ConfigureAwait(true);
                        }

                        foreach (var item in undecided)
                        {
                            var choice = decision.Choices.GetValueOrDefault(item.RelativePath);
                            if (choice == VbaConflictChoice.OverwriteExcel) grew |= overwrite.Add(item.RelativePath);
                            else skipped.Add(item.RelativePath); // 「反映しない」。未反映の印を付けて残す。
                        }
                    }

                    if (grew) continue; // 上書きすると決めたものを書くために、もう一度反映する。
                }
            }

            var hasFailure = wholeFailure || failedPaths.Count > 0 || skipped.Count > 0;
            if (!hasFailure)
            {
                await WarnUncreatableAsync(round.Issues, manifestPaths).ConfigureAwait(true);
                return new VbaPushLoopResult(VbaPushEnd.Completed, written, created, deletePending);
            }

            var choiceId = await ChooseAfterVbaPushFailureAsync(project, undoRevision, errors, failedPaths, skipped).ConfigureAwait(true);
            switch (choiceId)
            {
                case "retry":
                    skipped.Clear(); // もう一度、衝突も含めて確認する。
                    continue;
                case "undo" when undoRevision is int revision:
                    return await UndoApplyAfterVbaPushFailureAsync(project, revision, written, created).ConfigureAwait(true);
                default:
                    Logger?.Warn("vba-push", "Excel へ反映できないものがあるため、ファイルの変更を残しました（未反映の印を付けます）。", targetPath: project.Root);
                    return new VbaPushLoopResult(VbaPushEnd.KeptUnpushed, written, created, deletePending);
            }
        }
    }

    /// <summary>Push が一部でも失敗したときの選択（仕様 7.3-4-6）。既定は「ファイルの変更は残す」。</summary>
    private async Task<string> ChooseAfterVbaPushFailureAsync(
        Project project, int? undoRevision, IReadOnlyList<GraftIssue> errors, IReadOnlyList<string> failedPaths, IReadOnlyCollection<string> skipped)
    {
        var lines = new List<string>
        {
            undoRevision is int r
                ? $"r{r} の変更は Graft のファイルに適用済みですが、Excel へ反映できなかったものがあります。"
                : "ファイルは戻りましたが、Excel へ反映できなかったものがあります。",
        };
        if (failedPaths.Count > 0) lines.Add($"反映できなかったモジュール: {string.Join("、", failedPaths)}");
        if (skipped.Count > 0) lines.Add($"衝突のため反映しなかったモジュール: {string.Join("、", skipped)}");
        if (errors.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(string.Join(Environment.NewLine + Environment.NewLine, errors.Select(FormatVbaIssue)));
        }

        lines.Add(string.Empty);
        lines.Add("「ファイルの変更は残す」を選ぶと、未反映の印を付けて残します。あとからプロジェクトの右クリック「Excel へ反映」で送れます。");

        // 文字コードや作成の可否のように、やり直しても同じ結果になるものだけのときは「もう一度反映する」を出さない。
        var retryable = errors.Count == 0 || errors.Any(i => i.Code is not (ErrorCode.E811 or ErrorCode.E808));
        var options = new List<VbaChoiceOption> { new("keep", "ファイルの変更は残す（未反映の印）", IsDefault: true) };
        if (retryable) options.Add(new VbaChoiceOption("retry", "もう一度反映する"));
        if (undoRevision is int revision) options.Add(new VbaChoiceOption("undo", $"この適用（r{revision}）を取り消す"));

        return await ChooseVbaOptionAsync(new VbaChoiceRequest("Excel へ反映できませんでした", string.Join(Environment.NewLine, lines), options, OpenWorkbookPathFor(project, errors)))
            .ConfigureAwait(true);
    }

    /// <summary>
    /// 新規作成できないファイル（Documents/・Forms/ に作ったもの。E808）があれば、この適用で作られたものに限って知らせる。
    /// 以前からあるものは毎回の適用で繰り返さない（ログには残している）。
    /// </summary>
    private async Task WarnUncreatableAsync(IReadOnlyList<GraftIssue> issues, HashSet<string>? manifestPaths)
    {
        if (manifestPaths is null) return;
        var uncreatable = issues
            .Where(i => i.Code == ErrorCode.E808 && i.Path is not null && manifestPaths.Contains(i.Path))
            .Select(FormatVbaIssue)
            .ToList();
        if (uncreatable.Count > 0)
        {
            await _dialogs.ShowMessageAsync("Excel へ反映できないファイルがあります", string.Join(Environment.NewLine + Environment.NewLine, uncreatable))
                .ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Push の失敗でこの適用を取り消す（仕様 7.3-4-6）。<c>MainViewModel.Hooks.cs</c> の巻き戻しと同じ作りで、
    /// <b>rN を名指しで</b> <see cref="RevisionRestorer.RestoreAsync"/>（force）し、履歴を読み込み直す。
    /// <see cref="HistoryPaneViewModel.UndoLatestAsync"/> は使わない: この位置では履歴の一覧が最新でなく別のリビジョンを
    /// 取り消したり、絞り込みで先頭が変わったり、確認ダイアログが二重になったり、<c>RevisionRestored</c> で
    /// 復元後の Push が二重に走ったりするため。
    /// </summary>
    private async Task<VbaPushLoopResult> UndoApplyAfterVbaPushFailureAsync(
        Project project, int revision, IReadOnlyList<string> written, IReadOnlyList<string> created)
    {
        var none = new List<string>();

        var summary = await _revisionStore.ReadAsync(project.Id, revision).ConfigureAwait(true);
        if (!summary.IsSuccess)
        {
            await _dialogs.ShowMessageAsync("元の状態に戻せませんでした",
                $"履歴r{revision}が見つからないため、手動で履歴から復元してください。").ConfigureAwait(true);
            return new VbaPushLoopResult(VbaPushEnd.KeptUnpushed, written, created, none);
        }

        var restored = await _revisionRestorer.RestoreAsync(project.Id, project.Root, summary.Value, force: true).ConfigureAwait(true);
        if (!restored.IsSuccess)
        {
            await _dialogs.ShowMessageAsync("元の状態に戻せませんでした",
                string.Join(Environment.NewLine, restored.Errors.Select(i => i.ToDisplayText()))).ConfigureAwait(true);
            return new VbaPushLoopResult(VbaPushEnd.KeptUnpushed, written, created, none);
        }

        await History.LoadAsync(project.Id, project.Root).ConfigureAwait(true);
        Logger?.Warn("vba-push", $"Excel へ反映できなかったため、r{revision} を取り消しました。", revision: revision);

        // すでに Excel へ反映できていたモジュールを、取り消した後の内容（元の内容）で Push し直す。
        // 新規に作ったモジュールはミラーから消えるので、Excel 側に残り、「削除保留」になる（6.5）。
        var problems = new List<string>();
        var deletePending = new List<string>();
        var targets = written.Concat(created).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count > 0)
        {
            var again = await VbaSync.PushAsync(project, targets).ConfigureAwait(true);
            if (!again.IsSuccess || again.Value.Failed.Count > 0 || again.Value.Conflicts.Count > 0)
            {
                // さらに取り消すことはしない。警告を出し、未反映の印（ミラーと Excel の食い違い）のまま残す。
                problems.Add("Excel を取り消し後の内容へ戻せなかったモジュールがあります。Excel とファイルの内容が食い違ったまま残っているため、" +
                             "プロジェクトの右クリック「Excel へ反映」で送り直してください。");
                problems.AddRange(again.Issues.Where(i => i.Severity == Severity.Error).Select(FormatVbaIssue));
            }
            else
            {
                deletePending.AddRange(again.Value.DeletePending);
            }
        }

        if (problems.Count > 0)
        {
            await _dialogs.ShowMessageAsync($"r{revision} を取り消しました（Excel への反映に注意）", string.Join(Environment.NewLine + Environment.NewLine, problems))
                .ConfigureAwait(true);
        }
        else
        {
            Notice.Show($"r{revision} を取り消しました（Excel へ反映できなかったため）");
        }

        return new VbaPushLoopResult(VbaPushEnd.Undone, written, created, deletePending);
    }

    /// <summary>
    /// 新規モジュールを含む適用（または復元）を取り消すと、ミラーからは消えるが Excel 側には残る。
    /// 取り消しの完了時にその旨を知らせる（仕様 7.3-4-6・6.5）。
    /// </summary>
    private Task NotifyVbaRemainingInExcelAsync(IReadOnlyList<string> modules)
        => modules.Count == 0 ? Task.CompletedTask : _dialogs.ShowMessageAsync(
            "Excel 側にモジュールが残っています",
            $"次のモジュールは Excel 側に残っています: {string.Join("、", modules)}。" + Environment.NewLine +
            "ミラーからは削除され、「削除保留」になります。Excel 側に戻したいときは、プロジェクトの右クリック「Excel から復元」を使ってください。");

    // ------------------------------------------------------------------
    // 取り消し・復元の後の Push（7.4）
    // ------------------------------------------------------------------

    /// <summary>
    /// 単発の取り消し・復元、「ここまで戻す」、適用後フック失敗の巻き戻しの後に呼ぶ。戻したファイルのうち
    /// ミラーのモジュールを Excel へ反映する。衝突の選択肢は「Graft 側で上書き」「反映しない」の2つ
    /// （「Excel 側を採用」は、復元の意図を打ち消すので出さない）。
    /// </summary>
    private async Task PushVbaAfterRestoreAsync(IReadOnlyList<string> restoredPaths)
    {
        var project = ProjectPane.SelectedItem?.Project;
        if (!IsVbaActive(project) || !project!.Vba!.AutoPushAfterApply) return;

        var scope = VbaScopeOf(restoredPaths);
        if (scope.Count == 0) return;

        SetVbaPushing(true);
        try
        {
            var loop = await RunVbaPushLoopAsync(project, scope, VbaConflictScene.AfterRestore, undoRevision: null, manifestPaths: null)
                .ConfigureAwait(true);

            // 復元でその版には無かったモジュールのファイルが消えた場合は、Excel 側に残る（削除保留）。
            var remaining = scope.Where(p => !File.Exists(Path.Combine(project.Root, p.Replace('/', Path.DirectorySeparatorChar))))
                .Where(p => loop.DeletePending.Contains(p, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (remaining.Count > 0) await NotifyVbaRemainingInExcelAsync(remaining).ConfigureAwait(true);
        }
        finally
        {
            SetVbaPushing(false);
        }
    }
}
