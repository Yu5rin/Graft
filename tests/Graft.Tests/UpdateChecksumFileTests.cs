using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using FluentAssertions;
using Graft.Core.Update;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 1.0.29（実機不具合対応）: GitHub APIの回数上限に当たってもSHA256の照合を省かずに済むよう、
/// リリースに添付した照合値ファイル（<c>&lt;書庫名&gt;.sha256</c>）から照合値を得る経路を固定する。
/// 通信は一切行わず、フェイクの<see cref="HttpMessageHandler"/>・<see cref="IChecksumFileSource"/>・
/// <see cref="IReleaseFeed"/>に差し替える。
/// </summary>
public class UpdateChecksumFileTests
{
    private const string CheckUrl = "https://api.github.com/repos/Yu5rin/Graft/releases/latest";
    private const string UserAgent = "Graft/1.0.28";
    private const string ArchiveName = "Graft-1.0.29-win-x64.zip";
    private const string ArchiveUrl = "https://github.com/Yu5rin/Graft/releases/download/v1.0.29/Graft-1.0.29-win-x64.zip";
    private static readonly string Hex = new('a', 64);

    // ---------------------------------------------------------------- 書式の解釈

    [Theory(DisplayName = "照合値ファイル: sha256sum形式の1行から、ファイル名が一致すれば小文字16進を取り出す")]
    [InlineData("aaaa  Graft-1.0.29-win-x64.zip\n")]
    [InlineData("aaaa  Graft-1.0.29-win-x64.zip")]
    [InlineData("aaaa  Graft-1.0.29-win-x64.zip\r\n")]
    [InlineData("aaaa *Graft-1.0.29-win-x64.zip\n")]
    [InlineData("aaaa Graft-1.0.29-win-x64.zip\n")]
    public void 正しい書式なら取り出せる(string template)
    {
        var text = template.Replace("aaaa", new string('A', 64));

        Sha256Verifier.ParseChecksumFile(text, ArchiveName).Should().Be(Hex, "大文字で書かれていても小文字16進に直して返す");
    }

