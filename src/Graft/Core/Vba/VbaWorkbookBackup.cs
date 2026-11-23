using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Graft.Core.Vba;

/// <summary><see cref="VbaWorkbookBackupNaming.Parse"/> の結果。</summary>
/// <param name="WorkbookName">ブック名（拡張子なし。ファイル名に使えない文字は <c>_</c> に置き換わっている）。</param>
/// <param name="At">控えを残した日時（ファイル名の秒までの時刻。ローカル時刻のまま）。</param>
/// <param name="Sequence">同じ秒の連番（接尾辞の無いものは 1、<c>_2</c> なら 2）。</param>
/// <param name="Extension">元のブックの拡張子（<c>.xlsm</c>）。</param>
public sealed record VbaWorkbookBackupFileName(string WorkbookName, DateTime At, int Sequence, string Extension);

/// <summary>
/// リボンの書き換えの前に残す、ブックの丸ごとの控え（<c>vba-sync/&lt;プロジェクトId&gt;.workbooks/</c>）のファイル名の組み立てと解析
/// （仕様書_ブックの情報と依頼の観点 16.11）。<see cref="VbaRescueNaming"/>（モジュール本文の控え）と同じ作法。
/// <para>
/// 【拡張子を元のままにする理由】 非常時に、利用者が控えをそのまま Excel で開いて中身を確かめられるようにするため
/// （<c>.bak</c> にすると、開くのに改名が要る）。<c>-ribbon</c> の接尾辞で、何の操作の前の控えかを名前だけで分かるようにする。
/// </para>
/// </summary>
public static class VbaWorkbookBackupNaming
{
    // 【解析が一意に切れる理由】 ブック名には '-' も数字も使えるが、末尾の「-yyyyMMdd-HHmmss-ribbon[_n].拡張子」は固定の形なので、
    // 後ろから当てれば切れる（.+ は最長一致から戻って、末尾の形に合う所で止まる）。拡張子は 1 つ（.xlsm）だけを許す。
    private static readonly Regex Pattern = new(
        @"^(?<n>.+)-(?<d>\d{8}-\d{6})-ribbon(?:_(?<seq>\d+))?(?<ext>\.[A-Za-z0-9]+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// <c>&lt;ブック名&gt;-&lt;yyyyMMdd-HHmmss&gt;-ribbon&lt;元の拡張子&gt;</c>。ブック名のうちファイル名に使えない文字は <c>_</c> にする。
    /// 同じ秒に既存のファイルがあれば、呼び出し側が <see cref="WithSequence"/> で連番を付ける。
    /// </summary>
    /// <param name="workbookFileName">ブックのファイル名（フォルダを含んでもよい。<c>計画.xlsm</c>）。</param>
    /// <param name="at">控えを残す日時。</param>
    public static string BuildFileName(string workbookFileName, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookFileName);
        // Path.GetFileName は Linux では \ を区切りと見ない。ブックのパスは Windows の形（C:\…\計画.xlsm）で来るので、両方で切る。
        var fileName = workbookFileName[(workbookFileName.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
        var extension = Path.GetExtension(fileName);
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.Length == 0) name = "workbook";
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(c) ? '_' : c);
        }

