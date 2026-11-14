namespace Graft.Core;

/// <summary>
/// <see cref="ApplyEngine"/> の分割ファイル。ドライランの結果と、本適用の時点の実際のファイルが
/// 食い違っていないかの照合を担う。
///
/// 【修正前の問題】 本適用はファイルを読み直して再解決するが、その結果（<c>resolution.Units</c>）の
/// 成否を一切見ず、失敗したユニットがあっても「書き込むものが無かった」だけで成功として記録していた。
/// 次の3つが実際に再現した。
/// <list type="number">
/// <item><description>ドライランの後に対象の行を外部で書き換えて適用すると、成功になるがファイルは変わらない
/// （空の変更が「成功」のリビジョンとして残る）。</description></item>
/// <item><description>部分適用で RENAME のチェックを外し、移動先への SEARCH/REPLACE だけを適用すると、
/// 移動先のファイルが無いので空の <c>b.txt</c> が新規作成されて成功になる。</description></item>
/// <item><description>FULL と SEARCH/REPLACE が混在するファイルで FULL のチェックを外すと、SEARCH/REPLACE は
/// FULL適用後の内容に対して解決されていたため一致せず、何も変わらないまま成功になる。</description></item>
/// </list>
/// いずれも「プレビューで確認した内容」と「実際に起きたこと」が食い違うのに、利用者へ何も知らせない。
/// 食い違いを見つけたら、書き込みを始める前または途中で中止してロールバックし、原因が分かるエラーを返す。
/// </summary>
public sealed partial class ApplyEngine
{
    /// <summary>
    /// チェックの組み合わせが成り立つかを、ファイルを読まずにプランだけで調べる。成り立たない組み合わせの
    /// 説明（E301。Error）を返す。空なら問題なし。
    ///
    /// 【なぜ成り立たなくなるか】 ドライランは「パッチ全体を適用した状態」を前提に各ブロックを計算する。
    /// ・RENAME の移動先へのブロックは、移動元の内容を読んで計算している。RENAME のチェックを外すと、
    ///   適用時には移動先が存在しない。
    /// ・同じファイルの FULL と SEARCH/REPLACE（や APPEND/PREPEND）は、FULL適用後の内容に対して計算している。
    ///   FULL のチェックを外すと、元のファイルに対して解決し直すことになり、同じ結果にならない。
    /// 本適用は書き込む前に <see cref="ApplyAsync"/> でこの判定を行う。画面側（MainViewModel）も、要約入力や
    /// 確認ダイアログの前に同じ判定で案内を出せるよう公開している。
    /// </summary>
    public static IReadOnlyList<GraftIssue> CheckSelection(IReadOnlyList<BlockPlan> plans)
    {
        var issues = new List<GraftIssue>();
        var active = plans.Where(p => p.IsSelected && p.CanApply).ToList();
        var activeRenameTargets = new HashSet<string>(
            active.Where(p => p.Block is RenameBlock).Select(p => NormalizeKey(p.Path)), StringComparer.OrdinalIgnoreCase);

        foreach (var rename in plans.Where(p => p.Block is RenameBlock && p.CanApply && !p.IsSelected))
        {
            var dependents = active.Where(p =>
                p.Operation is EntryOperation.Modify or EntryOperation.Create
                && string.Equals(NormalizeKey(p.Path), NormalizeKey(rename.Path), StringComparison.OrdinalIgnoreCase)).ToList();
            if (dependents.Count == 0 || activeRenameTargets.Contains(NormalizeKey(rename.Path))) continue;

            var from = ((RenameBlock)rename.Block).FromPath;
            issues.Add(GraftIssue.Of(ErrorCode.E301,
                $"RENAME（{from} -> {rename.Path}）のチェックが外れていますが、移動先 {rename.Path} への変更にはチェックが付いています。" +
                "その変更は改名後のファイルを前提に計算されているため、改名しないままでは適用できません。" +
                "RENAME にもチェックを付けるか、移動先への変更のチェックも外してください。", path: rename.Path));
        }

        foreach (var group in plans.Where(p => p.Operation is EntryOperation.Modify or EntryOperation.Create)
                     .GroupBy(p => NormalizeKey(p.Path), StringComparer.OrdinalIgnoreCase))
        {
            var fullPlans = group.Where(p => p.Block is FullContentBlock && p.CanApply).ToList();
            if (fullPlans.Count == 0 || fullPlans.All(p => p.IsSelected)) continue;

            var dependents = group.Where(p => p.Block is not FullContentBlock && p.IsSelected && p.CanApply).ToList();
            if (dependents.Count == 0) continue;

            issues.Add(GraftIssue.Of(ErrorCode.E301,
                $"{group.First().Path} の全文置き換え（MODE=FULL）のチェックが外れていますが、同じファイルの他の変更にはチェックが付いています。" +
                "それらの変更は全文置き換えの後の内容に対して計算されているため、全文置き換えなしでは適用できません。" +
                "全文置き換えにもチェックを付けるか、他の変更のチェックも外してください。", path: group.First().Path));
        }

        return issues;
    }

