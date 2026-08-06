using AvaloniaEdit;
using AvaloniaEdit.Document;
using Graft.Core;

namespace Graft.Editor;

/// <summary>
/// 行操作・コメント切替・指定行への移動（4.4節）。<see cref="TextEditor"/>インスタンスを直接
/// 受け取って動作する静的メソッド群とし、ViewModelやEditorPaneへの組み込みは統合担当が行う
/// （E3ブリーフの制約により<c>Views/EditorPane.xaml.cs</c>は変更しない）。
/// 複数回の<see cref="TextDocument"/>操作は<see cref="UndoStack.StartUndoGroup()"/>で
/// 1回のCtrl+Zにまとめる。
/// v2.0のWPF版（AvalonEdit）からの移植。TextEditor/TextDocumentのAPIはAvaloniaEditでも
/// 同名同形のため、名前空間の差し替えのみで移植できる。
/// </summary>
public static class EditorCommands
{
    /// <summary>Shift+Alt+↓: 現在行（または選択範囲の行）を直下へ複製する。</summary>
    public static void DuplicateLines(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var doc = editor.Document;
        var (startLine, endLine) = GetSelectedLineRange(editor);
        var startOffset = doc.GetLineByNumber(startLine).Offset;
        var endDocLine = doc.GetLineByNumber(endLine);
        var blockEnd = endDocLine.Offset + endDocLine.TotalLength;
        var block = doc.GetText(startOffset, blockEnd - startOffset);
        var hasDelimiter = endDocLine.DelimiterLength > 0;

        doc.Insert(blockEnd, hasDelimiter ? block : "\n" + block);

        var lineCount = endLine - startLine + 1;
        editor.TextArea.Caret.Line = Math.Min(doc.LineCount, editor.TextArea.Caret.Line + lineCount);
    }

    /// <summary>Alt+↑: 現在行（または選択範囲の行）を1行上へ移動する。</summary>
    public static void MoveLinesUp(TextEditor editor) => MoveLines(editor, direction: -1);

    /// <summary>Alt+↓: 現在行（または選択範囲の行）を1行下へ移動する。</summary>
    public static void MoveLinesDown(TextEditor editor) => MoveLines(editor, direction: 1);

    /// <summary>Ctrl+Shift+K: 現在行（または選択範囲の行）を削除する。</summary>
    public static void DeleteLines(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var doc = editor.Document;
        var (startLine, endLine) = GetSelectedLineRange(editor);
        var startDocLine = doc.GetLineByNumber(startLine);
        var endDocLine = doc.GetLineByNumber(endLine);
        var (removeStart, removeEnd) = ResolveDeleteRange(doc, startDocLine, endDocLine);

        doc.Remove(removeStart, removeEnd - removeStart);

        editor.TextArea.Caret.Line = Math.Clamp(startLine, 1, Math.Max(1, doc.LineCount));
        editor.TextArea.Caret.Column = 1;
    }

