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

    /// <summary>
    /// ダウンロードURLのホストが信頼できないと判断した（セキュリティ点検指摘対応）。
    /// <see cref="UpdateHostPolicy"/>参照。期待ハッシュ（Digest）はダウンロードURLと
    /// 同じJSON応答から来るため、checkUrlを握った側は両方を自由に決められSHA256は
    /// 無力になる。ここでの検証はそれとは独立にホスト自体を確認する。
    /// </summary>
    UntrustedDownloadHost,

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
    /// <param name="installDirectory">
    /// Graft.exeが実際に置かれているフォルダ。
    /// 【注意】<see cref="Infra.AppPaths.BaseDirectory"/>（settings.json等の"データ保存先"。
    /// ポインタファイルがあれば%APPDATA%\Graftを指す）とは別物。混同すると「データ保存先を
    /// ユーザーフォルダへ移動」した環境で自動更新が必ず失敗する不具合を招く
    /// （詳しくは<see cref="Infra.AppRestart.TryResolveExecutableDirectory"/>のXMLコメント参照）。
    /// </param>
    /// <param name="workDirectory">
    /// ダウンロード先・展開先として使う専用の一時フォルダ（呼び出し側が用意する。
    /// <paramref name="installDirectory"/>とは別の場所にすること。settings.json等の利用者データが
    /// あるフォルダを一切経由させないため）。処理完了後（成功・失敗いずれも）このフォルダ自体を
    /// 含めて掃除する（プロセスが生きている間の後始末。プロセスが強制終了・クラッシュした
    /// 場合はここでは掃除できないため、次回起動時の掃除を<see cref="PendingUpdateWorkDirCleanup"/>
    /// が別途担う）。
    /// </param>
    /// <param name="checkUrl">
    /// 更新確認に使った<c>update.checkUrl</c>（設定画面で変更可能）。ダウンロードURLの
    /// ホスト検証（<see cref="UpdateHostPolicy"/>）に使う。期待ハッシュとダウンロードURLは
    /// checkUrlへの同じHTTP応答から来るため、checkUrl自体を書き換えられる攻撃者に対しては
    /// SHA256の一致だけでは配布元の正当性を保証できない（詳しくは<see cref="UpdateHostPolicy"/>
    /// のクラスコメント参照）。
    /// </param>
    public async Task<UpdateInstallResult> RunAsync(
        GitHubReleaseAsset asset,
        string installDirectory,
        string workDirectory,
        string checkUrl,
        IProgress<double>? downloadProgress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asset);

        // 【ダウンロード元ホストの検証】SHA256検証より前に行う。ここで拒否すれば、
        // 信頼できないホストへは一度も接続しない（DNS解決すら発生しない）。
        if (!UpdateHostPolicy.IsAllowedDownloadUrl(checkUrl, asset.BrowserDownloadUrl))
        {
            var host = Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var u) ? u.Host : asset.BrowserDownloadUrl;
            return new UpdateInstallResult(
                UpdateInstallStatus.UntrustedDownloadHost,
                $"ダウンロード元のホスト（{host}）が信頼できないため、更新を中止しました。" +
                "設定画面の「更新確認先URL」が意図したものか確認してください。");
        }

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

            // 【この検証で防げないもの（セキュリティ点検指摘対応・正直な注記）】
            // ここで比較する期待ハッシュ（asset.Digest）は、ダウンロードURL（asset.
            // BrowserDownloadUrl）と同じcheckUrlへのHTTP応答（同じJSON）から取り出している。
            // つまりcheckUrl自体を書き換えられる攻撃者は両方を自分の都合の良い値に決められる
            // ため、この一致は「配布元自体が悪意を持つ場合」の防御にはならない。それを防ぐのは
            // 上でRunAsync冒頭に行っているUpdateHostPolicyによるホスト検証の役割であり、
            // このSHA256検証が実際に守っているのは「通信経路の途中でファイルが壊れて
            // いないこと」だけである。以前はこの不一致時のメッセージに「改ざんされている
            // 可能性がある」と書いていたが、上記の理由でその説明は成立しない
            // （checkUrlが正規のままダウンロードだけが改ざんされる経路は無く、checkUrl自体が
            // 悪意を持つ場合はハッシュも一致してしまいこの分岐に到達しない）ため、成立する
            // 説明（通信起因の破損）だけを利用者に伝える文言へ直した。
            var actualHash = await Sha256Verifier.ComputeHexAsync(zipPath, ct).ConfigureAwait(false);
            if (!Sha256Verifier.Matches(actualHash, expectedHash))
            {
                return new UpdateInstallResult(
                    UpdateInstallStatus.ChecksumMismatch,
                    "ダウンロードしたファイルの検証（SHA256）に失敗しました。ダウンロードが途中で壊れた可能性があります。通信環境を確認してやり直してください。");
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
            // 不具合修正（利用者からの指摘・穴1「一時フォルダの入れ物が残る」）: 以前はzipPathと
            // stagingDirの中身だけを個別に消しており、それらを収めていたworkDirectory自体
            // （%TEMP%\GraftUpdate\<GUID>\）は空フォルダのまま残り続けていた。更新を試みる
            // たびに空フォルダが1つずつ溜まる。workDirectoryをまるごと再帰削除すれば、
            // 中のzipPath・stagingDirも合わせて消えるため、個別のTryCleanup呼び出しは不要になる。
            TryCleanup(workDirectory, isDirectory: true);
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
