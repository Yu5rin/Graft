using System.Globalization;
using System.Text;

namespace Graft.Core.Vba;

/// <summary>
/// ブックの情報（<see cref="VbaWorkbookInfoSnapshot"/>）を、依頼文に載せる Markdown の文にする純ロジック
/// （仕様書_ブックの情報と依頼の観点 5.1・5.7）。COM にも UI にも触れない。
/// <para>
/// 【出す内容は「全体の 3 状態」と「シートごとの見せ方」で決まる】 全体が <see cref="VbaWorkbookInfoLevel.Hidden"/> なら空の文字列
/// （シートごとの設定は無視）。<see cref="VbaWorkbookInfoLevel.Full"/> はシート・テーブル・名前付き範囲・参照設定に加えて、
/// シートごとの見せ方（<see cref="VbaSheetView"/>）で選ばれた値を出す。<see cref="VbaWorkbookInfoLevel.StructureOnly"/> は
/// シート・テーブル・名前付き範囲だけで、値は<b>どのシートでも</b>出さない（ただし「出さない」のシートは守る）。
/// スナップショットに値が入っていても、見せ方が値を出さないものなら出さない
/// （オフにした直後の古い取得結果から漏らさないための二重の鍵）。
/// </para>
/// <para>
/// 【「出さない」のシート】 シートの行・そのシートのテーブル・シート限定の名前・参照先がそのシートの名前付き範囲・
/// そのシートに触れる「読み取れなかったもの」の注記を、すべて落とす（「何も渡さない」の約束を、周辺の情報から崩さないため）。
/// </para>
/// <para>
/// 【改行は LF】 この文は、依頼文のテンプレート展開と、コンテキスト収集の出力の両方に差し込まれる。
/// 差し込み側が自分の出力の改行（<c>ContextCollector</c> は環境の改行、テンプレートは本文の改行）へそろえるので、
/// ここは LF 固定で作る（<c>PromptFocusComposer</c> と同じ作法）。
/// </para>
/// <para>
/// 【上限】 出力全体が <c>maxChars</c> を超えるときは、<b>シートの値（末尾のシートから）→ 参照設定 → 名前付き範囲 →
/// テーブルの列名 → テーブル → シート</b>の順に落として収める。構成（シート）ほど後まで残すのは、AI が
/// 「どのシートがあるか」を知らないまま書く害が、値が無い害より大きいため。値は 1 枚を丸ごと落とす前に、
/// 入るところまで行を減らす（「全部」の 500 行が上限を超えたとき、何も出ないより先頭の数十行が出るほうがよい）。
/// 落としたら末尾に <see cref="TruncatedNote"/> を付ける。
/// </para>
/// </summary>
public static class VbaWorkbookInfoFormatter
{
    /// <summary>上限で一部を落としたときの注記（末尾）。</summary>
    public const string TruncatedNote = "（長いため一部を省略しました）";

    /// <summary>最新を読めずに前回の取得結果を使っているときの、見出し行の文言。</summary>
    public const string StaleNote = "最新を読めていません";

    /// <summary>シートの値の節の見出し。</summary>
    public const string ValuesHeading = "## シートの値（AI に渡ります）";

