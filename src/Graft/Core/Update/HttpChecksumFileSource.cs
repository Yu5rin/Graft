using System.Net;
using System.Net.Http;

namespace Graft.Core.Update;

/// <summary>
/// <see cref="IChecksumFileSource"/>の実通信実装。
///
/// 【リダイレクトを自前で辿る理由】 <c>github.com/.../releases/download/...</c>は別ホスト
/// （<c>release-assets.githubusercontent.com</c>）へ302で飛ぶ。HttpClientの自動追従だと
/// 辿り先を検査できないが、ここで得た値は「これと一致すれば正しい」と信じる側の入力になる。
/// そこで自動追従を切り、1回ごとに<see cref="UpdateHostPolicy"/>で検査してから辿る
/// （最初のURLは書庫のダウンロードと同じ<see cref="UpdateHostPolicy.IsAllowedDownloadUrl"/>）。
/// 辿る回数は上限5回（実測は1回）。
///
/// 【本文の大きさ】 正しい本文は100バイト前後。1KBを超えたら失敗にする
/// （想定外の巨大な応答でメモリを使わないため）。
/// </summary>
public sealed class HttpChecksumFileSource : IChecksumFileSource
{
    // 自動追従は切る（上記）。プロキシ等の設定はHttpClientHandlerの既定のまま、他の更新通信と同じ。
    private static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private const int MaxRedirects = 5;
    private const int MaxBodyBytes = 1024;

    private readonly HttpClient _http;

    public HttpChecksumFileSource() : this(DefaultHttp)
    {
    }

    /// <summary>テスト専用: フェイクの<see cref="HttpMessageHandler"/>を載せた<see cref="HttpClient"/>に差し替える（自動追従は無いものとして扱う）。</summary>
    internal HttpChecksumFileSource(HttpClient httpClient) => _http = httpClient;

    public async Task<ChecksumFileResult> FetchAsync(
        string checkUrl, string archiveName, string archiveUrl, string userAgent, CancellationToken ct)
    {
        var checksumUrl = archiveUrl + ".sha256";
        if (!UpdateHostPolicy.IsAllowedDownloadUrl(checkUrl, checksumUrl))
        {
            return ChecksumFileResult.Fail($"照合値ファイルのURLのホストが信頼できないため取得しませんでした（{checksumUrl}）。");
        }

        try
        {
            var uri = new Uri(checksumUrl);
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(userAgent) ? "Graft" : userAgent);

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location is null) return ChecksumFileResult.Fail("リダイレクト先が示されていませんでした。");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (!UpdateHostPolicy.IsAllowedChecksumRedirectUrl(checkUrl, next))
                    {
                        return ChecksumFileResult.Fail($"信頼できないリダイレクト先のため辿りませんでした（{next.Host}）。");
                    }

                    uri = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return ChecksumFileResult.Fail($"HTTP {(int)response.StatusCode}（{checksumUrl}）。");
                }

                var text = await ReadLimitedTextAsync(response, ct).ConfigureAwait(false);
                if (text is null) return ChecksumFileResult.Fail("本文が大きすぎます。");

                var hex = Sha256Verifier.ParseChecksumFile(text, archiveName);
                return hex is null
                    ? ChecksumFileResult.Fail($"書式が正しくないか、ファイル名が書庫（{archiveName}）と一致しません。")
                    : ChecksumFileResult.Ok(hex);
            }

            return ChecksumFileResult.Fail("リダイレクトが多すぎます。");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // 利用者のキャンセルは握りつぶさず伝える。それ以外（タイムアウト等）はログ用の失敗理由にとどめる。
            ct.ThrowIfCancellationRequested();
            return ChecksumFileResult.Fail($"取得に失敗しました（{ex.GetType().Name}）。");
        }
    }

    private static bool IsRedirect(HttpStatusCode code)
        => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static async Task<string?> ReadLimitedTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        return total > MaxBodyBytes ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }
}
