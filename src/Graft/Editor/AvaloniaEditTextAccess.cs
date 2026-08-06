using AvaloniaEdit;
using Graft.Platform;

namespace Graft.Editor;

/// <summary>
/// Avalonia版（AvaloniaEdit）の<see cref="ITextEditorAccess"/>アダプタ。
/// v2.0のWPF版<c>AvalonEditTextAccess</c>と同じ役割で、参照するTextEditorの名前空間だけが異なる
/// （仕様書v2.1 19章 L3）。
/// </summary>
public sealed class AvaloniaEditTextAccess : ITextEditorAccess
{
    private readonly TextEditor _editor;

    public AvaloniaEditTextAccess(TextEditor editor)
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
