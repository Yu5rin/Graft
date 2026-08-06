using System.Windows.Input;
using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// diffの1セル（行の片側）を表す。並列表示では左＝変更前・右＝変更後として使い、
/// 統合表示では1セルのみを使う。生成後に値が変化しない読み取り専用の表示用モデル。
/// </summary>
public sealed class DiffCellViewModel
{
    public DiffCellViewModel(DiffLineKind kind, int? oldLine, int? newLine, string text,
        IReadOnlyList<InlineSpan> inlineSpans, IReadOnlyList<SyntaxToken> tokens, string automationName)
    {
        Kind = kind;
        OldLineText = oldLine?.ToString() ?? string.Empty;
        NewLineText = newLine?.ToString() ?? string.Empty;
        Text = text;
        InlineSpans = inlineSpans;
        Tokens = tokens;
        AutomationName = automationName;
    }

    /// <summary>行種別。背景色（8.3）とシンタックス上の重ね合わせ規則（8.6）の切り替えに使う。</summary>
    public DiffLineKind Kind { get; }

    /// <summary>変更前の行番号表示。存在しない場合は空文字。</summary>
    public string OldLineText { get; }

    /// <summary>変更後の行番号表示。存在しない場合は空文字。</summary>
    public string NewLineText { get; }

    /// <summary>行の内容。</summary>
    public string Text { get; }

    /// <summary>文字単位ハイライト範囲（8.3）。</summary>
    public IReadOnlyList<InlineSpan> InlineSpans { get; }

    /// <summary>シンタックストークン（8.6）。</summary>
    public IReadOnlyList<SyntaxToken> Tokens { get; }

    /// <summary>スクリーンリーダー向けの読み上げ文言（8.14）。</summary>
    public string AutomationName { get; }

    /// <summary>
    /// 追加行かどうか（8.3の背景色・左端バーの切り替え用）。UI側で
    /// <see cref="Kind"/> と特定値を突き合わせる条件分岐（WPFのDataTrigger等）を
    /// 書かずに済むよう、真偽値として公開する。
    /// </summary>
    public bool IsAdded => Kind == DiffLineKind.Added;

    /// <summary>削除行かどうか（8.3）。<see cref="IsAdded"/>と同じ理由で公開する。</summary>
    public bool IsRemoved => Kind == DiffLineKind.Removed;

    /// <summary>内容を持たない空セル（並列表示で対応する側が存在しない行）を生成する。</summary>
    public static DiffCellViewModel Blank { get; }
        = new(DiffLineKind.Unchanged, null, null, string.Empty,
            Array.Empty<InlineSpan>(), Array.Empty<SyntaxToken>(), "空白");
}

/// <summary>
/// diffの1行分の表示行を表す。並列表示では <see cref="Left"/>（変更前側）と
/// <see cref="Right"/>（変更後側）を両方持ち、統合表示では <see cref="Left"/> のみを使う。
/// 折りたたまれた省略行（<see cref="IsOmitted"/>）はクリックで段階的に展開する
/// （仕様書8.13）ための <see cref="ExpandCommand"/> を持つ。
/// </summary>
public sealed class DiffLineViewModel
{
    public DiffLineViewModel(DiffCellViewModel left, DiffCellViewModel? right, ICommand? expandCommand)
    {
        Left = left;
        Right = right;
        ExpandCommand = expandCommand;
        RowAutomationName = ComposeRowAutomationName(left, right);
    }

    /// <summary>変更前側（統合表示では唯一のセル）。</summary>
    public DiffCellViewModel Left { get; }

    /// <summary>変更後側。並列表示でのみ意味を持つ（統合表示では null）。</summary>
    public DiffCellViewModel? Right { get; }

    /// <summary>折りたたまれた省略行かどうか。</summary>
    public bool IsOmitted => Left.Kind == DiffLineKind.Omitted;

    /// <summary>省略行を段階的に展開するコマンド。省略行以外では null。</summary>
    public ICommand? ExpandCommand { get; }

    /// <summary>行全体の読み上げ文言（8.14）。一覧コンテナの AutomationProperties.Name に割り当てる。</summary>
    public string RowAutomationName { get; }

    private static string ComposeRowAutomationName(DiffCellViewModel left, DiffCellViewModel? right)
    {
        if (right is null || ReferenceEquals(right, DiffCellViewModel.Blank) || right.Text == left.Text)
        {
            return left.AutomationName;
        }

        if (ReferenceEquals(left, DiffCellViewModel.Blank))
        {
            return right.AutomationName;
        }

        return $"{left.AutomationName} / {right.AutomationName}";
    }
}
