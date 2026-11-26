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
/// <item>適用後の自動 Push と、失敗したときの選択・取り消し（7.3-4）、取り消し・復元の後の Push（7.4）は
/// <c>MainViewModel.VbaPush.cs</c></item>
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

    /// <summary>
    /// 「ファイルだけ適用する」と、「Graft 側で上書き」の承認を下ろす。どちらも<b>そのパッチの解析と適用だけ</b>の選択で、
    /// 新しいパッチ・キューの結合・解析結果の破棄・プロジェクトの切り替えをまたいで持ち越すと、Excel が開いているのに
    /// 反映されなかったり（適用直前の再検査も飛ばされる）、別のプロジェクトへ効いたりする（不具合 8）。
    /// </summary>
    private void ResetVbaApplyChoices()
    {
        _vbaFileOnly = false;
        _vbaApprovedOverwrites.Clear();
    }

    /// <summary>Push の最中か（貼り付け・取り消し・プロジェクトの切り替えの入口が見る）。</summary>
    private bool IsVbaPushing => _isVbaPushing;

    private void SetVbaPushing(bool value)
    {
        _isVbaPushing = value;
        CommandRequery.Invalidate(); // 貼り付け・取り消しなどのボタンの有効・無効を更新する。
        if (!value) FlushPendingSettingsIfIdle(); // 反映の間、保留していた設定を反映する（不具合 9）。
    }

    // ------------------------------------------------------------------
    // 小さな補助
    // ------------------------------------------------------------------

    /// <summary>理由に、対処を添えた表示文。</summary>
    private static string FormatVbaIssue(GraftIssue issue) => $"{issue.ToDisplayText()}{Environment.NewLine}対処: {issue.Remedy}";

    /// <summary>
    /// パッチの対象のうち、ミラーのモジュールとして同期の対象になるものの正規の相対パス。<b>リボン（<c>CustomUI/</c>）は含めない</b>。
    /// <para>
    /// 【取込の範囲（<see cref="VbaPullScopeOf"/>）と分ける理由】 この範囲は「Excel へ反映（Push）」に使う。リボンのパスを混ぜると、
    /// リボンだけの取り消し・復元でも範囲が空にならず、Excel の全モジュールを読みに行って、閉じたブックでは E804 で失敗する
    /// （VBA を 1 行も変えていないのに失敗のダイアログが出て、ブックが閉じていても動くはずのリボンの確認も出なくなった。不具合 2）。
    /// リボンは Excel の COM を通らず、ブックのファイルとして別に扱う（16.4・16.10）ので、VBA の反映の範囲には入れない。
    /// </para>
    /// </summary>
    private static List<string> VbaScopeOf(IEnumerable<string> paths) => ScopeOf(paths, includeRibbon: false);

    /// <summary>
    /// パッチの対象のうち、<b>取込（Pull・Check）</b>の範囲になるもの。モジュールに加えて、リボン（<c>CustomUI/customUI14.xml</c> など。
    /// 仕様書_ブックの情報と依頼の観点 16.4）を含む。パッチが <c>CustomUI/</c> を含むときも、解析の前にブックのファイルから取り込む
    /// （取込の 3 つ目の契機）。リボンの同期をしていないときは、同期の側が範囲から外す。
    /// </summary>
    private static List<string> VbaPullScopeOf(IEnumerable<string> paths) => ScopeOf(paths, includeRibbon: true);

    private static List<string> ScopeOf(IEnumerable<string> paths, bool includeRibbon)
    {
        var scope = new List<string>();
        foreach (var path in paths)
        {
            var normalized = path.Replace('\\', '/');
            string? relative = null;
            if (VbaMirrorLayout.TryParse(normalized, out var parsed, out _) && parsed is not null)
            {
                relative = parsed.RelativePath;
            }
            else if (includeRibbon && VbaRibbonMirrorLayout.TryParse(normalized, out var ribbon) && ribbon is not null)
            {
                relative = ribbon.RelativePath;
            }

            if (relative is not null && !scope.Contains(relative, StringComparer.OrdinalIgnoreCase)) scope.Add(relative);
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

    /// <summary>E804（ブックが開いていない）が含まれるとき、「Excel で開く」ボタンに渡すブックのフルパス。無ければ null。</summary>
    private static string? OpenWorkbookPathFor(Project project, IEnumerable<GraftIssue> issues)
        => project.Vba is { WorkbookFullName.Length: > 0 } link && issues.Any(i => i.Code == ErrorCode.E804) ? link.WorkbookFullName : null;

    /// <summary>
    /// Excel に接続できないなどで同期そのものが失敗したとき、「ファイルだけ適用する」か「中止」かを選ばせる
    /// （仕様 7.3-1）。「ファイルだけ」を選ぶと、この適用では Push を試みず、変更は「未反映」として残る。
    /// </summary>
    /// <returns>続行してよいなら true。</returns>
    private async Task<bool> ConfirmVbaFileOnlyAsync(Project project, IReadOnlyList<GraftIssue> issues)
    {
        // VBA プロジェクトがパスワードでロックされている（E803）ときは、専用の窓（既定は「中止してパッチを破棄する」）。
        if (issues.Any(i => i.Code == ErrorCode.E803)) return await ConfirmVbaLockedBeforeApplyAsync(project, issues).ConfigureAwait(true);

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
            },
            // ブックが開いていない（E804）ときは、ここから Excel で開けるようにする（仕様 9章）。
            OpenWorkbookPathFor(project, issues))).ConfigureAwait(true);
        if (choice != "fileOnly") return false;

        _vbaFileOnly = true;
        Logger?.Warn("vba", "Excel と同期できないため、ファイルだけ適用します（Excel には未反映のまま残ります）。", targetPath: project.Root);
        return true;
    }

    // 適用の前の同期（解析前の取込・適用直前の再検査）で、ロックの窓の「解除したのでもう一度試す」が選ばれた。
    // 呼び出し側（PullVbaBeforeDryRunAsync・RecheckVbaBeforeApplyAsync）は、false が返ったときにこの旗を見て、取込（または再検査）の
    // 先頭からやり直す。戻り値の bool（続けてよいか）に「やり直す」を足すと、既存の呼び出し箇所すべての書き換えになるため、旗にした。
    private bool _vbaPreflightRetry;

    /// <summary>
    /// 適用の前（解析前の取込・適用直前の再検査）の同期が、VBA プロジェクトのロック（E803）で失敗したときの窓。
    /// 「中止してパッチを破棄する」（既定。Enter・Esc・× も）／「VBE を開いてロックを解除する」（窓は閉じない）／
    /// 「解除したのでもう一度試す」（取込から続ける）／「ファイルだけ適用する」。
    /// 破棄は、既存の現在のパッチを破棄する処理（<see cref="DiscardCurrentPatch"/>）を使う（接ぎ木パネルが空になり、ファイルは変わらない）。
    /// </summary>
    /// <returns>続行してよいなら true（「ファイルだけ適用する」）。「もう一度試す」は false で、<see cref="_vbaPreflightRetry"/> を立てる。</returns>
    private async Task<bool> ConfirmVbaLockedBeforeApplyAsync(Project project, IReadOnlyList<GraftIssue> issues)
    {
        var choice = await ProjectPane.ChooseWhenVbaLockedAsync(
            project, VbaLockedScene.BeforeApply, "Excel と同期できませんでした。", undoRevision: null, issues).ConfigureAwait(true);
        switch (choice)
        {
            case VbaLockedText.RetryId:
                _vbaPreflightRetry = true;
                return false;
            case VbaLockedText.FileOnlyId:
                _vbaFileOnly = true;
                Logger?.Warn("vba", "VBA プロジェクトがロックされているため、ファイルだけ適用します（Excel には未反映のまま残ります）。", targetPath: project.Root);
                return true;
            default:
                DiscardCurrentPatch();
                Logger?.Warn("vba", "VBA プロジェクトがロックされているため、パッチを破棄しました。", targetPath: project.Root);
                Notice.Show("パッチを破棄しました（VBA プロジェクトがロックされているため）");
                return false;
        }
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
        // ロックの窓で「解除したのでもう一度試す」が選ばれたら、取込の先頭からやり直す（破棄・中止は false のまま戻る）。
        while (true)
        {
            _vbaPreflightRetry = false;
            var proceed = await PullVbaBeforeDryRunOnceAsync(project, patch).ConfigureAwait(true);
            if (!proceed && _vbaPreflightRetry) continue;
            return proceed;
        }
    }

    private async Task<bool> PullVbaBeforeDryRunOnceAsync(Project project, Patch patch)
    {
        if (!IsVbaActive(project)) return true;

        var scope = VbaPullScopeOf(patch.Blocks.SelectMany(b => b is RenameBlock rename ? new[] { b.Path, rename.ToPath } : new[] { b.Path }));
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
        foreach (var skipped in pulled.Value.Skipped)
        {
            Logger?.Warn("vba", $"未保存の編集があるため、Excel からの取り込みをスキップしました: {skipped}", targetPath: Path.Combine(project.Root, skipped));
        }

        // モジュールとリボンの衝突を、1 つの衝突ダイアログに並べる（リボンは、左がブックのファイルの部品。16.4）。
        var allConflicts = pulled.Value.AllConflictItems;
        if (allConflicts.Count > 0)
        {
            var decision = await AskVbaConflictAsync(new VbaConflictRequest(VbaConflictScene.BeforeAnalysis, allConflicts))
                .ConfigureAwait(true);
            if (decision.Aborted)
            {
                // 衝突の「中止」では解析しない。衝突が解決されずに処理が止まるので、E812 を中央ペインに残す（仕様 7.5）。
                CenterError = GraftIssue.Of(
                    ErrorCode.E812,
                    $"次のモジュール（リボンを含む）が衝突しているため、解析を中止しました: {string.Join("、", allConflicts.Select(c => c.RelativePath))}。" +
                    "Excel 側とファイル側のどちらを残すか決めてから、もう一度解析してください。");
                State = CenterPaneState.Error;
                return false;
            }

            var adopt = decision.PathsWith(VbaConflictChoice.AdoptExcel);
            if (adopt.Count > 0)
            {
                var adopted = await VbaSync.PullAsync(project, adopt, adoptConflicts: adopt).ConfigureAwait(true);
                if (!adopted.IsSuccess) return await ConfirmVbaFileOnlyAsync(project, adopted.Issues).ConfigureAwait(true);
                importedCount += adopted.Value.Pulled.Count;
            }

            var overwriteAll = decision.PathsWith(VbaConflictChoice.OverwriteExcel);
            var overwrite = overwriteAll.Where(p => !VbaRibbonMirrorLayout.TryParse(p, out _)).ToList();

            // リボンの「Graft 側で上書き」は、ブックを閉じて開き直す書き換え（確認ダイアログは省かない）。「中止」・失敗なら、解析を中止する
            // （上書きを選んだのに反映されていないまま、解析へ進まない）。
            var ribbonOverwrite = overwriteAll.Where(p => VbaRibbonMirrorLayout.TryParse(p, out _)).ToList();
            if (ribbonOverwrite.Count > 0
                && !await OverwriteRibbonConflictsAsync(project, ribbonOverwrite, VbaConflictScene.BeforeAnalysis).ConfigureAwait(true))
            {
                CenterError = GraftIssue.Of(
                    ErrorCode.E812,
                    $"「Graft 側で上書き」を選んだリボンをブックへ反映しなかったため、解析を中止しました: {string.Join("、", ribbonOverwrite)}。" +
                    "もう一度解析するか、ブックのリボンとファイル側のどちらを残すか決めてください。");
                State = CenterPaneState.Error;
                return false;
            }

            if (overwrite.Count > 0)
            {
                var pushed = await VbaSync.PushAsync(project, overwrite, overwrite).ConfigureAwait(true);
                if (!pushed.IsSuccess || pushed.Value.Failed.Count > 0)
                {
                    var failures = pushed.Issues.Where(i => i.Severity == Severity.Error).ToList();
                    await _dialogs.ShowMessageAsync(
                            "Excel を上書きできませんでした", string.Join(Environment.NewLine + Environment.NewLine, failures.Select(FormatVbaIssue)))
                        .ConfigureAwait(true);

                    // 「中止」のときと同じく、解析しなかった理由を中央ペインに残す（ダイアログを閉じたあとに何も残らないと、
                    // なぜ解析が始まらなかったのかが分からない。不具合 15）。
                    CenterError = failures.FirstOrDefault() ?? GraftIssue.Of(
                        ErrorCode.E812,
                        $"「Graft 側で上書き」を選んだモジュールを Excel へ反映できなかったため、解析を中止しました: {string.Join("、", overwrite)}。" +
                        "もう一度解析するか、Excel 側とファイル側のどちらを残すか決めてください。");
                    State = CenterPaneState.Error;
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

        var findings = VbaApplyGuard.Check(plans, checkRibbon: _settings.Vba.RibbonEditEnabled).ToList();

        // 新しく作るファイルが、既存のコンポーネントと同名で種別違いにならないか（不具合 1）。Excel を読む検査なので、
        // 新規ファイルがあるときだけ行う。
        if (plans.Any(VbaApplyGuard.CreatesMirrorFile))
        {
            var known = await VbaSync.ListKnownComponentsAsync(project!).ConfigureAwait(true);
            findings.AddRange(VbaApplyGuard.CheckNameCollisions(plans, known));
        }

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
        // ロックの窓で「解除したのでもう一度試す」が選ばれたら、再検査の先頭からやり直す。
        while (true)
        {
            _vbaPreflightRetry = false;
            var result = await RecheckVbaBeforeApplyOnceAsync(dryRun, context, plans).ConfigureAwait(true);
            if (result is null && _vbaPreflightRetry) continue;
            return result;
        }
    }

    private async Task<(DryRunResult DryRun, ApplyContext Context)?> RecheckVbaBeforeApplyOnceAsync(
        DryRunResult dryRun, ApplyContext context, IReadOnlyList<BlockPlan> plans)
    {
        var project = ProjectPane.SelectedItem?.Project;
        if (!IsVbaActive(project) || _vbaFileOnly) return (dryRun, context);

        var scope = VbaPullScopeOf(plans.Where(p => p.IsSelected && p.CanApply).SelectMany(p => new[] { p.Block.Path, p.Path }));
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
            if (decision.Aborted)
            {
                Notice.Show("適用を中止しました（Excel 側とファイル側の衝突が解決されていません）");
                return null;
            }

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
}
