namespace Graft.Core;

/// <summary>
/// マッチング段階5（行単位の正規化編集距離）を担当する。
///
/// 性能上の注意（本クラスの設計の中心）:
/// 素朴に「全開始位置 × 窓長のゆらぎ(2×maxDelta+1) × 編集距離DP(検索行数×窓長)」を回すと、
/// 計算量が O(ファイル行数 × maxDelta × 検索行数^2) になる。実測（本コミット時、4コアの
/// Linuxコンテナ・Release）では次のとおりで、SEARCHが長いほど二乗で効いていた。
///
///   2,000行 × SEARCH  20行 →   0.24秒
///   2,000行 × SEARCH  50行 →   0.90秒
///   2,000行 × SEARCH  80行 →   2.91秒
///   2,000行 × SEARCH 120行 →   8.78秒
///  10,000行 × SEARCH  40行 →   2.39秒
///  20,000行 × SEARCH 100行 →  52.24秒
/// 100,000行 × SEARCH 100行 → 281.35秒（！）
///
/// しかも段階5は「段階1〜4が一致しなかったとき」にしか動かない。つまり
/// 「一致しなかったときこそ何分も待たされる」という最悪の性質を持っていた。
/// CLAUDE.mdの「10万行のファイルでも編集が滞らない」という約束に、この経路だけ穴が空いていた。
///
/// 対策は「精度を一切落とさない枝刈り」を二段構えで入れることにした。速くするために
/// 当たらなくなるのでは本末転倒（段階5の価値は「AIの出力が少しずれても拾う」ことにある）なので、
/// 枝刈りはすべて「数学的に、その窓が採用され得ないと確定した場合だけ捨てる」ものに限る。
/// 結果（採用される窓・類似度・同点時にどれが選ばれるか）は素朴実装と完全に一致する
/// （tests/Graft.Tests/SimilarityScorerPruningTests.cs が素朴実装との一致を総当たりで固定している）。
///
/// 枝刈り1: 行の多重集合の共通部分による類似度の上限。
///   検索行列A（n行）と窓B（m行）の編集距離をdとすると、対応付けで一致できる行数kは
///   高々 |A ∩ B|（多重集合としての共通部分の要素数）であり、d ≥ max(n,m) - k が成り立つ。
///   よって 類似度 = 1 - d/max(n,m) ≤ |A∩B| / max(n,m)。この上限が
///   「しきい値」にも「暫定ベスト」にも届かない窓は、DPを一切回さずに捨ててよい。
///   さらに、ある開始位置から取り得る最大長の範囲について共通部分をスライド窓で
///   O(1)/開始位置 で更新できるため、大半の開始位置は1回の比較だけで捨てられる。
///
/// 枝刈り2: 編集距離DPの帯（バンド）制限と早期打ち切り。
///   採用され得る距離の上限capが分かっているとき、|i-j| > cap のセルは答えに影響しない。
///   DPを幅(2cap+1)の帯に限り、行の最小値がcapを超えた時点で打ち切る。
///   しきい値0.85・窓長100なら cap=15 なので、DPのセル数は 100×100 → 100×31 に減る。
///
/// 安全網: 上記でも詰められない病的な入力（似た行が延々と続くファイル等）に備え、
/// DPのセル数に予算を設けている（<see cref="DefaultCellBudget"/>）。予算を使い切ったら
/// 段階5を打ち切り、<see cref="SimilarityScan.Aborted"/> を立てて呼び出し元
/// （<see cref="MatchEngine"/>）が理由つきのE101を返せるようにする。
/// </summary>
public static class SimilarityScorer
{
    /// <summary>探索窓の長さを検索行数からどれだけ広げて試すかの上限（行数）。</summary>
    private const int MaxWindowDeltaCap = 20;

    /// <summary>
    /// 段階5全体で回してよい編集距離DPのセル数の上限（安全網）。
    /// 本環境の実測でDPは概ね毎秒3〜5億セル処理できるため、2億セルは最悪でも1秒弱に相当する。
    /// 枝刈りが効く通常の入力ではここには遠く届かない（10万行×SEARCH100行で実測 約1,900万セル、
    /// 予算の1割未満）。届くのは「ほぼ同じ行が延々と続くファイル」のような病的な場合だけで、
    /// そのときは何分も待たせるより「大きすぎて照合しきれませんでした」と理由を返す方がよい。
    /// </summary>
    private const long DefaultCellBudget = 200_000_000;

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

