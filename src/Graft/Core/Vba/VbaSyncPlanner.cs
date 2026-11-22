namespace Graft.Core.Vba;

/// <summary>
/// ミラーと Excel の三方比較による同期計画（仕様書_VBA連携 6.5）。入力も出力も純粋なレコードで、
/// COM にもファイルにも触れない（Linux の CI で全パターンをテストできるようにするため）。
/// <para>
/// モジュールごとに4つのハッシュを比べる。<c>M</c>=ミラーの現在の本文、<c>E</c>=Excel の現在の本文、
/// <c>Bm</c>=<see cref="VbaModuleState.MirrorHash"/>、<c>Be</c>=<see cref="VbaModuleState.ExcelHash"/>。
/// <c>mirrorChanged = (M ≠ Bm)</c>、<c>excelChanged = (E ≠ Be)</c>。ただし <c>excelChanged</c> が真でも、
/// ゆるいハッシュ（文字列リテラルとコメントの外だけで空白・大文字小文字を無視。行頭の字下げは残す）が
/// 基準の <see cref="VbaModuleState.ExcelLooseHash"/> と一致するなら、VBE の整形が変えただけとみなして
/// <c>excelChanged = false</c> とする。
/// VBE は識別子の大文字小文字をプロジェクト全体で揃える見込みなので、別のモジュールへの Push で
/// 無関係なモジュールの本文が大文字小文字だけ変わりうる。これを「Excel 側の変更」として取り込むと、
/// ミラーが履歴に無い変更を受けて取り消しが E301 になり、ミラーも変わっていれば偽の衝突になる。
/// </para>
/// </summary>
public static class VbaSyncPlanner
{
    /// <summary>
    /// 同期計画を作る。
    /// </summary>
    /// <param name="mirror">ミラーの現在のモジュール（<see cref="VbaMirrorLayout.TryParse"/> を通ったもの）。</param>
    /// <param name="excel">Excel の現在のモジュール。</param>
    /// <param name="state">同期基準。まだ無ければ null。</param>
    /// <param name="now">新しい基準に記録する日時（純ロジックに保つため呼び出し側から渡す）。</param>
    public static VbaSyncPlan Plan(
        IEnumerable<VbaMirrorModule> mirror,
        IEnumerable<VbaExcelModule> excel,
        VbaSyncState? state,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(excel);

        var mirrorByPath = new Dictionary<string, VbaMirrorModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mirror) mirrorByPath[m.RelativePath] = m;

