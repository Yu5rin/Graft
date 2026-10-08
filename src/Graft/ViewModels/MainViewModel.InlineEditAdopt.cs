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
        if (_currentPatch is not { } oldPatch) return;

        // 画面の表示（差分タブ）と現在のパッチが食い違っていないことを、参照の同一性で確かめる。
        // 別のパッチが貼られた・破棄された後に、古い画面の操作で別のブロックを書き換えてしまわないため。
        var blockIndex = IndexOfReference(oldPatch.Blocks, plan.Block);
        if (blockIndex < 0 || oldPatch.Blocks[blockIndex] is not SearchReplaceBlock block) return;
        var pairIndex = IndexOfReference(block.Pairs, original);
        if (pairIndex < 0) return;

        // 差し替えるのはSEARCH部だけ、という約束の最後の砦。InlineEditViewModel.BuildEditedPair が
        // そう作っているが、ここで破れていたら適用内容が意図とずれるため、黙って進まず止める。
        if (!string.Equals(original.ReplaceText, edited.ReplaceText, StringComparison.Ordinal)
            || !edited.IsSearchEdited)
        {
            return;
        }

        var newPairs = block.Pairs.ToList();
        newPairs[pairIndex] = edited;
        var newBlocks = oldPatch.Blocks.ToList();
        newBlocks[blockIndex] = block with { Pairs = newPairs };
        var newPatch = oldPatch with { Blocks = newBlocks };

        // 一覧のチェック状態を引き継ぐ。ドライランのやり直しは一覧を作り直し、全ブロックが
        // 「適用可なら選択済み」へ戻ってしまう。利用者が意図して外していたチェックまで
        // 黙って入れ直さないよう、ペア（無ければブロック）の参照をキーに覚えておく。
        var previousSelection = new Dictionary<object, bool>(ReferenceEqualityComparer.Instance);
        foreach (var b in Blocks) previousSelection[SelectionKey(b.Plan)] = b.IsSelected;

        var matchWasEvaluated = ReferenceEquals(_matchEvaluatedPatch, oldPatch);
        if (matchWasEvaluated) _matchEvaluatedPatch = newPatch;
        _currentPatch = newPatch;

        await RunDryRunAsync().ConfigureAwait(true);

        if (_currentPatch is null) return; // 判定でブロック/キャンセルされ、解析結果ごと破棄された。

        if (!ReferenceEquals(_dryRun?.Patch, newPatch))
        {
            // ドライランが完走しなかった（未保存の保存確認での中止、エラーなど）。画面上の結果は
            // 差し替え前のままなので、パッチも差し替え前へ戻して食い違いを残さない。
            _currentPatch = oldPatch;
            if (matchWasEvaluated) _matchEvaluatedPatch = oldPatch;
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
