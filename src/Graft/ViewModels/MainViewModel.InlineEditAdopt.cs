using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル（1ファイル400行上限のため）。
/// 差分画面のインライン編集（仕様書8.7）で書き換えたSEARCH部を、適用に含める経路。
///
/// 【背景】失敗したブロックのSEARCH部は差分画面で書き換えて再判定できる（<see cref="InlineEditViewModel"/>）
/// が、書き換えた結果を適用へ渡す経路が無かった。利用者は「一致した」ところまで確認できても、
/// 結局AIに依頼し直すしかなかった。ここでその最後の1歩をつなぐ。
///
/// 【触るもの・触らないもの】差し替えるのは<b>メモリ上の現在のパッチ（<c>_currentPatch</c>）の該当ペアだけ</b>。
/// クリップボード・元のテキスト（<see cref="Patch.RawText"/>）・REPLACE部には一切触れない。
/// RawTextを書き換えないので、二重適用検知のハッシュ（仕様書6.2）は元のAI出力に対するもののままになり、
/// 「同じパッチをもう一度貼った」ことを従来どおり検知できる。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 書き換えたSEARCH部（<paramref name="edited"/>）で、現在のパッチの<paramref name="original"/>を差し替え、
    /// ドライランをやり直す。<see cref="DiffViewModel.InlineEditAdopter"/> として配線される。
    ///
    /// 【ドライランのやり直しに <see cref="RunDryRunAsync"/> を直接使う理由】
    /// 入口の候補は3つあった。
    /// <list type="number">
    /// <item><b>PasteAndParse / ParseTextAndLoadAsync</b>: テキストを再解析する経路。差し替えたペアは
    /// 元のテキストに存在しないため、再解析すると書き換えが消える。使えない。</item>
    /// <item><b>PreviewCommand</b>: 中身は <see cref="RunDryRunAsync"/> そのもので、CanExecuteの判定が
    /// 加わるだけ。本処理は押された瞬間の状態を自分で検証するので、コマンド越しにする利点が無い。</item>
    /// <item><b><see cref="RunDryRunAsync"/> を直接</b>: プロジェクト自動判定・未保存ファイルの確認・
    /// ドライラン・一覧の更新までを、これまでと同じ1本の流れで行う唯一の入口。新規の経路を足さないので、
    /// 「通し忘れ」が構造的に起きない。採用。</item>
    /// </list>
    /// ただし素のままだとプロジェクト判定の確認ダイアログが二重に出る。判定結果は「同一のパッチ参照」で
    /// キャッシュされている（<c>_matchEvaluatedPatch</c>）が、差し替えで新しいパッチ参照になるため
    /// キャッシュが外れるからだ。差し替えたのは1ペアのSEARCH部だけでプロジェクトとの一致率に実質影響しない
    /// ので、判定済みの印を新しいパッチへ引き継ぐ。プロジェクトが途中で切り替わっていれば
    /// （<c>_matchEvaluatedProjectId</c> が合わなくなるので）通常どおり再判定される。
    /// 一方、未保存ファイルの確認（<see cref="ConfirmTargetsSavedAsync"/>）は引き継がずに毎回走らせる。
    /// 判定の基準になるのはディスク上のファイル内容であり、前回のドライラン以降にエディタで
    /// 編集された可能性があるうえ、未保存の編集が無ければダイアログは出ないので二重にはならない。
    ///
    /// 【安全検査を迂回しない】差し替え後のペアは、通常のパッチと同じ <c>ApplyEngine.DryRunAsync</c>
    /// （<c>MatchEngine</c> のE217を含むインデント補正の不変条件、PathGuard、サイズ上限など）で
    /// 改めて検証される。本適用の際も、<c>ApplyEngine</c> がファイルを読み直して同じ検査をやり直す。
    /// </summary>
    private async Task AdoptInlineEditAsync(BlockPlan plan, SearchReplacePair original, SearchReplacePair edited)
    {
        // 差し替え対象のパッチを決める。通常は解析したばかりの現在のパッチ（_currentPatch）。
        // 一部だけ適用できた後は、_currentPatch が null で、_dryRun に「失敗したブロックだけ」が
        // 残っている（MainViewModel.PartialApply.cs の KeepOnlyFailedBlocks）。利用者がいちばん
        // やりたい「一部適用 → 残りのSEARCHを直す → 適用に含める → 適用」の流れはこちらなので、
        // 黙って何もせず終わらないよう、残りのパッチからも差し替えられるようにする。
        var oldCurrent = _currentPatch;
        var fromRemainder = oldCurrent is null;
        Patch basePatch;
        IReadOnlyList<PatchBlock> planBlocks; // 画面のプランが指すブロック（参照の突き合わせ用）
        List<PatchBlock> workBlocks;          // 実際に差し替えて使うブロック列
        if (oldCurrent is { } current)
        {
            basePatch = current;
            planBlocks = current.Blocks;
            workBlocks = current.Blocks.ToList();
        }
        else if (_dryRun is { Plans.Count: > 0 } remaining)
        {
            basePatch = remaining.Patch;
            (planBlocks, workBlocks) = BuildRemainingBlocks(remaining);
        }
        else
        {
            await ShowAdoptRefusalAsync("差し替える対象のパッチが残っていません。もう一度パッチを貼り付けてください。").ConfigureAwait(true);
            return;
        }

        // 画面の表示（差分タブ）と対象のパッチが食い違っていないことを、参照の同一性で確かめる。
        // 別のパッチが貼られた・破棄された後に、古い画面の操作で別のブロックを書き換えてしまわないため。
        var blockIndex = IndexOfReference(planBlocks, plan.Block);
        var block = blockIndex >= 0 ? workBlocks[blockIndex] as SearchReplaceBlock : null;
        var pairIndex = block is null ? -1 : IndexOfReference(block.Pairs, original);
        if (block is null || pairIndex < 0)
        {
            await ShowAdoptRefusalAsync("この画面の内容が、いまの解析結果と一致しません。ブロックを選び直してからやり直してください。").ConfigureAwait(true);
            return;
        }

        // 差し替えるのはSEARCH部だけ、という約束の最後の砦。InlineEditViewModel.BuildEditedPair が
        // そう作っているが、ここで破れていたら適用内容が意図とずれるため、黙って進まず止める。
        if (!string.Equals(original.ReplaceText, edited.ReplaceText, StringComparison.Ordinal)
            || !edited.IsSearchEdited)
        {
            await ShowAdoptRefusalAsync("書き換えの内容を確認できませんでした。もう一度SEARCH部を編集してください。").ConfigureAwait(true);
            return;
        }

        var newPairs = block.Pairs.ToList();
        newPairs[pairIndex] = edited;
        workBlocks[blockIndex] = block with { Pairs = newPairs };
        var newPatch = basePatch with
        {
            Blocks = workBlocks,
            // 【E302の誤判定を避ける】残りのパッチは RawText が元のAI出力のままなので、そのままだと
            // 直前に記録したリビジョンと同じハッシュになり E302（適用済み）で「適用」が押せなくなる。
            // 残りの中身から別の基準テキストを作って与える（PatchIdentity参照）。通常のパッチは従来どおり
            // RawText のハッシュ。元の出力を貼り直したときは RawText のハッシュなので従来どおり E302 になる。
            PatchHashSource = fromRemainder ? PatchIdentity.ForRemainder(basePatch, workBlocks) : basePatch.PatchHashSource,
        };

        // 一覧のチェック状態を引き継ぐ。ドライランのやり直しは一覧を作り直し、全ブロックが
        // 「適用可なら選択済み」へ戻ってしまう。利用者が意図して外していたチェックまで
        // 黙って入れ直さないよう、ペア（無ければブロック）の参照をキーに覚えておく。
        var previousSelection = new Dictionary<object, bool>(ReferenceEqualityComparer.Instance);
        foreach (var b in Blocks) previousSelection[SelectionKey(b.Plan)] = b.IsSelected;

        // プロジェクト判定済みの印を新しいパッチへ引き継ぐ。通常は旧パッチの参照で判定済みか分かる。
        // 一部適用の後は、判定に使った元のパッチ（_matchEvaluatedPatch）と残りのパッチが別の参照になるが、
        // 残りは元のパッチの一部で、判定済みのプロジェクトと同じ場所に対するもの。同じプロジェクトを
        // 選んでいる限り判定済みとみなす（プロジェクトを切り替えれば解析結果ごと破棄される）。
        var previousEvaluated = _matchEvaluatedPatch;
        var matchWasEvaluated = fromRemainder
            ? _matchEvaluatedPatch is not null && _matchEvaluatedProjectId == ProjectPane.SelectedItem?.Project.Id
            : ReferenceEquals(_matchEvaluatedPatch, oldCurrent);
        if (matchWasEvaluated) _matchEvaluatedPatch = newPatch;
        _currentPatch = newPatch;

        await RunDryRunAsync().ConfigureAwait(true);

        if (_currentPatch is null && _dryRun is null) return; // 判定でブロック/キャンセルされ、解析結果ごと破棄された。

        if (!ReferenceEquals(_dryRun?.Patch, newPatch))
        {
            // ドライランが完走しなかった（未保存の保存確認での中止、エラーなど）。画面上の結果は
            // 差し替え前のままなので、パッチも差し替え前（残りのパッチなら null）へ戻して食い違いを残さない。
            _currentPatch = oldCurrent;
            _matchEvaluatedPatch = previousEvaluated;
            return;
        }

        foreach (var item in Blocks)
        {
            if (item.CanToggle && previousSelection.TryGetValue(SelectionKey(item.Plan), out var wasSelected))
            {
                item.IsSelected = wasSelected;
            }
        }

        var adopted = Blocks.FirstOrDefault(b => ReferenceEquals(b.Plan.Pair, edited));
        if (adopted is not null) SelectedBlock = adopted;

        Logger?.Info("inline-edit",
            adopted is { Plan.CanApply: true }
                ? $"SEARCH部を差し替えて適用可能になりました（{block.Path}・{original.SourceLine}行目のペア）"
                : $"SEARCH部を差し替えましたが、まだ適用できません（{block.Path}・{original.SourceLine}行目のペア）",
            targetPath: block.Path);

        if (adopted is { Plan.CanApply: false } stillFailed)
        {
            // 編集画面では一致していたのに、実際のドライランでは別の理由（インデント補正の安全検査E217、
            // 上限・権限など）で通らなかった場合。画面が変わっただけでは何が起きたか分からないので、理由を伝える。
            var reason = stillFailed.Plan.Issues.FirstOrDefault()?.ToDisplayText() ?? "理由は不明です";
            await _dialogs.ShowMessageAsync("まだ適用できません",
                $"書き換えたSEARCH部で再判定しましたが、このブロックは適用できませんでした。{Environment.NewLine}{reason}")
                .ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 一部適用の後の「残りのパッチ」のブロック列を、失敗プランから作る。
    /// <see cref="DryRunResult.Patch"/> のブロックは失敗ブロックを丸ごと（成功済みのペアも含めて）
    /// 持っているため、そのまま再ドライランすると、すでに適用したペアのSEARCHが更新後のファイルに
    /// 一致せず、失敗ブロックとして再び現れてしまう（あるいは別の箇所に誤一致する）。
    /// そこでプランごとに、失敗したペアだけを残したブロックを作る。ファイル単位の失敗でペアを特定できない
    /// プラン（Pair が null）があるブロックは、全ペアを残す。
    /// </summary>
    /// <returns>
    /// PlanBlocks: プランが指す元のブロック（参照の突き合わせ用）。
    /// WorkBlocks: 同じ順序で、失敗したペアだけに絞ったブロック（SR以外はそのまま）。
    /// </returns>
    private static (IReadOnlyList<PatchBlock> PlanBlocks, List<PatchBlock> WorkBlocks) BuildRemainingBlocks(DryRunResult remaining)
    {
        var planBlocks = new List<PatchBlock>();
        var workBlocks = new List<PatchBlock>();
        foreach (var group in remaining.Plans.GroupBy(p => p.Block, ReferenceEqualityComparer.Instance))
        {
            var sourceBlock = group.First().Block;
            planBlocks.Add(sourceBlock);
            if (sourceBlock is SearchReplaceBlock sr && group.All(p => p.Pair is not null))
            {
                var failedPairs = new HashSet<SearchReplacePair>(group.Select(p => p.Pair!), ReferenceEqualityComparer.Instance);
                workBlocks.Add(sr with { Pairs = sr.Pairs.Where(failedPairs.Contains).ToList() });
            }
            else
            {
                workBlocks.Add(sourceBlock);
            }
        }
        return (planBlocks, workBlocks);
    }

    /// <summary>差し替えられなかった理由を伝える。ボタンが押せたのに何も起きない状態を作らない。</summary>
    private Task ShowAdoptRefusalAsync(string reason)
        => _dialogs.ShowMessageAsync("適用に含められません", reason);

    /// <summary>チェック状態の引き継ぎ用キー。ペア単位のプランはペア、それ以外はブロックの参照。</summary>
    private static object SelectionKey(BlockPlan plan) => (object?)plan.Pair ?? plan.Block;

    private static int IndexOfReference<T>(IReadOnlyList<T> list, T item) where T : class
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item)) return i;
        }
        return -1;
    }
}