    [Theory(DisplayName = "照合値ファイル: 書式が正しくない・ファイル名が違う・複数行なら照合値として使わない")]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("not a checksum\n")]
    [InlineData("aaaa\n")]                                              // ファイル名が無い
    [InlineData("aaaa  Graft-1.0.28-win-x64.zip\n")]                    // 別の書庫の値
    [InlineData("aaaa  graft-1.0.29-win-x64.zip\n")]                    // 大文字小文字違い
    [InlineData("aaaa  Graft-1.0.29-win-x64.zip\nbbbb  other.zip\n")]   // 複数行
    [InlineData("aaaa\tGraft-1.0.29-win-x64.zip\n")]                    // タブ区切り
    [InlineData("sha256:aaaa  Graft-1.0.29-win-x64.zip\n")]             // 接頭辞つき
    [InlineData("aaaa  Graft-1.0.29-win-x64.zip.bak\n")]                // 末尾違い
    public void 書式不正やファイル名不一致は使わない(string template)
    {
        var text = template.Replace("aaaa", new string('a', 64));

        Sha256Verifier.ParseChecksumFile(text, ArchiveName).Should().BeNull();
    }

    [Fact(DisplayName = "照合値ファイル: 16進でない文字・桁数違い・巨大な本文は使わない")]
    public void 桁数や文字が違えば使わない()
    {
        Sha256Verifier.ParseChecksumFile(new string('g', 64) + "  " + ArchiveName, ArchiveName).Should().BeNull();
        Sha256Verifier.ParseChecksumFile(new string('a', 63) + "  " + ArchiveName, ArchiveName).Should().BeNull();
        Sha256Verifier.ParseChecksumFile(new string('a', 65) + "  " + ArchiveName, ArchiveName).Should().BeNull();
        Sha256Verifier.ParseChecksumFile(new string('a', 64) + "  " + ArchiveName + new string('x', 2000), ArchiveName).Should().BeNull();
        Sha256Verifier.ParseChecksumFile(null, ArchiveName).Should().BeNull();
    }

    // ---------------------------------------------------------------- HttpChecksumFileSource（取得とホスト検証）

    [Fact(DisplayName = "取得: github.comからrelease-assets.githubusercontent.comへのリダイレクトを辿って照合値を得る（実測した経路）")]
    public async Task 許可されたホストへのリダイレクトは辿る()
    {
        var requested = new List<string>();
        var source = CreateSource((req, _) =>
        {
            requested.Add(req.RequestUri!.ToString());
            return Task.FromResult(req.RequestUri.Host == "github.com"
                ? Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/abc?sig=1")
                : Text($"{Hex}  {ArchiveName}\n"));
        });

        var result = await source.FetchAsync(CheckUrl, ArchiveName, ArchiveUrl, UserAgent, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Sha256Hex.Should().Be(Hex);
        requested.Should().Equal(ArchiveUrl + ".sha256", "https://release-assets.githubusercontent.com/github-production-release-asset/abc?sig=1");
    }

    [Fact(DisplayName = "取得: 許可していないホストへのリダイレクトは辿らず、そのホストへは接続しない")]
    public async Task 別ホストへのリダイレクトは辿らない()
    {
        var requested = new List<string>();
        var source = CreateSource((req, _) =>
        {
            requested.Add(req.RequestUri!.Host);
            return Task.FromResult(Redirect("https://evil.example.com/x.sha256"));
        });

        var result = await source.FetchAsync(CheckUrl, ArchiveName, ArchiveUrl, UserAgent, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("evil.example.com");
        requested.Should().Equal("github.com");
    }

    [Fact(DisplayName = "取得: raw.githubusercontent.comなど、利用者がファイルを置けるホストへのリダイレクトも辿らない")]
    public async Task rawgithubusercontentへのリダイレクトは辿らない()
    {
        var source = CreateSource((_, _) => Task.FromResult(Redirect("https://raw.githubusercontent.com/someone/repo/main/x.sha256")));

        var result = await source.FetchAsync(CheckUrl, ArchiveName, ArchiveUrl, UserAgent, CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact(DisplayName = "取得: HTTPへのリダイレクトは辿らない")]
    public async Task HTTPへのリダイレクトは辿らない()
    {
        var source = CreateSource((_, _) => Task.FromResult(Redirect("http://github.com/x.sha256")));

        (await source.FetchAsync(CheckUrl, ArchiveName, ArchiveUrl, UserAgent, CancellationToken.None)).Success.Should().BeFalse();
    }

    [Fact(DisplayName = "取得: リダイレクトが延々と続く場合は5回で諦める")]
    public async Task リダイレクトの回数に上限がある()
    {
        var count = 0;
        var source = CreateSource((_, _) =>
        {
            count++;
            return Task.FromResult(Redirect("https://github.com/loop"));
        });

        var result = await source.FetchAsync(CheckUrl, ArchiveName, ArchiveUrl, UserAgent, CancellationToken.None);

        result.Success.Should().BeFalse();
        count.Should().BeLessThanOrEqualTo(6);
    }

    [Fact(DisplayName = "取得: 404（古いリリースなど、照合値ファイルが無い）は失敗として返す")]
    public async Task 照合値ファイルが無ければ失敗()
    {
        var source = CreateSource((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await source.FetchAsync(CheckUrl, ArchiveName, ArchiveUrl, UserAgent, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureReason.Should().Contain("404");
    }

    [Fact(DisplayName = "取得: 書式不正・ファイル名不一致の本文は照合値として使わない")]
    public async Task 書式不正やファイル名不一致は失敗()
    {
        var malformed = CreateSource((_, _) => Task.FromResult(Text("<html>Not Found</html>")));
        var mismatched = CreateSource((_, _) => Task.FromResult(Text($"{Hex}  Graft-1.0.28-win-x64.zip\n")));

        (await malformed.FetchAsync(CheckUrl, ArchiveName, ArchiveUrl, UserAgent, CancellationToken.None)).Success.Should().BeFalse();
        (await mismatched.FetchAsync(CheckUrl, ArchiveName, ArchiveUrl, UserAgent, CancellationToken.None)).Success.Should().BeFalse();
    }

    [Fact(DisplayName = "取得: 通信の失敗（接続できない）は例外にせず失敗として返す")]
    public async Task 通信失敗は例外にならない()
    {
        var source = CreateSource((_, _) => throw new HttpRequestException("接続できない"));

        var result = await source.FetchAsync(CheckUrl, ArchiveName, ArchiveUrl, UserAgent, CancellationToken.None);

        result.Success.Should().BeFalse();
    }

    [Fact(DisplayName = "取得: ダウンロードURLのホストが既定のcheckUrlで許可されていなければ、通信せず失敗にする")]
    public async Task 最初のURLのホストが不正なら通信しない()
    {
        var called = false;
        var source = CreateSource((_, _) =>
        {
            called = true;
            return Task.FromResult(Text($"{Hex}  {ArchiveName}\n"));
        });

        var result = await source.FetchAsync(
            CheckUrl, ArchiveName, "https://evil.example.com/Graft-1.0.29-win-x64.zip", UserAgent, CancellationToken.None);

        result.Success.Should().BeFalse();
        called.Should().BeFalse();
    }

    [Fact(DisplayName = "取得: 確認先URLが既定から変更されている場合は、そのホストとだけ一致を認める（書庫のダウンロードと同じ方針）")]
    public async Task 変更された確認先URLでは確認先のホストだけ許可する()
    {
        const string customCheck = "https://mirror.example.co.jp/api/releases/latest";
        var requested = new List<string>();
        var source = CreateSource((req, _) =>
        {
            requested.Add(req.RequestUri!.Host);
            return Task.FromResult(Text($"{Hex}  {ArchiveName}\n"));
        });

        var ok = await source.FetchAsync(
            customCheck, ArchiveName, "https://mirror.example.co.jp/dl/Graft-1.0.29-win-x64.zip", UserAgent, CancellationToken.None);
        var ng = await source.FetchAsync(customCheck, ArchiveName, ArchiveUrl, UserAgent, CancellationToken.None);

        ok.Success.Should().BeTrue();
        ng.Success.Should().BeFalse("github.comは、確認先が別ホストに変更されている場合の許可先ではない");
        requested.Should().Equal("mirror.example.co.jp");
    }

    // ---------------------------------------------------------------- UpdateChecker（取得する段階の選択）

    [Fact(DisplayName = "回数上限（API 403）で規則からURLを組み立てた経路でも、照合値ファイルが取れればAllowMissingChecksumを外し、確認なしの自動更新に進める")]
    public async Task 回数上限でも照合値が取れれば照合を省かず自動更新に進める()
    {
        using var ws = new TempWorkspace();
        var checksums = new FakeChecksumSource(ChecksumFileResult.Ok(Hex));
        var checker = CreateChecker(ws, RateLimitedFeed(), checksums);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.28", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
        result.Release!.AllowMissingChecksum.Should().BeFalse("照合値が得られたので、照合を省く経路ではない");
        var asset = result.Release.FindAssetByNameSuffix("-win-x64.zip")!;
        asset.Digest.Should().Be("sha256:" + Hex);
        asset.DigestFromChecksumFile.Should().BeTrue();
        result.ChecksumFileAttempt!.Succeeded.Should().BeTrue();
        checksums.Calls.Should().ContainSingle()
            .Which.Should().Be((CheckUrl, ArchiveName, ArchiveUrl));

        // 修正前は ChecksumUnavailable になり、この環境では確認なしの自動更新が働かなかった。
        AutoUpdatePolicy.Evaluate(result.Release, "1.0.28", UpdatePlatform.Windows, CheckUrl)
            .CanProceed.Should().BeTrue();
    }

    [Fact(DisplayName = "照合値ファイルが取れなければ、従来どおり照合を省く経路（AllowMissingChecksum）のまま。取得を試みて失敗したことは結果に残る")]
    public async Task 照合値が取れなければ従来の経路のまま()
    {
        using var ws = new TempWorkspace();
        var checksums = new FakeChecksumSource(ChecksumFileResult.Fail("HTTP 404"));
        var checker = CreateChecker(ws, RateLimitedFeed(), checksums);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.28", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
        result.Release!.AllowMissingChecksum.Should().BeTrue();
        result.Release.FindAssetByNameSuffix("-win-x64.zip")!.Digest.Should().BeNull();
        result.ChecksumFileAttempt!.Succeeded.Should().BeFalse();
        result.ChecksumFileAttempt.Message.Should().Contain("404").And.Contain(ArchiveUrl + ".sha256");
        AutoUpdatePolicy.Evaluate(result.Release, "1.0.28", UpdatePlatform.Windows, CheckUrl)
            .Block.Should().Be(AutoInstallBlock.ChecksumUnavailable, "照合できないなら、確認なしでは進めない従来の挙動のまま");
    }

    [Fact(DisplayName = "照合値ファイルの取得手段が想定外の例外を投げても、更新確認は落ちず従来の経路になる")]
    public async Task 取得手段の例外は更新確認を落とさない()
    {
        using var ws = new TempWorkspace();
        var checker = CreateChecker(ws, RateLimitedFeed(), new ThrowingChecksumSource());

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.28", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
        result.Release!.AllowMissingChecksum.Should().BeTrue();
        result.ChecksumFileAttempt!.Succeeded.Should().BeFalse();
    }

    [Fact(DisplayName = "APIが応答したがdigestが空のときも、照合値ファイルが取れれば補う")]
    public async Task APIのdigestが空でも照合値ファイルで補える()
    {
        using var ws = new TempWorkspace();
        var feed = new FakeFeed(null, ReleaseFetchResult.Ok(ApiRelease(digest: "")));
        var checksums = new FakeChecksumSource(ChecksumFileResult.Ok(Hex));
        var checker = CreateChecker(ws, feed, checksums);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.28", UserAgent);

        var asset = result.Release!.FindAssetByNameSuffix("-win-x64.zip")!;
        asset.Digest.Should().Be("sha256:" + Hex);
        asset.DigestFromChecksumFile.Should().BeTrue();
        result.Release.AllowMissingChecksum.Should().BeFalse();
    }

    [Fact(DisplayName = "APIがdigestを返した通常の経路は変わらない（照合値ファイルを取りに行かず、digestもそのまま）")]
    public async Task APIのdigestがあれば照合値ファイルは使わない()
    {
        using var ws = new TempWorkspace();
        var apiDigest = "sha256:" + new string('b', 64);
        var feed = new FakeFeed(null, ReleaseFetchResult.Ok(ApiRelease(apiDigest)));
        var checksums = new FakeChecksumSource(ChecksumFileResult.Ok(Hex));
        var checker = CreateChecker(ws, feed, checksums);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.28", UserAgent);

        checksums.Calls.Should().BeEmpty();
        result.ChecksumFileAttempt.Should().BeNull();
        var asset = result.Release!.FindAssetByNameSuffix("-win-x64.zip")!;
        asset.Digest.Should().Be(apiDigest);
        asset.DigestFromChecksumFile.Should().BeFalse();
    }

    [Fact(DisplayName = "最新版だったとき（更新が無い）は、照合値ファイルを取りに行かない")]
    public async Task 更新が無ければ取りに行かない()
    {
        using var ws = new TempWorkspace();
        var feed = new FakeFeed(null, ReleaseFetchResult.Ok(ApiRelease(digest: "")));
        var checksums = new FakeChecksumSource(ChecksumFileResult.Ok(Hex));
        var checker = CreateChecker(ws, feed, checksums);

        var result = await checker.CheckOnStartupAsync(CheckUrl, "1.0.29", UserAgent);

        result.Status.Should().Be(UpdateCheckStatus.UpToDate);
        checksums.Calls.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- 更新確認からインストールまで（実際のHttpChecksumFileSource）

    [Fact(DisplayName = "回数上限の経路: 取得した照合値と書庫が一致すれば、SHA256を照合して入れ替えまで通る")]
    public async Task 回数上限の経路で照合値と一致すればインストールできる()
    {
        using var ws = new TempWorkspace();
        var zip = BuildValidZip();
        var installDir = CreateInstallDir(ws);
        var result = await CheckThenInstallAsync(ws, zip, ChecksumBody(zip), installDir);

        result.Status.Should().Be(UpdateInstallStatus.Success);
        File.ReadAllText(Path.Combine(installDir, "Graft.exe")).Should().Be("new-Graft.exe");
    }

    [Fact(DisplayName = "回数上限の経路: 取得した照合値と書庫が一致しなければChecksumMismatchで失敗し、入れ替えない")]
    public async Task 回数上限の経路で照合値と不一致なら失敗して入れ替えない()
    {
        using var ws = new TempWorkspace();
        var zip = BuildValidZip();
        var installDir = CreateInstallDir(ws);
        var wrongBody = $"{new string('0', 64)}  {ArchiveName}\n";

        var result = await CheckThenInstallAsync(ws, zip, wrongBody, installDir);

        result.Status.Should().Be(UpdateInstallStatus.ChecksumMismatch, "照合を省く経路には落とさず、不一致として止める");
        File.ReadAllText(Path.Combine(installDir, "Graft.exe")).Should().Be("old-Graft.exe");
    }

    [Fact(DisplayName = "回数上限の経路: 照合値ファイルの書式が不正なら、従来の「照合を省いて続行」経路になる")]
    public async Task 書式不正なら従来の照合を省く経路になる()
    {
        using var ws = new TempWorkspace();
        var zip = BuildValidZip();
        var installDir = CreateInstallDir(ws);

        var result = await CheckThenInstallAsync(ws, zip, "<html>oops</html>", installDir);

        // 従来の経路（allowMissingChecksum=true）のため、照合は省かれ、ZIP内容の検査を経て入れ替わる。
        result.Status.Should().Be(UpdateInstallStatus.Success);
    }

    /// <summary>
    /// 更新確認（Atomで新版・API 403・実際のHttpChecksumFileSource）→ インストール（実際のパイプライン）を通す。
    /// SettingsViewModelがやっているのと同じく、確認結果のAllowMissingChecksumをそのままパイプラインへ渡す。
    /// </summary>
    private static async Task<UpdateInstallResult> CheckThenInstallAsync(
        TempWorkspace ws, byte[] zip, string checksumBody, string installDir)
    {
        var source = CreateSource((req, _) => Task.FromResult(
            req.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) ? Text(checksumBody) : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var checker = CreateChecker(ws, RateLimitedFeed(), source);

        var check = await checker.CheckOnStartupAsync(CheckUrl, "1.0.28", UserAgent);
        check.Status.Should().Be(UpdateCheckStatus.UpdateAvailable);
        var asset = check.Release!.FindAssetByNameSuffix("-win-x64.zip")!;

        var pipeline = new UpdateInstallPipeline(new BytesDownloader(zip));
        return await pipeline.RunAsync(
            asset, installDir, ws.Combine("work"), CheckUrl, check.Release.AllowMissingChecksum,
            downloadProgress: null, CancellationToken.None);
    }

    // ---------------------------------------------------------------- 補助

    private static UpdateChecker CreateChecker(TempWorkspace ws, IReleaseFeed feed, IChecksumFileSource checksums)
        => new(feed, new UpdateCheckStateStore(new AppPaths(ws.CreateDirectory("app"))), checksumFiles: checksums);

    private static FakeFeed RateLimitedFeed()
        => new(new AtomFeedTag("v1.0.29", "https://github.com/Yu5rin/Graft/releases/tag/v1.0.29"),
            ReleaseFetchResult.Fail(ReleaseFetchFailureReason.RateLimited, 403));

    private static GitHubReleaseInfo ApiRelease(string? digest) => new()
    {
        TagName = "v1.0.29",
        HtmlUrl = "https://github.com/Yu5rin/Graft/releases/tag/v1.0.29",
        Assets = new[] { new GitHubReleaseAsset { Name = ArchiveName, BrowserDownloadUrl = ArchiveUrl, Digest = digest } },
    };

    private static HttpChecksumFileSource CreateSource(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new HttpClient(new StubHandler(responder)));

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string ChecksumBody(byte[] zip) => $"{Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()}  {ArchiveName}\n";

    private static string CreateInstallDir(TempWorkspace ws)
    {
        var dir = ws.CreateDirectory("install");
        foreach (var name in UpdateFiles.RequiredFileNames) File.WriteAllText(Path.Combine(dir, name), $"old-{name}");
        return dir;
    }

    private static byte[] BuildValidZip()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var fileName in UpdateFiles.RequiredFileNames)
            {
                var entry = archive.CreateEntry($"Graft/{fileName}");
                using var writer = new StreamWriter(entry.Open());
                writer.Write($"new-{fileName}");
            }
        }
        return stream.ToArray();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(request, cancellationToken);
    }

    private sealed class FakeFeed : IReleaseFeed
    {
        private readonly AtomFeedTag? _atom;
        private readonly ReleaseFetchResult _api;

        public FakeFeed(AtomFeedTag? atom, ReleaseFetchResult api)
        {
            _atom = atom;
            _api = api;
        }

        public Task<AtomFeedTag?> TryGetLatestTagFromAtomAsync(string checkUrl, CancellationToken ct) => Task.FromResult(_atom);

        public Task<ReleaseFetchResult> GetLatestReleaseAsync(string checkUrl, string userAgent, CancellationToken ct) => Task.FromResult(_api);
    }

    private sealed class FakeChecksumSource : IChecksumFileSource
    {
        private readonly ChecksumFileResult _result;

        public List<(string CheckUrl, string ArchiveName, string ArchiveUrl)> Calls { get; } = new();

        public FakeChecksumSource(ChecksumFileResult result) => _result = result;

        public Task<ChecksumFileResult> FetchAsync(string checkUrl, string archiveName, string archiveUrl, string userAgent, CancellationToken ct)
        {
            Calls.Add((checkUrl, archiveName, archiveUrl));
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingChecksumSource : IChecksumFileSource
    {
        public Task<ChecksumFileResult> FetchAsync(string checkUrl, string archiveName, string archiveUrl, string userAgent, CancellationToken ct)
            => throw new InvalidOperationException("実装ミスの想定");
    }

    private sealed class BytesDownloader : IUpdateDownloader
    {
        private readonly byte[] _bytes;

        public BytesDownloader(byte[] bytes) => _bytes = bytes;

        public async Task<UpdateDownloadOutcome> DownloadAsync(string url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
        {
            await File.WriteAllBytesAsync(destinationPath, _bytes, ct);
            return new UpdateDownloadOutcome(UpdateDownloadStatus.Success);
        }
    }
}
