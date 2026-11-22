namespace Graft.Core.Vba;

/// <summary>掃除の計画に渡す控え1件（解析できたファイルだけ）。</summary>
/// <param name="FileName">ファイル名（フォルダを含まない）。</param>
/// <param name="Name">解析したファイル名。</param>
/// <param name="Size">バイト数。</param>
public sealed record VbaRescueEntry(string FileName, VbaRescueFileName Name, long Size)
{
    /// <summary>モジュール名。</summary>
    public string Module => Name.Module;

    /// <summary>種別。</summary>
    public VbaRescueKind Kind => Name.Kind;
}

/// <summary>掃除の計画。</summary>
/// <param name="Delete">消すファイル（古い順）。</param>
/// <param name="StillOverLimit">
/// 消したあとも、全体の容量が上限を超えている（消せるものが無い）。<c>Legacy</c> は自動では消さないので、
/// <c>Legacy</c> だけで上限を超えたときにこうなる。一覧の画面で警告に使う。
/// </param>
/// <param name="TotalBytesAfter">消したあとの全体の容量。</param>
public sealed record VbaRescuePrunePlan(IReadOnlyList<VbaRescueEntry> Delete, bool StillOverLimit, long TotalBytesAfter);

/// <summary>
/// 控えの掃除の計画（仕様書_ブックの情報と依頼の観点 7.3）。ファイルを触らない純ロジック。
/// <para>
/// 【自動で消すのは <c>Auto</c> と <c>Mirror</c> だけ】 <c>Legacy</c>（衝突の上書き前・書き戻し失敗）は、利用者の VBE 上の
/// 編集を救う最後の砦なので、世代数でも容量でも自動では消さない（容量の合計には数える）。
/// </para>
/// </summary>
public static class VbaRescuePruner
{
    /// <param name="entries">解析できた控え。</param>
    /// <param name="maxGenerations">1 モジュールあたりの <c>Auto</c>＋<c>Mirror</c> の世代数。</param>
    /// <param name="maxTotalBytes">控え全体（<c>Legacy</c> を含む）の容量。</param>
    public static VbaRescuePrunePlan Plan(IReadOnlyCollection<VbaRescueEntry> entries, int maxGenerations, long maxTotalBytes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        maxGenerations = Math.Max(1, maxGenerations);

        var delete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 各モジュールの最新の Auto は、世代数でも容量でも消さない（手順 1・2 の両方で除く）。
        // 「戻す」を押した直後の掃除で、戻し元の自動控えが消えると、利用者が取り戻そうとした内容そのものが無くなる。
        // 世代数だけで数えると、Mirror（戻す前のミラー）が新しく何件も積まれたとき、同じ内容を書き直さない（SkippedSame）
        // せいで古い日時のまま残った最新の Auto が押し出されて消える（世代数 1 なら Mirror が 1 件新しいだけで消える）。
        // 保護したぶん、そのモジュールは世代数をちょうど 1 件超えうる（Auto は最新の 1 件だけなので、増えるのは最大 1）。
        var protectedLatestAuto = entries
            .Where(e => e.Kind == VbaRescueKind.Auto)
            .GroupBy(e => e.Module, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e, ByAge.Instance).First().FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. モジュールごとに、Auto と Mirror を新しい順に並べて、世代数を超えた古い方を消す。
        foreach (var group in entries.Where(e => e.Kind != VbaRescueKind.Legacy)
                     .GroupBy(e => e.Module, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var old in group.OrderByDescending(e => e, ByAge.Instance).Skip(maxGenerations))
            {
                if (protectedLatestAuto.Contains(old.FileName)) continue;
                delete.Add(old.FileName);
            }
        }

        // 2. 全体の容量が上限を超えるなら、Auto と Mirror を古い順に、収まるまで消す。
        // 各モジュールの最新の Auto は、容量を理由には消さない（上限が小さすぎる設定でも、直近の控えは残す）。
        var total = entries.Where(e => !delete.Contains(e.FileName)).Sum(e => e.Size);
        if (total > maxTotalBytes)
        {
            foreach (var candidate in entries
                         .Where(e => e.Kind != VbaRescueKind.Legacy && !delete.Contains(e.FileName) && !protectedLatestAuto.Contains(e.FileName))
                         .OrderBy(e => e, ByAge.Instance))
            {
                if (total <= maxTotalBytes) break;
                delete.Add(candidate.FileName);
                total -= candidate.Size;
            }
        }

        // 3. Legacy は消さない。残りが上限を超えていれば、呼び出し側が一覧で警告する。
        var ordered = entries.Where(e => delete.Contains(e.FileName)).OrderBy(e => e, ByAge.Instance).ToList();
        return new VbaRescuePrunePlan(ordered, total > maxTotalBytes, total);
    }

    /// <summary>
    /// 古い順の昇順（日時、同じ秒は連番、それでも同じならファイル名）。同じ時刻でも並びが毎回同じになるよう、
    /// 最後はファイル名で決める（どれを消すかが実行のたびに変わらないように）。<c>OrderByDescending</c> で新しい順になる。
    /// </summary>
    private sealed class ByAge : IComparer<VbaRescueEntry>
    {
        public static readonly ByAge Instance = new();

        public int Compare(VbaRescueEntry? x, VbaRescueEntry? y)
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
