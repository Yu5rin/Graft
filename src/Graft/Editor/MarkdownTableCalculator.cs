using System.Text;
using System.Text.RegularExpressions;

namespace Graft.Editor;

/// <summary>表の列揃え（<c>:---</c> 等の区切り行から読み取る）。</summary>
public enum MarkdownTableAlign
{
    None,
    Left,
    Center,
    Right,
}

/// <summary>検出済みのMarkdown表1件。行番号はすべて0始まり。</summary>
public sealed class MarkdownTable
{
    public required int StartLine { get; init; } // 見出し行
    public required int SeparatorLine { get; init; } // 区切り行（StartLine+1）
    public required int EndLine { get; init; } // 表の最終行（本文が無ければSeparatorLineと同じ）
    public required List<string> Header { get; init; }
    public required List<MarkdownTableAlign> Aligns { get; init; }
    public required List<List<string>> Body { get; init; }

    public int ColumnCount => Math.Max(Header.Count, Body.Count == 0 ? 1 : Body.Max(r => r.Count));
}

/// <summary>
/// Markdown表編集支援（検討書「Markdownの編集支援」）の位置計算・整形を担う純粋関数群。
/// AvaloniaEditに一切依存しないため<c>tests/Graft.Tests</c>から直接検証できる
/// （<see cref="IndentGuideCalculator"/>と同じ「計算はここ、AvaloniaEditへの反映は別クラス」という
/// このプロジェクトの作法に倣った）。
///
/// GraftにはMarkdown構文木が無いため、Pane（<c>@lezer/markdown</c>のTableノード）のように構文木を
/// 辿ることはできない。代わりに「区切り行（<c>|---|---|</c>等）の直前が見出し行、直後から空行や
/// 非表形式の行までが本文行」というGFM表の形そのものをテキストから直接判定する
/// （<see cref="TryFindTableAt"/>）。移植するのは列移動・行追加のアルゴリズムと整形方針
/// （Pane <c>editor.js</c>の<c>splitCells</c>・<c>formatTableText</c>・<c>handleTableKey</c>）で、
/// コード自体はC#で書き直している。
/// </summary>
public static class MarkdownTableCalculator
{
    private static readonly Regex SeparatorRowPattern =
        new(@"^\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?$", RegexOptions.Compiled);

    /// <summary>行が表の行らしい形（<c>|</c>を含む）かどうか。</summary>
    private static bool LooksLikeRow(string line) => line.Contains('|');

    private static bool IsSeparatorRow(string line)
    {
        var t = line.Trim();
        return t.Length > 0 && t.Contains('-') && SeparatorRowPattern.IsMatch(t);
    }

    /// <summary>セル文字列へ分割する（前後の<c>|</c>を落とし、各セルをtrimする）。</summary>
    public static List<string> SplitCells(string rowText)
    {
        var s = rowText.Trim();
        if (s.StartsWith('|')) s = s[1..];
        if (s.EndsWith('|')) s = s[..^1];
        return s.Split('|').Select(c => c.Trim()).ToList();
    }

    private static MarkdownTableAlign ParseAlign(string sep)
    {
        var left = sep.StartsWith(':');
        var right = sep.EndsWith(':');
        return (left, right) switch
        {
            (true, true) => MarkdownTableAlign.Center,
            (false, true) => MarkdownTableAlign.Right,
            (true, false) => MarkdownTableAlign.Left,
            _ => MarkdownTableAlign.None,
        };
    }

    /// <summary>
    /// <paramref name="lineIndex"/>を含む表を探す。見出し行の直後が区切り行という形そのものを
    /// 手がかりにするため、まず<c>|</c>を含む行が連続する範囲へ広げ、その範囲内で
    /// 「見出し行の直後が区切り行」という並びを探す。見つからなければnull。
    /// </summary>
    public static MarkdownTable? TryFindTableAt(IMarkdownLineSource source, int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= source.LineCount) return null;

        var top = lineIndex;
        while (top > 0 && LooksLikeRow(source.GetLine(top - 1))) top--;
        var bottom = lineIndex;
        while (bottom < source.LineCount - 1 && LooksLikeRow(source.GetLine(bottom + 1))) bottom++;
        if (!LooksLikeRow(source.GetLine(lineIndex))) { top = lineIndex; bottom = lineIndex; }

