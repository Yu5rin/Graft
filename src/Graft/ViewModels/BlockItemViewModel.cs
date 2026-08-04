using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// ブロックの状態種別。色と形状の両方で区別するため（仕様書8.5）、
/// アイコン・状態色の選択はこの列挙をキーにXAML側のDataTriggerで行う。
/// </summary>
public enum BlockStatusKind
{
    /// <summary>適用可。</summary>
    Ok,
    /// <summary>要確認（マッチ段階5など）。</summary>
    Warn,
    /// <summary>失敗。</summary>
    Error,
}

/// <summary>
/// 中央ペイン「ブロック一覧」の1行に対応する。<see cref="BlockPlan"/>（ドライラン結果）を
/// ラップし、1行目にファイルパス、2行目に変更説明を表示するための表示用プロパティと、
/// Space キーで切り替える適用可否チェック状態を持つ（仕様書8.2・8.10）。
/// </summary>
public sealed class BlockItemViewModel : ObservableObject
{
    private bool _isSelected;

    public BlockItemViewModel(BlockPlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _isSelected = plan.IsSelected;
    }

    /// <summary>元のドライラン結果。</summary>
    public BlockPlan Plan { get; private set; }

    /// <summary>1行目: ファイルパス。</summary>
    public string PathText => Plan.Path;

    /// <summary>2行目: 変更説明。未指定時は操作種別から機械的に補う。</summary>
    public string DescriptionText => string.IsNullOrWhiteSpace(Plan.Description) ? OperationFallbackText : Plan.Description!;

    private string OperationFallbackText => Plan.Operation switch
    {
        EntryOperation.Create => "新規作成",
        EntryOperation.Delete => "削除",
        EntryOperation.Rename => "移動・改名",
        EntryOperation.Mkdir => "フォルダ作成",
        _ => "変更",
    };

    /// <summary>状態種別。色と形状の両方の切り替えに使う（8.5）。</summary>
    public BlockStatusKind Status => !Plan.CanApply
        ? BlockStatusKind.Error
        : Plan.NeedsConfirmation
            ? BlockStatusKind.Warn
            : BlockStatusKind.Ok;

    /// <summary>状態の文字表現。色のみに依存しないための読み上げ・表示用テキスト（8.14）。</summary>
    public string StatusText => Status switch
    {
        BlockStatusKind.Ok => "適用可",
        BlockStatusKind.Warn => "要確認",
        _ => "失敗",
    };

    /// <summary>検出された問題があるかどうか。</summary>
    public bool HasIssue => Plan.Issues.Count > 0;

    /// <summary>
    /// 問題のインライン表示テキスト。エラーコードと対処方法を併記する（仕様書8.8）。
    /// 赤い帯ではなく該当行に表示することを想定している。
    /// </summary>
    public string? IssueText => Plan.Issues.Count == 0
        ? null
        : string.Join(Environment.NewLine, Plan.Issues.Select(i => $"{i.ToDisplayText()}  対処: {i.Remedy}"));

    /// <summary>追加・削除行数の表示。</summary>
    public string AddedRemovedText => $"+{Plan.Added} -{Plan.Removed}";

    /// <summary>適用対象として選択されているか（Space キーで切り替える）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>失敗ブロックは適用しようがないためトグル操作の対象外とする。</summary>
    public bool CanToggle => Plan.CanApply;

    /// <summary>読み上げ・アクセシビリティ用の行全体の説明（8.14）。</summary>
    public string AutomationName => $"{PathText}、{DescriptionText}、{StatusText}、{AddedRemovedText}";

    /// <summary>Space キーによる適用可否の切り替え。失敗ブロックには効果がない。</summary>
    public void Toggle()
    {
        if (CanToggle)
        {
            IsSelected = !IsSelected;
        }
    }

    /// <summary>
    /// インライン編集（SEARCH部修正）等でドライラン結果が更新された場合に差し替える。
    /// 8.7のインライン編集は担当外だが、将来の差し替え経路として最小限用意しておく。
    /// </summary>
    public void ReplacePlan(BlockPlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        IsSelected = plan.IsSelected;
        OnPropertyChanged(string.Empty);
    }
}
