using System.Globalization;
using System.Text;

namespace Graft.Core.Vba;

/// <summary>
/// <c>Range.Value2</c> の値（<c>object?</c>）を、依頼文に載せる 1 セル分の短い文字列にする純ロジック
/// （仕様書_ブックの情報と依頼の観点 5.5）。COM に触れないので Linux でテストできる。
/// <para>
/// 【日付を判別しない理由】 <c>Value2</c> では日付は日付の連番（<c>double</c>）で返り、日付かどうかは表示形式を
/// 読まないと分からない。表示形式の取得は COM の呼び出しが増えるので、この版では連番のまま出す
/// （仕様書付録A-7。AI は 4 万台の数を日付と推測できることが多い）。
/// </para>
/// </summary>
public static class VbaCellText
{
    /// <summary>切り詰めたことを示す記号。</summary>
    public const string Ellipsis = "…";

    /// <summary>
    /// <c>Value2</c> でのエラー値は、HRESULT に近い負の整数で返る（値は仕様書 5.5 のとおり。実機で要確認、付録B）。
    /// 整数 1 つが「#N/A のエラー値」を表す。
    /// </summary>
    private static readonly Dictionary<int, string> ErrorNames = new()
    {
        [-2146826246] = "#N/A",
        [-2146826281] = "#DIV/0!",
        [-2146826273] = "#VALUE!",
        [-2146826265] = "#REF!",
        [-2146826259] = "#NAME?",
        [-2146826252] = "#NUM!",
        [-2146826288] = "#NULL!",
    };

    /// <summary>
    /// 1 セルの値を文字列にする。
    /// <list type="bullet">
    /// <item><c>null</c>（<c>Empty</c>）・空文字 → 空。</item>
    /// <item>文字列 → 改行・タブを空白 1 つに畳み、<paramref name="maxChars"/> を超えたら <c>…</c> つきで切る。</item>
    /// <item>数値 → 不変カルチャ（桁区切りなし・小数点はピリオド）。</item>
    /// <item>真偽値 → <c>TRUE</c>／<c>FALSE</c>。</item>
    /// <item>エラー値（上の 7 種の整数）→ <c>#N/A</c> など。</item>
    /// </list>
    /// </summary>
    public static string Render(object? value2, int maxChars)
    {
        switch (value2)
        {
            case null:
                return string.Empty;
            case string text:
                return Truncate(Collapse(text), maxChars);
            case bool flag:
                return flag ? "TRUE" : "FALSE";
            case int whole:
                return ErrorNames.TryGetValue(whole, out var errorName) ? errorName : whole.ToString(CultureInfo.InvariantCulture);
            case double number:
                return RenderDouble(number);
            case float single:
                return RenderDouble(single);
            case decimal money:
                return money.ToString(CultureInfo.InvariantCulture);
            case long big:
                return big.ToString(CultureInfo.InvariantCulture);
            case short small:
                return small.ToString(CultureInfo.InvariantCulture);
            case DateTime date:
                // Value2 では来ない想定だが、来たときに壊れないよう ISO 形式で出す。
                return date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            default:
                return Truncate(Collapse(Convert.ToString(value2, CultureInfo.InvariantCulture) ?? string.Empty), maxChars);
        }
    }

    /// <summary>
    /// 数式つきの見本（<c>=式 → 値</c>）。<paramref name="formula"/> が <c>=</c> で始まるときだけ数式として出し、
    /// そうでなければ値だけを返す（<c>Formula</c> は定数セルでも値を返すため。仕様書付録B）。
    /// 数式の文字数も <paramref name="maxChars"/> で切る。
    /// </summary>
    public static string RenderWithFormula(object? formula, object? value2, int maxChars)
    {
        var valueText = Render(value2, maxChars);
        if (formula is not string text || !text.StartsWith('='))
        {
            return valueText;
        }

        var formulaText = Truncate(Collapse(text), maxChars);
        return valueText.Length == 0 ? formulaText : $"{formulaText} → {valueText}";
    }

    private static string RenderDouble(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number)) return string.Empty;

        // 整数値の double（46000 など）は "46000" と出す（"4.6E+04" にならない書き方）。
        if (number == Math.Floor(number) && Math.Abs(number) < 1e15)
        {
            return ((long)number).ToString(CultureInfo.InvariantCulture);
        }

        // "R" は往復できる最短の表記。不変カルチャなので小数点はピリオドになる。
        var text = number.ToString("R", CultureInfo.InvariantCulture);
        // 指数表記（1E-05 など）は AI にも人にも読みにくいので、10 進で出し直す。
        if (text.Contains('E', StringComparison.Ordinal))
        {
            text = number.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        return text;
    }

    /// <summary>改行・タブ・連続する空白を、空白 1 つに畳む（表の 1 行に収めるため）。前後の空白は落とす。</summary>
    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string Truncate(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars) return text;

        // サロゲートペア（絵文字など）を割らない。割ると文字化けの原因になる。
        var cut = maxChars;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + Ellipsis;
    }
}
