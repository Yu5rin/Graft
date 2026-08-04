namespace Graft.Core;

/// <summary>
/// マッチング段階5（行単位の正規化編集距離）を担当する。
/// </summary>
public static class SimilarityScorer
{
    /// <summary>探索窓の長さを検索行数からどれだけ広げて試すかの上限（行数）。</summary>
    private const int MaxWindowDeltaCap = 20;

    /// <summary>段階5で見つかった候補。</summary>
    public sealed record SimilarityMatch
    {
        /// <summary>一致開始行（0始まり）。</summary>
        public required int StartLine { get; init; }

        /// <summary>置換対象と見なす行数。</summary>
        public required int LineCount { get; init; }

        /// <summary>算出された類似度（0〜1）。</summary>
        public required double Similarity { get; init; }
    }

    /// <summary>
    /// ファイル全体から、閾値以上の類似度を持つ最良の窓を探す。見つからなければ null。
    /// 検索行数から大きく外れた長さの窓は、類似度が閾値に届き得ないため事前に枝刈りする。
    /// </summary>
    public static SimilarityMatch? FindBestMatch(
        IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines, double threshold)
    {
        if (searchLines.Count == 0 || fileLines.Count == 0) return null;

        var searchLen = searchLines.Count;
        var maxDelta = Math.Min(MaxWindowDeltaCap,
            Math.Max(1, (int)Math.Ceiling(searchLen * (1 - threshold)) + 1));

        SimilarityMatch? best = null;
        for (var start = 0; start < fileLines.Count; start++)
        {
            best = EvaluateStart(fileLines, searchLines, threshold, start, searchLen, maxDelta, best);
        }

        return best;
    }

    private static SimilarityMatch? EvaluateStart(IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines,
        double threshold, int start, int searchLen, int maxDelta, SimilarityMatch? best)
    {
        for (var delta = -maxDelta; delta <= maxDelta; delta++)
        {
            var len = searchLen + delta;
            if (len <= 0 || start + len > fileLines.Count) continue;

            var window = ExtractWindow(fileLines, start, len);
            var similarity = Similarity(searchLines, window);
            if (similarity < threshold) continue;
            if (best is null || similarity > best.Similarity)
            {
                best = new SimilarityMatch { StartLine = start, LineCount = len, Similarity = similarity };
            }
        }

        return best;
    }

    private static string[] ExtractWindow(IReadOnlyList<string> lines, int start, int len)
    {
        var arr = new string[len];
        for (var i = 0; i < len; i++) arr[i] = lines[start + i];
        return arr;
    }

    /// <summary>行配列同士の正規化編集距離による類似度（1 - 距離 / 最大長）を返す。</summary>
    public static double Similarity(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var maxLen = Math.Max(a.Count, b.Count);
        if (maxLen == 0) return 1.0;
        var distance = LineEditDistance(a, b);
        return 1.0 - (double)distance / maxLen;
    }

    /// <summary>行配列同士のLevenshtein編集距離を、2行分のバッファのみでO(n*m)で求める。</summary>
    public static int LineEditDistance(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var n = a.Count;
        var m = b.Count;
        if (n == 0) return m;
        if (m == 0) return n;

        var previous = new int[m + 1];
        var current = new int[m + 1];
        for (var j = 0; j <= m; j++) previous[j] = j;

        for (var i = 1; i <= n; i++)
        {
            current[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var deletion = previous[j] + 1;
                var insertion = current[j - 1] + 1;
                var substitution = previous[j - 1] + cost;
                current[j] = Math.Min(deletion, Math.Min(insertion, substitution));
            }

            (previous, current) = (current, previous);
        }

        return previous[m];
    }
}