        var excelList = excel.ToList();
        var plans = new List<VbaModulePlan>();
        var excelByPath = new Dictionary<string, VbaExcelModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in excelList)
        {
            if (!VbaNameRules.CanMirror(e.Component))
            {
                // ミラーのファイルを作れない名前（Windows の予約名など）。Pull でもスキップして警告する。
                var attempted = VbaMirrorLayout.ToRelativePath(e.Kind, e.Component);
                plans.Add(new VbaModulePlan(attempted, e.Kind, e.Component, VbaSyncAction.Unsupported, Issue: GraftIssue.Of(
                    ErrorCode.E810,
                    detail: VbaNameRules.IsWindowsReservedName(e.Component)
                        ? $"Excel のコンポーネント「{e.Component}」は Windows の予約名のため、ミラーに写せません。"
                        : $"Excel のコンポーネント「{e.Component}」は、ミラーのファイル名にできない文字を含んでいます。",
                    path: attempted,
                    severity: Severity.Warning)));
                continue;
            }

            excelByPath[VbaMirrorLayout.ToRelativePath(e.Kind, e.Component)] = e;
        }

        var records = new Dictionary<string, VbaModuleState>(StringComparer.OrdinalIgnoreCase);
        if (state is not null)
        {
            foreach (var (key, value) in state.Modules) records[key] = value;
        }

        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(mirrorByPath.Keys);
        keys.UnionWith(excelByPath.Keys);
        keys.UnionWith(records.Keys);

        foreach (var key in keys)
        {
            mirrorByPath.TryGetValue(key, out var m);
            excelByPath.TryGetValue(key, out var e);
            records.TryGetValue(key, out var r);
            plans.Add(PlanOne(key, m, e, r, now));
        }

        return new VbaSyncPlan(StopNameCollisions(plans, excelList));
    }

    /// <summary>
    /// 新規作成（<see cref="VbaSyncAction.PushCreate"/>）になったもののうち、<b>同じ名前で種別違い</b>のものを E808 で止める。
    /// <para>
    /// 【なぜ要るか】 計画のキーは「フォルダ/名前.拡張子」なので、Excel に Document の <c>ThisWorkbook</c>
    /// （<c>Documents/ThisWorkbook.cls</c>）があっても、AI が <c>Classes/ThisWorkbook.cls</c> を新しく作ると、記録も
    /// Excel 側も無い別のキーとして <c>PushCreate</c> になる。ところが VBA のコンポーネント名は種別をまたいで一意で、
    /// 書き込み側（ブリッジ）は名前だけで既存のモジュールを引くため、ドキュメントモジュールの本文が確認も控えも
    /// なしに上書きされていた（不具合 1）。ミラーに <c>Modules/Foo.bas</c> と <c>Classes/Foo.cls</c> が両方
    /// 新規で現れたときも、Excel には同じ名前を 1 つしか作れず、書き込みの結果の対応がずれる。
    /// どちらも「書かずに止めて、書くべきファイルを案内する」側へ倒す。
    /// </para>
    /// </summary>
    private static List<VbaModulePlan> StopNameCollisions(List<VbaModulePlan> plans, IReadOnlyList<VbaExcelModule> excel)
    {
        if (!plans.Any(p => p.Action == VbaSyncAction.PushCreate)) return plans;

        var excelKinds = new Dictionary<string, VbaModuleKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in excel) excelKinds[e.Component] = e.Kind;

        var creatingCounts = plans
            .Where(p => p.Action == VbaSyncAction.PushCreate)
            .GroupBy(p => p.Component, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var result = new List<VbaModulePlan>(plans.Count);
        foreach (var plan in plans)
        {
            if (plan.Action != VbaSyncAction.PushCreate)
            {
                result.Add(plan);
                continue;
            }

            string? detail = null;
            if (excelKinds.TryGetValue(plan.Component, out var existingKind) && existingKind != plan.Kind)
            {
                var correct = VbaMirrorLayout.ToRelativePath(existingKind, plan.Component);
                detail = $"{plan.RelativePath}: Excel 側には同じ名前「{plan.Component}」の{VbaModuleKinds.Label(existingKind)}があります。" +
                         $"別の種別としては作成できません（既存の本文を上書きしてしまうため、書きませんでした）。{correct} に書いてください。";
            }
            else if (creatingCounts.TryGetValue(plan.Component, out var count) && count > 1)
            {
                detail = $"{plan.RelativePath}: 同じ名前「{plan.Component}」のファイルが複数の種別のフォルダに新しく作られています。" +
                         "Excel 側には同じ名前のモジュールを 1 つしか作れないため、どれも書きませんでした。名前を変えるか、片方を削除してください。";
            }

            result.Add(detail is null
                ? plan
                : plan with
                {
                    Action = VbaSyncAction.NotCreatable,
                    Notice = VbaSyncNotice.None,
                    Issue = GraftIssue.Of(ErrorCode.E808, detail: detail, path: plan.RelativePath, severity: Severity.Warning),
                });
        }

        return result;
    }

    private static VbaModulePlan PlanOne(
        string key, VbaMirrorModule? m, VbaExcelModule? e, VbaModuleState? r, DateTimeOffset now)
    {
        // 種別と名前は、どれか存在するものから取る（キーに種別とコンポーネント名が含まれるので、
        // 存在する側の値は必ず一致する）。
        var kind = m?.Kind ?? e?.Kind ?? r!.Kind;
        var component = m?.Component ?? e?.Component ?? r!.Component;

        VbaModulePlan Make(VbaSyncAction action, VbaModuleState? baseline = null,
            VbaSyncNotice notice = VbaSyncNotice.None, GraftIssue? issue = null,
            VbaConflictReason reason = VbaConflictReason.BothChanged)
            => new(key, kind, component, action, baseline, notice, issue, reason);

        // 衝突の理由。Excel 側が空でミラー側が空でないなら、それを先に伝える（取り込むとミラーが 0 行になるため）。
        VbaConflictReason ReasonOf() => e is { IsEmpty: true } && m is { IsEmpty: false }
            ? VbaConflictReason.ExcelEmpty
            : VbaConflictReason.BothChanged;

        VbaModulePlan NotCreatable() => Make(VbaSyncAction.NotCreatable, issue: GraftIssue.Of(
            ErrorCode.E808,
            detail: $"{VbaModuleKinds.FolderName(kind)}/ のモジュール「{component}」は Excel 側に存在しないため、作成できません。",
            path: key,
            severity: Severity.Warning));

        // --- 基準の記録がない -------------------------------------------------
        if (r is null)
        {
            if (m is not null && e is not null)
            {
                // 本文が同じなら基準を作るだけ。違えば初回の食い違いとして衝突にする。
                return SameBody(m, e)
                    ? Make(VbaSyncAction.Rebaseline, NewBaseline(m, e, e.Caption, now))
                    : Make(VbaSyncAction.Conflict, reason: ReasonOf());
            }

            if (m is not null)
            {
                return VbaModuleKinds.CanCreateOnExcel(kind) ? Make(VbaSyncAction.PushCreate) : NotCreatable();
            }

            return Make(VbaSyncAction.Pull);
        }

        // --- 記録がある -------------------------------------------------------
        if (m is null && e is null) return Make(VbaSyncAction.DiscardRecord);

        if (m is null)
        {
            // ミラーのファイルが無いが Excel にはある＝削除保留。自動 Pull では復活させない。
            // 典型は、新規モジュールを含む適用を取り消したとき（ミラーからは消えるが Excel には残る）。
            // 既に保留の印が付いていて変わりがなければ、基準の更新は不要（null）。
            var pending = r with { DeletePending = true, Caption = e!.Caption ?? r.Caption };
            return Make(VbaSyncAction.DeletePending, pending == r ? null : pending, VbaSyncNotice.MirrorFileDeleted);
        }

        var mirrorChanged = !string.Equals(m.Hash, r.MirrorHash, StringComparison.Ordinal);

        // 【要確認のモジュールは、ハッシュを見ずに衝突にする】 書き戻しに失敗したモジュールは、Excel 側の本文が実態と
        // 違うかもしれず、基準のハッシュで「どちらが変わったか」を決められない。自動で取り込むとミラーを壊れた本文で
        // 上書きし、自動で反映すると Excel 側の壊れた状態を確かめないまま上書きする。どちらもせず、利用者に選んでもらう。
        // ただし両側の本文が実質同じなら守るものが無いので、基準を作り直して要確認を外す。Excel 側にモジュールが
        // 無いときは、書いて困る本文が Excel に無いので通常どおり（下の e is null の枝）。
        if (r.NeedsReview && e is not null)
        {
            return SameBody(m, e)
                ? Make(VbaSyncAction.Rebaseline, NewBaseline(m, e, e.Caption ?? r.Caption, now))
                : Make(VbaSyncAction.Conflict, reason: VbaConflictReason.NeedsReview);
        }

        if (e is null)
        {
            // Excel 側でモジュールが無くなっている。ミラーが変わっていなければ何もしない（警告だけ）。
            if (!mirrorChanged) return Make(VbaSyncAction.None, ClearDeletePending(r), VbaSyncNotice.ExcelModuleMissing);
            return VbaModuleKinds.CanCreateOnExcel(kind)
                ? Make(VbaSyncAction.PushCreate, notice: VbaSyncNotice.ExcelModuleMissing)
                : NotCreatable();
        }

        var excelChanged = !string.Equals(e.Hash, r.ExcelHash, StringComparison.Ordinal);
        var excelFormattingOnly = false;

        // 【ゆるいハッシュの算法を変えたときの移行】 版が古い記録（既存の同期状態はすべてこれ）の excelLooseHash は、
        // 文字列・コメントの中の差も消す旧算法の値で、いまの算法の e.LooseHash とは比べられない。比べると、旧算法で
        // 消えていた差が一斉に不一致になる（逆に旧算法で一致させると、不具合 2 の「文字列だけの編集を見逃す」が
        // 移行の 1 回だけ残る）。そこで版が古い記録は、最初の 1 回だけ厳密なハッシュで判定する。厳密に一致していれば
        // 「何もしない」の枝で基準をいまの算法に更新し、以後はゆるい比較が使える。厳密に違えば Pull（または衝突）に
        // なるが、その場合も取り込んだ内容は無損失で、最悪でも VBE の整形差を 1 回取り込むだけで済む。
        var looseUsable = r.LooseFormVersion == VbaTextNormalizer.LooseFormVersion && r.ExcelLooseHash.Length > 0;
        if (excelChanged && looseUsable
            && string.Equals(e.LooseHash, r.ExcelLooseHash, StringComparison.Ordinal))
        {
            excelChanged = false;
            excelFormattingOnly = true;
        }

        if (!mirrorChanged && !excelChanged)
        {
            // 何もしない。ただし整形差だけ変わっていたら excelHash を更新し、削除保留が解除された
            // （ミラーのファイルが再び現れた）なら印を外す。
            var refreshed = r with
            {
                DeletePending = false,
                Caption = e.Caption ?? r.Caption,
                ExcelHash = excelFormattingOnly ? e.Hash : r.ExcelHash,
            };
            if (r.LooseFormVersion != VbaTextNormalizer.LooseFormVersion)
            {
                // Excel 側は基準と厳密に同じなので、いまの算法のゆるいハッシュに差し替えてよい（上の移行の説明を参照）。
                refreshed = refreshed with { ExcelLooseHash = e.LooseHash, LooseFormVersion = VbaTextNormalizer.LooseFormVersion };
            }

            return Make(VbaSyncAction.None, refreshed == r ? null : refreshed);
        }

        if (mirrorChanged && !excelChanged) return Make(VbaSyncAction.Push);
        if (!mirrorChanged)
        {
            // 【空の Excel は黙って取り込まず、衝突にする】 取り込むとミラーが 0 行になる。実機で、書き込みに失敗した
            // あとの自動取込がミラーを 0 行にした。Excel 側が本当に空だったのか、利用者が VBE で消したのかは分からない
            // ので、どちらを残すかは利用者に選んでもらう（手動の取込でも同じ衝突画面で確認になる）。
            // ミラーも空なら（ハッシュが同じはずで Pull にならないが）失うものが無いので、通常どおり。
            return e.IsEmpty && !m.IsEmpty
                ? Make(VbaSyncAction.Conflict, reason: VbaConflictReason.ExcelEmpty)
                : Make(VbaSyncAction.Pull);
        }

        // 両方変わっている。実質同じ本文なら基準だけ更新し、違えば衝突。
        return SameBody(m, e)
            ? Make(VbaSyncAction.Rebaseline, NewBaseline(m, e, e.Caption ?? r.Caption, now))
            : Make(VbaSyncAction.Conflict, reason: ReasonOf());
    }

    private static bool SameBody(VbaMirrorModule m, VbaExcelModule e)
        => string.Equals(m.Hash, e.Hash, StringComparison.Ordinal)
           || string.Equals(m.LooseHash, e.LooseHash, StringComparison.Ordinal);

    private static VbaModuleState NewBaseline(VbaMirrorModule m, VbaExcelModule e, string? caption, DateTimeOffset now)
        => VbaModuleState.Create(m.Kind, m.Component, caption, m.Hash, e.Hash, e.LooseHash, now);

    private static VbaModuleState? ClearDeletePending(VbaModuleState r)
        => r.DeletePending ? r with { DeletePending = false } : null;
}
