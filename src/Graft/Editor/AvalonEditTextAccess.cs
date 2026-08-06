using Graft.Platform;
using ICSharpCode.AvalonEdit;

namespace Graft.Editor;

/// <summary>
/// WPF版（AvalonEdit）の<see cref="ITextEditorAccess"/>アダプタ。
/// <see cref="SearchOverlayViewModel"/>がエディタコントロールの型に依存しないようにするための
/// 薄いラッパで、ロジックは持たない（仕様書v2.1 19章 L3）。
/// </summary>
public sealed class AvalonEditTextAccess : ITextEditorAccess
{
    private readonly TextEditor _editor;

    public AvalonEditTextAccess(TextEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
    }

    public string Text => _editor.Document.Text;

    public int CaretOffset => _editor.CaretOffset;

    public void Select(int offset, int length) => _editor.Select(offset, length);

    public void ScrollToOffset(int offset)
        => _editor.ScrollToLine(_editor.Document.GetLineByOffset(offset).LineNumber);

    public void Replace(int offset, int length, string replacement)
        => _editor.Document.Replace(offset, length, replacement);

    public void BeginUndoGroup() => _editor.Document.UndoStack.StartUndoGroup();

    public void EndUndoGroup() => _editor.Document.UndoStack.EndUndoGroup();
}
