namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> のうち、画面に出す文言を組み立てる部分
/// （1ファイル400行の上限のための分割）。表示以外の判断はここに置かない。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// ステータスバー表示。仕様書8.2「2件適用可 / 1件要確認」の書式。
    ///
    /// UI点検（項目9）: 従来はApplicableCount（CanApply）とConfirmationCount（NeedsConfirmation）
    /// の2つしか出しておらず、失敗（BlockStatusKind.Error、!CanApplyかつ要確認でもない）が
    /// 何件あるかがステータスバーから一切読み取れなかった（実機Xvfbで4ブロック中2件が赤い×
    /// なのに「2件適用可 / 0件要確認」としか出ないことを確認済み）。DryRunResult.FailedCount
    /// （既存。BlockItemViewModel.IsErrorと同じ!CanApply判定）を末尾に足す。0件のときまで
    /// 「0件失敗」と出すと平常時にノイズになるため、失敗が無ければ従来どおりの2項目のみにする。
    /// </summary>
    public string StatusSummaryText
    {
        get
        {
            if (_dryRun is null) return "解析結果はありません";

            var baseText = $"{_dryRun.ApplicableCount}件適用可 / {_dryRun.ConfirmationCount}件要確認";
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