    private static string NormalizeKey(string path) => path.Replace('\\', '/');

    /// <summary>
    /// 本適用の再解決の結果を、ドライラン時のプランと突き合わせる。食い違いがあれば E301（Error）を返す。
    /// 突き合わせる項目は次のとおり。
    /// ・ドライランで存在した（Modify）のに今は無い／存在しなかった（Create）のに今はある。
    /// ・選択したユニットが再解決で適用不能になった（SEARCHが見つからない等）。
    /// ・SR形式で、一致した位置や照合の段階がドライランと違う。
    /// APPEND/PREPEND は位置が「末尾」「先頭」で決まり、ファイルの行数が変わっても意味が変わらないため、
    /// 位置は比べない。FULL は全文を置き換えるので比べるものが無い（変更前のハッシュは entries に残す）。
    /// </summary>
    private static GraftResult<bool>? VerifyResolutionMatchesPlans(
        string path, IReadOnlyList<BlockPlan> plansForFile, FileResolution resolution, bool existed)
    {
        const string Advice = "解析のあとでファイルが変更されました。もう一度解析してください。";

        var plannedAsExisting = plansForFile[0].Operation == EntryOperation.Modify;
        if (plannedAsExisting && !existed)
        {
            return GraftResult<bool>.Fail(ErrorCode.E301,
                $"{Advice}（解析のときは存在した {path} が、適用の時点では見つかりません。" +
                "RENAME のチェックを外したために、改名前の状態を読んでいた変更だけが残った場合もこうなります）", path: path);
        }
        if (!plannedAsExisting && existed)
        {
            return GraftResult<bool>.Fail(ErrorCode.E301,
                $"{Advice}（解析のときは存在しなかった {path} が、適用の時点では存在します。新規作成のつもりの内容で上書きしてしまうため中止しました）",
                path: path);
        }

        foreach (var plan in plansForFile)
        {
            var unit = resolution.Units.FirstOrDefault(u =>
                ReferenceEquals(u.SourceBlock, plan.Block) && ReferenceEquals(u.SourcePair, plan.Pair));
            if (unit is null)
            {
                return GraftResult<bool>.Fail(ErrorCode.E301,
                    $"{Advice}（{path} の「{plan.Description}」に対応する変更を再解決で作れませんでした）", path: path);
            }

            if (!unit.CanApply)
            {
                var reasons = string.Join(" / ", unit.Issues.Select(i => i.ToDisplayText()));
                return GraftResult<bool>.Fail(ErrorCode.E301,
                    $"{Advice}（{path} の「{plan.Description}」が、適用の時点では当てはまりません" +
                    (reasons.Length > 0 ? $": {reasons}" : "") +
                    "。全文置き換えのチェックを外した場合もこうなります）", path: path);
            }

            if (plan.Pair is null) continue;

            if (unit.Stage != plan.Stage)
            {
                return GraftResult<bool>.Fail(ErrorCode.E301,
                    $"{Advice}（{path} の「{plan.Description}」の照合の段階が、解析のとき（段階{(int)plan.Stage}）と" +
                    $"適用の時点（段階{(int)unit.Stage}）で違います）", path: path);
            }

            if (!unit.MatchStartLines.SequenceEqual(plan.MatchStartLines))
            {
                var before = string.Join("、", plan.MatchStartLines.Select(l => l + 1));
                var after = string.Join("、", unit.MatchStartLines.Select(l => l + 1));
                return GraftResult<bool>.Fail(ErrorCode.E301,
                    $"{Advice}（{path} の「{plan.Description}」が当たる位置が、解析のとき（{before}行目）と" +
                    $"適用の時点（{after}行目）で違います）", path: path);
            }
        }

        return null;
    }
}
