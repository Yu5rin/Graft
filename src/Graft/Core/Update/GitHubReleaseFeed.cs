using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Graft.Core.Update;

/// <summary>
/// <see cref="IReleaseFeed"/>の実通信実装。GitHub Releases API
/// （<c>GET /repos/{owner}/{repo}/releases/latest</c>）とAtomフィード
/// （<c>GET /{owner}/{repo}/releases.atom</c>）、通信を行う唯一の場所。
///
/// 【なぜAtomとAPIの2経路を持つか（実機不具合対応）】
/// 実機ログ（2026-09-11、社内の共有回線 <c>\\gfs\inaden\...</c>）で、起動時・手動いずれの
/// 更新確認も「通信に失敗しました」で毎回終わっていた。GitHubのAPIには未認証で1時間60回
/// （IPアドレス単位）の上限があり、会社などの共有回線では他の通信で先に使い切られる。
/// 別リポジトリ「pane」の<c>UpdateService.cs</c>が同じ社内の共有回線で「更新の確認が
/// 5回とも403(rate limit exceeded)で失敗していた」ことを実機ログから既に確認しており
/// （このリポジトリのコメントとして残っている）、その知見に倣い、普段の確認は回数上限の
/// 外にあるAtomフィードへ寄せ、新しい版が見つかったときだけAPIへ配布物の詳細
/// （ダウンロードURL・SHA256）を取りに行く（<see cref="UpdateChecker.CheckNowAsync"/>が
/// 呼び出し順序を決める。このクラス自身は「Atomを読む」「APIを読む」の2つの手段を
/// 提供するだけで、どちらをいつ呼ぶかの判断は持たない）。
/// ※原因が回数上限であることは推測であり断定ではない（実機ログに状態コードが残って
/// いなかったため）。<see cref="ReleaseFetchFailureReason"/>で407・タイムアウト・
/// 名前解決不能など他の原因も区別できるようにしてあるのはそのため。
///
/// 【HTTPS必須】 <see cref="GetLatestReleaseAsync"/>はスキームがhttps以外のURLに対しては
/// 通信そのものを行わない（設定画面でチェック先URLを変更できる仕様のため、誤ってhttpの
/// URLを入力しても平文通信が発生しないようにする防御）。
///
/// 【User-Agent必須】 GitHub APIはUser-Agentヘッダの無いリクエストを拒否する仕様のため、
/// 呼び出し側が"Graft/&lt;バージョン&gt;"形式の値を渡す。
///
/// 【Acceptは要求ごとに付ける】 かつて別リポジトリpaneでは、HttpClientの既定ヘッダに
/// "application/vnd.github+json" を設定しており、Zip（配布物）を取りに行く要求にまで
/// 「JSONをください」と言ってしまっていた（<see cref="HttpUpdateDownloader"/>参照。
/// GitHub自体はこの不一致を無視するが、中身とヘッダの整合を見る経路（社内プロキシ等）を
/// 通ると弾かれる余地が生まれる）。Graftではその教訓を踏まえ、<see cref="Http"/>には
/// 既定ヘッダを一切設定せず、Acceptは各リクエストごとに用途に合わせて付ける
/// （API要求は"application/vnd.github+json"、Atom要求は"application/atom+xml"）。
/// </summary>
public sealed class GitHubReleaseFeed : IReleaseFeed
{
    // 複数回の確認（起動時・手動）にまたがって使い回す。HttpClientは使い捨てにすると
    // ソケットが枯渇しうることが知られているため、静的に1つだけ持つ（MSのガイドライン）。
    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;

    public GitHubReleaseFeed() : this(DefaultHttp)
    {
    }

    /// <summary>
    /// テスト専用（<c>internal</c>）: <see cref="HttpClient"/>を差し替える。フェイクの
    /// <see cref="HttpMessageHandler"/>で403・407・タイムアウト・名前解決不能などの応答を
    /// 模し、実際には通信せずに<see cref="ReleaseFetchFailureReason"/>への分類を検証する
    /// （<c>GitHubReleaseFeedTests</c>参照）。
    /// </summary>
    internal GitHubReleaseFeed(HttpClient httpClient) => _http = httpClient;

