using AvaloniaEdit;
using AvaloniaEdit.Document;

namespace Graft.Editor;

/// <summary>
/// Markdown編集支援（検討書「Markdownの編集支援」）: リスト・引用のEnter継続/脱出、表のTab/Enter移動を
/// <see cref="TextEditor"/>へ適用する。判定そのものは<see cref="MarkdownBlockContinuation"/>・
/// <see cref="MarkdownTableCalculator"/>の純粋ロジックに委ね、本クラスは「その結果を1回の
/// <see cref="TextDocument.Replace(int,int,string)"/>で書き込む」薄い橋渡しに徹する
/// （<see cref="EditorCommands"/>と同じ、TextEditorを直接受け取る作法）。
///
/// 呼び出し側（<c>EditorPane</c>）は、対象タブが<c>.md</c>で、かつMarkdownプレビュー表示中で
/// **ない**（編集モードである）ときだけ<see cref="HandleEnter"/>・<see cref="HandleTab"/>を呼ぶこと。
/// プレビュー表示中はEditor自体が非表示（<c>Editor.IsVisible = false</c>）でキー入力が
/// そもそも届かないが、呼び出し側の判定でも二重に保証する（クラスコメントの安全側の方針）。
/// </summary>
public static class MarkdownEditingSupport
{
    /// <summary>
    /// Enterキー。表の最終セルでの行追加、リストの継続/脱出のいずれかに該当すれば処理して
    /// <c>true</c>を返す。該当しなければ何もせず<c>false</c>を返す（呼び出し側は通常のEnterへ
    /// フォールバックする）。
    /// </summary>
    public static bool HandleEnter(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!editor.TextArea.Selection.IsEmpty) return false; // 選択があるときは通常のEnterに委ねる。

        var doc = editor.Document;
        var caret = editor.CaretOffset;
        var docLine = doc.GetLineByOffset(caret);
        var lineIndex = docLine.LineNumber - 1;
        var lineText = doc.GetText(docLine.Offset, docLine.Length);
        var colInLine = caret - docLine.Offset;
        var before = lineText[..colInLine];
        var after = lineText[colInLine..];
        var source = new DocumentLineSource(doc);

        if (MarkdownTableCalculator.TryFindTableAt(source, lineIndex) is { } table)
        {
            // 表の中では、リスト/引用としての継続・脱出判定は行わない（セルの'|'を誤認しないため）。
            // 特別扱いするのは「表の最終行の最終セルでEnter」＝行追加のときだけで、それ以外の
            // 表内Enterは素通しする（Pane handleTableKeyと同じ設計。行の途中で改行すると表の記法が
            // 崩れるが、そこは利用者の選択に委ねる）。
            var (rowKind, column) = MarkdownTableCalculator.LocateCursor(table, lineIndex, before);
            if (rowKind != lineIndex - table.StartLine) return false; // 安全側（本来常に一致する）。
            if (lineIndex != table.EndLine || column < table.ColumnCount - 1) return false;

            AppendTableRow(editor, table);
            return true;
        }

        var continuation = MarkdownBlockContinuation.ComputeListContinuationMarker(before);
        if (continuation is not null)
        {
            doc.Insert(caret, "\n" + continuation);
            editor.CaretOffset = caret + 1 + continuation.Length;
            editor.TextArea.Caret.BringCaretToView();
            return true;
        }

        if (after.Trim().Length != 0) return false; // カーソルの後ろに本文が残るなら通常のEnterへ。

        var ctx = MarkdownBlockContinuation.ComputeExitContext(source, lineIndex);
        if (ctx is not { Levels.Count: > 0 } value) return false;
        if (colInLine < value.ConsumedWidth) return false; // カーソルがまだマーカーの途中。

