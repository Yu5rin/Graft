namespace Graft.Core.Vba;

/// <summary>
/// リボン（customUI）の文字列を、依頼文に載せられる 1 行の短い形にそろえる純ロジック
/// （仕様書_ブックの情報と依頼の観点 15.5「依頼文に出す文字列の扱い」）。
/// <para>
/// 【なぜ全部の属性値に掛けるか】 ブックは外から受け取ったファイルで、中の customUI の属性値は信用できない。
/// 属性値に XML の <c>&amp;#10;</c> で改行を入れると、そのまま依頼文の「行」になり、行頭の <c># 対象ファイル</c> のような
/// 見出しや表の行を偽装できてしまう。また id やコールバック名に 10 万文字を書き、コールバック属性（<c>getX0</c>〜<c>getX199</c>）を
/// 1 要素に並べ、照合表では名前ごとに「使われている所」を繰り返すと、元の XML の約 195 倍（実測: 10 万文字 → 約 2000 万文字）に
/// 膨らむ。そこで「label だけ」ではなく、id・idMso・コールバック名・所有者（<c>種類 id</c>）のすべてを、
/// 空白類（改行・タブ・連続した空白）を 1 つの空白に畳んで 1 行にし、<see cref="MaxFieldChars"/> で切る。
/// </para>
/// </summary>
internal static class VbaRibbonText
{
    /// <summary>
    /// 1 つの文字列（id・idMso・コールバック名・所有者など）を出す最大文字数（超えたら「…」をつけて切る）。
    /// 120 = 実物の id やコールバック名（VBA の識別子は 255 文字が上限だが、実際は数十文字）に十分な余裕があり、
    /// 悪意のある長い値は確実に切れる値。label の <see cref="VbaRibbonFormatter.MaxLabelChars"/>（80）よりは長くしてある
    /// （識別子は切ると別の名前になって照合が外れるため、表示名より慎重に）。
    /// </summary>
    public const int MaxFieldChars = 120;

    /// <summary>空白類を 1 つの空白に畳み、前後の空白を除いて、最大 <paramref name="max"/> 文字（超えたら「…」つき）にする。</summary>
    public static string OneLine(string? text, int max = MaxFieldChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= max) return flat;
        // サロゲートペアの途中では切らない（壊れた文字が依頼文に混ざるのを避ける）。
        var cut = char.IsHighSurrogate(flat[max - 1]) ? max - 1 : max;
        return flat[..cut] + "…";
    }
}
