using System.Text;
using System.Text.RegularExpressions;

namespace Graft.Core.Vba;

/// <summary>
/// ブックの情報を読む <c>WindowsVbaBridge.Info</c> が使う、COM に触れない判定と変換
/// （仕様書_ブックの情報と依頼の観点 5.5）。COM の呼び出し順・解放は実機でしか確かめられないので、
/// 確かめられるものはここへ切り出して <c>Graft.Tests</c> で固めている。
/// </summary>
public static class VbaWorkbookInfoReadRules
{
    /// <summary>テーブル数の上限（仕様書 5.5 の表）。</summary>
    public const int MaxTables = 100;

    /// <summary>1 表の列名の上限。</summary>
    public const int MaxTableColumns = 60;

    /// <summary>名前付き範囲の上限。</summary>
    public const int MaxNames = 200;

    /// <summary>参照設定の上限。</summary>
    public const int MaxReferences = 100;

    /// <summary>
    /// <c>Range.Value2</c> が返した値を、行×列の文字列の表にする。
    /// <list type="bullet">
    /// <item>複数セルの範囲は <c>object[,]</c>（<b>下限 1</b>）で返る。下限は 0 のこともあるので、
    /// <see cref="Array.GetLowerBound(int)"/> で読む。</item>
    /// <item>1 セルだけの範囲は、配列ではなくスカラーが返る。1×1 の表にする。</item>
    /// <item>null（空のセル 1 つ）は 1×1 の空文字の表。</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ToGrid(object? value2, int maxCellChars)
        => ToGrid(value2, null, maxCellChars);

    /// <summary>
    /// <see cref="ToGrid(object?, int)"/> の数式つき版。<paramref name="formulas"/>（同じ範囲の <c>Formula</c>）が
    /// 非 null のとき、<c>=</c> で始まるセルを <c>=式 → 値</c> にする。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ToGrid(object? value2, object? formulas, int maxCellChars)
    {
        if (value2 is not Array values || values.Rank != 2)
        {
            var text = formulas is null
                ? VbaCellText.Render(value2, maxCellChars)
                : VbaCellText.RenderWithFormula(FirstOf(formulas), value2, maxCellChars);
            return new[] { new[] { text } };
        }

        var rowLow = values.GetLowerBound(0);
        var rowHigh = values.GetUpperBound(0);
        var colLow = values.GetLowerBound(1);
        var colHigh = values.GetUpperBound(1);
        var formulaGrid = formulas as Array;
        var rows = new List<IReadOnlyList<string>>(Math.Max(0, rowHigh - rowLow + 1));
        for (var r = rowLow; r <= rowHigh; r++)
        {
            var cells = new List<string>(Math.Max(0, colHigh - colLow + 1));
            for (var c = colLow; c <= colHigh; c++)
            {
                var value = values.GetValue(r, c);
                if (formulaGrid is { Rank: 2 } && TryGetAligned(formulaGrid, r - rowLow, c - colLow, out var formula))
                {
                    cells.Add(VbaCellText.RenderWithFormula(formula, value, maxCellChars));
                }
                else
                {
                    cells.Add(VbaCellText.Render(value, maxCellChars));
                }
            }

            rows.Add(cells);
        }

        return rows;
    }

    private static object? FirstOf(object? formulas)
        => formulas is Array { Rank: 2 } grid && grid.Length > 0
            ? grid.GetValue(grid.GetLowerBound(0), grid.GetLowerBound(1))
            : formulas;