        // 拡張子が無い・おかしい（英数字以外）ブックは、解析できる形にするため .xlsm とみなす。リボンの書き換えは .xlsm／.xlsb／.xlam が対象。
        if (extension.Length < 2 || !extension[1..].All(char.IsAsciiLetterOrDigit)) extension = ".xlsm";
        return $"{builder}-{at:yyyyMMdd-HHmmss}-ribbon{extension}";
    }

    /// <summary>連番つきのファイル名（<c>計画-20261123-100000-ribbon_2.xlsm</c>）。<paramref name="sequence"/> は 2 以上。</summary>
    public static string WithSequence(string fileName, int sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (sequence < 2) throw new ArgumentOutOfRangeException(nameof(sequence), "連番は 2 以上です。");
        return $"{Path.GetFileNameWithoutExtension(fileName)}_{sequence.ToString(CultureInfo.InvariantCulture)}{Path.GetExtension(fileName)}";
    }

    /// <summary>
    /// ファイル名（フォルダを含まない）を解析する。この形でないファイルは null（利用者が置いたファイルを一覧に出さず、掃除でも消さないため）。
    /// </summary>
    public static VbaWorkbookBackupFileName? Parse(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var match = Pattern.Match(fileName);
        if (!match.Success) return null;
        if (!DateTime.TryParseExact(match.Groups["d"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return null; // 99999999-999999 のように形だけ合っている名前。

        var sequence = 1;
        if (match.Groups["seq"].Success
            && (!int.TryParse(match.Groups["seq"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence < 2))
        {
            return null;
        }

        return new VbaWorkbookBackupFileName(match.Groups["n"].Value, at, sequence, match.Groups["ext"].Value);
    }
}

/// <summary>掃除の計画に渡す、ブックの控え 1 件（解析できたファイルだけ）。</summary>
/// <param name="FileName">ファイル名（フォルダを含まない）。</param>
/// <param name="Name">解析したファイル名。</param>
/// <param name="Size">バイト数。</param>
public sealed record VbaWorkbookBackupEntry(string FileName, VbaWorkbookBackupFileName Name, long Size);

/// <summary>掃除の計画。</summary>
/// <param name="Delete">消すファイル（古い順）。</param>
/// <param name="StillOverLimit">消したあとも容量の上限を超えている（最新の 1 件だけで上限を超えるとき）。</param>
/// <param name="TotalBytesAfter">消したあとの合計の容量。</param>
public sealed record VbaWorkbookBackupPrunePlan(IReadOnlyList<VbaWorkbookBackupEntry> Delete, bool StillOverLimit, long TotalBytesAfter);

/// <summary>
/// ブックの丸ごとの控えの掃除の計画（仕様書_ブックの情報と依頼の観点 16.11。既定 5 世代・500 MB）。ファイルを触らない純ロジック。
/// <para>
/// 【最新の 1 件は消さない】 控えは「いま書き換えたブックを元へ戻す」ための最後の手段。上限が小さすぎる設定や、1 件で上限を超える
/// 大きなブックでも、直前の控えを消してしまうと、書き換えの失敗に気付いたときに戻せない。最新の 1 件だけは、世代数でも容量でも残す。
/// </para>
/// <para>
/// 【数える単位はプロジェクトのフォルダ全体】 控えのフォルダはプロジェクトごと（<c>&lt;Id&gt;.workbooks/</c>）で、紐付けるブックは通常 1 つ。
/// ブックを結び直したあとの古いブックの控えも、同じ上限の中で古い順に消える（ブック名ごとに 5 世代持つと、結び直すたびに上限が増える）。
/// </para>
/// </summary>
public static class VbaWorkbookBackupPruner
{
    /// <param name="entries">解析できた控え。</param>
    /// <param name="maxGenerations">残す世代数（1 以上に丸める）。</param>
    /// <param name="maxTotalBytes">合計の容量の上限。</param>
    public static VbaWorkbookBackupPrunePlan Plan(IReadOnlyCollection<VbaWorkbookBackupEntry> entries, int maxGenerations, long maxTotalBytes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        maxGenerations = Math.Max(1, maxGenerations);

        var newestFirst = entries.OrderByDescending(e => e, ByAge.Instance).ToList();
        var delete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 世代数を超えた古い方を消す。
        foreach (var old in newestFirst.Skip(maxGenerations)) delete.Add(old.FileName);

        // 2. 容量を超えるなら、古い順に、収まるまで消す。最新の 1 件（newestFirst[0]）は残す。
        var total = entries.Where(e => !delete.Contains(e.FileName)).Sum(e => e.Size);
        if (total > maxTotalBytes)
        {
            foreach (var candidate in newestFirst.Skip(1).Where(e => !delete.Contains(e.FileName)).Reverse())
            {
                if (total <= maxTotalBytes) break;
                delete.Add(candidate.FileName);
                total -= candidate.Size;
            }
        }

        var ordered = entries.Where(e => delete.Contains(e.FileName)).OrderBy(e => e, ByAge.Instance).ToList();
        return new VbaWorkbookBackupPrunePlan(ordered, total > maxTotalBytes, total);
    }

    /// <summary>古い順の昇順（日時、同じ秒は連番、それでも同じならファイル名）。並びを毎回同じにして、消すものが実行ごとに変わらないようにする。</summary>
    private sealed class ByAge : IComparer<VbaWorkbookBackupEntry>
    {
        public static readonly ByAge Instance = new();

        public int Compare(VbaWorkbookBackupEntry? x, VbaWorkbookBackupEntry? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var c = x.Name.At.CompareTo(y.Name.At);
            if (c != 0) return c;
            c = x.Name.Sequence.CompareTo(y.Name.Sequence);
            if (c != 0) return c;
            return string.CompareOrdinal(x.FileName, y.FileName);
        }
    }
}
