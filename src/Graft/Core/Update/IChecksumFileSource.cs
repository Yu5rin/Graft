namespace Graft.Core.Update;

/// <summary>
/// 照合値ファイルの取得結果。成功なら<see cref="Sha256Hex"/>（64桁の小文字16進）、失敗なら
/// <see cref="FailureReason"/>（ログ用の日本語1文。利用者には出さない）。
/// </summary>
public sealed record ChecksumFileResult(string? Sha256Hex, string? FailureReason)
{
    public bool Success => Sha256Hex is not null;

    public static ChecksumFileResult Ok(string sha256Hex) => new(sha256Hex, null);

    public static ChecksumFileResult Fail(string reason) => new(null, reason);
}

/// <summary>
/// 書庫のURLに<c>.sha256</c>を付けたURLから、SHA256の照合値を取る手段の抽象。
///
/// 【なぜ必要か（実機不具合対応、v1.0.28）】 GitHub APIの回数上限（未認証で1時間60回）に当たると
/// digestがAPIから取れず、SHA256の照合を省いて更新していた。<c>github.com/.../releases/download/</c>
/// 配下のファイルはAPIではなく回数上限の対象外なので、リリースに照合値のファイルを添付して
/// そこから取れば、上限に当たっても照合を省かずに済む。
/// テストでは通信しないフェイクに差し替える。実装は失敗を例外にせず、
/// <see cref="ChecksumFileResult.Fail"/>で返す（利用者のキャンセルだけは例外のまま伝える）。
/// </summary>
public interface IChecksumFileSource
{
    /// <param name="checkUrl">更新確認先（ホスト検証の基準。<see cref="UpdateHostPolicy"/>）。</param>
    /// <param name="archiveName">書庫のファイル名。照合値ファイルの中のファイル名と突き合わせる。</param>
    /// <param name="archiveUrl">書庫のダウンロードURL。末尾に<c>.sha256</c>を付けたURLを取りに行く。</param>
    Task<ChecksumFileResult> FetchAsync(
        string checkUrl, string archiveName, string archiveUrl, string userAgent, CancellationToken ct);
}
