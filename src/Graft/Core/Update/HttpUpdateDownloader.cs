using System.Net.Http;

namespace Graft.Core.Update;

/// <summary>
/// <see cref="IUpdateDownloader"/>の実通信実装。配布物ZIP（50MB超）を一時フォルダへ
/// ストリーミングで書き出しながら進捗を報告する。<see cref="CancellationToken"/>による中断に
/// 対応し、中断・失敗いずれの場合も書きかけの部分ファイルを削除してから返る。
/// </summary>
public sealed class HttpUpdateDownloader : IUpdateDownloader
{
    // ダウンロード自体は数十MBに及び、低速回線では時間がかかりうるため、HttpClient側の
    // 固定タイムアウトは設けず（Timeout.InfiniteTimeSpan）、呼び出し側が渡すCancellationTokenの
    // みで中断を制御する（要件: 中断できるようにすること）。
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private const int BufferSize = 81920;

    public async Task<UpdateDownloadOutcome> DownloadAsync(
        string url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new UpdateDownloadOutcome(UpdateDownloadStatus.Failed, "HTTPS以外のダウンロード元は許可していません。");
        }

        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateDownloadOutcome(
                    UpdateDownloadStatus.Failed, $"ダウンロードに失敗しました（HTTP {(int)response.StatusCode}）。");
            }

            var totalBytes = response.Content.Headers.ContentLength;

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var readBytes = 0L;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
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
        catch (OperationCanceledException)
        {
            TryDeletePartialFile(destinationPath);
            return new UpdateDownloadOutcome(UpdateDownloadStatus.Cancelled, "ダウンロードを中断しました。");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDeletePartialFile(destinationPath);
            return new UpdateDownloadOutcome(UpdateDownloadStatus.Failed, ex.Message);
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
