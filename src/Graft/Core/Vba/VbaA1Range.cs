using System.Text.RegularExpressions;

namespace Graft.Core.Vba;

/// <summary>
/// セル範囲（行・列の矩形）。「範囲を指定」の入力の検証と、読む範囲の計算（使用範囲との共通部分・上限での切り詰め）に使う
/// 純ロジック（仕様書_ブックの情報と依頼の観点 5.2・5.5）。COM には触れない。
/// <para>
/// 【受け付ける書き方】 <c>A1</c>・<c>A1:F30</c>（<c>$</c> と小文字と前後の空白は許す）、列範囲 <c>A:F</c>、行範囲 <c>3:10</c>。
/// 向きが逆（<c>F30:A1</c>）なら正規化する（Excel も同じ扱い）。
/// </para>
/// <para>
/// 【受け付けない書き方】 <b>名前付き範囲</b>（<c>売上</c>・<c>税率</c>）は不可。名前は Excel に問い合わせないと範囲が決まらず、
/// 別のシートや別のブックを指していることもあり、「このシートの値だけを渡す」という選択の意味が崩れるため。
/// シート名つき（<c>Sheet1!A1</c>）と複数領域（<c>A1:B2,D1:E2</c>）も不可（範囲は、見せ方を選んだシートのものだけ）。
/// 入力欄では、何が不可かを理由つきで知らせる。
/// </para>
/// </summary>
/// <param name="Row1">先頭行（1 始まり）。</param>
/// <param name="Column1">先頭列（1 始まり）。</param>
/// <param name="Row2">末尾行。</param>
/// <param name="Column2">末尾列。</param>
public readonly record struct VbaA1Range(int Row1, int Column1, int Row2, int Column2)
{
    /// <summary>Excel の最大行（1,048,576）。</summary>
    public const int MaxRow = 1_048_576;

    /// <summary>Excel の最大列（XFD = 16,384）。</summary>
    public const int MaxColumn = 16_384;

    private static readonly Regex CellPattern = new(@"^([A-Za-z]{1,3})(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ColumnsPattern = new(@"^([A-Za-z]{1,3})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RowsPattern = new(@"^(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>行数。</summary>
    public int RowCount => Row2 - Row1 + 1;

    /// <summary>列数。</summary>
    public int ColumnCount => Column2 - Column1 + 1;

    /// <summary>
    /// 入力を読む。失敗したら <paramref name="error"/> に、入力欄に出す理由（日本語・1 文）を返す。
    /// 成功したら <paramref name="error"/> は null。
    /// </summary>
    public static bool TryParse(string? text, out VbaA1Range range, out string? error)
    {
        range = default;
        error = null;
        var input = (text ?? string.Empty).Trim();
        if (input.Length == 0)
        {
            error = "範囲を入力してください（例 A1:F30）。";
            return false;
        }

        if (input.Contains('!', StringComparison.Ordinal))
        {
            error = "シート名は付けずに入力してください（このシートの範囲として扱います。例 A1:F30）。";
            return false;
        }

        if (input.Contains(',', StringComparison.Ordinal) || input.Contains(';', StringComparison.Ordinal))
        {
            error = "範囲は 1 つだけ指定できます（例 A1:F30）。";
            return false;
        }

        var compact = input.Replace("$", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        var parts = compact.Split(':');
        if (parts.Length > 2)
        {
            error = "範囲は A1:F30 の形で入力してください。";
            return false;
        }

        var first = parts[0];
        var second = parts.Length == 2 ? parts[1] : null;

        // セル 1 つ（A1）またはセルからセル（A1:F30）。
        if (CellPattern.Match(first) is { Success: true } c1 && (second is null || CellPattern.IsMatch(second)))
        {
            var (col1, row1) = (ColumnOf(c1.Groups[1].Value), RowOf(c1.Groups[2].Value));
            var (col2, row2) = (col1, row1);
            if (second is not null)
            {
                var c2 = CellPattern.Match(second);
                (col2, row2) = (ColumnOf(c2.Groups[1].Value), RowOf(c2.Groups[2].Value));
            }

            return Build(row1, col1, row2, col2, out range, out error);
        }

        // 列範囲（A:F）。「A」だけは名前（またはタイプミス）と見分けられないので不可。
        if (second is not null && ColumnsPattern.IsMatch(first) && ColumnsPattern.IsMatch(second))
        {
            return Build(1, ColumnOf(first), MaxRow, ColumnOf(second), out range, out error);
        }

        // 行範囲（3:10）。
        if (second is not null && RowsPattern.IsMatch(first) && RowsPattern.IsMatch(second))
        {
            return Build(RowOf(first), 1, RowOf(second), MaxColumn, out range, out error);
        }

        error = Regex.IsMatch(compact, @"^[\p{L}_\\][\p{L}\p{N}_.]*$")
            ? "名前付き範囲は指定できません。A1:F30 のようにセルの範囲で入力してください。"
            : "範囲は A1:F30・A:F・3:10 のような形で入力してください。";
        return false;
    }

    private static int ColumnOf(string letters) => VbaWorkbookInfoReadRules.ColumnNumber(letters);

    private static int RowOf(string digits) => int.TryParse(digits, out var row) ? row : int.MaxValue;

    private static bool Build(int row1, int col1, int row2, int col2, out VbaA1Range range, out string? error)
    {
        range = default;
        error = null;
        if (row1 < 1 || row2 < 1 || row1 > MaxRow || row2 > MaxRow)
        {
            error = $"行は 1〜{MaxRow:N0} の範囲で入力してください。";
            return false;
        }

        if (col1 < 1 || col2 < 1 || col1 > MaxColumn || col2 > MaxColumn)
        {
            error = "列は A〜XFD の範囲で入力してください。";
            return false;
        }

        range = new VbaA1Range(Math.Min(row1, row2), Math.Min(col1, col2), Math.Max(row1, row2), Math.Max(col1, col2));
        return true;
    }

    /// <summary>
    /// 範囲のアドレス（Excel の <c>Address(False, False)</c> が返す <c>A1</c>・<c>A1:F120</c>）を読む。
    /// 読めなければ null（使用範囲が読めないシートは、値を読まない側に倒す）。
    /// </summary>
    public static VbaA1Range? FromAddress(string? address)
        => TryParse(address, out var range, out _) ? range : null;

    /// <summary>2 つの範囲の共通部分。重ならなければ null。</summary>
    public VbaA1Range? Intersect(VbaA1Range other)
    {
        var row1 = Math.Max(Row1, other.Row1);
        var col1 = Math.Max(Column1, other.Column1);
        var row2 = Math.Min(Row2, other.Row2);
        var col2 = Math.Min(Column2, other.Column2);
        return row1 > row2 || col1 > col2 ? null : new VbaA1Range(row1, col1, row2, col2);
    }

    /// <summary>
    /// 入力を正規化した文字列（<c>A1:F30</c>。1 セルなら <c>A1</c>。列全体は <c>A:F</c>、行全体は <c>3:10</c> のまま）。
    /// 保存する値と、依頼文の見出しに使う。
    /// </summary>
    public string ToA1()
    {
        var wholeColumns = Row1 == 1 && Row2 == MaxRow;
        var wholeRows = Column1 == 1 && Column2 == MaxColumn;
        if (wholeColumns && !wholeRows)
        {
            return $"{VbaWorkbookInfoReadRules.ColumnLetters(Column1)}:{VbaWorkbookInfoReadRules.ColumnLetters(Column2)}";
        }

        if (wholeRows && !wholeColumns) return $"{Row1}:{Row2}";

        var first = $"{VbaWorkbookInfoReadRules.ColumnLetters(Column1)}{Row1}";
        return Row1 == Row2 && Column1 == Column2
            ? first
            : $"{first}:{VbaWorkbookInfoReadRules.ColumnLetters(Column2)}{Row2}";
    }
}