    public async Task<AtomFeedTag?> TryGetLatestTagFromAtomAsync(string checkUrl, CancellationToken ct)
    {
        var atomUrl = UpdateAtomFeedLogic.TryBuildAtomUrl(checkUrl);
        if (atomUrl is null) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, atomUrl);
            // 既定のAcceptではなく、この要求にだけAtom用の値を付ける（クラスのコメント参照）。
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/atom+xml"));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Atom側の失敗は致命的に扱わない。呼び出し元はAPIへの問い合わせへ進む
                // （IReleaseFeedのコメント参照）。
                return null;
            }

            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var tag = UpdateAtomFeedLogic.ExtractLatestTag(xml);
            return tag is null ? null : new AtomFeedTag(tag, UpdateAtomFeedLogic.BuildReleasePageUrl(atomUrl, tag));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return null;
        }
    }

    public async Task<ReleaseFetchResult> GetLatestReleaseAsync(string checkUrl, string userAgent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(checkUrl)) return ReleaseFetchResult.Fail(ReleaseFetchFailureReason.Unknown);
        if (!Uri.TryCreate(checkUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return ReleaseFetchResult.Fail(ReleaseFetchFailureReason.Unknown);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(userAgent) ? "Graft" : userAgent);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 例外を投げさせず、状態コードから直接理由を分類する（EnsureSuccessStatusCode +
                // catchより素直で、状態コードの取りこぼしが無い）。
                return ReleaseFetchResult.Fail(ClassifyStatusCode(response.StatusCode), (int)response.StatusCode);
            }

            var dto = await response.Content.ReadFromJsonAsyncCompat<ReleaseDto>(JsonOptions, ct).ConfigureAwait(false);
            if (dto?.TagName is null || dto.TagName.Length == 0)
            {
                return ReleaseFetchResult.Fail(ReleaseFetchFailureReason.Unknown, exceptionTypeName: "応答にtag_nameが無い");
            }

            return ReleaseFetchResult.Ok(new GitHubReleaseInfo
            {
                TagName = dto.TagName,
                HtmlUrl = dto.HtmlUrl ?? "",
                Prerelease = dto.Prerelease,
                Assets = (dto.Assets ?? new List<AssetDto>())
                    .Select(a => new GitHubReleaseAsset
                    {
                        Name = a.Name ?? "",
                        BrowserDownloadUrl = a.BrowserDownloadUrl ?? "",
                        Size = a.Size,
                        Digest = a.Digest,
                    })
                    .ToList(),
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return ReleaseFetchResult.Fail(ClassifyException(ex, ct), StatusCodeOf(ex), ex.GetType().Name);
        }
    }

    /// <summary>
    /// 応答は受け取れた（＝通信そのものは成功した）が状態コードが失敗を示す場合の分類。
    /// 403・407以外は、コード自体を利用者へ見せて判断材料にしてもらう
    /// （<see cref="ReleaseFetchFailureReason.HttpError"/>。詳細はUpdateCheckerが文言化する）。
    /// </summary>
    private static ReleaseFetchFailureReason ClassifyStatusCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Forbidden => ReleaseFetchFailureReason.RateLimited,
        HttpStatusCode.ProxyAuthenticationRequired => ReleaseFetchFailureReason.ProxyAuthenticationRequired,
        _ => ReleaseFetchFailureReason.HttpError,
    };

    /// <summary>
    /// 応答そのものを受け取れなかった（通信の途中で例外になった）場合の分類。
    ///
    /// 【タイムアウトの判定】 <see cref="HttpClient.Timeout"/>による打ち切りは
    /// <see cref="TaskCanceledException"/>として届く。呼び出し元が渡した<paramref name="ct"/>
    /// 自身がキャンセルされていなければ、それはHttpClient内部のタイムアウトだと判定できる
    /// （<see cref="HttpUpdateDownloader"/>の停滞検出と同じ判定手法）。
    ///
    /// 【407の判定】 プロキシとのCONNECTトンネル確立が認証で拒否された場合、.NETは
    /// <see cref="HttpRequestException.StatusCode"/>を設定せず、メッセージ文字列
    /// （例: "The proxy tunnel request to proxy '...' failed with status code '407'"。
    /// <see cref="Core.ExceptionMessages"/>のコメントに実測値として記録済み）でしか
    /// 表現しないことが知られているため、状態コードに加えてメッセージも見る。
    /// </summary>
    private static ReleaseFetchFailureReason ClassifyException(Exception ex, CancellationToken ct)
    {
        if (ex is TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return ReleaseFetchFailureReason.TimedOut;
        }

        if (ex is not HttpRequestException http) return ReleaseFetchFailureReason.Unknown;

        if (http.StatusCode == HttpStatusCode.ProxyAuthenticationRequired || LooksLikeProxyTunnelFailure(http))
        {
            return ReleaseFetchFailureReason.ProxyAuthenticationRequired;
        }
        if (http.StatusCode is { } code) return ClassifyStatusCode(code);
        if (ExceptionMessages.IsNameResolutionFailure(http)) return ReleaseFetchFailureReason.NameResolutionFailed;

        return ReleaseFetchFailureReason.ConnectionFailed;
    }

    private static bool LooksLikeProxyTunnelFailure(HttpRequestException ex)
        => ex.Message.Contains("proxy tunnel", StringComparison.OrdinalIgnoreCase)
           && ex.Message.Contains("407", StringComparison.Ordinal);

    private static int? StatusCodeOf(Exception ex)
        => ex is HttpRequestException { StatusCode: { } code } ? (int)code : null;

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<AssetDto>? Assets { get; set; }
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}

/// <summary>
/// <see cref="HttpContent.ReadFromJsonAsync"/>相当をSystem.Net.Http.Jsonパッケージへ依存せず
/// 呼ぶための最小限のヘルパ（附録A.2 依存最小化: このためだけに追加パッケージを増やさない）。
/// </summary>
internal static class HttpContentJsonExtensions
{
    public static async Task<TDto?> ReadFromJsonAsyncCompat<TDto>(
        this HttpContent content, JsonSerializerOptions options, CancellationToken ct)
    {
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var _ = stream.ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<TDto>(stream, options, ct).ConfigureAwait(false);
    }
}
