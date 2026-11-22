using System.Globalization;
using System.Text;

namespace Graft.Core.Vba;

/// <summary>
/// ブックの情報（<see cref="VbaWorkbookInfoSnapshot"/>）を、依頼文に載せる Markdown の文にする純ロジック
/// （仕様書_ブックの情報と依頼の観点 5.1・5.7）。COM にも UI にも触れない。
/// <para>
/// 【出す内容は 3 状態で決まる】 <see cref="VbaWorkbookInfoLevel.Full"/> はシート・テーブル・名前付き範囲に加えて
/// 先頭行の見出しと参照設定を、<see cref="VbaWorkbookInfoLevel.StructureOnly"/> は前 3 つだけを、
/// <see cref="VbaWorkbookInfoLevel.Hidden"/> は空の文字列を返す（見出しだけが残ることもない）。
/// 値の見本は<b>呼び出し側がオンにしたときだけ</b>（<c>includeSamples</c>）出す。スナップショットに見本が
/// 入っていても、オフなら出さない（オフにした直後の古い取得結果から漏らさないための二重の鍵）。
/// </para>
/// <para>
/// 【改行は LF】 この文は、依頼文のテンプレート展開と、コンテキスト収集の出力の両方に差し込まれる。
/// 差し込み側が自分の出力の改行（<c>ContextCollector</c> は環境の改行、テンプレートは本文の改行）へそろえるので、
/// ここは LF 固定で作る（<c>PromptFocusComposer</c> と同じ作法）。
/// </para>
/// <para>
/// 【上限】 出力全体が <c>maxChars</c> を超えるときは、<b>値の見本 → 見出し → 参照設定 → 名前付き範囲 →
/// テーブルの列名 → テーブル → シート</b>の順に、末尾の要素から 1 つずつ落として収める。構成（シート）ほど
/// 後まで残すのは、AI が「どのシートがあるか」を知らないまま書く害が、値の見本が無い害より大きいため。
/// 落としたら末尾に <see cref="TruncatedNote"/> を付ける。
/// </para>
/// </summary>
public static class VbaWorkbookInfoFormatter
{
    /// <summary>上限で一部を落としたときの注記（末尾）。</summary>
    public const string TruncatedNote = "（長いため一部を省略しました）";

    /// <summary>最新を読めずに前回の取得結果を使っているときの、見出し行の文言。</summary>
    public const string StaleNote = "最新を読めていません";

    /// <summary>
    /// 依頼文用の文にする。<see cref="VbaWorkbookInfoLevel.Hidden"/> なら空の文字列。
    /// </summary>
    /// <param name="snapshot">読み取り結果。</param>
    /// <param name="level">出し方の 3 状態。</param>
    /// <param name="includeSamples">値の見本を出すか（オプトイン。オフなら、スナップショットに見本があっても出さない）。</param>
    /// <param name="maxChars">出力全体の最大文字数（<c>Vba.WorkbookInfoMaxChars</c>）。</param>
    /// <param name="isStale">最新を読めず、前回の成功分を使っているか（見出し行に注記する）。</param>
    public static string Format(
        VbaWorkbookInfoSnapshot snapshot, VbaWorkbookInfoLevel level, bool includeSamples, int maxChars, bool isStale = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (level == VbaWorkbookInfoLevel.Hidden) return string.Empty;

        var full = level == VbaWorkbookInfoLevel.Full;
        var plan = new Plan
        {
            SampleSheets = includeSamples && full ? snapshot.Sheets.Count(s => s.SampleRows.Count > 0) : 0,
            HeaderSheets = full ? snapshot.Sheets.Count(s => s.UsedRange is not null && s.HeaderRow.Count > 0) : 0,
            References = full ? snapshot.References.Count : 0,
            Names = snapshot.Names.Count,
            TableColumns = snapshot.Tables.Count(t => t.Columns.Count > 0),
            Tables = snapshot.Tables.Count,
            Sheets = snapshot.Sheets.Count,
        };

        // 範囲参照でない式（定数など）を指す名前は、値の見本がオンのときだけ式を出す（IsRangeReference 参照）。
        plan.IncludeConstantNames = includeSamples && full;

        var text = Render(snapshot, plan, isStale, truncated: false);
        if (maxChars <= 0 || text.Length <= maxChars) return text;

        // 収まるまで、落とす順に 1 つずつ減らす。1 回の描画は小さい（数 KB）ので、最悪でも数百回の繰り返しで終わる。
        while (text.Length > maxChars && plan.CutOne())
        {
            text = Render(snapshot, plan, isStale, truncated: true);
        }

        return text;
    }

    /// <summary>どこまで出すか。落とす順に、数を 1 つずつ減らす。</summary>
    private sealed class Plan
    {
        /// <summary>値の見本を出すシート（見本を持つシートのうち先頭から数えて何枚か）。</summary>
        public int SampleSheets { get; set; }

        /// <summary>先頭行の見出しを出すシートの数。</summary>
        public int HeaderSheets { get; set; }

        public int References { get; set; }
        public int Names { get; set; }

        /// <summary>列名を出すテーブルの数（列名を持つテーブルのうち先頭から）。</summary>
        public int TableColumns { get; set; }

        public int Tables { get; set; }
        public int Sheets { get; set; }

        /// <summary>範囲参照でない名前の式（定数・数式）も出すか。値の見本と同じ鍵（オプトイン）で決める。</summary>
        public bool IncludeConstantNames { get; set; }