    /// <summary>
    /// 依頼文用の文にする。<see cref="VbaWorkbookInfoLevel.Hidden"/> なら空の文字列。
    /// </summary>
    /// <param name="snapshot">読み取り結果。</param>
    /// <param name="level">全体の出し方の 3 状態。</param>
    /// <param name="views">シートごとの見せ方（コード名で引く）。</param>
    /// <param name="maxChars">出力全体の最大文字数（<c>Vba.WorkbookInfoMaxChars</c>）。</param>
    /// <param name="isStale">最新を読めず、前回の成功分を使っているか（見出し行に注記する）。</param>
    public static string Format(
        VbaWorkbookInfoSnapshot snapshot, VbaWorkbookInfoLevel level, VbaSheetViewSet views, int maxChars, bool isStale = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(views);
        if (level == VbaWorkbookInfoLevel.Hidden) return string.Empty;

        var effective = views.ForLevel(level);
        var full = level == VbaWorkbookInfoLevel.Full;

        // 「出さない」のシートとその周辺を落とした、出してよい部分。
        var excluded = snapshot.Sheets.Where(s => effective.For(s.CodeName).Mode == VbaSheetViewMode.None).ToArray();
        var visible = snapshot.Sheets.Except(excluded).ToArray();
        var tables = snapshot.Tables.Where(t => !excluded.Any(e => SameSheet(e.Name, t.SheetName))).ToArray();
        var names = snapshot.Names.Where(n => !IsTiedToExcluded(n, excluded)).ToArray();
        var omittedNotes = snapshot.Omitted.Where(o => !excluded.Any(e => o.Contains($"「{e.Name}」", StringComparison.Ordinal))).ToArray();

        var entries = full ? BuildValueEntries(visible, effective) : new List<ValueEntry>();
        var plan = new Plan
        {
            References = full ? snapshot.References.Count : 0,
            Names = names.Length,
            TableColumns = tables.Count(t => t.Columns.Count > 0),
            Tables = tables.Length,
            Sheets = visible.Length,
            // 範囲参照でない式（定数・数式）を指す名前の式は、値を渡すと選んだシートがあるときだけ出す
            // （値を渡す選択をしていないのに、名前の式から値が渡るのを避ける。IsRangeReference 参照）。
            IncludeConstantNames = entries.Count > 0,
        };

        var ctx = new Context(snapshot, visible, tables, names, omittedNotes, entries, isStale);
        var text = Render(ctx, plan, truncated: false);
        if (maxChars <= 0 || text.Length <= maxChars) return text;

        // 収まるまで落とす。まず値を末尾のシートから 1 枚ずつ。1 枚を落としたら収まるなら、その 1 枚は入る行数まで戻す。
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            entry.Dropped = true;
            if (Render(ctx, plan, truncated: true).Length > maxChars) continue;

            RestoreAsManyRowsAsFit(ctx, plan, entry, maxChars);
            return Render(ctx, plan, truncated: true);
        }

        // 値をすべて落としても収まらないときは、残りを 1 つずつ落とす。1 回の描画は小さい（数 KB）ので、最悪でも数百回で終わる。
        text = Render(ctx, plan, truncated: true);
        while (text.Length > maxChars && plan.CutOne())
        {
            text = Render(ctx, plan, truncated: true);
        }

