namespace Graft.Core.Update;

/// <summary>インストール結果の種別。</summary>
public enum UpdateInstallStatus
{
    Success,
    Cancelled,
    DownloadFailed,

    /// <summary>GitHubのアセットにSHA256のdigestが無く、安全のため検証できないと判断した。</summary>
    ChecksumUnavailable,

    /// <summary>SHA256が一致しなかった（改ざん・破損の疑い）。</summary>
    ChecksumMismatch,

    /// <summary>ZIPの中身が想定外だった（不足・過剰・重複のいずれか）。</summary>
    UnexpectedZipContents,

    /// <summary>自己置き換え（ファイルの入れ替え）自体が失敗した。ロールバック済み。</summary>
    InstallFailed,
}

/// <summary>インストール結果。</summary>
public sealed record UpdateInstallResult(UpdateInstallStatus Status, string? ErrorMessage = null)
{
    public bool Success => Status == UpdateInstallStatus.Success;
}

/// <summary>
/// 「ダウンロード → SHA256検証 → ZIP内容検証 → 展開 → 自己置き換え」までの一連の流れ。
/// UIの判断（確認ダイアログ・進捗表示・再起動要求）は呼び出し側（<c>SettingsViewModel.Update.cs</c>）が
/// 担い、ここではファイル操作と検証だけを行う（単体テスト容易性のため。Avalonia等のUI層に
/// 一切依存しない）。
/// </summary>
public sealed class UpdateInstallPipeline
{
    private readonly IUpdateDownloader _downloader;
    private readonly IUpdateFileSystem _fileSystem;

    public UpdateInstallPipeline(IUpdateDownloader downloader, IUpdateFileSystem? fileSystem = null)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _fileSystem = fileSystem ?? new RealUpdateFileSystem();
    }

    /// <param name="asset">Windows版配布物ZIPのアセット情報（<see cref="GitHubReleaseInfo.FindAssetByNameSuffix"/>）。</param>
    /// <param name="installDirectory">Graft.exeが実際に置かれているフォルダ。</param>
    /// <param name="workDirectory">
    /// ダウンロード先・展開先として使う専用の一時フォルダ（呼び出し側が用意する。
    /// <paramref name="installDirectory"/>とは別の場所にすること。settings.json等の利用者データが
    /// あるフォルダを一切経由させないため）。処理完了後（成功・失敗いずれも）このフォルダの
    /// 中身は掃除する。
    /// </param>
    public async Task<UpdateInstallResult> RunAsync(
        GitHubReleaseAsset asset,
        string installDirectory,
        string workDirectory,
        IProgress<double>? downloadProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asset);
        Directory.CreateDirectory(workDirectory);
        var zipPath = Path.Combine(workDirectory, "graft-update.zip");
        var stagingDir = Path.Combine(workDirectory, "staged");

        try
        {
            var download = await _downloader.DownloadAsync(asset.BrowserDownloadUrl, zipPath, downloadProgress, ct)
                .ConfigureAwait(false);
            if (download.Status == UpdateDownloadStatus.Cancelled)
            {
                return new UpdateInstallResult(UpdateInstallStatus.Cancelled, download.ErrorMessage);
            }
            if (download.Status != UpdateDownloadStatus.Success)
            {
                return new UpdateInstallResult(UpdateInstallStatus.DownloadFailed, download.ErrorMessage);
            }

            // 【SHA256検証】digestが無い・解釈できない場合はインストールしない（安全側）。
            var expectedHash = Sha256Verifier.ExtractSha256(asset.Digest);
            if (expectedHash is null)
            {
                return new UpdateInstallResult(
                    UpdateInstallStatus.ChecksumUnavailable,
                    "配布物の整合性情報（SHA256）が取得できなかったため、安全のため更新を中止しました。");
            }

            var actualHash = await Sha256Verifier.ComputeHexAsync(zipPath, ct).ConfigureAwait(false);
            if (!Sha256Verifier.Matches(actualHash, expectedHash))
            {
                return new UpdateInstallResult(
                    UpdateInstallStatus.ChecksumMismatch,
                    "ダウンロードしたファイルの検証（SHA256）に失敗しました。通信の途中で壊れたか、改ざんされている可能性があるため更新を中止しました。");
            }

            var validation = UpdateZipInspector.Validate(zipPath);
            if (!validation.IsValid)
            {
                return new UpdateInstallResult(UpdateInstallStatus.UnexpectedZipContents, validation.ErrorMessage);
            }

            UpdateZipInspector.ExtractTo(zipPath, validation.EntryByFileName!, stagingDir);

            var install = SelfUpdateInstaller.Install(installDirectory, stagingDir, _fileSystem);
            return install.Success
                ? new UpdateInstallResult(UpdateInstallStatus.Success)
                : new UpdateInstallResult(UpdateInstallStatus.InstallFailed, install.ErrorMessage);
        }
        finally
        {
            TryCleanup(zipPath, isDirectory: false);
            TryCleanup(stagingDir, isDirectory: true);
        }
    }

    private static void TryCleanup(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一時フォルダの掃除失敗は致命的ではない。
        }
    }
}
