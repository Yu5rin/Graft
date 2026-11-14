using System.ComponentModel;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル。押せないボタンが「なぜ押せないか」を、
/// ツールチップの先頭に1行で出すための文言（HelpTip.DisabledReason に結ぶ）。
///
/// 無効なボタンにはツールチップが出ず、利用者は何をすれば押せるようになるのか分からなかった。
/// 文言は「次に何をするか」を言う（原因の説明ではなく）。ボタンの有効条件そのものは変えない。
/// </summary>
public sealed partial class MainViewModel
{
    private const string NeedProjectText = "プロジェクトを選ぶと使えます";
    private const string NeedAnalysisText = "先に解析してください";

    /// <summary>解析結果が必要なボタン（再チェック・破棄・キューへ追加）用。</summary>
    public string NeedAnalysisReason
        => ProjectPane.SelectedItem is null ? NeedProjectText : NeedAnalysisText;

    /// <summary>プロジェクトが必要なボタン（ファイルを選ぶ・依頼文をコピー）用。</summary>
    public string NeedProjectReason => NeedProjectText;

    /// <summary>「適用」が押せない理由。</summary>
    public string ApplyDisabledReason
    {
        get
        {
            if (ProjectPane.SelectedItem is null) return NeedProjectText;
            if (_currentPatch is null && _dryRun is null) return NeedAnalysisText;
            if (_alreadyAppliedRevision is { } revision) return $"r{revision} で適用済みのパッチです";
            return "適用できる変更がありません";
        }
    }

    /// <summary>「修正を依頼」が押せない理由。</summary>
    public string RecoveryDisabledReason
        => ProjectPane.SelectedItem is null ? NeedProjectText : "適用できなかったブロックがあるときに使えます";

    private void WireDisabledReasons()
    {
        PropertyChanged += OnPropertyChangedForDisabledReasons;
    }

    private void OnPropertyChangedForDisabledReasons(object? sender, PropertyChangedEventArgs e)
    {
        // 理由の元になる状態（プロジェクト・解析結果の有無・適用済み）が変わる合図。
        // 「適用 (N件)」の件数は、ステータスバーの要約が再評価される場面（解析・チェックの付け外し・
        // 適用後の入れ替え）と常に同じタイミングで変わるため、その合図に相乗りする。
        if (e.PropertyName == nameof(StatusSummaryMainText)) OnPropertyChanged(nameof(ApplyButtonText));

        if (e.PropertyName is not (nameof(State) or nameof(StatusSummaryText) or nameof(CurrentProjectName)
            or nameof(HasAlreadyAppliedNotice))) return;
        OnPropertyChanged(nameof(NeedAnalysisReason));
        OnPropertyChanged(nameof(ApplyDisabledReason));
        OnPropertyChanged(nameof(RecoveryDisabledReason));
    }
}
