namespace Graft.Core.Vba;

/// <summary>画面側の検査が見つけた問題と、その対象のブロック。</summary>
/// <param name="Plan">問題のあるブロック（チェックを外せば適用できるようになることを画面で案内する）。</param>
/// <param name="Issue">E814 または E811。</param>
public sealed record VbaGuardFinding(BlockPlan Plan, GraftIssue Issue);

/// <summary>既に存在する VBA コンポーネントの名前と種別（ミラーのファイル、または Excel のコンポーネント）。</summary>
/// <param name="Component">コンポーネント名。</param>
/// <param name="Kind">種別。</param>
public sealed record VbaComponentRef(string Component, VbaModuleKind Kind);

/// <summary>
/// 解析結果から VBA プロジェクトで適用できないブロックを拾う純ロジック（仕様書_VBA連携 7.3-2）。
/// 適用エンジン（<c>ApplyEngine</c>）は変えず、確認・プレビューの前に画面側で検査するための部品。
/// <para>
/// 対象は<b>チェックが付いている</b>ブロックだけ。外せばそのブロックは見逃され、ほかのブロックは
/// 適用できる。
/// </para>
/// <list type="bullet">
/// <item>
/// <b>DELETE／RENAME（E814）</b>: 削除と名前変更を Excel へ反映しない設計なので、ミラーだけ変えると
/// ミラーと Excel が食い違い、RENAME は旧名と新名の二重定義になりうる（推測。VBA では同名プロシージャで
/// コンパイルエラーになる見込み）。<c>Mkdir</c> はミラーのフォルダを作るだけで同期に影響しない
/// （フォルダは同期の対象外）ので止めない。
/// </item>
/// <item>
/// <b>CP932 で表せない文字（E811）</b>: ミラーのファイルは UTF-8 なので、既存の E219 は働かない。
/// 適用後の本文（<see cref="BlockPlan.AfterText"/>）を検査する。同期の対象外のパス
/// （<see cref="VbaMirrorLayout.TryParse"/> が失敗するもの）は Excel に反映されないので検査しない。
/// </item>
/// <item>
/// <b>同名で種別違いの新規ファイル（E808）</b>: VBA のコンポーネント名は種別をまたいで一意なので、
/// Excel に Document の <c>ThisWorkbook</c> があるのに <c>Classes/ThisWorkbook.cls</c> を作ったり、
/// <c>Modules/Foo.bas</c> と <c>Classes/Foo.cls</c> を同時に作ったりすると、Excel へは反映できない
/// （反映すると既存の本文を上書きしてしまう）。ミラーにゴミのファイルが残らないよう、適用の前に止める
/// （<see cref="CheckNameCollisions"/>。既存の名前の一覧は呼び出し側が渡す）。
/// </item>
/// </list>
/// </summary>
public static class VbaApplyGuard
{
    /// <summary>
    /// 新規に作るミラーのファイルが、既存のコンポーネントや、同時に作るほかのファイルと「同名で種別違い」に
    /// なっていないかを検査する（不具合 1）。
    /// </summary>
    /// <param name="plans">現在のチェックの状態を反映した解析結果。</param>
    /// <param name="existing">既に存在するコンポーネント（ミラーのファイルと Excel のコンポーネント）。</param>
    public static IReadOnlyList<VbaGuardFinding> CheckNameCollisions(IEnumerable<BlockPlan> plans, IEnumerable<VbaComponentRef> existing)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(existing);

        var known = new Dictionary<string, VbaComponentRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existing) known[item.Component] = item;

        var creating = new List<(BlockPlan Plan, VbaMirrorPath Parsed)>();
        foreach (var plan in NewMirrorFiles(plans))
        {
            if (VbaMirrorLayout.TryParse(plan.Path, out var parsed, out _) && parsed is not null
                && VbaModuleKinds.CanCreateOnExcel(parsed.Kind))
            {
                creating.Add((plan, parsed));
            }
        }

        var findings = new List<VbaGuardFinding>();
        foreach (var (plan, parsed) in creating)
        {
            string? detail = null;
            if (known.TryGetValue(parsed.ComponentName, out var other) && other.Kind != parsed.Kind)
            {
                detail = $"{plan.Path}: 同じ名前「{parsed.ComponentName}」の{VbaModuleKinds.Label(other.Kind)}が既にあります。" +
                         $"別の種別としては作成できません。{VbaMirrorLayout.ToRelativePath(other.Kind, other.Component)} に書いてください。";
            }
            else if (creating.Any(c => !ReferenceEquals(c.Plan, plan)
                                       && string.Equals(c.Parsed.ComponentName, parsed.ComponentName, StringComparison.OrdinalIgnoreCase)
                                       && c.Parsed.Kind != parsed.Kind))
            {
                detail = $"{plan.Path}: 同じ名前「{parsed.ComponentName}」のファイルを、複数の種別のフォルダに同時に作ろうとしています。" +
                         "Excel 側には同じ名前のモジュールを 1 つしか作れません。名前を変えるか、片方のチェックを外してください。";
            }

            if (detail is not null)
            {
                findings.Add(new VbaGuardFinding(plan, GraftIssue.Of(ErrorCode.E808, detail: detail, path: plan.Path)));
            }
        }

        return findings;
    }

    /// <summary>チェックが付いていて、ミラーに新しくファイルを作るブロック（削除・名前変更・フォルダ作成を除く）。</summary>
    public static bool CreatesMirrorFile(BlockPlan plan)
        => plan.IsSelected && plan.BeforeText is null && plan.AfterText is not null
           && plan.Block.Kind is not (BlockKind.Delete or BlockKind.Rename or BlockKind.Mkdir)
           && VbaMirrorLayout.TryParse(plan.Path, out _, out _);

    private static IEnumerable<BlockPlan> NewMirrorFiles(IEnumerable<BlockPlan> plans) => plans.Where(CreatesMirrorFile);

    /// <summary>適用を止めるべき問題を列挙する。なければ空。</summary>
    public static IReadOnlyList<VbaGuardFinding> Check(IEnumerable<BlockPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        var findings = new List<VbaGuardFinding>();
        foreach (var plan in plans)
        {
            if (!plan.IsSelected) continue;

            var kind = plan.Block.Kind;
            if (kind is BlockKind.Delete or BlockKind.Rename)
            {
                var operation = kind == BlockKind.Delete ? "削除（DELETE）" : "名前変更（RENAME）";
                findings.Add(new VbaGuardFinding(plan, GraftIssue.Of(
                    ErrorCode.E814,
                    detail: $"{plan.Path} の{operation}は適用できません。",
                    line: plan.Block.HeaderLine > 0 ? plan.Block.HeaderLine : null,
                    path: plan.Path)));
                continue;
            }

            if (plan.AfterText is null) continue;
            if (!VbaMirrorLayout.TryParse(plan.Path, out _, out _)) continue;

            var issue = VbaCodePageGuard.ToIssue(plan.Path, plan.AfterText);
            if (issue is not null) findings.Add(new VbaGuardFinding(plan, issue));
        }

        return findings;
    }
}
