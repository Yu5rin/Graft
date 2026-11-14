namespace Graft.ViewModels;

/// <summary>
/// 等幅フォントで表示したときの「桁数」を数える。全角（日本語など）は2桁、それ以外は1桁。
/// 差分ビューの横スクロール幅の見積もり用で、正確なピクセル幅ではなく「だいたい最も長い行はどれか」を
/// 安く求めるために使う（実際のピクセル幅は、フォントを知っているViewが1桁の幅を掛けて出す）。
/// </summary>
internal static class TextCells
{
    public static int Count(string text)
    {
        var cells = 0;
        foreach (var ch in text)
        {
            cells += IsWide(ch) ? 2 : 1;
        }
        return cells;
    }

    // 東アジアの全角・ハングル・かな・CJK統合漢字・全角記号の主な範囲。サロゲート（絵文字など）は
    // 1文字ずつ2桁に数えず、見積もりが大きめにずれないよう1桁として扱う（足りない分はViewの余白で吸収）。
    private static bool IsWide(char ch) =>
        ch is >= 'ᄀ' and <= 'ᅟ'
        || ch is >= '⺀' and <= '꓏'
        || ch is >= '가' and <= '힣'
        || ch is >= '豈' and <= '﫿'
        || ch is >= '︰' and <= '﹯'
        || ch is >= '＀' and <= '｠'
        || ch is >= '￠' and <= '￦';
}
