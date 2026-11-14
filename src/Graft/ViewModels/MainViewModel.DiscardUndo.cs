using Graft.Core;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル。解析結果を「破棄」したあとの「元に戻す」。
///
/// 【なぜ必要か】 破棄は、貼り付けて解析した結果（と、利用者が外したチェックの状態）を捨てる操作で、
/// 取り消す手段が無かった。以前は Esc 1回で破棄されたため、絞り込み欄やポップアップを閉じようと
/// しただけで消えることがあった（実機で確認済み）。Esc での破棄はやめたが、「破棄」ボタンや
/// コマンドパレットからの破棄も、押し間違いに備えて数秒だけ戻せるようにする。
/// 戻す先は、適用後の通知（MainViewModel.ApplyUndoNotice.cs）と同じステータスバーの通知。
///
/// 戻すのは解析結果の表示状態だけで、ファイルには一切触れない（破棄はファイルを書き換えない
/// 操作なので、戻すのもメモリ上の状態の復元で足りる）。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>ステータスバーに数秒だけ出す汎用の通知。破棄の「元に戻す」・コピー完了などに使う。</summary>
    public StatusNoticeViewModel Notice { get; }

    /// <summary>破棄の直前の解析結果（戻すときに使う）。</summary>
    private sealed record DiscardSnapshot(
        string? ProjectId,
        Patch? Patch,
        DryRunResult? DryRun,
        ApplyContext? Context,
        bool FromQueue,
        int? AlreadyAppliedRevision,
        GraftIssue? Error,
        IReadOnlyList<bool> Checked,
        int SelectedIndex);

    /// <summary>
    /// 利用者の操作による破棄（「破棄」ボタン・コマンドパレット）。破棄したうえで、
    /// 数秒だけ「破棄しました — 元に戻す」を出す。プロジェクトの切り替え・履歴の復元など、
    /// 内部の都合で捨てる経路は <see cref="DiscardCurrentPatch"/> を直接呼ぶ（戻す意味が無い）。
    /// </summary>
    private void DiscardByUser()
    {
        var snapshot = new DiscardSnapshot(
            ProjectPane.SelectedItem?.Project.Id,
            _currentPatch,
            _dryRun,
            _lastContext,
            _dryRunFromQueue,
            _alreadyAppliedRevision,
            CenterError,
            Blocks.Select(b => b.IsSelected).ToList(),
            SelectedBlock is null ? -1 : Blocks.IndexOf(SelectedBlock));

        DiscardCurrentPatch();
        Notice.Show("解析結果を破棄しました", "元に戻す", () =>
        {
            RestoreDiscarded(snapshot);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// 破棄前の解析結果へ戻す。通知が出ている数秒のあいだに別のプロジェクトへ切り替えた、
    /// または新しく解析した場合は、戻すと今の画面を壊すので何もしない
    /// （新しい解析結果を黙って上書きするほうが、戻せないことより害が大きい）。
    /// </summary>
    private void RestoreDiscarded(DiscardSnapshot snapshot)
    {
        if (_currentPatch is not null || _dryRun is not null) return;
        if (snapshot.ProjectId != ProjectPane.SelectedItem?.Project.Id) return;

        _currentPatch = snapshot.Patch;
        _dryRun = snapshot.DryRun;
        _lastContext = snapshot.Context;
        _dryRunFromQueue = snapshot.FromQueue;
        SetAlreadyAppliedRevision(snapshot.AlreadyAppliedRevision);

        if (snapshot.DryRun is not null)
        {
            ReplaceBlocks(snapshot.DryRun.Plans);
            for (var i = 0; i < Blocks.Count && i < snapshot.Checked.Count; i++) Blocks[i].IsSelected = snapshot.Checked[i];
            if (snapshot.SelectedIndex >= 0 && snapshot.SelectedIndex < Blocks.Count) SelectedBlock = Blocks[snapshot.SelectedIndex];
        }

        if (snapshot.Error is not null)
        {
            CenterError = snapshot.Error;
            State = CenterPaneState.Error;
        }

        OnPropertyChanged(nameof(StatusSummaryText));

        OnPropertyChanged(nameof(StatusSummaryMainText));

        OnPropertyChanged(nameof(StatusSummaryFailedText));
        OnPropertyChanged(nameof(HasFailedBlocks));
        OnPropertyChanged(nameof(TargetSummaryText));
        CommandRequery.Invalidate();
    }
}
