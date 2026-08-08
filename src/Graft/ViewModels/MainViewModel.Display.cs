namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> のうち、画面に出す文言を組み立てる部分
/// （1ファイル400行の上限のための分割）。表示以外の判断はここに置かない。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>ステータスバー表示。仕様書8.2「2件適用可 / 1件要確認」の書式。</summary>
    public string StatusSummaryText => _dryRun is null
        ? "解析結果はありません"
        : $"{_dryRun.ApplicableCount}件適用可 / {_dryRun.ConfirmationCount}件要確認";

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