    /// <summary>段階5の探索結果。打ち切りの有無を呼び出し元へ伝えるために <see cref="SimilarityMatch"/> を包む。</summary>
    public sealed record SimilarityScan
    {
        /// <summary>見つかった最良の窓。無ければ null。</summary>
        public SimilarityMatch? Match { get; init; }

        /// <summary>DPの予算を使い切って探索を打ち切ったなら true（Matchはそれまでの最良）。</summary>
        public bool Aborted { get; init; }
    }

    /// <summary>
    /// ファイル全体から、閾値以上の類似度を持つ最良の窓を探す。見つからなければ null。
    /// 検索行数から大きく外れた長さの窓は、類似度が閾値に届き得ないため事前に枝刈りする。
    /// 打ち切りの有無まで知りたい場合は <see cref="Scan"/> を使う。
    /// </summary>
    public static SimilarityMatch? FindBestMatch(
        IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines, double threshold)
        => Scan(fileLines, searchLines, threshold).Match;

    /// <summary>
    /// <see cref="FindBestMatch"/> と同じ探索を行い、DP予算を使い切って打ち切ったかどうかも返す。
    /// </summary>
    /// <param name="cellBudget">回してよいDPセル数の上限。0以下なら無制限（テスト・検証用）。</param>
    public static SimilarityScan Scan(IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines,
        double threshold, long cellBudget = DefaultCellBudget)
    {
        if (searchLines.Count == 0 || fileLines.Count == 0) return new SimilarityScan();

        var searchLen = searchLines.Count;
        var fileCount = fileLines.Count;
        var maxDelta = Math.Min(MaxWindowDeltaCap,
            Math.Max(1, (int)Math.Ceiling(searchLen * (1 - threshold)) + 1));

        // 行を「検索行の語彙」に対する整数IDへ写しておく。以降の比較（DPの一致判定・多重集合の
        // 数え上げ）はすべてint比較になり、文字列比較・文字列ハッシュの費用が消える。
        // 検索行に無い行は-1（どの検索行とも一致しない）でまとめてよい。DPが比較するのは
        // 常に「検索行 対 ファイル行」であり、ファイル行同士は比較しないため、これで厳密に等価。
        var vocabulary = new Dictionary<string, int>(searchLen, StringComparer.Ordinal);
        var searchIds = new int[searchLen];
        for (var i = 0; i < searchLen; i++)
        {
            if (!vocabulary.TryGetValue(searchLines[i], out var id))
            {
                id = vocabulary.Count;
                vocabulary.Add(searchLines[i], id);
            }

            searchIds[i] = id;
        }

        var vocabSize = vocabulary.Count;
        var searchCount = new int[vocabSize];
        foreach (var id in searchIds) searchCount[id]++;

        var fileIds = new int[fileCount];
        for (var i = 0; i < fileCount; i++)
        {
            fileIds[i] = vocabulary.TryGetValue(fileLines[i], out var id) ? id : -1;
        }

        var minLen = Math.Max(1, searchLen - maxDelta);
        var maxLen = searchLen + maxDelta;

        var state = new ScanState
        {
            FileIds = fileIds,
            SearchIds = searchIds,
            SearchCount = searchCount,
            CoarseCount = new int[vocabSize],
            WindowCount = new int[vocabSize],
            Threshold = threshold,
            MinLen = minLen,
            MaxLen = maxLen,
            Budget = cellBudget > 0 ? cellBudget : long.MaxValue,
            Floor = threshold,
            // DPの作業バッファ。窓長の最大＋1で足りるため、開始位置ごとに確保し直さない。
            Previous = new int[maxLen + 2],
            Current = new int[maxLen + 2],
        };

        // 事前パス: 粗い上限が高い開始位置をいくつか先に評価し、枝刈りの下限(Floor)を底上げする。
        // これが無いと、しきい値が低いとき（<see cref="Features.RecoveryPrompt"/>はしきい値0で
        // 「最も似た箇所」を探す）に暫定ベストが0.0のまま延々とDPを回すことになり、枝刈りが
        // 全く効かなかった（実測: 10万行×SEARCH100行・しきい値0 で予算切れまで0.61秒走っても
        // 本来の一致箇所へ到達できなかった）。先に有力候補を1件確定させておけば、そのあとは
        // 上限がそれに届かない窓を全部捨てられる（同条件が0.02秒で正しい答えに到達するようになった）。
        // Floorを使った足切りが結果を変えないことの根拠: Floorは「実在する候補の類似度」なので、
        // 全体の最大類似度は必ずFloor以上。上限がFloorに届かない窓は最大値になり得ず、
        // 元実装が選ぶ「最初に現れた最大値」にもなり得ない。
        SeedFloor(state, fileCount);

        // 粗い枝刈り用のスライド窓を start=0 の位置に用意する。窓は
        // [start, min(start+MaxLen, fileCount)) ＝ その開始位置から取り得る最長の候補窓。
        Array.Clear(state.CoarseCount);
        var coarseInter = 0;
        for (var i = 0; i < Math.Min(maxLen, fileCount); i++) AddToCoarse(state, fileIds[i], ref coarseInter);

        SimilarityMatch? best = null;
        var aborted = false;
        for (var start = 0; start < fileCount; start++)
        {
            if (start > 0)
            {
                RemoveFromCoarse(state, fileIds[start - 1], ref coarseInter);
                var right = start + maxLen - 1;
                if (right < fileCount) AddToCoarse(state, fileIds[right], ref coarseInter);
            }

            // 枝刈り1（粗）: この開始位置から取り得るどの窓でも、類似度は
            // coarseInter / searchLen を超えられない（窓は上のスライド窓の部分集合であり、
            // 正規化の分母 max(検索行数, 窓長) は searchLen 以上のため）。
            // 上限がしきい値にも暫定ベストにも届かないなら、この開始位置はDPを一切回さずに捨てる。
            if (!CanWin((double)coarseInter / searchLen, Bar(state, best))) continue;

            best = EvaluateStart(state, start, best, out var budgetExhausted);
            if (budgetExhausted)
            {
                aborted = true;
                break;
            }
        }

        return new SimilarityScan { Match = best, Aborted = aborted };
    }

