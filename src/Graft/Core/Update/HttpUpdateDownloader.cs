using System.Net.Http;

namespace Graft.Core.Update;

/// <summary>
/// <see cref="IUpdateDownloader"/>の実通信実装。配布物ZIP（50MB超）を一時フォルダへ
/// ストリーミングで書き出しながら進捗を報告する。<see cref="CancellationToken"/>による中断に
/// 対応し、中断・失敗いずれの場合も書きかけの部分ファイルを削除してから返る。
///
/// 【異常系点検「低」5件目の対応: 停滞検出】ダウンロード全体の所要時間には固定の上限を
/// 設けない（<see cref="Http"/>のTimeoutが<see cref="Timeout.InfiniteTimeSpan"/>のままなのは
/// 従来どおり。低速回線での正常な長時間ダウンロードを打ち切ってしまわないため）。
/// その一方で、サーバがTCP接続を張ったまま応答しなくなった場合、<c>ReadAsync</c>は
/// 無期限に待ち続け、進捗バーが止まったまま「ダウンロードしています…」の表示だけが
/// 残り続けてしまう（利用者からは「フリーズしたのか、単に遅いだけなのか」区別が付かない）。
/// これを防ぐため、<see cref="StallTimeout"/>だけ新しいバイトが1つも届かなければ内部で
/// 自動的に中断する「停滞検出」を、既存の「中断」ボタン（<paramref name="ct"/>）とは別の
/// 内部用<see cref="CancellationTokenSource"/>（<paramref name="ct"/>にリンクする形で作る）で
/// 実装する。バイトを1つ受け取るたびにタイマーを<see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
/// で延長し続けることで、「合計時間」ではなく「直近の無通信時間」だけを見る。
/// <paramref name="ct"/>自身が発火した場合（利用者の「中断」ボタン）は
/// <see cref="UpdateDownloadStatus.Cancelled"/>、内部のタイマーが先に発火した場合（停滞）は
/// <see cref="UpdateDownloadStatus.Failed"/>として、原因ごとに違う日本語メッセージを返す
/// （どちらも<see cref="OperationCanceledException"/>として送出されるため、catch節では
/// <c>ct.IsCancellationRequested</c>で発火元を判定する）。
/// </summary>
public sealed class HttpUpdateDownloader : IUpdateDownloader
{
    // ダウンロード自体は数十MBに及び、低速回線では時間がかかりうるため、HttpClient側の
    // 固定タイムアウトは設けず（Timeout.InfiniteTimeSpan）、呼び出し側が渡すCancellationTokenと
    // 下記の停滞検出（無通信時間の上限）だけで中断を制御する（要件: 中断できるようにすること）。
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private const int BufferSize = 81920;

    /// <summary>
    /// 停滞（無通信）とみなすまでの時間。30秒あれば、低速回線でも数十KB程度は流れてくるのが
    /// 通常であり、それすら届かない場合は「進行中の遅さ」ではなく「応答が止まっている」と
    /// 判断してよい、という経験則に基づく既定値。
    /// </summary>
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly TimeSpan _stallTimeout;

    public HttpUpdateDownloader() : this(Http, DefaultStallTimeout)
    {
    }

    /// <summary>
    /// テスト専用（<c>internal</c>）: <see cref="HttpClient"/>と停滞判定までの時間を差し替える。
    /// 本番の停滞タイムアウト（30秒）をそのままテストで待つのは非現実的なため、遅いストリームを
    /// 模したフェイクの<see cref="HttpMessageHandler"/>と、短い<paramref name="stallTimeout"/>を
    /// 組み合わせて検証する（<c>HttpUpdateDownloaderStallTests</c>参照）。
    /// </summary>
    internal HttpUpdateDownloader(HttpClient httpClient, TimeSpan stallTimeout)
    {
        _http = httpClient;
        _stallTimeout = stallTimeout;
    }

    public async Task<UpdateDownloadOutcome> DownloadAsync(
        string url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new UpdateDownloadOutcome(UpdateDownloadStatus.Failed, "HTTPS以外のダウンロード元は許可していません。");
        }

        // 利用者の「中断」ボタン（ct）と、停滞検出の内部タイマーの両方でキャンセルできるよう
        // リンクしたトークンを使う。ctが直接発火した場合との判別はcatch節でct自身を見て行う
        // （stallCts.Token.IsCancellationRequestedだけでは、どちらが発火源か区別できないため）。
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stallCts.CancelAfter(_stallTimeout);

        try
        {
            using var response = await _http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, stallCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateDownloadOutcome(
                    UpdateDownloadStatus.Failed, $"ダウンロードに失敗しました（HTTP {(int)response.StatusCode}）。");
            }

            var totalBytes = response.Content.Headers.ContentLength;

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var readBytes = 0L;
            await using (var source = await response.Content.ReadAsStreamAsync(stallCts.Token).ConfigureAwait(false))
            await using (var target = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), stallCts.Token).ConfigureAwait(false)) > 0)
                {
                    // バイトを受け取れた＝停滞していない証拠なので、ここでタイマーを延長する。
                    // CancelAfterは呼ぶたびに以前の予約を破棄して積み直すため、「合計時間」では
                    // なく「直近の無通信時間」だけがStallTimeoutと比較される。
                    stallCts.CancelAfter(_stallTimeout);

                    await target.WriteAsync(buffer.AsMemory(0, read), stallCts.Token).ConfigureAwait(false);
                    readBytes += read;
                    if (totalBytes is > 0)
                    {
                        progress?.Report(Math.Clamp((double)readBytes / totalBytes.Value, 0.0, 1.0));
                    }
                }
            }

            progress?.Report(1.0);
            return new UpdateDownloadOutcome(UpdateDownloadStatus.Success);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 利用者が「中断」ボタンを押した経路。停滞検出のタイマーがたまたま同時に
            // 発火していたとしても、利用者の意思による中断を優先して報告する。
            TryDeletePartialFile(destinationPath);
            return new UpdateDownloadOutcome(UpdateDownloadStatus.Cancelled, "ダウンロードを中断しました。");
        }
        catch (OperationCanceledException)
        {
            // ctはまだキャンセルされていないため、発火元は停滞検出の内部タイマーと確定できる。
            TryDeletePartialFile(destinationPath);
            return new UpdateDownloadOutcome(
                UpdateDownloadStatus.Failed,
                $"サーバーからの応答が{_stallTimeout.TotalSeconds:0}秒間ありませんでした。ネットワーク状況を確認して再試行してください。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDeletePartialFile(destinationPath);
            // 実機不具合対応: 以前はex.Messageをそのまま返しており、「The proxy tunnel request
            // to proxy '...' failed...」のような英語の生の例外メッセージがダイアログに
            // そのまま出ていた（名前解決不能・プロキシ到達不能等）。他のI/O例外と同じく
            // ExceptionMessages.Describeへ通し、日本語の理由＋（詳細: 原文）へ揃える。
            return new UpdateDownloadOutcome(UpdateDownloadStatus.Failed, ExceptionMessages.Describe(ex));
        }
    }

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 掃除に失敗しても致命的ではない（次回のダウンロードで上書きされる）。
        }
    }
}
