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

    /// <summary>
    /// 状態が「適用可」かどうか（8.5の状態アイコン切り替え用）。UI側で<see cref="Status"/>と
    /// 特定値を突き合わせる条件分岐（WPFのDataTrigger等）を書かずに済むよう真偽値で公開する。
    /// </summary>
    public bool IsOk => Status == BlockStatusKind.Ok;

    /// <inheritdoc cref="IsOk"/>
    public bool IsWarn => Status == BlockStatusKind.Warn;

    /// <inheritdoc cref="IsOk"/>
    public bool IsError => Status == BlockStatusKind.Error;

    /// <summary>検出された問題があるかどうか。</summary>
    public bool HasIssue => Plan.Issues.Count > 0;

    /// <summary>
    /// 1ブロックに複数の問題が付く場合（例: PathGuard.Inspectがサイズ超過E203と排他ロックE204を
    /// 同時に返す、読み取り専用の解決不能パスなど）に一覧へ表示する上限件数。
    /// ShellViewModel.StatusBarWarning.cs と同じ「上限＋『ほかN件』」の流儀（不具合1対応）。
    /// 際限なく縦へ伸びて他の行の表示を圧迫しないよう抑える。
    /// </summary>
    private const int MaxIssueLines = 3;

    /// <summary>
    /// 問題のインライン表示行。エラーコードと対処方法を併記する（仕様書8.8）。
    /// 赤い帯ではなく該当行に表示することを想定している。
    /// 不具合1対応: 以前は<see cref="Environment.NewLine"/>で連結した1本のTextBlock.Textに
    /// していたが、1ブロックに複数件の問題が付いたとき（前述のPathGuard.Inspectの例など）
    /// 表示件数の上限が無く際限なく縦に伸びうる構造だった。GraftPanel.axaml側は
    /// ItemsControl（ItemsPanelにStackPanelを明示）でこのプロパティを1件ずつ別々の
    /// TextBlockとして描画するため、1行に収める処理（改行の埋め込み方）に依存せず、
    /// 各問題が確実に縦へ並ぶ。
    /// </summary>
    public IReadOnlyList<string> IssueLines
    {
        get
        {
            if (Plan.Issues.Count == 0) return Array.Empty<string>();

            var lines = Plan.Issues.Take(MaxIssueLines)
                .Select(i => $"{i.ToDisplayText()}  対処: {i.Remedy}")
                .ToList();
            if (Plan.Issues.Count > MaxIssueLines)
            {
                lines.Add($"ほか{Plan.Issues.Count - MaxIssueLines}件");
            }

            return lines;
        }
    }

    /// <summary>
    /// 問題のインライン表示テキスト（1本の文字列版）。読み上げ・テストからの参照用に残す。
    /// 画面表示は<see cref="IssueLines"/>（ItemsControl）側を使う。
    /// </summary>
    public string? IssueText => Plan.Issues.Count == 0 ? null : string.Join("\n", IssueLines);

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
