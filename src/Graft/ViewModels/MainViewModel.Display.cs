using System.Linq;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> のうち、画面に出す文言を組み立てる部分
/// （1ファイル400行の上限のための分割）。表示以外の判断はここに置かない。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// ステータスバー表示。仕様書8.2「2件適用可 / 1件要確認」の書式が基本形。
    ///
    /// 不具合5対応（実機点検）: 修正前はドライラン時点の<see cref="DryRunResult.ApplicableCount"/>
    /// （＝チェックの状態を一切見ない「そもそも適用可能なブロック数」）をそのまま表示していたため、
    /// 解析後にチェックを外しても表示が変わらなかった（実測: 2ブロックのうち1件のチェックを
    /// 外しても「2件適用可 / 0件要確認」のまま）。ここでは<see cref="Blocks"/>（各行の
    /// <see cref="BlockItemViewModel.IsSelected"/>、チェックボックスの現在値）を都度数え直し、
    /// 「実際に今チェックが入っている（＝次に「適用」を押したときに書き込まれる）件数」を
    /// 表示する。値が変わるたびに再評価されるよう、<see cref="ReplaceBlocks"/>で各行の
    /// <see cref="BlockItemViewModel.PropertyChanged"/>（IsSelected）を購読して
    /// <see cref="OnPropertyChanged(string?)"/>を発火させている。
    ///
    /// 「対象外」は「適用可能なのにチェックを外した」件数のみを指し、そもそもマッチ失敗等で
    /// 適用不可能なブロック（<see cref="BlockPlan.CanApply"/>がfalse。DryRunPlannerが
    /// 自動的にIsSelected=falseへ倒すため、放っておくと「対象外」に紛れ込む）は含めない
    /// （チェックを外した覚えが無いのに「対象外」と言われると混乱するため区別する）。
    ///
    /// UI点検（項目9、別担当対応との統合）: 従来はApplicableCount（CanApply）と
    /// ConfirmationCount（NeedsConfirmation）の2つしか出しておらず、失敗
    /// （BlockStatusKind.Error、!CanApplyかつ要確認でもない）が何件あるかがステータスバーから
    /// 一切読み取れなかった（実機Xvfbで4ブロック中2件が赤い×なのに「2件適用可 / 0件要確認」
    /// としか出ないことを確認済み）。<see cref="DryRunResult.FailedCount"/>（既存。
    /// BlockItemViewModel.IsErrorと同じ!CanApply判定。チェックの状態を見ないため、
    /// 上記の「対象外」件数とは独立した別の軸）を末尾に足す。0件のときまで「0件失敗」と出すと
    /// 平常時にノイズになるため、失敗が無ければ従来どおりの表示のみにする。
    /// </summary>
    public string StatusSummaryText
    {
        get
        {
            if (_dryRun is null) return "解析結果はありません";

            var applying = Blocks.Count(b => b.IsSelected && b.Plan.CanApply);
            var excludedByChoice = Blocks.Count(b => !b.IsSelected && b.Plan.CanApply);
            var head = excludedByChoice > 0
                ? $"{applying}件を適用（{excludedByChoice}件は対象外）"
                : $"{applying}件適用可";
            var baseText = $"{head} / {_dryRun.ConfirmationCount}件要確認";
            return _dryRun.FailedCount > 0 ? $"{baseText} / {_dryRun.FailedCount}件失敗" : baseText;
        }
    }

    /// <summary>
    /// UI点検（項目9）: ステータスバー右側の接ぎ木状態表示を、失敗が1件でもあれば色で目立たせる
    /// ためのフラグ。StatusBarView.axaml側は既存の<c>StateError</c>トークンをDynamicResourceで
    /// 参照するだけで、トークンの定義値自体はここでは一切変更しない（色定義は別担当）。
    /// </summary>
    public bool HasFailedBlocks => (_dryRun?.FailedCount ?? 0) > 0;

    /// <summary>
    /// 接ぎ木パネルの見出しに出す、対象ファイルの要約。
    /// 件数の要約はステータスバーが担うため、ここでは「何に対する解析か」を示す。
    /// パネルを畳んだ状態でも対象を見失わないようにするのが目的。
    /// </summary>
    public string TargetSummaryText
    {
        get
        {
            if (Blocks.Count == 0) return string.Empty;

            var first = Blocks[0].PathText;
            return Blocks.Count == 1 ? first : $"{first} ほか{Blocks.Count - 1}件";
        }
    }

    /// <summary>選択中のプロジェクト名。未選択のときはその旨を示す。</summary>
    public string CurrentProjectName => ProjectPane.SelectedItem?.DisplayName ?? "(プロジェクト未選択)";
}
