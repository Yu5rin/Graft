using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Graft.Core.Update;

/// <summary>
/// <see cref="IReleaseFeed"/>の実通信実装。GitHub Releases API
/// （<c>GET /repos/{owner}/{repo}/releases/latest</c>）を叩く唯一の場所。
///
/// 【HTTPS必須】<see cref="GetLatestReleaseAsync"/>はスキームがhttps以外のURLに対しては
/// 通信そのものを行わずnullを返す（設定画面でチェック先URLを変更できる仕様のため、
/// 誤ってhttpのURLを入力しても平文通信が発生しないようにする防御）。
///
/// 【User-Agent必須】GitHub APIはUser-Agentヘッダの無いリクエストを拒否する仕様のため、
/// 呼び出し側が"Graft/&lt;バージョン&gt;"形式の値を渡す。
/// </summary>
public sealed class GitHubReleaseFeed : IReleaseFeed
{
    // 複数回の確認（起動時・手動）にまたがって使い回す。HttpClientは使い捨てにすると
    // ソケットが枯渇しうることが知られているため、静的に1つだけ持つ（MSのガイドライン）。
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<GitHubReleaseInfo?> GetLatestReleaseAsync(string checkUrl, string userAgent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(checkUrl)) return null;
        if (!Uri.TryCreate(checkUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(userAgent) ? "Graft" : userAgent);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var dto = await response.Content.ReadFromJsonAsyncCompat<ReleaseDto>(JsonOptions, ct).ConfigureAwait(false);
            if (dto?.TagName is null || dto.TagName.Length == 0) return null;

            return new GitHubReleaseInfo
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
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            // 通信・解析いずれの失敗も「確認できなかった」に丸める（要件: 起動を妨げない）。
            return null;
        }
    }

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
