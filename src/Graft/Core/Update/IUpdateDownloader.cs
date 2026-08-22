namespace Graft.Core.Update;

/// <summary>ダウンロード結果の種別。</summary>
public enum UpdateDownloadStatus
{
    Success,
    Cancelled,
    Failed,
}

/// <summary>ダウンロード結果。</summary>
public sealed record UpdateDownloadOutcome(UpdateDownloadStatus Status, string? ErrorMessage = null);

/// <summary>
/// 配布物ZIPのダウンロード手段の抽象。50MB超のファイルを扱うため進捗報告と中断（キャンセル）に
/// 対応する。テストでは実際に通信しないフェイク実装に差し替える。
/// </summary>
public interface IUpdateDownloader
{
    /// <param name="url">ダウンロードURL。HTTPS以外は拒否する。</param>
    /// <param name="destinationPath">保存先の絶対パス（一時フォルダ内を想定）。</param>
    /// <param name="progress">0.0〜1.0の進捗（応答にContent-Lengthが無い場合は報告されない）。</param>
    Task<UpdateDownloadOutcome> DownloadAsync(
        string url, string destinationPath, IProgress<double>? progress, CancellationToken ct);
}