    /// <summary>事前パスで評価する開始位置の数（粗い上限の高い順）。</summary>
    private const int SeedStartCount = 8;

    /// <summary>
    /// 粗い上限（行の多重集合の共通部分）が大きい開始位置を上位 <see cref="SeedStartCount"/> 件だけ
    /// 先に本評価し、得られた類似度を枝刈りの下限（<see cref="ScanState.Floor"/>）に採用する。
    /// 上限が大きい開始位置は実際に似ている可能性も高いため、たいていは1件目で本命が当たる。
    /// 費用は「粗いスライド窓を1周（10万行で実測1ミリ秒未満）＋最大8開始位置ぶんのDP」で、
    /// 本走査に対して無視できる。
    /// </summary>
    private static void SeedFloor(ScanState state, int fileCount)
    {
        var fileIds = state.FileIds;
        var maxLen = state.MaxLen;

        // 上位SeedStartCount件を、挿入で維持する小さな配列で選ぶ（並べ替えの費用を避ける）。
        var bestInter = new int[SeedStartCount];
        var bestStart = new int[SeedStartCount];
        for (var i = 0; i < SeedStartCount; i++) bestStart[i] = -1;

        var inter = 0;
        for (var i = 0; i < Math.Min(maxLen, fileCount); i++) AddToCoarse(state, fileIds[i], ref inter);

        for (var start = 0; start < fileCount; start++)
        {
            if (start > 0)
            {
                RemoveFromCoarse(state, fileIds[start - 1], ref inter);
                var right = start + maxLen - 1;
                if (right < fileCount) AddToCoarse(state, fileIds[right], ref inter);
            }

            if (bestStart[SeedStartCount - 1] >= 0 && inter <= bestInter[SeedStartCount - 1]) continue;

            var pos = SeedStartCount - 1;
            while (pos > 0 && (bestStart[pos - 1] < 0 || inter > bestInter[pos - 1]))
            {
                bestInter[pos] = bestInter[pos - 1];
                bestStart[pos] = bestStart[pos - 1];
                pos--;
            }

            bestInter[pos] = inter;
            bestStart[pos] = start;
        }

        SimilarityMatch? seed = null;
        for (var i = 0; i < SeedStartCount; i++)
        {
            if (bestStart[i] < 0) continue;
            seed = EvaluateStart(state, bestStart[i], seed, out var exhausted);
            if (exhausted) break;
        }

        if (seed is not null) state.Floor = Math.Max(state.Floor, seed.Similarity);
    }

