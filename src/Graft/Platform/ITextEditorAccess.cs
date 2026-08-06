namespace Graft.Platform;

/// <summary>
/// ViewModelから編集中のテキストエディタを操作するための抽象（仕様書v2.1 19章・20章 L3）。
/// エディタ内検索・置換（4.4節）は選択位置の変更やスクロールといったUI操作を伴うため、
/// ViewModel層がエディタコントロールの型（WPF版は<c>ICSharpCode.AvalonEdit.TextEditor</c>、
/// Avalonia版は<c>AvaloniaEdit.TextEditor</c>）を直接参照してしまうと、ViewModelを
/// 両プラットフォームで共有できなくなる。必要な操作だけをこの抽象に切り出し、
/// 各UI側でアダプタを実装する。
/// </summary>
public interface ITextEditorAccess
{
    /// <summary>編集中の全文。検索対象のテキストとして使う。</summary>
    string Text { get; }

    /// <summary>キャレット位置（文字オフセット）。</summary>
    int CaretOffset { get; }

    /// <summary>指定範囲を選択する。</summary>
    void Select(int offset, int length);

    /// <summary>指定オフセットを含む行が見えるようスクロールする。</summary>
    void ScrollToOffset(int offset);

    /// <summary>指定範囲を置き換える。</summary>
    void Replace(int offset, int length, string replacement);

    /// <summary>
    /// 以降の編集を1つのUndo単位にまとめ始める。「すべて置換」を1回のCtrl+Zで
    /// 元に戻せるようにするために使う。<see cref="EndUndoGroup"/>と必ず対で呼ぶこと。
    /// </summary>
    void BeginUndoGroup();

    /// <summary><see cref="BeginUndoGroup"/>で開始したUndo単位を閉じる。</summary>
    void EndUndoGroup();
}
