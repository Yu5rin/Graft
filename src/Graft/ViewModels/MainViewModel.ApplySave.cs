using System.IO;
using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel.BeforeApplyAsync"/>（対象ファイルの未保存確認）の結果。
/// 以前はbool（続行してよいか）だったが、「保存が実際に起きたか」を呼び出し側が知る必要がある。
/// 保存でディスクの内容が解析時から変わると、解析結果（一致位置・行番号）が古くなるため（C3×A5）。
/// </summary>
public enum TargetSaveOutcome
{
    /// <summary>利用者が保存を断った、または保存に失敗した。処理を中止する。</summary>
    Cancelled,

    /// <summary>未保存の編集が無かった（ディスクの内容は解析時のまま）。そのまま続行できる。</summary>
    NothingToSave,

    /// <summary>1つ以上のファイルを保存した。ディスクの内容が変わっているので解析し直す必要がある。</summary>
    Saved,
}

public sealed partial class MainViewModel
{
    /// <summary>
    /// 適用の早い段階で、対象ファイルの未保存の編集を保存させ、保存が起きたら解析し直す（C3×A5）。
    /// <para>
    /// 【なぜ解析し直すか】適用エンジンは、本適用の再解決がドライラン時のプランと食い違うと
    /// 書く前にE301で中止する（A5）。保存で先頭に1行足されただけでも行番号がずれて食い違うため、
    /// 保存したまま適用を進めると必ず中止になる。検出を緩めると「見せた差分と違う場所へ書く」
    /// 危険が戻るので、画面側で保存後の内容に対する解析へ差し替える。確認ダイアログ・プレビューより
    /// 前にここを置くのは、利用者が見て判断する差分を保存後の内容に対するものにするため。
    /// </para>
    /// <para>
    /// チェックの状態はやり直しの前後で保つ（InlineEditAdoptと同じSelectionKeyで引き継ぐ）。
    /// 保存が起きなかったときは何もしない（余計なドライランをしない）。
    /// </para>
    /// 戻り値がnullなら適用を進めない（保存を断った／やり直しが完走しなかった／チェック済みの変更が
    /// 適用できなくなった）。それ以外はこの後の処理に使う最新のドライラン結果と文脈。
    /// </summary>
    private async Task<(DryRunResult DryRun, ApplyContext Context)?> SaveTargetsAndReanalyzeAsync(
        DryRunResult dryRun, ApplyContext context, IReadOnlyList<BlockPlan> plans)
    {
        var project = ProjectPane.SelectedItem?.Project;
        if (project is null || BeforeApplyAsync is null) return (dryRun, context);

        var targets = plans
            .Where(p => p.IsSelected && p.CanApply)
            .SelectMany(p => new[] { p.Block.Path, p.Path }) // RENAMEは移動元と移動先の両方。
            .Select(path => Path.Combine(project.Root, path.Replace('/', Path.DirectorySeparatorChar)))
            .Distinct()
            .ToList();
        var outcome = await BeforeApplyAsync(targets).ConfigureAwait(true);
        if (outcome == TargetSaveOutcome.Cancelled) return null;
        if (outcome == TargetSaveOutcome.NothingToSave) return (dryRun, context);

        // 保存が起きた。やり直しは一覧を作り直してチェックを既定へ戻すため、先に控える。
        var previousSelection = new Dictionary<object, bool>(ReferenceEqualityComparer.Instance);
        foreach (var b in Blocks) previousSelection[SelectionKey(b.Plan)] = b.IsSelected;
        var mustStayApplicable = Blocks
            .Where(b => b.IsSelected && b.Plan.CanApply)
            .Select(b => SelectionKey(b.Plan))
            .ToList();

        await RunDryRunAsync().ConfigureAwait(true);

        // やり直しが完走しなかった（エラー・破棄・プロジェクト切替）。画面側にエラー等が出ているので、
        // 古い結果のまま適用へ進まない。
        if (_dryRun is not { } fresh || ReferenceEquals(fresh, dryRun) || _lastContext is not { } freshContext) return null;

        foreach (var item in Blocks)
        {
            if (item.CanToggle && previousSelection.TryGetValue(SelectionKey(item.Plan), out var wasSelected))
            {
                item.IsSelected = wasSelected;
            }
        }

        var broken = mustStayApplicable.Any(key => !Blocks.Any(b => ReferenceEquals(SelectionKey(b.Plan), key) && b.Plan.CanApply));
        if (broken)
        {
            // やり直した結果は画面に残したまま、適用は始めない。利用者が理由（各ブロックの赤字）を見て決める。
            await _dialogs
                .ShowMessageAsync("適用できない変更があります",
                    "保存した内容に合わせて解析し直したところ、適用できない変更があります。内容を確認してください。")
                .ConfigureAwait(true);
            return null;
        }

        return (fresh, freshContext);
    }
}
