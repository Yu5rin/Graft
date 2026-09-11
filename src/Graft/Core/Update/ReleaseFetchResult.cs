namespace Graft.Core.Update;

/// <summary>
/// <see cref="IReleaseFeed.GetLatestReleaseAsync"/>が確認できなかったときの理由。
///
/// 【なぜ列挙で区別するか（実機不具合対応）】
/// 実機ログ（2026-09-11、社内の共有回線）では、起動時・手動いずれの更新確認も
/// 「通信に失敗しました（更新の確認に失敗しました。ネットワーク接続や設定画面の
/// チェック先URLを確認してください。）」としか記録されておらず、状態コードも例外の型も
/// 残っていなかったため、原因（回数上限か・プロキシか・単なる不調か）を実機ログから
/// 切り分けられなかった。以後同じことが起きないよう、失敗の理由を最初からここで区別し、
/// 利用者向けの文言（<see cref="UpdateChecker"/>）とログ（<c>SettingsViewModel.Update.cs</c>）の
/// 両方に反映する。
///
/// 回数上限（<see cref="RateLimited"/>）が主因だろうという見立ては、GitHubのAPIが未認証で
/// 1時間60回・IPアドレス単位という制限を持つこと、および別リポジトリpaneの
/// <c>UpdateService.cs</c>が同じ社内の共有回線で「5回とも403」を実際に確認済みであることに
/// 基づく推測であり、断定ではない（実機ログに状態コードが残っていなかったため）。
/// 407・タイムアウト・名前解決不能など他の原因も同じログで見分けられるようにしておくのは
/// そのため。
/// </summary>
public enum ReleaseFetchFailureReason
{
    /// <summary>
    /// HTTP 403。GitHub APIの未認証時の上限（1時間60回、IPアドレス単位）に達したと
    /// みなす。403は本来「アクセス拒否」全般を表すコードだが、GitHub APIの実際の運用では
    /// ほぼこの回数上限を指す（レスポンスヘッダの<c>x-ratelimit-remaining</c>で確定できるが、
    /// 現状は本文・ヘッダの詳細解析までは行っていない）。
    /// </summary>
    RateLimited,

    /// <summary>
    /// HTTP 407、または.NETの<see cref="System.Net.Http.HttpRequestException"/>が
    /// プロキシとのトンネル確立を認証で拒否されたことを示すメッセージを持つ場合。
    /// 社内プロキシが未認証の通信を弾く構成でよく起こる。
    /// </summary>
    ProxyAuthenticationRequired,

    /// <summary>制限時間内に応答が無かった。</summary>
    TimedOut,

    /// <summary>配布元のサーバー名を解決できなかった（DNS）。</summary>
    NameResolutionFailed,

    /// <summary>
    /// 名前解決はできた（または判定できなかった）が、接続そのものに失敗した。
    /// TLS検査プロキシによる証明書の不一致や、api.github.com自体がブロックされている
    /// 場合もここに含まれる。
    /// </summary>
    ConnectionFailed,

    /// <summary>403・407以外の失敗した状態コード。</summary>
    HttpError,

    /// <summary>解析失敗（想定外のJSON形状等）や、上記のいずれにも当てはまらない例外。</summary>
    Unknown,
}

/// <summary>
/// <see cref="IReleaseFeed.GetLatestReleaseAsync"/>の結果。成功時は<see cref="Release"/>を、
/// 失敗時は理由（<see cref="FailureReason"/>）と、ログにだけ残す診断情報
/// （<see cref="HttpStatusCode"/>・<see cref="ExceptionTypeName"/>）を持つ。
///
/// 診断情報を利用者向けの文言と分離しているのは、<see cref="Core.ExceptionMessages"/>と
/// 同じ方針（利用者向けの文には型名や生の状態コードを出さない。「次に何をすればよいか」が
/// 分かる日本語の理由だけを見せ、詳細はログへ）に揃えるため。
/// </summary>
public sealed record ReleaseFetchResult
{
    public required bool Success { get; init; }
    public GitHubReleaseInfo? Release { get; init; }
    public ReleaseFetchFailureReason? FailureReason { get; init; }

    /// <summary>失敗時の実際のHTTP状態コード（応答を受け取れた場合のみ）。ログ専用。</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>失敗の原因になった例外の型名（例: "TaskCanceledException"）。ログ専用。</summary>
    public string? ExceptionTypeName { get; init; }

    public static ReleaseFetchResult Ok(GitHubReleaseInfo release) => new() { Success = true, Release = release };

    public static ReleaseFetchResult Fail(
        ReleaseFetchFailureReason reason, int? httpStatusCode = null, string? exceptionTypeName = null)
        => new()
        {
            Success = false,
            FailureReason = reason,
            HttpStatusCode = httpStatusCode,
            ExceptionTypeName = exceptionTypeName,
        };
}