        var newPrefix = MarkdownBlockContinuation.RenderShallowerPrefix(value.Levels);
        var exitingQuote = MarkdownBlockContinuation.ExitsQuoteCompletely(value.Levels);
        var insert = exitingQuote ? "\n" + newPrefix : newPrefix;
        doc.Replace(docLine.Offset, caret - docLine.Offset, insert);
        editor.CaretOffset = docLine.Offset + insert.Length;
        editor.TextArea.Caret.BringCaretToView();
        return true;
    }

    /// <summary>
    /// Tab/Shift+Tab。カーソルが表の中にあるときだけセル移動として処理し<c>true</c>を返す。
    /// 表の外（通常のMarkdown本文）では何もせず<c>false</c>を返し、既定のTab（インデント挿入等）に委ねる
    /// （利用者指示: <c>.md</c>以外は元よりTabの意味を一切変えないが、<c>.md</c>であっても表の外では
    /// 変えない）。
    /// </summary>
    public static bool HandleTab(TextEditor editor, bool shift)
    {
        ArgumentNullException.ThrowIfNull(editor);
        // HandleEnterと違い、選択の有無では判定しない。表の中でセルを移動すると
        // SelectCellが移動先のセル内容を選択状態にする（次の入力でセルの中身を
        // そのまま置き換えられるようにするため）ため、もし選択中はTabを諦める
        // ようにすると、1回目のTabで選択が作られた直後の2回目以降のTab/Shift+Tabが
        // 常に「選択があるから」と抜けてしまい、セル間を連続移動できなくなる
        // （実機のXvfb操作テストで発覚: 2回目のTabが無反応になっていた）。
        // 表の中にいる限りTabは常にセル移動を意味する、という利用者指示のとおり、
        // 選択状態にかかわらず表の中かどうかだけで判定する。

        var doc = editor.Document;
        var caret = editor.CaretOffset;
        var docLine = doc.GetLineByOffset(caret);
        var lineIndex = docLine.LineNumber - 1;
        var source = new DocumentLineSource(doc);
        if (MarkdownTableCalculator.TryFindTableAt(source, lineIndex) is not { } table) return false;

        var lineText = doc.GetText(docLine.Offset, docLine.Length);
        var before = lineText[..(caret - docLine.Offset)];
        var (rowKind, column) = MarkdownTableCalculator.LocateCursor(table, lineIndex, before);
        var (moved, nextRow, nextColumn) = MarkdownTableCalculator.NextCell(table, rowKind, column, shift);
        // 表の端（Shift+Tabで先頭セルより前・Tabで最終セルより後）では、キーを消費だけして何もしない。
        // 表の中でだけTabの意味を変える、という利用者指示のとおり「意味を変えた」結果として
        // 「何もしない」も含まれる（既定のインデント挿入を表の中で発生させないため）。
        if (moved) SelectCell(editor, table, nextRow, nextColumn);
        return true;
    }

    private static void AppendTableRow(TextEditor editor, MarkdownTable table)
    {
        var doc = editor.Document;
        var updated = MarkdownTableCalculator.AppendEmptyRow(table);
        var text = MarkdownTableCalculator.FormatTableText(updated);
        var startOffset = doc.GetLineByNumber(table.StartLine + 1).Offset;
        var endDocLine = doc.GetLineByNumber(table.EndLine + 1);
        var endOffset = endDocLine.Offset + endDocLine.Length;

        doc.UndoStack.StartUndoGroup();
        try
        {
            doc.Replace(startOffset, endOffset - startOffset, text);
        }
        finally
        {
            doc.UndoStack.EndUndoGroup();
        }

        SelectCell(editor, updated, updated.EndLine - updated.StartLine, 0);
    }

    private static void SelectCell(TextEditor editor, MarkdownTable table, int rowKind, int column)
    {
        var doc = editor.Document;
        var docLine = doc.GetLineByNumber(table.StartLine + rowKind + 1);
        var text = doc.GetText(docLine.Offset, docLine.Length);
        var (start, end) = MarkdownTableCalculator.CellSpanInLine(text, column);
        editor.Select(docLine.Offset + start, end - start);
        editor.TextArea.Caret.BringCaretToView();
    }

    /// <summary><see cref="TextDocument"/>を<see cref="IMarkdownLineSource"/>として見せるアダプタ。
    /// 行を要求されたときだけ<c>GetText</c>する（10万行級の文書でも触れた行だけにコストが依存する）。</summary>
    private sealed class DocumentLineSource(TextDocument document) : IMarkdownLineSource
    {
        public int LineCount => document.LineCount;

        public string GetLine(int index)
        {
            var line = document.GetLineByNumber(index + 1);
            return document.GetText(line.Offset, line.Length);
        }
    }
}
