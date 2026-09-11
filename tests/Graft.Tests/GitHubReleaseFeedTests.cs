using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using FluentAssertions;
using Graft.Core.Update;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="GitHubReleaseFeed"/>の要件を固定する。実際の通信は一切行わず、フェイクの
/// <see cref="HttpMessageHandler"/>で応答（403・407・タイムアウト・名前解決不能等）を模す
/// （<c>HttpUpdateDownloaderStallTests</c>と同じ手法）。
///
/// 実機不具合対応（実機ログ2026-09-11、社内の共有回線でGitHub APIの回数上限(403)により
/// 更新確認が失敗し続けていた件）: 403とその他の通信障害を区別できること、
/// Acceptヘッダを要求ごとに使い分けていることを固定する。
/// </summary>
public class GitHubReleaseFeedTests
{
    private const string CheckUrl = "https://api.github.com/repos/Yu5rin/Graft/releases/latest";
    private const string AtomUrl = "https://github.com/Yu5rin/Graft/releases.atom";
    private const string UserAgent = "Graft/1.0.7";

    [Fact(DisplayName = "APIが403を返す場合、RateLimitedとして状態コードとともに報告する")]
    public async Task API403はRateLimitedになる()
    {
        var feed = CreateFeed((req, _) =>
        {
            req.RequestUri!.ToString().Should().Be(CheckUrl);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)403));
        });

        var result = await feed.GetLatestReleaseAsync(CheckUrl, UserAgent, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(ReleaseFetchFailureReason.RateLimited);
        result.HttpStatusCode.Should().Be(403);
    }

    [Fact(DisplayName = "APIが407を返す場合、ProxyAuthenticationRequiredとして報告する")]
    public async Task API407はプロキシ認証要求になる()
    {
        var feed = CreateFeed((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)407)));

        var result = await feed.GetLatestReleaseAsync(CheckUrl, UserAgent, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(ReleaseFetchFailureReason.ProxyAuthenticationRequired);
        result.HttpStatusCode.Should().Be(407);
    }

    [Fact(DisplayName = "APIが500を返す場合、その他の状態コードとして状態コードを保持する")]
    public async Task APIのその他の状態コードはHttpErrorになる()
    {
        var feed = CreateFeed((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var result = await feed.GetLatestReleaseAsync(CheckUrl, UserAgent, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(ReleaseFetchFailureReason.HttpError);
        result.HttpStatusCode.Should().Be(500);
    }

    [Fact(DisplayName = "タイムアウト（利用者による中断ではない）はTimedOutとして報告する")]
    public async Task タイムアウトはTimedOutになる()
    {
        // HttpClient.Timeoutによる打ち切りはTaskCanceledExceptionとして届く。呼び出し側の
        // CancellationToken（ここではCancellationToken.None）自体はキャンセルされていないため、
        // GitHubReleaseFeed側はこれを「利用者の中断」ではなく「タイムアウト」だと判定できる
        // はず（HttpUpdateDownloaderの停滞検出と同じ判定手法）。
        var feed = CreateFeed((_, ct) => throw new TaskCanceledException("timeout", new TimeoutException(), ct));

        var result = await feed.GetLatestReleaseAsync(CheckUrl, UserAgent, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(ReleaseFetchFailureReason.TimedOut);
        result.ExceptionTypeName.Should().Be(nameof(TaskCanceledException));
    }

    [Fact(DisplayName = "利用者の中断（ctが発火）はTimedOutとして扱わない")]
    public async Task 利用者の中断はタイムアウトと区別する()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var feed = CreateFeed((_, ct) => throw new TaskCanceledException("cancelled", null, ct));

        var result = await feed.GetLatestReleaseAsync(CheckUrl, UserAgent, cts.Token);

        result.FailureReason.Should().NotBe(ReleaseFetchFailureReason.TimedOut);
    }

    [Fact(DisplayName = "名前解決不能はNameResolutionFailedとして、その他の接続失敗と区別して報告する")]
    public async Task 名前解決不能は区別して報告される()
    {
        var socketEx = new SocketException((int)SocketError.HostNotFound);
        var feed = CreateFeed((_, _) => throw new HttpRequestException("Name or service not known", socketEx));

        var result = await feed.GetLatestReleaseAsync(CheckUrl, UserAgent, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Be(ReleaseFetchFailureReason.NameResolutionFailed);
        result.ExceptionTypeName.Should().Be(nameof(HttpRequestException));
    }

    [Fact(DisplayName = "名前解決以外の接続失敗はConnectionFailedとして報告する")]
    public async Task その他の接続失敗はConnectionFailedになる()
    {
        var feed = CreateFeed((_, _) => throw new HttpRequestException("Connection refused"));

        var result = await feed.GetLatestReleaseAsync(CheckUrl, UserAgent, CancellationToken.None);

        result.FailureReason.Should().Be(ReleaseFetchFailureReason.ConnectionFailed);
    }

    [Fact(DisplayName = "プロキシとのトンネル確立が407で拒否された場合もProxyAuthenticationRequiredとして報告する")]
    public async Task プロキシトンネルの407も専用の理由になる()
    {
        // .NETはCONNECTトンネルの407を構造化された状態コードではなくメッセージでしか表現
        // しないことが知られている（Core.ExceptionMessagesのコメントに実測値として記録済み）。
        var feed = CreateFeed((_, _) => throw new HttpRequestException(
            "The proxy tunnel request to proxy 'http://proxy.example:8080/' failed with status code '407'."));

        var result = await feed.GetLatestReleaseAsync(CheckUrl, UserAgent, CancellationToken.None);

        result.FailureReason.Should().Be(ReleaseFetchFailureReason.ProxyAuthenticationRequired);
    }

    [Fact(DisplayName = "APIが成功する場合、配布物のURLとSHA256を含む結果を返し、Acceptにはgithub+jsonを使う")]
    public async Task API成功時は配布物の詳細を返す()
    {
        HttpRequestMessage? capturedRequest = null;
        var feed = CreateFeed((req, _) =>
        {
            capturedRequest = req;
            const string json = """
                {
                  "tag_name": "v1.0.10",
                  "html_url": "https://github.com/Yu5rin/Graft/releases/tag/v1.0.10",
                  "prerelease": false,
                  "assets": [
                    { "name": "Graft-1.0.10-win-x64.zip",
                      "browser_download_url": "https://github.com/Yu5rin/Graft/releases/download/v1.0.10/Graft-1.0.10-win-x64.zip",
                      "size": 12345,
                      "digest": "sha256:abcdef" }
                  ]
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        });

        var result = await feed.GetLatestReleaseAsync(CheckUrl, UserAgent, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Release!.TagName.Should().Be("v1.0.10");
        var asset = result.Release!.FindAssetByNameSuffix("win-x64.zip");
        asset.Should().NotBeNull();
        asset!.Digest.Should().Be("sha256:abcdef");

        capturedRequest!.Headers.Accept.Should().Contain(h => h.MediaType == "application/vnd.github+json",
            "GitHub Releases APIへの要求にはJSONを求めるAcceptを付けるべき");
    }

    [Fact(DisplayName = "Atomフィードが成功する場合、いちばん新しいタグを読み、要求にはatom+xmlを使う")]
    public async Task Atom成功時は最新タグを読みatom用Acceptを使う()
    {
        HttpRequestMessage? capturedRequest = null;
        var feed = CreateFeed((req, _) =>
        {
            capturedRequest = req;
            const string xml = """
                <?xml version="1.0" encoding="UTF-8"?>
                <feed xmlns="http://www.w3.org/2005/Atom">
                  <entry><link href="https://github.com/Yu5rin/Graft/releases/tag/v1.0.9"/></entry>
                  <entry><link href="https://github.com/Yu5rin/Graft/releases/tag/v1.0.10"/></entry>
                </feed>
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml) });
        });

        var tag = await feed.TryGetLatestTagFromAtomAsync(CheckUrl, CancellationToken.None);

        tag.Should().NotBeNull();
        tag!.TagName.Should().Be("v1.0.10");
        tag.ReleasePageUrl.Should().Be("https://github.com/Yu5rin/Graft/releases/tag/v1.0.10");
        capturedRequest!.RequestUri!.ToString().Should().Be(AtomUrl);
        capturedRequest!.Headers.Accept.Should().Contain(h => h.MediaType == "application/atom+xml");
    }

    [Fact(DisplayName = "checkUrlからAtomのURLを組み立てられない場合、通信そのものを行わずnullを返す")]
    public async Task Atomを組み立てられなければ通信しない()
    {
        var called = false;
        var feed = CreateFeed((_, _) =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var tag = await feed.TryGetLatestTagFromAtomAsync("https://example.com/not-github", CancellationToken.None);

        tag.Should().BeNull();
        called.Should().BeFalse("api.github.com形式でないURLでは通信せずに諦めるべき");
    }

    [Fact(DisplayName = "Atomフィードが失敗しても例外を投げず、静かにnullを返す（呼び出し元はAPIへ進む）")]
    public async Task Atom失敗時は静かにnullを返す()
    {
        var feed = CreateFeed((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var tag = await feed.TryGetLatestTagFromAtomAsync(CheckUrl, CancellationToken.None);

        tag.Should().BeNull();
    }

    private static GitHubReleaseFeed CreateFeed(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new HttpClient(new StubHandler(responder)));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(request, cancellationToken);
    }
}