    /// <summary>Ctrl+G: 指定行（1始まり）へ移動する。範囲外は安全側へ丸める。</summary>
    public static void GoToLine(TextEditor editor, int lineNumber)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var doc = editor.Document;
        var clamped = Math.Clamp(lineNumber, 1, Math.Max(1, doc.LineCount));
        editor.TextArea.Caret.Line = clamped;
        editor.TextArea.Caret.Column = 1;
        editor.ScrollToLine(clamped);
        editor.TextArea.Caret.BringCaretToView();
    }

    /// <summary>
    /// Ctrl+/: 言語ルールの行コメント記号でコメント切替を行う（4.4節）。選択範囲があれば
    /// その全行を対象にし、空白以外の行がすべてコメント済みなら解除、そうでなければ付与する。
    /// 行コメント記号を持たない言語（<paramref name="rule"/>がnull、または空）では何もしない。
    /// </summary>
    public static void ToggleLineComment(TextEditor editor, LanguageRule? rule)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (rule is null || rule.LineCommentPrefixes.Count == 0) return;
        var prefix = rule.LineCommentPrefixes[0];

        var doc = editor.Document;
        var (startLine, endLine) = GetSelectedLineRange(editor);
        var lines = new List<DocumentLine>();
        for (var n = startLine; n <= endLine; n++) lines.Add(doc.GetLineByNumber(n));

        var nonBlank = lines.Where(l => !IsBlank(doc, l)).ToList();
        var allCommented = nonBlank.Count > 0 && nonBlank.All(l => IsLineCommented(doc, l, prefix));

        doc.UndoStack.StartUndoGroup();
        try
        {
            // 末尾側の行から処理し、挿入・削除によるオフセットのズレが前方の行へ影響しないようにする。
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (allCommented) RemoveCommentPrefix(doc, lines[i], prefix);
                else AddCommentPrefix(doc, lines[i], prefix);
            }
        }
        finally
        {
            doc.UndoStack.EndUndoGroup();
        }
    }

    private static void MoveLines(TextEditor editor, int direction)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var doc = editor.Document;
        var (startLine, endLine) = GetSelectedLineRange(editor);
        if (direction < 0 && startLine <= 1) return;
        if (direction > 0 && endLine >= doc.LineCount) return;

        doc.UndoStack.StartUndoGroup();
        try
        {
            if (direction < 0) SwapAdjacent(doc, startLine - 1, startLine - 1, startLine, endLine);
            else SwapAdjacent(doc, startLine, endLine, endLine + 1, endLine + 1);
        }
        finally
        {
            doc.UndoStack.EndUndoGroup();
        }

        editor.TextArea.Caret.Line = Math.Clamp(editor.TextArea.Caret.Line + direction, 1, doc.LineCount);
    }

    /// <summary>
    /// aStart..aEnd の直後に続く bStart..bEnd（隣接する2ブロック）の内容を入れ替える。
    /// 境界の改行コードはaブロック末尾のものを、bブロックの末尾側の改行（無い場合は空）は
    /// そのまま維持することで、末尾行（改行なし）が絡む移動でも改行の数を変えない。
    /// </summary>
    private static void SwapAdjacent(TextDocument doc, int aStart, int aEnd, int bStart, int bEnd)
    {
        var aFirstOffset = doc.GetLineByNumber(aStart).Offset;
        var aLastLine = doc.GetLineByNumber(aEnd);
        var bLastLine = doc.GetLineByNumber(bEnd);

        var boundaryOffset = aLastLine.Offset + aLastLine.Length;
        var boundaryDelimiter = doc.GetText(boundaryOffset, aLastLine.DelimiterLength);
        var bContentEnd = bLastLine.Offset + bLastLine.Length;
        var bTrailing = doc.GetText(bContentEnd, bLastLine.DelimiterLength);

        var aContent = doc.GetText(aFirstOffset, boundaryOffset - aFirstOffset);
        var bContentStart = boundaryOffset + boundaryDelimiter.Length;
        var bContent = doc.GetText(bContentStart, bContentEnd - bContentStart);

        var totalEnd = bContentEnd + bTrailing.Length;
        var replacement = bContent + boundaryDelimiter + aContent + bTrailing;
        doc.Replace(aFirstOffset, totalEnd - aFirstOffset, replacement);
    }

    private static (int Start, int End) ResolveDeleteRange(TextDocument doc, DocumentLine startLine, DocumentLine endLine)
    {
        if (endLine.NextLine is not null)
        {
            return (startLine.Offset, endLine.NextLine.Offset);
        }
        if (startLine.PreviousLine is not null)
        {
            var prev = startLine.PreviousLine;
            return (prev.Offset + prev.Length, endLine.Offset + endLine.Length);
        }
        return (0, doc.TextLength);
    }

    private static bool IsBlank(TextDocument doc, DocumentLine line)
        => string.IsNullOrWhiteSpace(doc.GetText(line.Offset, line.Length));

    private static bool IsLineCommented(TextDocument doc, DocumentLine line, string prefix)
        => doc.GetText(line.Offset, line.Length).TrimStart().StartsWith(prefix, StringComparison.Ordinal);

    private static void AddCommentPrefix(TextDocument doc, DocumentLine line, string prefix)
    {
        if (IsBlank(doc, line)) return;
        var text = doc.GetText(line.Offset, line.Length);
        var indent = text.Length - text.TrimStart().Length;
        doc.Insert(line.Offset + indent, prefix);
    }

    private static void RemoveCommentPrefix(TextDocument doc, DocumentLine line, string prefix)
    {
        var text = doc.GetText(line.Offset, line.Length);
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)) return;
        var indent = text.Length - trimmed.Length;
        doc.Remove(line.Offset + indent, prefix.Length);
    }

    /// <summary>選択範囲があればその行範囲、無ければ現在行を返す（1始まり）。選択末尾がちょうど
    /// 次行の先頭にある場合、その行は選択に含めない（行単位選択の慣習に合わせる）。</summary>
    private static (int Start, int End) GetSelectedLineRange(TextEditor editor)
    {
        if (editor.SelectionLength <= 0)
        {
            var line = editor.TextArea.Caret.Line;
            return (line, line);
        }

        var doc = editor.Document;
        var selectionEnd = editor.SelectionStart + editor.SelectionLength;
        var start = doc.GetLineByOffset(editor.SelectionStart).LineNumber;
        var endLine = doc.GetLineByOffset(selectionEnd);
        var end = endLine.LineNumber;
        if (end > start && selectionEnd == endLine.Offset) end--;
        return (start, end);
    }
}