    /// <summary>探索中に使い回す作業領域。開始位置ごとの確保を避けるためにまとめて持ち回る。</summary>
    private sealed class ScanState
    {
        public required int[] FileIds { get; init; }
        public required int[] SearchIds { get; init; }
        public required int[] SearchCount { get; init; }
        public required int[] CoarseCount { get; init; }
        public required int[] WindowCount { get; init; }
        public required double Threshold { get; init; }

        /// <summary>枝刈りの下限。しきい値、または事前パスで見つけた実在候補の類似度のうち大きい方。
        /// これ未満の類似度しか出せない窓は、最良になり得ないため捨ててよい（Scanのコメント参照）。</summary>
        public double Floor { get; set; }
        public required int MinLen { get; init; }
        public required int MaxLen { get; init; }
        public required int[] Previous { get; init; }
        public required int[] Current { get; init; }
        public long Budget { get; set; }
    }

    private static void AddToCoarse(ScanState state, int id, ref int inter)
    {
        if (id < 0) return;
        if (state.CoarseCount[id] < state.SearchCount[id]) inter++;
        state.CoarseCount[id]++;
    }

    private static void RemoveFromCoarse(ScanState state, int id, ref int inter)
    {
        if (id < 0) return;
        state.CoarseCount[id]--;
        if (state.CoarseCount[id] < state.SearchCount[id]) inter--;
    }

    /// <summary>
    /// 枝刈りの基準値（bar）。しきい値・事前パスで得た下限・暫定ベストのうち最も大きいもの。
    /// 類似度の上限がこれに届かない窓は、最良として採用され得ない。
    /// </summary>
    private static double Bar(ScanState state, SimilarityMatch? best)
        => best is null ? state.Floor : Math.Max(state.Floor, best.Similarity);

    /// <summary>
    /// 類似度の上限 <paramref name="upperBound"/> の窓が、最良として採用され得るか。
    /// 浮動小数の丸めで取りこぼさないよう、境界付近（epsilon以内）は捨てずにDPで判定させる。
    /// 最終的な採否は必ずDPの正確な距離と元実装と同じ条件で決める（ここは足切りだけ）。
    /// </summary>
    private static bool CanWin(double upperBound, double bar)
    {
        const double epsilon = 1e-9;
        return upperBound > bar - epsilon;
    }

