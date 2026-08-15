namespace Graft.Editor;

/// <summary>
/// インデントガイド（縦線、検討書「インデントガイド（縦線）」）の位置計算を担う純粋関数群。
/// AvaloniaEdit（<c>TextDocument</c>・<c>TextView</c>）に一切依存しないため、
/// <c>tests/Graft.Tests</c>から直接検証できる（実際の描画は<see cref="IndentGuideRenderer"/>が
/// この結果をピクセル位置へ変換する）。
///
/// Pane（github.com/Yu5rin/pane）の <c>src/editor.js</c>
/// （<c>lineIndentColumn</c>・<c>isLineDeeperThanLevel</c>・<c>collectActiveGuideSegments</c>）が
/// 試行錯誤の末にたどり着いた結論をそのまま踏襲する。移植するのは結論とアルゴリズムのみで、
/// コード自体はC#で書き直している。
/// </summary>
public static class IndentGuideCalculator
{
    /// <summary>
    /// 行頭の空白（半角スペース・タブ）の「表示上の列数」を求める。タブは1文字だが表示幅は
    /// タブ幅ぶんあるため、文字数ではなくタブストップに基づく列数で数える（検討書の必須要件）。
    /// 例: タブ幅4で"\t\tx"の先頭空白は列数8（4+4）。"  \tx"（スペース2つ+タブ）は列数4
    /// （スペース2つ分進んだ後、次のタブストップである4まで進む）。
    /// </summary>
    public static int LeadingWhitespaceVisualColumn(ReadOnlySpan<char> text, int tabSize)
    {
        if (tabSize <= 0) tabSize = 1; // 呼び出し側の設定不備に対する安全側の既定（0除算を避ける）。

        var column = 0;
        foreach (var ch in text)
        {
            if (ch == ' ')
            {
                column++;
            }
            else if (ch == '\t')
            {
                // 次のタブストップまで進める（タブ幅の倍数の列へ揃える）。
                column += tabSize - (column % tabSize);
            }
            else
            {
                break;
            }
        }
        return column;
    }

    /// <summary>
    /// 折りたたみ範囲1つぶんの「縦線を引く行の内側範囲」を求める（検討書の核心部分）。
    /// 開始行（ヘッダ行）は常に除外する（範囲自身のインデントより1段浅いため）。
    /// 終了行は機械的に「1つ前まで」とするのではなく、その行自身の表示インデントが
    /// <paramref name="baseIndentColumn"/>（範囲の基準インデント＝ヘッダ行の列数）より
    /// 深いかどうかで判定する:
    ///   - 深い（<paramref name="lastLineIndentColumn"/> &gt; baseIndentColumn）:
    ///     Pythonのようなインデントベース言語のブロック最終行に相当し、内側そのものなので含める。
    ///   - 深くない（同じか浅い）: C系言語の閉じ括弧行（`}`）に相当し、ヘッダ行と同じく
    ///     除外する。
    /// </summary>
    /// <param name="headerLine">折りたたみ範囲の開始行（1始まり）。</param>
    /// <param name="lastLineByOffset">
    /// 折りたたみのEndOffsetが指す行番号（1始まり）。headerLineと同じ、またはそれより前
    /// （不正な範囲）の場合はnullを返す。
    /// </param>
    /// <param name="baseIndentColumn">範囲の基準インデント列（ヘッダ行の表示インデント列）。</param>
    /// <param name="lastLineIndentColumn">
    /// <paramref name="lastLineByOffset"/>行の表示インデント列。その行が空行等でインデントの
    /// 判定に使えない場合はnullを渡すと、境界行として扱い除外する（安全側）。
    /// </param>
    /// <returns>線を引く行範囲（開始行, 終了行。両方とも1始まり、headerLineより後ろ）。
    /// 内側行が1行も無い場合はnull。</returns>
    public static (int Start, int End)? ComputeInteriorRange(
        int headerLine, int lastLineByOffset, int baseIndentColumn, int? lastLineIndentColumn)
    {
        if (lastLineByOffset <= headerLine) return null; // ヘッダ行だけの範囲は内側を持たない。

        var start = headerLine + 1;
        var end = lastLineIndentColumn is int column && column > baseIndentColumn
            ? lastLineByOffset // 終了行自身の実インデントが深い→内側の内容行そのもの（含める）。
            : lastLineByOffset - 1; // 境界行（閉じ括弧等）→除外する。

        return end >= start ? (start, end) : null;
    }

    /// <summary>
    /// 「すべてのインデント」モード用: 表示インデント列<paramref name="column"/>に対し、
    /// 縦線を引くべき階層の数を返す（0列目・<paramref name="indentUnit"/>列目・
    /// 2*indentUnit列目…と、column未満の階層すべて）。Pane（editor.js buildAllIndentGuides）の
    /// <c>Math.floor(col / indentSize)</c>と同じ考え方。
    /// </summary>
    public static int LevelCount(int column, int indentUnit)
    {
        if (indentUnit <= 0 || column <= 0) return 0;
        return column / indentUnit;
    }
}
