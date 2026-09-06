using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core.Update;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 異常系点検「低」5件目（更新ダウンロードに読み取りタイムアウトが無い）の回帰テスト。
///
/// 正直な前提: サンドボックスのプロキシ制約により、実際のネットワーク越しにダウンロード
/// 途中の切断・無応答を再現することはできない。ここでは実際の通信は一切行わず、
/// <see cref="HttpMessageHandler"/>を差し替えたフェイクの応答（遅いストリームを模した
/// <see cref="StallingStream"/>）で「一定時間バイトが来なければ内部で中断する」という
/// <see cref="HttpUpdateDownloader"/>の契約だけを検証する。本番の停滞タイムアウト（30秒）を
/// そのまま待つのは非現実的なため、テスト専用の<c>internal</c>コンストラクタで短い
/// タイムアウト（数百ミリ秒）に差し替える。
/// </summary>
public class HttpUpdateDownloaderStallTests
{
    private const string Url = "https://example.com/graft-update.zip";

    [Fact(DisplayName = "不具合5回帰: 応答開始後、バイトが1つも届かないまま停滞タイムアウトを超えると失敗として報告し、既存の中断（Cancelled）とは区別する")]
    public async Task 停滞が続くと失敗として報告される()
    {
        using var ws = new TempWorkspace();
        var destination = ws.Combine("update.zip");
        // 最初のReadAsyncが永久に完了しない（＝応答はあったがバイトが1つも届かない）ストリーム。
        using var handler = new StubHandler((_, _) => Task.FromResult(MakeOkResponse(new StallingStream(Array.Empty<byte[]>(), TimeSpan.FromSeconds(999)))));
        using var http = new HttpClient(handler);
        var downloader = new HttpUpdateDownloader(http, stallTimeout: TimeSpan.FromMilliseconds(200));

        var outcome = await downloader.DownloadAsync(Url, destination, progress: null, CancellationToken.None);

        outcome.Status.Should().Be(UpdateDownloadStatus.Failed, "停滞は利用者による中断ではなく失敗として報告するべき");
        outcome.ErrorMessage.Should().Contain("応答").And.Contain("秒間", "停滞であることが利用者に伝わる日本語メッセージであるべき");
        File.Exists(destination).Should().BeFalse("失敗時は書きかけの部分ファイルを残してはならない");
    }

    [Fact(DisplayName = "不具合5回帰: 停滞タイムアウトより短い間隔でバイトが届き続ける限り、合計時間が停滞タイムアウトを超えてもダウンロードは成功する（タイマーが読み取りごとに延長される証拠）")]
    public async Task 細切れでも届き続ける限り成功する()
    {
        using var ws = new TempWorkspace();
        var destination = ws.Combine("update.zip");
        var chunkDelay = TimeSpan.FromMilliseconds(80);
        var stallTimeout = TimeSpan.FromMilliseconds(200); // チャンク間隔(80ms) < 停滞タイムアウト(200ms)
        // 5チャンク×80ms ≒ 400ms は停滞タイムアウト(200ms)を優に超えるが、
        // 個々の間隔は停滞タイムアウト未満なので、都度タイマーが延長されれば成功するはず。
        var chunks = new[]
        {
            new byte[] { 1, 2, 3 },
            new byte[] { 4, 5, 6 },
            new byte[] { 7, 8, 9 },
            new byte[] { 10, 11, 12 },
            new byte[] { 13, 14, 15 },
        };
        using var handler = new StubHandler((_, _) => Task.FromResult(MakeOkResponse(new StallingStream(chunks, chunkDelay))));
        using var http = new HttpClient(handler);
        var downloader = new HttpUpdateDownloader(http, stallTimeout);

        var outcome = await downloader.DownloadAsync(Url, destination, progress: null, CancellationToken.None);

        outcome.Status.Should().Be(UpdateDownloadStatus.Success, "読み取りのたびに停滞タイマーが延長されるため、合計時間が長くても成功するべき");
        var written = await File.ReadAllBytesAsync(destination);
        written.Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
    }

    [Fact(DisplayName = "既存の「中断」ボタンの経路（呼び出し元のCancellationToken）は停滞検出の追加後も壊れておらず、Cancelledとして報告される")]
    public async Task 利用者による中断は引き続きCancelledになる()
    {
        using var ws = new TempWorkspace();
        var destination = ws.Combine("update.zip");
        using var cts = new CancellationTokenSource();
        // 停滞タイムアウトを長めに取り、「停滞検出ではなく利用者の中断が先に発火した」ことを
        // はっきりさせる。
        using var handler = new StubHandler((_, _) => Task.FromResult(MakeOkResponse(new StallingStream(Array.Empty<byte[]>(), TimeSpan.FromSeconds(999)))));
        using var http = new HttpClient(handler);
        var downloader = new HttpUpdateDownloader(http, stallTimeout: TimeSpan.FromSeconds(999));

        var downloadTask = downloader.DownloadAsync(Url, destination, progress: null, cts.Token);
        await Task.Delay(50).ConfigureAwait(true); // ダウンロード開始（応答待ち）を待つ
        cts.Cancel();
        var outcome = await downloadTask;

        outcome.Status.Should().Be(UpdateDownloadStatus.Cancelled, "利用者の中断は停滞検出の追加後もCancelledとして区別されるべき（中断ボタンの経路を壊さない）");
    }

    [Fact(DisplayName = "不具合5回帰: 名前解決不能等のHttpRequestExceptionは、生の英語メッセージではなく日本語の理由付きで報告される")]
    public async Task 名前解決不能は日本語で報告される()
    {
        using var ws = new TempWorkspace();
        var destination = ws.Combine("update.zip");
        var socketEx = new SocketException((int)SocketError.HostNotFound);
        var httpEx = new HttpRequestException("Name or service not known", socketEx);
        using var handler = new StubHandler((_, _) => throw httpEx);
        using var http = new HttpClient(handler);
        var downloader = new HttpUpdateDownloader(http, stallTimeout: TimeSpan.FromSeconds(30));

        var outcome = await downloader.DownloadAsync(Url, destination, progress: null, CancellationToken.None);

        outcome.Status.Should().Be(UpdateDownloadStatus.Failed);
        outcome.ErrorMessage.Should().Contain("名前を解決できません", "英語の生の例外メッセージ（The proxy tunnel request...等）をそのまま出してはならない");
        outcome.ErrorMessage.Should().Contain("Name or service not known", "原文は「（詳細: ...）」として残す既存方針（ExceptionMessages）は維持する");
    }

    private static HttpResponseMessage MakeOkResponse(Stream content)
        => new(HttpStatusCode.OK) { Content = new StreamContent(content) };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(request, cancellationToken);
    }

    /// <summary>
    /// 遅いネットワークストリームを模したテスト用Stream。<paramref name="chunks"/>を1つずつ、
    /// <paramref name="delayBetweenReads"/>だけ待ってから返す。<paramref name="chunks"/>が
    /// 空であれば、最初のReadAsyncが（キャンセルされるまで）永久に完了しない
    /// ＝「応答はあったがバイトが1つも届かない」停滞状態を表す。
    /// </summary>
    private sealed class StallingStream : Stream
    {
        private readonly byte[][] _chunks;
        private readonly TimeSpan _delayBetweenReads;
        private int _index;

        public StallingStream(byte[][] chunks, TimeSpan delayBetweenReads)
        {
            _chunks = chunks;
            _delayBetweenReads = delayBetweenReads;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delayBetweenReads, cancellationToken).ConfigureAwait(false);

            if (_index >= _chunks.Length) return 0;

            var chunk = _chunks[_index++];
            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
