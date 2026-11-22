using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Graft.Core.Vba;

/// <summary>
/// 控えの種別（仕様書_ブックの情報と依頼の観点 4.4）。ファイル名の接尾辞で表す。
/// </summary>
public enum VbaRescueKind
{
    /// <summary>従来の控え（接尾辞なし）。衝突で「Graft 側で上書き」を選んだ直前、書き戻し失敗（E809）。<b>自動では消さない</b>。</summary>
    Legacy = 0,

    /// <summary>「Excel へ反映」で Excel 側を上書きする直前の自動控え（<c>-auto</c>）。世代数・容量の掃除の対象。</summary>
    Auto = 1,

    /// <summary>控えをミラーへ戻す直前の、ミラー側の本文（<c>-mirror</c>）。掃除の対象。</summary>
    Mirror = 2,
}

/// <summary><see cref="VbaRescueNaming.Parse"/> の結果。</summary>
/// <param name="Module">モジュール名（ファイル名に使えない文字が <c>_</c> に置き換わっている場合は置換後の名前）。</param>
/// <param name="At">控えを残した日時（ファイル名の秒までの時刻。ローカル時刻のまま）。</param>
/// <param name="Kind">種別。</param>
/// <param name="Sequence">同じ秒の連番（接尾辞の無いものは 1、<c>_2</c> なら 2）。</param>
public sealed record VbaRescueFileName(string Module, DateTime At, VbaRescueKind Kind, int Sequence);

/// <summary>
/// 控え（<c>vba-sync/&lt;プロジェクトId&gt;.rescue/</c>）のファイル名の組み立てと解析（仕様書_VBA連携 5.5・7.3-4-3、
/// 仕様書_ブックの情報と依頼の観点 4.4）。
/// 書き戻しに失敗したときや、衝突で「Graft 側で上書き」を選んだとき、反映の直前に、Excel 側の本文を失わないよう書き出す。
/// </summary>
public static class VbaRescueNaming
{
    // 【解析が一意に切れる理由】 VBA のモジュール名には '-' が使えない（VbaNameRules）ので、名前と日時の区切りの
    // 「-yyyyMMdd-HHmmss」で必ず切れる。解析できないファイル（利用者が置いたもの）は一覧にも掃除にも載せない。
    private static readonly Regex Pattern = new(
        @"^(?<n>.+?)-(?<d>\d{8}-\d{6})(?:-(?<k>auto|mirror))?(?:_(?<seq>\d+))?\.txt$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// <c>&lt;モジュール名&gt;-&lt;日時&gt;[-auto|-mirror].txt</c>。日時は秒まで。同じ秒に同じモジュールを2回控えることは
    /// 通常無いが、呼び出し側が書き出すときに既存のファイルと衝突したら連番を付ける（<see cref="WithSequence"/>）。
    /// モジュール名のうちファイル名に使えない文字は <c>_</c> にする。
    /// </summary>
    public static string BuildFileName(string componentName, DateTimeOffset at, VbaRescueKind kind = VbaRescueKind.Legacy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        var builder = new StringBuilder(componentName.Length);
        foreach (var c in componentName)
        {
            builder.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(c) ? '_' : c);
        }

        var suffix = kind switch
        {
            VbaRescueKind.Auto => "-auto",
            VbaRescueKind.Mirror => "-mirror",
            _ => string.Empty,
        };
        return $"{builder}-{at:yyyyMMdd-HHmmss}{suffix}.txt";
    }

    /// <summary>連番つきのファイル名（<c>Mod-20261121-103000-auto_2.txt</c>）。<paramref name="sequence"/> は 2 以上。</summary>
    public static string WithSequence(string fileName, int sequence)
        => $"{Path.GetFileNameWithoutExtension(fileName)}_{sequence}.txt";

    /// <summary>
    /// ファイル名（フォルダを含まない）を解析する。この形でないファイルは null
    /// （利用者が置いたファイルを一覧に出さず、掃除でも消さないため）。
    /// </summary>
    public static VbaRescueFileName? Parse(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var match = Pattern.Match(fileName);
        if (!match.Success) return null;

        if (!DateTime.TryParseExact(
                match.Groups["d"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            return null; // 99999999-999999 のように形だけ合っている名前。
        }

        var kind = match.Groups["k"].Value switch
        {
            "auto" => VbaRescueKind.Auto,
            "mirror" => VbaRescueKind.Mirror,
            _ => VbaRescueKind.Legacy,
        };
        var sequence = 1;
        if (match.Groups["seq"].Success && !int.TryParse(match.Groups["seq"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out sequence))
        {
            return null;
        }

        return new VbaRescueFileName(match.Groups["n"].Value, at, kind, sequence);
    }
}