        // [top, bottom] の範囲内で「見出し行の直後が区切り行」という並びを探す。
        // lineIndexがどの行にあっても、その行を含む表がこの範囲のどこかで見つかるはず。
        for (var headerLine = top; headerLine < bottom; headerLine++)
        {
            if (headerLine + 1 > bottom) break;
            if (lineIndex < headerLine) continue; // カーソル行より後ろから始まる表は対象外。
            if (!IsSeparatorRow(source.GetLine(headerLine + 1))) continue;
            // 本文行はheaderLine+2から、|を含む行が続く限り。
            var bodyEnd = headerLine + 1;
            while (bodyEnd + 1 <= bottom && LooksLikeRow(source.GetLine(bodyEnd + 1))) bodyEnd++;
            if (lineIndex > bodyEnd) continue; // カーソル行はこの表の範囲より後ろ＝別の表を探す。

            var header = SplitCells(source.GetLine(headerLine));
            var aligns = SplitCells(source.GetLine(headerLine + 1)).Select(ParseAlign).ToList();
            var body = new List<List<string>>();
            for (var i = headerLine + 2; i <= bodyEnd; i++) body.Add(SplitCells(source.GetLine(i)));

            return new MarkdownTable
            {
                StartLine = headerLine,
                SeparatorLine = headerLine + 1,
                EndLine = bodyEnd,
                Header = header,
                Aligns = aligns,
                Body = body,
            };
        }
        return null;
    }

    /// <summary>表内の行番号（0=見出し, 1=区切り, 2以降=本文）と、その行の中でカーソルが何列目に
    /// いるか（0始まり、列数でクランプ）を求める。<paramref name="textBeforeCaret"/>はカーソル行の
    /// カーソルより前の部分（'|'の出現数をそのまま列番号として使う。Pane <c>handleTableKey</c>と
    /// 同じ考え方）。</summary>
    public static (int RowKind, int Column) LocateCursor(MarkdownTable table, int lineIndex, string textBeforeCaret)
    {
        var rowKind = lineIndex - table.StartLine;
        var cols = table.ColumnCount;
        var pipesBefore = textBeforeCaret.Count(c => c == '|');
        return (rowKind, Math.Clamp(pipesBefore - 1, 0, Math.Max(0, cols - 1)));
    }

    /// <summary>
    /// Tab/Shift+Tabでの次/前セルの行種別・列を求める。<paramref name="shift"/>がtrueなら逆方向。
    /// 表の最後（最終行の最終セルでTab、または先頭行の先頭セルでShift+Tab）では現在位置のまま
    /// （<c>Moved=false</c>）を返す。
    /// </summary>
    public static (bool Moved, int RowKind, int Column) NextCell(MarkdownTable table, int rowKind, int column, bool shift)
    {
        var cols = table.ColumnCount;
        var r = rowKind == 1 ? 2 : rowKind; // 区切り行には止まらない。
        var nc = column + (shift ? -1 : 1);
        var nr = r;
        if (!shift && nc >= cols) { nc = 0; nr = r == 0 ? 2 : r + 1; }
        if (shift && nc < 0)
        {
            if (r == 0) return (false, rowKind, column);
            nr = r == 2 ? 0 : r - 1;
            nc = cols - 1;
        }
        if (!shift && nr >= 2 && nr - 2 >= table.Body.Count) return (false, rowKind, column); // 最終セルのTabは何もしない。
        return (true, nr, nc);
    }

    /// <summary>
    /// Enterを最終行の最終セルで押したときのための、行を1つ追加した新しい<see cref="MarkdownTable"/>
    /// を返す（追加した行の内容は空文字列の列がColumnCount個）。それ以外の位置での呼び出しは想定しない
    /// （呼び出し側が「最終行・最終セルかどうか」を先に判定する。Pane <c>handleTableKey</c>と同じ設計）。
    /// </summary>
    public static MarkdownTable AppendEmptyRow(MarkdownTable table)
    {
        var cols = table.ColumnCount;
        var newBody = new List<List<string>>(table.Body) { Enumerable.Repeat(string.Empty, cols).ToList() };
        return new MarkdownTable
        {
            StartLine = table.StartLine,
            SeparatorLine = table.SeparatorLine,
            EndLine = table.EndLine + 1,
            Header = table.Header,
            Aligns = table.Aligns,
            Body = newBody,
        };
    }

    // 全角文字を2、それ以外を1として数える表示幅（Pane editor.js の dispW と同じ考え方）。
    private static int DisplayWidth(string s)
    {
        var w = 0;
        foreach (var rune in s.EnumerateRunes()) w += rune.Value > 0xFF ? 2 : 1;
        return w;
    }

    private static string PadCell(string s, int width, MarkdownTableAlign align)
    {
        var gap = Math.Max(0, width - DisplayWidth(s));
        return align switch
        {
            MarkdownTableAlign.Right => new string(' ', gap) + s,
            MarkdownTableAlign.Center => new string(' ', gap / 2) + s + new string(' ', gap - gap / 2),
            _ => s + new string(' ', gap),
        };
    }

    /// <summary>列幅を揃えたMarkdownテキストを生成する（<see cref="TryFindTableAt"/>が返した表を
    /// そのままテキストへ書き戻すための整形。Pane <c>formatTableText</c>と同じアルゴリズム）。</summary>
    public static string FormatTableText(MarkdownTable table)
    {
        var cols = table.ColumnCount;
        List<string> Norm(List<string> row) => Enumerable.Range(0, cols).Select(i => i < row.Count ? row[i] : string.Empty).ToList();

        var header = Norm(table.Header);
        var body = table.Body.Select(Norm).ToList();
        var aligns = Enumerable.Range(0, cols).Select(i => i < table.Aligns.Count ? table.Aligns[i] : MarkdownTableAlign.None).ToList();
        var widths = Enumerable.Range(0, cols)
            .Select(i => new[] { 3, DisplayWidth(header[i]) }.Concat(body.Select(r => DisplayWidth(r[i]))).Max())
            .ToList();

        string Row(List<string> r) => "| " + string.Join(" | ", r.Select((c, i) => PadCell(c, widths[i], aligns[i]))) + " |";
        string Sep()
        {
            var parts = Enumerable.Range(0, cols).Select(i =>
            {
                var w = widths[i];
                return aligns[i] switch
                {
                    MarkdownTableAlign.Center => ":" + new string('-', Math.Max(1, w - 2)) + ":",
                    MarkdownTableAlign.Right => new string('-', Math.Max(1, w - 1)) + ":",
                    MarkdownTableAlign.Left => ":" + new string('-', Math.Max(1, w - 1)),
                    _ => new string('-', w),
                };
            });
            return "| " + string.Join(" | ", parts) + " |";
        }

        var lines = new List<string> { Row(header), Sep() };
        lines.AddRange(body.Select(Row));
        return string.Join("\n", lines);
    }

    /// <summary>表内の1行のテキストから、指定列（0始まり）の「トリム済みセル内容」の文字範囲
    /// （行頭からのオフセット）を求める。セルが存在しない列（本文行が短い等）は行末を返す。</summary>
    public static (int Start, int End) CellSpanInLine(string lineText, int column)
    {
        var i = lineText.IndexOf('|') + 1;
        var cell = 0;
        while (cell < column)
        {
            var p = lineText.IndexOf('|', i);
            if (p < 0) break;
            i = p + 1;
            cell++;
        }
        var j = lineText.IndexOf('|', i);
        if (j < 0) j = lineText.Length;
        var seg = lineText[i..j];

        // 空セル（整形後の空白ぶんのパディングのみ）は要注意: seg全体が空白だと
        // TrimStart()もTrimEnd()も""になり、素朴に「先頭からの空白数」「末尾からの空白数」を
        // 別々に数えると同じ空白を二重に数えてしまい、start > end（負の長さ）になってしまう
        // （行追加直後の空セルを選択しようとして実際にクラッシュした不具合の修正）。
        // 中身が空なら、幅0の位置（先頭側に寄せる）を返す。
        if (seg.Trim().Length == 0)
        {
            var empty = i + Math.Min(1, seg.Length); // "| " の直後（1文字の余白ぶんは避ける）。
            return (empty, empty);
        }

        var lead = seg.Length - seg.TrimStart().Length;
        var trail = seg.Length - seg.TrimEnd().Length;
        return (i + lead, j - trail);
    }
}
