namespace Graft.Editor;

/// <summary>
/// 等幅判定のうち、幅比較部分の純粋ロジックだけを切り出したもの（Avalonia型に一切依存しない
/// ためtests/Graft.Tests側で直接検証できる。Graft.Tests.csprojのCompile Include参照）。
/// <see cref="SystemFontCatalog"/>の等幅判定は「フォント自身が申告するIsFixedPitchメタデータ」
/// を最優先で使うが、そのメタデータを正しく申告しないフォントの保険として、Pane
/// （github.com/Yu5rin/pane）の<c>Pane/FontService.cs</c>の<c>IsMonospace</c>と同じ考え方
/// （細い文字"i"と太い文字"W"の描画幅がほぼ同じなら等幅とみなす）も併用する。この幅比較の
/// 判定式だけがここに独立している（グリフの実測自体はAvalonia依存のためSystemFontCatalog側）。
/// </summary>
public static class MonospaceHeuristic
{
    /// <summary>
    /// "i"と"W"の描画幅の差がこの割合（narrowWidthに対する比率）以内なら等幅とみなす。
    /// Paneは16px描画時に1px以内の差を許容していた（誤差率6.25%相当）。Graft側は
    /// フォントサイズを固定せず比率で判定するため、Paneの実測値とほぼ同じ許容度になるよう
    /// 6%を採用した。
    /// </summary>
    private const double ToleranceRatio = 0.06;

    /// <summary>
    /// 2つの計測幅から等幅かどうかを判定する。計測できなかった・異常な値（0以下）の場合は
    /// 「等幅ではない」側へ倒す（Paneの方針と同じ。等幅リストへ誤って混入させないため）。
    /// </summary>
    public static bool IsMonospace(double narrowWidth, double wideWidth)
    {
        if (narrowWidth <= 0 || wideWidth <= 0 || double.IsNaN(narrowWidth) || double.IsNaN(wideWidth))
        {
            return false;
        }

        var diff = Math.Abs(narrowWidth - wideWidth);
        return diff <= narrowWidth * ToleranceRatio;
    }
}