        return text;
    }

    /// <summary>
    /// 落とした 1 枚の値を、収まる最大の行数まで戻す（2 分探索。描画の長さは行数に対して単調に増える）。
    /// 1 行も入らなければ、落としたままにする。
    /// </summary>
    private static void RestoreAsManyRowsAsFit(Context ctx, Plan plan, ValueEntry entry, int maxChars)
    {
        if (entry.Grid.Count == 0)
        {
            // 行の無い項目（指定の範囲が使用範囲の外）は、見出しと注記だけなので、余裕があれば戻す。
            entry.Dropped = false;
            if (Render(ctx, plan, truncated: true).Length > maxChars) entry.Dropped = true;
            return;
        }

        var low = 0;
        var high = entry.Grid.Count - 1; // 全行は収まらなかった（呼び出し側が確認済み）。
        entry.Dropped = false;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            entry.Keep = mid;
            if (Render(ctx, plan, truncated: true).Length <= maxChars) low = mid;
            else high = mid - 1;
        }

        entry.Keep = low;
        entry.Dropped = low == 0;
    }

    private static bool SameSheet(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>名前が「出さない」のシートに結び付いているか（シート限定、または参照先がそのシート）。</summary>
    private static bool IsTiedToExcluded(VbaNamedRangeInfo name, IReadOnlyList<VbaSheetInfo> excluded)
        => excluded.Any(e =>
            (name.Scope is not null && SameSheet(name.Scope, e.Name))
            || VbaWorkbookInfoReadRules.RefersToSheet(name.RefersTo, e.Name));

    /// <summary>描画に使う、変わらない材料。</summary>
    private sealed record Context(
        VbaWorkbookInfoSnapshot Snapshot, IReadOnlyList<VbaSheetInfo> Sheets, IReadOnlyList<VbaTableInfo> Tables,
        IReadOnlyList<VbaNamedRangeInfo> Names, IReadOnlyList<string> Omitted, IReadOnlyList<ValueEntry> Entries, bool IsStale);

    /// <summary>値を出す 1 枚のシート。<see cref="Keep"/>・<see cref="Dropped"/> は、上限で収めるときに減らす。</summary>
    private sealed class ValueEntry
    {
        public required string Heading { get; init; }

        /// <summary>見出し＋N 行（先頭行が見出しの表）か、「全部」「範囲を指定」（行番号つきの表）か。</summary>
        public required bool IsHeaderStyle { get; init; }

        /// <summary>出す行（見出し＋N 行のときは先頭が見出し）。</summary>
        public required IReadOnlyList<IReadOnlyList<string>> Grid { get; init; }

        public required int StartRow { get; init; }
        public required int StartColumn { get; init; }

        /// <summary>常に添える注記（切った・範囲の外など。切った理由と「範囲を指定」の案内を含む）。</summary>
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

        public int Keep { get; set; }
        public bool Dropped { get; set; }
    }

    private static List<ValueEntry> BuildValueEntries(IReadOnlyList<VbaSheetInfo> sheets, VbaSheetViewSet views)
    {
        var entries = new List<ValueEntry>();
        foreach (var sheet in sheets)
        {
            var view = views.For(sheet.CodeName);
            if (!view.PassesValues) continue;

            var title = $"### {sheet.Name}（{sheet.CodeName}）: ";
            switch (view.Mode)
            {
                case VbaSheetViewMode.HeaderRows:
                {
                    if (sheet.UsedRange is null || sheet.HeaderRow.Count == 0) break;

                    // 取得時の行数ではなく、いまの設定の行数までに絞る（行数を減らした直後の古い結果から余分に出さない）。
                    var body = sheet.SampleRows.Take(VbaSheetViewLimits.ClampHeaderRows(view.HeaderRows)).ToArray();
                    var grid = new List<IReadOnlyList<string>> { sheet.HeaderRow };
                    grid.AddRange(body);
                    var notes = new List<string>();
                    if (sheet.OmittedColumns > 0) notes.Add($"（他 {sheet.OmittedColumns} 列は省略）");
                    entries.Add(new ValueEntry
                    {
                        Heading = $"{title}見出し＋{body.Length} 行",
                        IsHeaderStyle = true,
                        Grid = grid,
                        StartRow = StartRowOf(sheet.UsedRange),
                        StartColumn = VbaWorkbookInfoReadRules.StartColumnOf(sheet.UsedRange),
                        Notes = notes,
                        Keep = grid.Count,
                    });
                    break;
                }

                case VbaSheetViewMode.All or VbaSheetViewMode.Range:
                {
                    // 古い取得結果を使わない: 見せ方と一致しないかたまり（範囲を変えた直後など）は出さない。
                    if (sheet.Block is not { } block || block.Mode != view.Mode) break;
                    if (view.Mode == VbaSheetViewMode.Range
                        && !string.Equals(block.Requested, view.Range, StringComparison.OrdinalIgnoreCase)) break;

                    entries.Add(new ValueEntry
                    {
                        Heading = view.Mode == VbaSheetViewMode.All
                            ? $"{title}全部"
                            : $"{title}範囲 {block.Requested}",
                        IsHeaderStyle = false,
                        Grid = block.Rows,
                        StartRow = block.StartRow,
                        StartColumn = block.StartColumn,
                        Notes = BlockNotes(sheet, block),
                        Keep = block.Rows.Count,
                    });
                    break;
                }
            }
        }

        return entries;
    }

    private static int StartRowOf(string? address)
        => VbaA1Range.FromAddress(address) is { } range ? range.Row1 : 1;

    /// <summary>
    /// 「全部」「範囲を指定」の注記。<b>上限で切ったときは、何行・何列のうち何行・何列を出したかを言い、「範囲を指定」を案内する</b>
    /// （AI が「これで全部」と誤解しないように。続きが要るなら、利用者が範囲を選んで取り直せる）。
    /// </summary>
    private static IReadOnlyList<string> BlockNotes(VbaSheetInfo sheet, VbaSheetBlock block)
    {
        var notes = new List<string>();
        if (block.OutsideUsedRange)
        {
            notes.Add($"（指定の範囲 {block.Requested} は、このシートの使用範囲 {sheet.UsedRange ?? "（空）"} の外で、値はありません）");
            return notes;
        }

        if (block.RowsCut || block.ColumnsCut)
        {
            var shown = block.Address ?? string.Empty;
            notes.Add(
                $"（切りました: 対象は {block.TotalRows} 行・{block.TotalColumns} 列ありますが、先頭の {block.Rows.Count} 行・{block.ColumnCount} 列（{shown}）だけを出しています。"
                + "続きが必要なときは、このシートの見せ方を「範囲を指定」にして、読みたい範囲（例 "
                + SuggestNextRange(block) + "）を入力してください）");
        }
        else if (block.Mode == VbaSheetViewMode.Range && block.Address is { } address
                 && !string.Equals(address, block.Requested, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"（使用範囲との共通部分 {address} を出しています）");
        }

        return notes;
    }

    /// <summary>続きを読むための範囲の例。行を切ったなら、切った次の行から同じ幅。列だけなら、切った次の列から同じ行。</summary>
    private static string SuggestNextRange(VbaSheetBlock block)
    {
        if (block.RowsCut)
        {
            var row1 = block.StartRow + block.Rows.Count;
            var row2 = Math.Min(VbaA1Range.MaxRow, row1 + Math.Max(1, block.Rows.Count) - 1);
            var col2 = block.StartColumn + block.ColumnCount - 1;
            return new VbaA1Range(row1, block.StartColumn, row2, col2).ToA1();
        }

        var col1 = block.StartColumn + block.ColumnCount;
        var col2b = Math.Min(VbaA1Range.MaxColumn, col1 + Math.Max(1, block.ColumnCount) - 1);
        return new VbaA1Range(block.StartRow, col1, block.StartRow + Math.Max(1, block.Rows.Count) - 1, col2b).ToA1();
    }

    /// <summary>どこまで出すか。値は <see cref="ValueEntry"/> 側で持ち、ここは残りの要素。落とす順に、数を 1 つずつ減らす。</summary>
    private sealed class Plan
    {
        public int References { get; set; }
        public int Names { get; set; }

        /// <summary>列名を出すテーブルの数（列名を持つテーブルのうち先頭から）。</summary>
        public int TableColumns { get; set; }

        public int Tables { get; set; }
        public int Sheets { get; set; }

        /// <summary>範囲参照でない名前の式（定数・数式）も出すか。値を渡す選択と同じ鍵（オプトイン）で決める。</summary>
        public bool IncludeConstantNames { get; set; }

        /// <summary>1 つ落とす。もう落とせるものが無ければ false。</summary>
        public bool CutOne()
        {
            if (References > 0) { References--; return true; }
            if (Names > 0) { Names--; return true; }
            if (TableColumns > 0) { TableColumns--; return true; }
            if (Tables > 0) { Tables--; return true; }
            if (Sheets > 0) { Sheets--; return true; }
            return false;
        }
    }

    private static string Render(Context ctx, Plan plan, bool truncated)
    {
        var snapshot = ctx.Snapshot;
        var sb = new StringBuilder();
        sb.Append("# ブックの情報\n");
        var readAt = snapshot.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        sb.Append(ctx.IsStale
                ? $"（Excel から取得: {readAt} / {snapshot.WorkbookName}。{StaleNote}）"
                : $"（Excel から取得: {readAt} / {snapshot.WorkbookName}）")
            .Append('\n');

        var sheets = ctx.Sheets.Take(plan.Sheets).ToArray();
        if (sheets.Length > 0)
        {
            sb.Append("\n## シート\n");
            sb.Append("| シート名 | コード名 | 表示 | 使用範囲 |\n");
            sb.Append("|---|---|---|---|\n");
            foreach (var sheet in sheets)
            {
                sb.Append("| ").Append(Cell(sheet.Name))
                    .Append(" | ").Append(Cell(sheet.CodeName))
                    .Append(" | ").Append(VisibilityLabel(sheet.Visibility))
                    .Append(" | ").Append(sheet.UsedRange is null ? "（空）" : Cell(sheet.UsedRange))
                    .Append(" |\n");
            }
        }

        var tables = ctx.Tables.Take(plan.Tables).ToArray();
        if (tables.Length > 0)
        {
            sb.Append("\n## テーブル\n");
            var withColumns = 0;
            foreach (var table in tables)
            {
                sb.Append("- ").Append(table.Name)
                    .Append("（シート「").Append(table.SheetName).Append("」 ").Append(table.Range).Append('）');
                if (table.Columns.Count > 0 && withColumns < plan.TableColumns)
                {
                    withColumns++;
                    sb.Append(": ").Append(string.Join(", ", table.Columns));
                }

                sb.Append('\n');
            }
        }

        var names = ctx.Names.Take(plan.Names).ToArray();
        if (names.Length > 0)
        {
            sb.Append("\n## 名前付き範囲\n");
            foreach (var name in names)
            {
                sb.Append("- ").Append(name.Name);
                // 式が範囲参照でないときは、値そのものが書かれていることがある。値を渡す選択が無ければ名前だけにする。
                if (plan.IncludeConstantNames || VbaWorkbookInfoReadRules.IsRangeReference(name.RefersTo))
                {
                    sb.Append(" = ").Append(name.RefersTo);
                }

                sb.Append(name.Scope is null ? "（ブック全体）" : $"（シート「{name.Scope}」限定）")
                    .Append('\n');
            }
        }

        var references = snapshot.References.Take(plan.References).ToArray();
        if (references.Length > 0)
        {
            sb.Append("\n## 参照設定\n- ");
            sb.Append(string.Join(" / ", references.Select(DescribeReference))).Append('\n');
        }

        AppendValues(sb, ctx.Entries);

        if (ctx.Omitted.Count > 0)
        {
            // 読めなかったものを AI にも伝える。伝えないと「そのシートは無い」と誤解して書かれうる。
            sb.Append("\n## 読み取れなかったもの\n");
            foreach (var note in ctx.Omitted) sb.Append("- ").Append(note).Append('\n');
        }

        if (truncated) sb.Append('\n').Append(TruncatedNote).Append('\n');
        return sb.ToString();
    }

    private static void AppendValues(StringBuilder sb, IReadOnlyList<ValueEntry> entries)
    {
        var shown = entries.Where(e => !e.Dropped).ToArray();
        if (shown.Length == 0) return;

        sb.Append('\n').Append(ValuesHeading).Append('\n');
        foreach (var entry in shown)
        {
            sb.Append(entry.Heading).Append('\n');
            var rows = entry.Grid.Take(entry.Keep).ToArray();
            if (rows.Length > 0)
            {
                if (entry.IsHeaderStyle) AppendHeaderStyleTable(sb, entry, rows);
                else AppendBlockTable(sb, entry, rows);
            }

            foreach (var note in entry.Notes) sb.Append(note).Append('\n');
            if (entry.Keep < entry.Grid.Count)
            {
                var total = entry.IsHeaderStyle ? entry.Grid.Count - 1 : entry.Grid.Count;
                var kept = entry.IsHeaderStyle ? Math.Max(0, entry.Keep - 1) : entry.Keep;
                sb.Append(entry.IsHeaderStyle
                        ? $"（文字数の上限のため、見出しに続く {total} 行のうち {kept} 行だけを出しています）"
                        : $"（文字数の上限のため、{total} 行のうち先頭の {kept} 行だけを出しています）")
                    .Append('\n');
            }
        }
    }

    /// <summary>見出し＋N 行: 先頭行を表の見出しにする（空の見出しは列の文字）。</summary>
    private static void AppendHeaderStyleTable(StringBuilder sb, ValueEntry entry, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var header = rows[0];
        var width = Math.Max(header.Count, rows.Max(r => r.Count));
        var headerCells = Enumerable.Range(0, width).Select(i =>
            i < header.Count && header[i].Length > 0
                ? Cell(header[i])
                : VbaWorkbookInfoReadRules.ColumnLetters(entry.StartColumn + i));
        sb.Append("| ").Append(string.Join(" | ", headerCells)).Append(" |\n");
        sb.Append('|').Append(string.Concat(Enumerable.Repeat("---|", width))).Append('\n');
        foreach (var row in rows.Skip(1))
        {
            var cells = Enumerable.Range(0, width).Select(i => i < row.Count ? Cell(row[i]) : string.Empty);
            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
        }
    }

    /// <summary>
    /// 全部・範囲を指定: 先頭の列は行番号、見出しの行は列の文字。範囲の先頭が見出し行とは限らないので、
    /// 先頭行を表の見出しにはせず、行番号と列の文字で「どのセルか」を AI に示す（A1 形式で指示を書きやすくなる）。
    /// </summary>
    private static void AppendBlockTable(StringBuilder sb, ValueEntry entry, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var width = rows.Max(r => r.Count);
        var letters = Enumerable.Range(0, width).Select(i => VbaWorkbookInfoReadRules.ColumnLetters(entry.StartColumn + i));
        sb.Append("| 行 | ").Append(string.Join(" | ", letters)).Append(" |\n");
        sb.Append("|---|").Append(string.Concat(Enumerable.Repeat("---|", width))).Append('\n');
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var cells = Enumerable.Range(0, width).Select(i => i < row.Count ? Cell(row[i]) : string.Empty);
            sb.Append("| ").Append(entry.StartRow + r).Append(" | ").Append(string.Join(" | ", cells)).Append(" |\n");
        }
    }

    private static string DescribeReference(VbaReferenceInfo reference)
    {
        var detail = reference.IsBuiltIn
            ? "組み込み"
            : string.Join("、", new[] { reference.Description, reference.FullPath }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
        var text = detail.Length == 0 ? reference.Name : $"{reference.Name}（{detail}）";
        return reference.IsBroken ? text + "【参照切れ】" : text;
    }

    private static string VisibilityLabel(VbaSheetVisibility visibility) => visibility switch
    {
        VbaSheetVisibility.Hidden => "非表示",
        VbaSheetVisibility.VeryHidden => "VeryHidden",
        _ => "表示",
    };

    /// <summary>Markdown の表のセル用。縦棒は表の区切りと紛れるので逃がす。</summary>
    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