    /// <summary>
    /// 開始位置 <paramref name="start"/> を固定し、窓長を昇順（元実装の delta 昇順と同じ順序）に評価する。
    /// 同点のときにどの候補が残るかまで元実装と一致させるため、この評価順序は変更しないこと。
    /// </summary>
    private static SimilarityMatch? EvaluateStart(ScanState state, int start,
        SimilarityMatch? best, out bool budgetExhausted)
    {
        budgetExhausted = false;
        var fileIds = state.FileIds;
        var searchLen = state.SearchIds.Length;
        var hi = Math.Min(state.MaxLen, fileIds.Length - start);
        if (hi < state.MinLen) return best;

        // 窓を1行ずつ伸ばしながら、その窓に対する多重集合の共通部分（inter）を更新する。
        // 窓は入れ子（len昇順で単調に伸びる）なので、この1パスで全窓長ぶんの上限が得られる。
        var inter = 0;
        // 数え上げをどこまで進めたか（予算切れで途中終了した場合、巻き戻しも同じ位置までで止める。
        // ここを取り違えるとWindowCountが負に汚れ、事前パスで打ち切ったあとの本走査が壊れる）。
        var counted = hi;
        for (var len = 1; len <= hi; len++)
        {
            var id = fileIds[start + len - 1];
            if (id >= 0)
            {
                if (state.WindowCount[id] < state.SearchCount[id]) inter++;
                state.WindowCount[id]++;
            }

            if (len < state.MinLen) continue;

            var longest = Math.Max(searchLen, len);

            // 枝刈り1（細）: この窓の類似度は inter / max(検索行数, 窓長) を超えられない。
            var bar = Bar(state, best);
            if (!CanWin((double)inter / longest, bar)) continue;

            // 枝刈り2: 採用され得る編集距離の上限capを求め、その帯だけDPを回す。
            // 1 - d/longest ≥ bar ⇔ d ≤ longest×(1-bar)。等号側は「DPで正確に測ってから
            // 元実装と同じ条件で判定」するため、切り捨て側へ丸めて広めに取れば取りこぼさない。
            var cap = (int)Math.Floor(longest * (1 - bar) + 1e-9);
            if (cap < 0) continue;

            if (state.Budget <= 0)
            {
                budgetExhausted = true;
                counted = len;
                break;
            }

            var distance = CappedEditDistance(state, start, len, cap);
            if (distance > cap) continue;

            var similarity = 1.0 - (double)distance / longest;
            if (similarity < state.Threshold) continue;
            if (best is null || similarity > best.Similarity)
            {
                best = new SimilarityMatch { StartLine = start, LineCount = len, Similarity = similarity };
            }
        }

        // 窓の数え上げを次の開始位置のために巻き戻す（配列を作り直すより安い）。
        for (var len = 1; len <= counted; len++)
        {
            var id = fileIds[start + len - 1];
            if (id >= 0) state.WindowCount[id]--;
        }

        return best;
    }

    /// <summary>
    /// 検索行列と、ファイルの [start, start+len) の窓との編集距離を、帯幅 <paramref name="cap"/> に
    /// 制限して求める。距離が cap を超えることが確定したら cap+1 を返して打ち切る
    /// （呼び出し元はその窓を捨てるだけなので、正確な値は不要）。
    /// cap以下の範囲では素朴なDPと必ず同じ値を返す。
    /// </summary>
    private static int CappedEditDistance(ScanState state, int start, int len, int cap)
    {
        var a = state.SearchIds;
        var b = state.FileIds;
        var n = a.Length;
        var m = len;

        // 長さの差だけで距離の下限が決まる（挿入・削除が最低でも |n-m| 回要る）。
        if (Math.Abs(n - m) > cap) return cap + 1;

        var infinity = cap + 1;
        var previous = state.Previous;
        var current = state.Current;

        for (var j = 0; j <= m + 1 && j < previous.Length; j++) previous[j] = j <= cap ? j : infinity;

        for (var i = 1; i <= n; i++)
        {
            var from = Math.Max(1, i - cap);
            var to = Math.Min(m, i + cap);
            if (from > to) return cap + 1;

            current[from - 1] = from - 1 == 0 ? Math.Min(i, infinity) : infinity;

            var rowMin = infinity;
            var ai = a[i - 1];
            for (var j = from; j <= to; j++)
            {
                var cost = ai == b[start + j - 1] ? 0 : 1;
                var deletion = previous[j] + 1;
                var insertion = current[j - 1] + 1;
                var substitution = previous[j - 1] + cost;
                var value = Math.Min(deletion, Math.Min(insertion, substitution));
                if (value > infinity) value = infinity;
                current[j] = value;
                if (value < rowMin) rowMin = value;
            }

            // 帯の右外側は「cap超」として次の行から読ませる（帯の外へ答えが漏れないようにする）。
            if (to + 1 <= m) current[to + 1] = infinity;

            state.Budget -= to - from + 1;

            // 行全体の最小値がcapを超えたら、以降どう進んでも距離はcapを超える。
            if (rowMin > cap) return cap + 1;

            (previous, current) = (current, previous);
        }

        // 参照の入れ替えは局所変数に対してのみ行っているため、State側のバッファは
        // どちらがpreviousかを気にせず使い回せる（次回の呼び出しで先頭から初期化する）。
        return previous[m] > cap ? cap + 1 : previous[m];
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
