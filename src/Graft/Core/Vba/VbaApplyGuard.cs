namespace Graft.Core.Vba;

/// <summary>画面側の検査が見つけた問題と、その対象のブロック。</summary>
/// <param name="Plan">問題のあるブロック（チェックを外せば適用できるようになることを画面で案内する）。</param>
/// <param name="Issue">E814 または E811。</param>
public sealed record VbaGuardFinding(BlockPlan Plan, GraftIssue Issue);

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
/// </list>
/// </summary>
public static class VbaApplyGuard
{
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
