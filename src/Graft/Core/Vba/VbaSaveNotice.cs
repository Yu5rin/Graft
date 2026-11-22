namespace Graft.Core.Vba;

/// <summary>
/// 保存時の自動反映（機能 D）を行わなかったときの通知の間引きと文言（仕様書_ブックの情報と依頼の観点 8.5）。純ロジック。
/// <para>
/// 【間引く理由】 保存のたびに同じ理由（マクロの実行中など）で失敗すると、通知が毎回出て作業の邪魔になる。
/// 同じ理由は <see cref="Interval"/>（60 秒）に 1 回までにする。E811（Shift_JIS で表せない文字）は、文字を直すまで
/// 保存のたびに起きるので、モジュール名つきで 1 回に絞る（別のモジュールの E811 は別に知らせる）。
/// </para>
/// </summary>
public sealed class VbaSaveNoticeThrottle
{
    /// <summary>同じ理由の通知を出す最小の間隔（仕様 8.5）。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, DateTimeOffset> _lastShown = new(StringComparer.Ordinal);

    /// <param name="clock">現在時刻（テストで差し替える）。</param>
    public VbaSaveNoticeThrottle(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>間引きの単位（キー）。E811 だけ、モジュール名を加える。</summary>
    public static string KeyOf(ErrorCode code, string? module)
        => code == ErrorCode.E811 && !string.IsNullOrEmpty(module) ? $"{code}:{module}" : code.ToString();

    /// <summary>
    /// いま通知を出してよいか。出してよいときは、出したものとして時刻を記録する（呼ぶと状態が変わる）。
    /// </summary>
    public bool TryShow(ErrorCode code, string? module = null)
    {
        var key = KeyOf(code, module);
        var now = _clock();
        if (_lastShown.TryGetValue(key, out var last) && now - last < Interval) return false;

        _lastShown[key] = now;
        Prune(now);
        return true;
    }

    // 古いキーを捨てて、E811 のモジュール名つきのキーが長いセッションで溜まり続けないようにする。
    private void Prune(DateTimeOffset now)
    {
        if (_lastShown.Count < 64) return;
        foreach (var key in _lastShown.Where(kv => now - kv.Value >= Interval).Select(kv => kv.Key).ToList())
        {
            _lastShown.Remove(key);
        }
    }

    /// <summary>
    /// 控えを保存できなかったために Excel の本文を上書きしなかった（E809）ときの、課題の文の共通部分。
    /// <c>VbaSyncService</c> がこの句を E809 の説明に含め、通知（<see cref="ShortReason"/>）がこの句で
    /// 「書き戻したあとの内容が一致しない」（同じ E809）と見分ける。
    /// </summary>
    public const string NotOverwrittenPhrase = "保存できなかったため、上書きしませんでした";

    /// <summary>理由のあとに続く、結果の言い回し。ファイルは保存済みで、Excel には送っていない。</summary>
    public const string ResultText = "保存のみ。Excel へは未反映";

    /// <summary>
    /// 通知の文言。<b>理由を文の先頭に置く</b>: 「E813 マクロの実行中: 保存のみ。Excel へは未反映」。
    /// モジュール名が要るもの（E811 など）は、理由のすぐあとに添える:
    /// 「E811 Shift_JIS で表せない文字（Module1）: 保存のみ。Excel へは未反映」。
    /// </summary>
    /// <remarks>
    /// 【理由を先頭にする理由】 ステータスバーの通知は幅の上限（<c>StatusBarView.axaml</c> の <c>MaxWidth</c>）で、
    /// 末尾が <c>…</c> に省略される。以前は「保存しました。Excel への自動反映は行いませんでした（E813: マクロの実行中です）」と、
    /// 理由が文の<b>末尾</b>にあったため、省略されると肝心の理由とモジュール名から消えた（ヘッドレスの計測では 11 種類すべてが
    /// 上限を超え、理由が丸ごと切れた）。理由とモジュール名を先に、定型の言い回し（結果）を後ろに回せば、省略されても
    /// 読みたい部分が残る。詳しい対処は、エラーコードの案内・ログ・通知のツールチップ（全文）に残る。
    /// </remarks>
    /// <param name="reason">理由のコード（E813・E805・E811 など）。</param>
    /// <param name="module">モジュール名（E811 など、どのモジュールか分かると助かるとき）。</param>
    /// <param name="detail">理由の補足（同じコードで意味が分かれるもの、E809 の見分けに使う）。</param>
    public static string BuildText(ErrorCode reason, string? module = null, string? detail = null)
    {
        var where = string.IsNullOrEmpty(module) ? string.Empty : $"（{module}）";
        return $"{reason} {ShortReason(reason, detail)}{where}: {ResultText}";
    }

    /// <summary>
    /// 通知用の短い理由。ここに無いコードは、エラーの見出しをそのまま使う。
    /// E809 は 2 つの意味がある（書き戻したあとの内容が違う／控えを残せないので上書きしなかった）ので、
    /// <paramref name="detail"/>（E809 の説明）で言い分ける。
    /// </summary>
    public static string ShortReason(ErrorCode reason, string? detail = null) => reason switch
    {
        ErrorCode.E801 => "Excel と通信できません",
        ErrorCode.E802 => "VBA へのアクセスが未許可",
        ErrorCode.E803 => "VBA プロジェクトがロック中",
        ErrorCode.E804 => "ブックが開いていません",
        ErrorCode.E805 => "Excel が応答しません",
        ErrorCode.E807 => "ブックが読み取り専用",
        ErrorCode.E808 => "モジュールを作れません",
        ErrorCode.E809 => detail is not null && detail.Contains(NotOverwrittenPhrase, StringComparison.Ordinal)
            ? "控えを残せず上書きせず"
            : "反映後の内容が不一致",
        ErrorCode.E811 => "Shift_JIS で表せない文字",
        ErrorCode.E813 => "マクロの実行中",
        ErrorCode.E816 => "控えを保存できません",
        _ => ErrorCatalog.SummaryOf(reason),
    };
}
