using System.IO;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル。Excel VBA 連携のうち、適用の流れに差し込む部分
/// （仕様書_VBA連携 7.3・7.4。段階 P3）。
/// <list type="bullet">
/// <item>解析の前の自動 Pull（7.3-1）</item>
/// <item>適用の直前の再検査（7.3-3）と、画面側のガード（E814・E811。7.3-2）</item>
/// <item>適用後の自動 Push と、失敗したときの選択・取り消し（7.3-4）</item>
/// <item>取り消し・復元の後の Push（7.4）</item>
/// </list>
/// <para>
/// 【VBA プロジェクトでないときは何もしない】 入口はすべて <see cref="IsVbaActive"/> で守る。通常のファイル
/// プロジェクトの適用の流れは、ここへ来ても一切変わらない（性能の約束とも両立する）。
/// </para>
/// <para>
/// 【ダイアログは Window の型を知らない側から出す】 衝突ダイアログと選択ダイアログは Window なので、ViewModel は
/// デリゲート（<see cref="ShowVbaConflictAsync"/>・<see cref="ChooseVbaAsync"/>）で ShellWindow に任せる
/// （<c>ApplyPreviewRequested</c> と同じ作法）。未配線の環境（テスト、画面なし）では、何もしない・反映しないという
/// 安全側の既定値で進む。
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 衝突ダイアログ（<c>VbaConflictWindow</c>）を出して、利用者の選択を返す（仕様 7.5）。ShellWindow が配線する。
    /// 未設定のときは <see cref="VbaConflictRequest.DefaultDecision"/>（中止、または「反映しない」）で進む。
    /// </summary>
    public Func<VbaConflictRequest, Task<VbaConflictDecision?>>? ShowVbaConflictAsync { get; set; }

    /// <summary>
    /// 選択肢つきの確認ダイアログ（<c>VbaChoiceWindow</c>）を出して、選ばれたボタンの
    /// <see cref="VbaChoiceOption.Id"/> を返す。ShellWindow が配線する。未設定、または選ばずに閉じたときは
    /// 既定のボタン（<see cref="VbaChoiceRequest.Default"/>）として扱う。
    /// </summary>
    public Func<VbaChoiceRequest, Task<string?>>? ChooseVbaAsync { get; set; }

    /// <summary>衝突ダイアログの ViewModel を作る（現在の設定と UI 機能を渡す。ShellWindow が窓を開くときに使う）。</summary>
    public VbaConflictViewModel CreateVbaConflictViewModel(VbaConflictRequest request) => new(request, _settings, _ui);

    // Push の最中（COM の応答待ち）に、貼り付け・取り消し・プロジェクトの切り替えを受け付けないための旗
    // （仕様 7.3-4-2）。適用中の旗（_isApplyInProgress）は適用の全体を覆っているが、取り消しの後の Push は
    // 適用ではないので、別の旗で足りない入口を塞ぐ。必ず finally で下ろす（COM が固まっても時間切れで戻る）。
    private bool _isVbaPushing;

    // 解析の前または適用の直前に「ファイルだけ適用する」を選んだ。この適用では Excel へ Push しようとしない
    // （変更は「未反映」として残る）。新しいパッチを貼ったときと、適用の最後に下ろす。
    private bool _vbaFileOnly;

    // 解析後の再検査で「Graft 側で上書き」と承認したモジュールと、承認した時点の Excel 側の本文のハッシュ。
    // Push の直前に Excel 側がさらに変わっていれば承認は無効（同じ本文のままのときだけ、もう一度聞かない）。
    private readonly Dictionary<string, string> _vbaApprovedOverwrites = new(StringComparer.OrdinalIgnoreCase);

    // 適用の最中の再解析（ReanalyzeKeepingSelectionAsync）では、「適用できない変更がある」案内を重ねて出さない
    // （適用を始める前の検査が、再解析の後にもう一度同じ案内を出すため）。
    private bool _suppressVbaGuardNotice;

    /// <summary>
    /// 設定で有効で、この環境で使えて、VBA 連携のプロジェクトか。false なら普通のファイルプロジェクトとして
    /// 扱う（仕様 7.6）。
    /// </summary>
    private bool IsVbaActive(Project? project)
        => project?.Vba is not null && _settings.Vba.Enabled && _vbaBridge.IsSupported;

    /// <summary>Push の最中か（貼り付け・取り消し・プロジェクトの切り替えの入口が見る）。</summary>
    private bool IsVbaPushing => _isVbaPushing;

    private void SetVbaPushing(bool value)
    {
        _isVbaPushing = value;
        CommandRequery.Invalidate(); // 貼り付け・取り消しなどのボタンの有効・無効を更新する。
    }

    // ------------------------------------------------------------------
    // 小さな補助
    // ------------------------------------------------------------------

    /// <summary>理由に、対処を添えた表示文。</summary>
    private static string FormatVbaIssue(GraftIssue issue) => $"{issue.ToDisplayText()}{Environment.NewLine}対処: {issue.Remedy}";

    /// <summary>パッチの対象のうち、ミラーのモジュールとして同期の対象になるものの正規の相対パス。</summary>
    private static List<string> VbaScopeOf(IEnumerable<string> paths)
    {
        var scope = new List<string>();
        foreach (var path in paths)
        {
            if (VbaMirrorLayout.TryParse(path.Replace('\\', '/'), out var parsed, out _) && parsed is not null
                && !scope.Contains(parsed.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                scope.Add(parsed.RelativePath);
            }
        }

        return scope;
    }

    private async Task<VbaConflictDecision> AskVbaConflictAsync(VbaConflictRequest request)
    {
        if (ShowVbaConflictAsync is null) return request.DefaultDecision;
        return await ShowVbaConflictAsync(request).ConfigureAwait(true) ?? request.DefaultDecision;
    }

    private async Task<string> ChooseVbaOptionAsync(VbaChoiceRequest request)
    {
        if (ChooseVbaAsync is null) return request.Default.Id;
        var id = await ChooseVbaAsync(request).ConfigureAwait(true);
        return request.Options.Any(o => o.Id == id) ? id! : request.Default.Id;
    }

    /// <summary>
    /// Excel に接続できないなどで同期そのものが失敗したとき、「ファイルだけ適用する」か「中止」かを選ばせる
    /// （仕様 7.3-1）。「ファイルだけ」を選ぶと、この適用では Push を試みず、変更は「未反映」として残る。
    /// </summary>
    /// <returns>続行してよいなら true。</returns>
    private async Task<bool> ConfirmVbaFileOnlyAsync(Project project, IReadOnlyList<GraftIssue> issues)
    {
        var text = string.Join(Environment.NewLine + Environment.NewLine, issues.Where(i => i.Severity == Severity.Error).Select(FormatVbaIssue));
        var choice = await ChooseVbaOptionAsync(new VbaChoiceRequest(
            "Excel と同期できませんでした",
            $"{text}{Environment.NewLine}{Environment.NewLine}" +
            "「ファイルだけ適用する」を選ぶと、Graft のファイルにだけ適用します。Excel には未反映のまま残り、" +
            "あとからプロジェクトの右クリック「Excel へ反映」で送れます。",
            new[]
            {
                new VbaChoiceOption("fileOnly", "ファイルだけ適用する"),
                new VbaChoiceOption("abort", "中止", IsDefault: true),
            })).ConfigureAwait(true);
        if (choice != "fileOnly") return false;

        _vbaFileOnly = true;
        Logger?.Warn("vba", "Excel と同期できないため、ファイルだけ適用します（Excel には未反映のまま残ります）。", targetPath: project.Root);
        return true;
    }

    // ------------------------------------------------------------------
    // 解析の前の自動 Pull（7.3-1）
    // ------------------------------------------------------------------

    /// <summary>
    /// 解析（ドライラン）の前に、パッチの対象モジュールに限って Excel 側の変更を取り込む。
    /// <c>Vba.PullBeforeDryRun</c> が false のときは、同じ内容を確認ダイアログで行う。
    /// </summary>
    /// <returns>解析を続けてよいか（衝突で「中止」・接続できず「中止」・確認で「中止」なら false）。</returns>
    private async Task<bool> PullVbaBeforeDryRunAsync(Project project, Patch patch)
    {
        if (!IsVbaActive(project)) return true;

        var scope = VbaScopeOf(patch.Blocks.SelectMany(b => b is RenameBlock rename ? new[] { b.Path, rename.ToPath } : new[] { b.Path }));
        if (scope.Count == 0) return true;

        if (!_settings.Vba.PullBeforeDryRun)
        {
            // 自動で取り込まない設定。先に検査だけして、取り込むものか衝突があるときだけ確認する。
            var check = await VbaSync.CheckAsync(project, scope).ConfigureAwait(true);
            if (!check.IsSuccess) return await ConfirmVbaFileOnlyAsync(project, check.Issues).ConfigureAwait(true);
            if (check.Value.IsClean) return true;

            var names = string.Join("、", check.Value.Pull.Concat(check.Value.Conflicts.Select(c => c.RelativePath)));
            var choice = await ChooseVbaOptionAsync(new VbaChoiceRequest(
                "Excel 側の変更があります",
                $"解析する前に、Excel 側で変更されたモジュールを取り込みますか？{Environment.NewLine}{Environment.NewLine}{names}",
                new[]
                {
                    new VbaChoiceOption("pull", "取り込む", IsDefault: true),
                    new VbaChoiceOption("skip", "取り込まずに続ける"),
                    new VbaChoiceOption("abort", "中止"),
                })).ConfigureAwait(true);
            if (choice == "abort") return false;
            if (choice == "skip") return true;
        }

        var pulled = await VbaSync.PullAsync(project, scope).ConfigureAwait(true);
        if (!pulled.IsSuccess) return await ConfirmVbaFileOnlyAsync(project, pulled.Issues).ConfigureAwait(true);

        var importedCount = pulled.Value.Pulled.Count;
        if (pulled.Value.Conflicts.Count > 0)
        {
            var decision = await AskVbaConflictAsync(new VbaConflictRequest(VbaConflictScene.BeforeAnalysis, pulled.Value.ConflictItems))
                .ConfigureAwait(true);
            if (decision.Aborted) return false; // 衝突の「中止」では解析しない。

            var adopt = decision.PathsWith(VbaConflictChoice.AdoptExcel);
            if (adopt.Count > 0)
            {
                var adopted = await VbaSync.PullAsync(project, adopt, adoptConflicts: adopt).ConfigureAwait(true);
                if (!adopted.IsSuccess) return await ConfirmVbaFileOnlyAsync(project, adopted.Issues).ConfigureAwait(true);
                importedCount += adopted.Value.Pulled.Count;
            }

            var overwrite = decision.PathsWith(VbaConflictChoice.OverwriteExcel);
            if (overwrite.Count > 0)
            {
                var pushed = await VbaSync.PushAsync(project, overwrite, overwrite).ConfigureAwait(true);
                if (!pushed.IsSuccess || pushed.Value.Failed.Count > 0)
                {
                    var issues = pushed.Issues.Where(i => i.Severity == Severity.Error).Select(FormatVbaIssue);
                    await _dialogs.ShowMessageAsync("Excel を上書きできませんでした", string.Join(Environment.NewLine + Environment.NewLine, issues))
                        .ConfigureAwait(true);
                    return false;
                }
            }
        }

        if (importedCount > 0)
        {
            Notice.Show("Excel 側の変更を取り込みました");
            Logger?.Info("vba", $"解析の前に Excel 側の変更を取り込みました（{importedCount}件）", targetPath: project.Root);
        }

        return true;
    }

    // ------------------------------------------------------------------
    // 画面側のガード（7.3-2）
    // ------------------------------------------------------------------

    /// <summary>
    /// 解析結果のうちチェックが付いているブロックに、VBA プロジェクトで適用できないもの（DELETE／RENAME の E814、
    /// CP932 で表せない文字の E811）があれば案内を出す。<paramref name="plans"/> は現在のチェックの状態を反映したもの。
    /// </summary>
    /// <returns>適用を始めてはいけないなら true。</returns>
    private async Task<bool> VbaGuardBlocksApplyAsync(Project? project, IReadOnlyList<BlockPlan> plans)
    {
        if (!IsVbaActive(project)) return false;

        var findings = VbaApplyGuard.Check(plans);
        if (findings.Count == 0) return false;

        var text = string.Join(Environment.NewLine + Environment.NewLine, findings.Select(f => FormatVbaIssue(f.Issue)));
        await _dialogs.ShowMessageAsync(
            "VBA プロジェクトでは適用できない変更があります",
            $"{text}{Environment.NewLine}{Environment.NewLine}該当するブロックのチェックを外すと、ほかの変更は適用できます。")
            .ConfigureAwait(true);
        return true;
    }

    /// <summary>解析が出た直後に、同じ検査を案内として出す（適用を押す前に気付けるように。仕様 7.3-2）。</summary>
    private Task NotifyVbaGuardAfterAnalysisAsync(Project project)
    {
        if (_suppressVbaGuardNotice) return Task.CompletedTask;
        var plans = Blocks.Select(b => b.Plan with { IsSelected = b.IsSelected }).ToList();
        return VbaGuardBlocksApplyAsync(project, plans);
    }

    // ------------------------------------------------------------------
    // 適用の直前の再検査（7.3-3）
    // ------------------------------------------------------------------

    /// <summary>
    /// 解析から「適用」までは、利用者が差分を見る時間である。その間に Excel 側が変わっていないかを、書き込みの前に
    /// もう一度 Planner で確かめる。取り込むなら <see cref="ReanalyzeKeepingSelectionAsync"/>（チェック状態を
    /// 引き継ぐ再解析）を使う。
    /// </summary>
    /// <returns>null なら適用を進めない。それ以外は、この後の処理に使う最新の解析結果と文脈。</returns>
    private async Task<(DryRunResult DryRun, ApplyContext Context)?> RecheckVbaBeforeApplyAsync(
        DryRunResult dryRun, ApplyContext context, IReadOnlyList<BlockPlan> plans)
    {
        var project = ProjectPane.SelectedItem?.Project;
        if (!IsVbaActive(project) || _vbaFileOnly) return (dryRun, context);

        var scope = VbaScopeOf(plans.Where(p => p.IsSelected && p.CanApply).SelectMany(p => new[] { p.Block.Path, p.Path }));
        if (scope.Count == 0) return (dryRun, context);

        var check = await VbaSync.CheckAsync(project!, scope).ConfigureAwait(true);
        if (!check.IsSuccess)
        {
            return await ConfirmVbaFileOnlyAsync(project!, check.Issues).ConfigureAwait(true) ? (dryRun, context) : null;
        }

        if (check.Value.IsClean) return (dryRun, context);

        var adopt = new List<string>();
        var pending = check.Value.Conflicts
            .Where(c => !(_vbaApprovedOverwrites.TryGetValue(c.RelativePath, out var hash) && hash == c.ExcelHash))
            .ToList();
        if (pending.Count > 0)
        {
            var decision = await AskVbaConflictAsync(new VbaConflictRequest(VbaConflictScene.Reanalysis, pending)).ConfigureAwait(true);
            if (decision.Aborted) return null;

            adopt.AddRange(decision.PathsWith(VbaConflictChoice.AdoptExcel));
            foreach (var path in decision.PathsWith(VbaConflictChoice.OverwriteExcel))
            {
                // 「Graft 側で上書き」は、適用して Push する時点で行う（いま Excel へ書いても、パッチが当たる前の内容を送るだけ）。
                // 承認した時点の Excel 側の本文を覚えておき、Push の直前までに変わっていなければ聞き直さない。
                _vbaApprovedOverwrites[path] = pending.First(c => string.Equals(c.RelativePath, path, StringComparison.OrdinalIgnoreCase)).ExcelHash;
            }
        }

        var toPull = new List<string>(adopt);
        if (check.Value.Pull.Count > 0)
        {
            var choice = await ChooseVbaOptionAsync(new VbaChoiceRequest(
                "Excel 側が変更されています",
                "解析したあとに、Excel 側で次のモジュールが変更されました。" + Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, check.Value.Pull) + Environment.NewLine + Environment.NewLine +
                "「このまま続ける」を選ぶと、Excel へ反映する直前に衝突として確認します。",
                new[]
                {
                    new VbaChoiceOption("reanalyze", "取り込んで解析をやり直す", IsDefault: true),
                    new VbaChoiceOption("continue", "このまま続ける"),
                    new VbaChoiceOption("abort", "中止"),
                })).ConfigureAwait(true);
            if (choice == "abort") return null;
            if (choice == "reanalyze") toPull.AddRange(check.Value.Pull);
        }

        if (toPull.Count == 0) return (dryRun, context);

        var pulled = await VbaSync.PullAsync(project!, toPull, adoptConflicts: adopt).ConfigureAwait(true);
        if (!pulled.IsSuccess)
        {
            return await ConfirmVbaFileOnlyAsync(project!, pulled.Issues).ConfigureAwait(true) ? (dryRun, context) : null;
        }

        Notice.Show("Excel 側の変更を取り込みました");
        Logger?.Info("vba", $"適用の前に Excel 側の変更を取り込み、解析をやり直します（{pulled.Value.Pulled.Count}件）", targetPath: project!.Root);

        // 取り込んだ内容に対する解析へ差し替える。チェックの状態は引き継ぐ（保存後の再解析と同じ仕組み）。
        return await ReanalyzeKeepingSelectionAsync(dryRun).ConfigureAwait(true);
    }

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

            var choiceId = await ChooseAfterVbaPushFailureAsync(undoRevision, errors, failedPaths, skipped).ConfigureAwait(true);
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
        int? undoRevision, IReadOnlyList<GraftIssue> errors, IReadOnlyList<string> failedPaths, IReadOnlyCollection<string> skipped)
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

        return await ChooseVbaOptionAsync(new VbaChoiceRequest("Excel へ反映できませんでした", string.Join(Environment.NewLine, lines), options))
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
