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
    /// 通知の文言。「保存しました。Excel への自動反映は行いませんでした（E813: マクロの実行中です）」の形
    /// （見出しは E817。理由のコードを併記する）。
    /// </summary>
    /// <remarks>
    /// 【理由の言い回しを短くする理由】 ステータスバーの通知は、幅の上限（約 460px。全角で 40 文字ほど）で末尾が省略される。
    /// エラーの見出し（<see cref="ErrorCatalog.SummaryOf"/>。例: 「マクロの実行中または中断中です」）をそのまま使うと、
    /// 文の後ろにある理由のコードと見出しが省略されて、肝心の理由が読めない（実測: 見出しそのままの文は 50 文字を超えて省略された）。
    /// そこで、通知では理由の見出しを短い言い回しにする（詳しい対処は、エラーコードの案内とログに残る）。
    /// </remarks>
    /// <param name="reason">理由のコード（E813・E805・E811 など）。</param>
    /// <param name="module">モジュール名（E811 など、どのモジュールか分かると助かるとき）。</param>
    public static string BuildText(ErrorCode reason, string? module = null)
    {
        var where = string.IsNullOrEmpty(module) ? string.Empty : $" — {module}";
        return $"保存しました。Excel への自動反映は行いませんでした（{reason}: {ShortReason(reason)}{where}）";
    }

    /// <summary>通知用の短い理由。ここに無いコードは、エラーの見出しをそのまま使う。</summary>
    public static string ShortReason(ErrorCode reason) => reason switch
    {
        ErrorCode.E801 => "Excel と通信できません",
        ErrorCode.E802 => "VBA プロジェクトへのアクセスが未許可です",
        ErrorCode.E803 => "VBA プロジェクトがロック中です",
        ErrorCode.E804 => "ブックが開いていません",
        ErrorCode.E805 => "Excel が応答しません",
        ErrorCode.E807 => "ブックが読み取り専用です",
        ErrorCode.E809 => "反映後の内容が一致しません",
        ErrorCode.E811 => "Shift_JIS で表せない文字",
        ErrorCode.E813 => "マクロの実行中です",
        _ => ErrorCatalog.SummaryOf(reason),
    };
}