    private static bool TryGetAligned(Array grid, int rowOffset, int colOffset, out object? value)
    {
        var r = grid.GetLowerBound(0) + rowOffset;
        var c = grid.GetLowerBound(1) + colOffset;
        if (r <= grid.GetUpperBound(0) && c <= grid.GetUpperBound(1))
        {
            value = grid.GetValue(r, c);
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// 見出しの行の右側を切る。<paramref name="row"/> が <paramref name="maxColumns"/> を超えるとき、
    /// 切った列数を <paramref name="omitted"/> に返す。
    /// </summary>
    public static IReadOnlyList<string> LimitColumns(IReadOnlyList<string> row, int maxColumns, out int omitted)
    {
        if (maxColumns <= 0 || row.Count <= maxColumns)
        {
            omitted = 0;
            return row;
        }

        omitted = row.Count - maxColumns;
        return row.Take(maxColumns).ToArray();
    }

    /// <summary>列番号（1 始まり）を列の文字（1→A、27→AA、16384→XFD）にする。</summary>
    public static string ColumnLetters(int column)
    {
        if (column < 1) return string.Empty;
        var builder = new StringBuilder();
        while (column > 0)
        {
            column--;
            builder.Insert(0, (char)('A' + column % 26));
            column /= 26;
        }

        return builder.ToString();
    }

    /// <summary>列の文字を列番号（1 始まり）にする。読めなければ 0。</summary>
    public static int ColumnNumber(string letters)
    {
        if (string.IsNullOrEmpty(letters)) return 0;
        var number = 0;
        foreach (var ch in letters)
        {
            var upper = char.ToUpperInvariant(ch);
            if (upper is < 'A' or > 'Z') return 0;
            number = number * 26 + (upper - 'A' + 1);
        }

        return number;
    }

    /// <summary>
    /// 範囲のアドレス（<c>A1:F120</c>・<c>$B$2:$D$9</c>・<c>C3</c>）の左端の列番号。読めなければ 1。
    /// 見出しの行に列の文字（A、B…）を付けるとき、使用範囲が A 列から始まらなくても実際の列文字にするために使う。
    /// </summary>
    public static int StartColumnOf(string? address)
    {
        if (string.IsNullOrEmpty(address)) return 1;
        var index = 0;
        if (address[index] == '$') index++;
        var start = index;
        while (index < address.Length && char.IsAsciiLetter(address[index])) index++;
        var number = ColumnNumber(address[start..index]);
        return number > 0 ? number : 1;
    }

    /// <summary>
    /// <c>Worksheet.Visible</c>（<c>xlSheetVisible</c> = -1、<c>xlSheetHidden</c> = 0、<c>xlSheetVeryHidden</c> = 2）を表示状態にする。
    /// 未知の値は、隠れているとは断定せず表示として扱う（AI に「無い」と誤解させないため）。
    /// </summary>
    public static VbaSheetVisibility VisibilityFromExcel(int xlSheetVisibility) => xlSheetVisibility switch
    {
        0 => VbaSheetVisibility.Hidden,
        2 => VbaSheetVisibility.VeryHidden,
        _ => VbaSheetVisibility.Visible,
    };

    /// <summary>
    /// <c>Name.Name</c> の文字列を、スコープと名前に分ける。シート限定の名前は <c>Sheet1!税率</c>・<c>'売上 2026'!税率</c>
    /// の形で返る（<c>Parent</c> の COM 呼び出しを増やさずに、スコープが分かる）。ブック全体の名前は <c>!</c> を含まない。
    /// </summary>
    public static (string? Scope, string Name) SplitName(string rawName)
    {
        ArgumentNullException.ThrowIfNull(rawName);

        // シート名が '...' で引用されている場合、その中に ! や ' があってもよい（' は '' に重ねる）。
        if (rawName.StartsWith('\''))
        {
            var index = 1;
            var scope = new StringBuilder();
            while (index < rawName.Length)
            {
                if (rawName[index] == '\'')
                {
                    if (index + 1 < rawName.Length && rawName[index + 1] == '\'')
                    {
                        scope.Append('\'');
                        index += 2;
                        continue;
                    }

                    break;
                }

                scope.Append(rawName[index]);
                index++;
            }

            if (index + 1 < rawName.Length && rawName[index] == '\'' && rawName[index + 1] == '!')
            {
                return (scope.ToString(), rawName[(index + 2)..]);
            }
        }

        var bang = rawName.IndexOf('!', StringComparison.Ordinal);
        return bang > 0 ? (rawName[..bang], rawName[(bang + 1)..]) : (null, rawName);
    }

    // セル参照 1 つ分: A1・$A$1・A1:B2・A:A・1:3。シート名の接頭辞（Sheet1!・'売上 2026'!）が付くこともある。
    private const string CellPart = @"\$?[A-Za-z]{1,3}\$?\d+(?::\$?[A-Za-z]{1,3}\$?\d+)?|\$?[A-Za-z]{1,3}:\$?[A-Za-z]{1,3}|\$?\d+:\$?\d+";
    private const string SheetPrefix = @"(?:'(?:[^']|'')+'|[^'!\[\],:=()\s]+)!";
    private static readonly Regex RangeReferencePart = new(
        $@"^(?:{SheetPrefix})?(?:{CellPart})$|^(?:{SheetPrefix})?#REF!$|^[\p{{L}}_\\][\p{{L}}\p{{N}}_.]*\[[^\]]*\](?:\[[^\]]*\])*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 名前付き範囲の <c>RefersTo</c>（<c>=Sheet1!$A$1:$B$9</c> など）が、セル範囲への<b>参照だけ</b>でできているか。
    /// 参照なら、それは「どこを指すか」という構造の情報で、値そのものではない。
    /// 参照でない（<c>="パスワード"</c>・<c>=0.08</c>・<c>={1,2,3}</c>・数式）ときは、式の中に<b>値そのもの</b>が書かれている。
    /// 名前付き範囲に定数を入れる使い方は珍しくなく（税率・社名・接続文字列など）、見本がオフでも
    /// 依頼文へ出てしまうと、見本のオプトイン（確認ダイアログ）を迂回して値が渡る。呼び出し側は、これが false の名前の式を
    /// 値の見本がオンのときだけ出す。判定は「確実に参照と分かるものだけ true」に倒す（迷うものは値を伏せる側）。
    /// <para>
    /// 複数領域（<c>=Sheet1!$A$1,Sheet1!$C$1</c>）と、括弧でくくった形（<c>=(Sheet1!$A$1,Sheet1!$C$1)</c>）、
    /// テーブルの構造化参照（<c>=Table1[列名]</c>）は参照として扱う。関数を含む式（<c>=OFFSET(...)</c> など）は、
    /// 引数に定数を持ちうるので参照とは見ない（動的な範囲の式は、見本がオンのときだけ出る）。
    /// </para>
    /// </summary>
    public static bool IsRangeReference(string? refersTo)
    {
        if (string.IsNullOrWhiteSpace(refersTo)) return false;
        var text = refersTo.Trim();
        if (text.StartsWith('=')) text = text[1..].Trim();
        if (text.Length >= 2 && text[0] == '(' && text[^1] == ')') text = text[1..^1];
        if (text.Length == 0) return false;

        // カンマ（複数領域）で分ける。引用されたシート名の中のカンマは分けない。
        foreach (var part in SplitTopLevel(text, ','))
        {
            if (!RangeReferencePart.IsMatch(part.Trim())) return false;
        }

        return true;
    }

    private static IEnumerable<string> SplitTopLevel(string text, char separator)
    {
        var start = 0;
        var inQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\'') inQuote = !inQuote;
            else if (text[i] == separator && !inQuote)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }

    /// <summary>
    /// 利用者が作った名前ではなく、Excel が内部で持つ名前（印刷範囲・印刷タイトル・オートフィルターの
    /// <c>_xlnm.*</c>、<c>_FilterDatabase</c>）か。名前付き範囲の一覧から除く。
    /// </summary>
    public static bool IsInternalName(string name)
    {
        var (_, bare) = SplitName(name);
        return bare.StartsWith("_xlnm.", StringComparison.OrdinalIgnoreCase)
               || bare.Equals("_FilterDatabase", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 時間の予算を使い切ったか。シートの合間に見て、超えていたらそこまで読めた分を返す（仕様書 5.5「時間切れ」）。
    /// <paramref name="budget"/> が 0 以下のときは打ち切らない（予算を設けない呼び出し）。
    /// </summary>
    public static bool IsOverBudget(TimeSpan elapsed, TimeSpan budget) => budget > TimeSpan.Zero && elapsed >= budget;

    /// <summary>時間切れで読めなかったシートの説明（<see cref="VbaWorkbookInfoSnapshot.Omitted"/> に入れる文）。</summary>
    public static string OmittedByBudgetMessage(string firstUnreadSheetName)
        => $"時間がかかったため、シート「{firstUnreadSheetName}」以降は読めていません。";

    /// <summary>
    /// 1 枚のシートの読み取りが失敗したときの説明。<paramref name="sheetListed"/> は、見出し・見本を読めてシートを一覧に
    /// 足したあとか。テーブルの読み取りだけが失敗したのに「シートは読めませんでした」と言うと、一覧にあるシートを
    /// AI が「無い」と誤解しかねないので、何が読めなかったかを正確に言い分ける。
    /// </summary>
    public static string OmittedSheetMessage(string sheetName, bool sheetListed, int hresult)
        => sheetListed
            ? $"シート「{sheetName}」のテーブルは読めませんでした（0x{hresult:X8}。シートの見出しなどは読めています）。"
            : $"シート「{sheetName}」は読めませんでした（0x{hresult:X8}）。";

    /// <summary>
    /// 名前付き範囲の読み取りが失敗したときの説明。<paramref name="readCount"/> が 1 以上なら、途中までは読めて
    /// 一覧に載っている（全く読めなかったと言うと、載っている名前まで無いように聞こえる）。
    /// </summary>
    public static string OmittedNamesMessage(int readCount, int hresult)
        => readCount > 0
            ? $"名前付き範囲は途中までしか読めませんでした（読めたのは {readCount} 件。0x{hresult:X8}）。"
            : $"名前付き範囲は読めませんでした（0x{hresult:X8}）。";

    /// <summary>シート数の上限で省いたときの説明。</summary>
    public static string OmittedByMaxSheetsMessage(int omittedCount, int maxSheets)
        => $"シートが多いため、先頭の {maxSheets} 枚だけを読みました（他 {omittedCount} 枚）。";

    /// <summary>参照設定を読めなかったときの説明（E802／E803 相当）。</summary>
    public const string OmittedReferencesMessage =
        "参照設定は読めませんでした（トラストセンターで「VBA プロジェクト オブジェクト モデルへのアクセスを信頼する」を許可するか、VBA プロジェクトのロックを解除してください）。";
}