        /// <summary>1 つ落とす。もう落とせるものが無ければ false。</summary>
        public bool CutOne()
        {
            if (SampleSheets > 0) { SampleSheets--; return true; }
            if (HeaderSheets > 0) { HeaderSheets--; return true; }
            if (References > 0) { References--; return true; }
            if (Names > 0) { Names--; return true; }
            if (TableColumns > 0) { TableColumns--; return true; }
            if (Tables > 0) { Tables--; return true; }
            if (Sheets > 0) { Sheets--; return true; }
            return false;
        }
    }

    private static string Render(VbaWorkbookInfoSnapshot snapshot, Plan plan, bool isStale, bool truncated)
    {
        var sb = new StringBuilder();
        sb.Append("# ブックの情報\n");
        var readAt = snapshot.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        sb.Append(isStale
                ? $"（Excel から取得: {readAt} / {snapshot.WorkbookName}。{StaleNote}）"
                : $"（Excel から取得: {readAt} / {snapshot.WorkbookName}）")
            .Append('\n');

        var sheets = snapshot.Sheets.Take(plan.Sheets).ToArray();
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

        var tables = snapshot.Tables.Take(plan.Tables).ToArray();
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

        var names = snapshot.Names.Take(plan.Names).ToArray();
        if (names.Length > 0)
        {
            sb.Append("\n## 名前付き範囲\n");
            foreach (var name in names)
            {
                sb.Append("- ").Append(name.Name);
                // 式が範囲参照でないときは、値そのものが書かれていることがある。見本がオフなら名前だけにする。
                if (plan.IncludeConstantNames || VbaWorkbookInfoReadRules.IsRangeReference(name.RefersTo))
                {
                    sb.Append(" = ").Append(name.RefersTo);
                }

                sb.Append(name.Scope is null ? "（ブック全体）" : $"（シート「{name.Scope}」限定）")
                    .Append('\n');
            }
        }

        AppendHeaders(sb, snapshot, plan.HeaderSheets);

        var references = snapshot.References.Take(plan.References).ToArray();
        if (references.Length > 0)
        {
            sb.Append("\n## 参照設定\n- ");
            sb.Append(string.Join(" / ", references.Select(DescribeReference))).Append('\n');
        }

        AppendSamples(sb, snapshot, plan.SampleSheets);

        if (snapshot.Omitted.Count > 0)
        {
            // 読めなかったものを AI にも伝える。伝えないと「そのシートは無い」と誤解して書かれうる。
            sb.Append("\n## 読み取れなかったもの\n");
            foreach (var note in snapshot.Omitted) sb.Append("- ").Append(note).Append('\n');
        }

        if (truncated) sb.Append('\n').Append(TruncatedNote).Append('\n');
        return sb.ToString();
    }

    private static void AppendHeaders(StringBuilder sb, VbaWorkbookInfoSnapshot snapshot, int sheetLimit)
    {
        if (sheetLimit <= 0) return;

        var lines = new List<string>();
        foreach (var sheet in snapshot.Sheets)
        {
            if (lines.Count >= sheetLimit) break;
            if (sheet.UsedRange is null || sheet.HeaderRow.Count == 0) continue;

            var start = VbaWorkbookInfoReadRules.StartColumnOf(sheet.UsedRange);
            var cells = sheet.HeaderRow.Select((text, i) =>
                $"{VbaWorkbookInfoReadRules.ColumnLetters(start + i)} {(text.Length == 0 ? "（空）" : text)}");
            var line = $"- {sheet.Name}（{sheet.CodeName}）: {string.Join(" | ", cells)}";
            if (sheet.OmittedColumns > 0) line += $" | …（他 {sheet.OmittedColumns} 列）";
            lines.Add(line);
        }

        if (lines.Count == 0) return;
        sb.Append("\n## 先頭行の見出し\n");
        foreach (var line in lines) sb.Append(line).Append('\n');
    }

    private static void AppendSamples(StringBuilder sb, VbaWorkbookInfoSnapshot snapshot, int sheetLimit)
    {
        if (sheetLimit <= 0) return;

        var withSamples = snapshot.Sheets.Where(s => s.SampleRows.Count > 0).Take(sheetLimit).ToArray();
        if (withSamples.Length == 0) return;

        var rowCount = withSamples.Max(s => s.SampleRows.Count);
        // 行数は「見出し行に続く」見本の数（見出しは表の見出し行として別に出る）。「先頭 N 行」と書くと実際より 1 行少なく読める。
        sb.Append($"\n## 値の見本（各シートの見出し行に続く {rowCount} 行。AI に渡ります）\n");
        foreach (var sheet in withSamples)
        {
            sb.Append($"### {sheet.Name}（{sheet.CodeName}）\n");
            var width = Math.Max(sheet.HeaderRow.Count, sheet.SampleRows.Max(r => r.Count));
            var start = VbaWorkbookInfoReadRules.StartColumnOf(sheet.UsedRange);
            var header = Enumerable.Range(0, width).Select(i =>
                i < sheet.HeaderRow.Count && sheet.HeaderRow[i].Length > 0
                    ? Cell(sheet.HeaderRow[i])
                    : VbaWorkbookInfoReadRules.ColumnLetters(start + i));
            sb.Append("| ").Append(string.Join(" | ", header)).Append(" |\n");
            sb.Append('|').Append(string.Concat(Enumerable.Repeat("---|", width))).Append('\n');
            foreach (var row in sheet.SampleRows)
            {
                var cells = Enumerable.Range(0, width).Select(i => i < row.Count ? Cell(row[i]) : string.Empty);
                sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            }

            if (sheet.OmittedColumns > 0) sb.Append($"（他 {sheet.OmittedColumns} 列は省略）\n");
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
