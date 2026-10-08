using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル。一部のブロックだけ適用できたあとの後始末を担う。
///
/// 【修正前の問題】 適用の完了ダイアログは「適用できなかった理由は、接ぎ木パネルの各ブロックに
/// 赤字で残っています」と案内していたが、その前に<see cref="DiscardCurrentPatch"/>でブロック一覧を
/// 空にしていた。理由は見られず、「修正を依頼」（<see cref="CopyRecoveryPromptCommand"/>、有効条件は
/// 失敗ブロックの有無）も押せなくなっていた。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 失敗ブロックが1件以上あった適用の直後に呼ぶ。接ぎ木パネルには<b>失敗したブロックだけ</b>を
    /// 残し（理由は<see cref="BlockPlan.Issues"/>のとおり赤字で見える）、適用済みのブロックは外す。
    /// <para>
    /// 【各フィールドの扱いと理由】
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>_currentPatch</c>はnullにする。元のパッチ全体を残すと、(1)「プレビュー」を押した再ドライランが
    /// いま確定したばかりのリビジョンと同じパッチ本文のハッシュになりE302（適用済み）で全体が止まり、
    /// (2)「キューへ追加」が適用済みのブロックまで積み、(3) 適用済みのブロックを含むパッチを
    /// 「未処理の解析結果」（<see cref="HasUnprocessedResult"/>）と見なしてクリップボード監視の自動解析を
    /// 止めてしまう。残っているのは「適用できなかった」という記録と理由であり、
    /// やり直しは新しい回答のパッチを貼って行う（貼れば従来どおり丸ごと置き換わる）。
    /// </description></item>
    /// <item><description>
    /// <c>_dryRun</c>は失敗ブロックだけに絞った結果へ差し替える（nullにはしない）。nullにすると
    /// ステータスバーが「解析結果はありません」になり、接ぎ木パネルに失敗ブロックが見えているのと
    /// 食い違う。絞った結果なら<see cref="DryRunResult.ApplicableCount"/>が0になり、
    /// <see cref="ApplyCommand"/>の有効条件（<c>ApplicableCount &gt; 0</c>）によって「適用」は自然に
    /// 押せなくなる（残っているのは失敗ブロックだけで、適用できるものは無い）。
    /// 状態の要約も「0件適用可 / N件失敗」と実態に合う。
    /// </description></item>
    /// <item><description>
    /// <c>_lastContext</c>はnullにする。リビジョン番号は適用で消費済みで、この文脈を二度と
    /// 使ってはならない（<see cref="ApplyAsync"/>は文脈がnullなら何もしない）。
    /// </description></item>
    /// </list>
    /// <para>
    /// 破棄は<see cref="DiscardCommand"/>で行える（有効条件が<c>_dryRun</c>も見る）。履歴から適用を
    /// 取り消した場合（<see cref="OnRevisionRestored"/>）は従来どおり全部を捨てる。
    /// チェックを外した（適用可能だが適用しなかった）ブロックは、従来どおり適用後に残さない
    /// （利用者が自分で外したもので、失敗の理由として見せるものが無いため）。
    /// </para>
    /// </summary>
    private void KeepOnlyFailedBlocks(DryRunResult applied)
    {
        var failedPlans = applied.Plans.Where(p => !p.CanApply).ToList();

        // 元のパッチ本文（RawText）は元のまま。再解析・再適用には使わず、ブロックだけを絞る。
        var failedBlocks = failedPlans.Select(p => p.Block).Distinct().ToList();

        _currentPatch = null;
        _lastContext = null;
        _dryRun = applied with
        {
            Patch = applied.Patch with { Blocks = failedBlocks },
            Plans = failedPlans,
            AlreadyAppliedRevision = null,
        };
        CenterError = null;
        SetAlreadyAppliedRevision(null);
        ReplaceBlocks(failedPlans);
        OnPropertyChanged(nameof(StatusSummaryText));
        OnPropertyChanged(nameof(HasFailedBlocks));
        OnPropertyChanged(nameof(TargetSummaryText));
        CommandRequery.Invalidate();
    }
}
