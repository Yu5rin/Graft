using System.Text;

namespace Graft.Core.Vba;

/// <summary>
/// 控え（<c>vba-sync/&lt;プロジェクトId&gt;.rescue/</c>）のファイル名の組み立て（仕様書_VBA連携 5.5・7.3-4-3）。
/// 書き戻しに失敗したときや、衝突で「Graft 側で上書き」を選んだときに、Excel 側の本文を失わないよう書き出す。
/// </summary>
public static class VbaRescueNaming
{
    /// <summary>
    /// <c>&lt;モジュール名&gt;-&lt;日時&gt;.txt</c>。日時は秒まで。同じ秒に同じモジュールを2回控えることは通常
    /// 無いが、呼び出し側が書き出すときに既存のファイルと衝突したら連番を付ける。
    /// モジュール名のうちファイル名に使えない文字は <c>_</c> にする。
    /// </summary>
    public static string BuildFileName(string componentName, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        var builder = new StringBuilder(componentName.Length);
        foreach (var c in componentName)
        {
            builder.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(c) ? '_' : c);
        }

        return $"{builder}-{at:yyyyMMdd-HHmmss}.txt";
    }
}
