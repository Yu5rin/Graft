using System.IO;
using System.Reflection;
using Graft.Core.Update;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="SettingsViewModel"/> の分割ファイル（1ファイル400行上限）。
///
/// 機能追加（自動更新）: 「更新」の一連の流れ
/// （確認 → 見つかれば確認ダイアログ → 書き込み権限確認 → ダウンロード＋進捗表示 →
/// SHA256/ZIP検証 → 自己置き換え → 未保存の確認 → 再起動要求）をすべてここに集約する。
/// 実際のネットワーク通信・ファイル検証・自己置き換えは<see cref="Graft.Core.Update"/>名前空間の
/// 各クラス（Avalonia非依存・単体テスト対象）に委ね、ここではダイアログでの確認・進捗表示・
/// 結果の通知・再起動要求（<see cref="RestartRequested"/>、SettingsViewModel.DataDirectory.csの
/// データ保存先移行と同じイベントを共用する。「後始末→新プロセス起動→旧プロセス終了」の
/// 実処理はView側（<see cref="Views.SettingsWindow"/>経由で<see cref="Graft.App.RequestRestart"/>）
/// が担う、という役割分担も同じ）だけを担う。
///
/// 通信は「起動時1日1回まで」（<see cref="CheckForUpdateOnStartupIfDueAsync"/>、
/// <see cref="StartupCoordinator.StartAsync"/>からのみ呼ばれる）と「今すぐ更新を確認」ボタン
/// （<see cref="CheckForUpdateNowCommand"/>）でのみ発生し、それ以外の経路は無い。
/// </summary>
public sealed partial class SettingsViewModel
{
    private IReleaseFeed _releaseFeed = null!;
    private IUpdateDownloader _updateDownloader = null!;
    private IExternalLinkLauncher _externalLinks = null!;
    private UpdateChecker _updateChecker = null!;
    private UpdateInstallPipeline _updateInstallPipeline = null!;
    private CancellationTokenSource? _updateDownloadCts;

    private bool _isUpdateBusy;
    private string? _updateStatusMessage;
    private double _updateProgressPercent;
    private bool _isUpdateDownloading;

    /// <summary>
    /// 更新の再起動前に未保存の編集を確認するための差し替え口。<see cref="SettingsViewModel"/>は
    /// <see cref="Views.StartupCoordinator.StartAsync"/>内でShellViewModelより先に生成されるため
    /// コンストラクタでは渡せず、ShellViewModel生成後にStartupCoordinator側が設定する
    /// （<c>() =&gt; shellViewModel.Editor.CloseAllAsync()</c>。未保存があれば保存/破棄/キャンセルの
    /// 確認ダイアログが出る既存の仕組みをそのまま流用する）。未設定（null）の間は
    /// 「確認済み扱い（true）」として続行する（設定画面単体でのテスト・利用を妨げないため）。
    /// </summary>
    public Func<Task<bool>>? ConfirmUnsavedDocumentsAsync { get; set; }

    /// <summary>「今すぐ更新を確認」ボタン。</summary>
    public AsyncRelayCommand CheckForUpdateNowCommand { get; private set; } = null!;

    public bool IsUpdateBusy { get => _isUpdateBusy; private set => SetProperty(ref _isUpdateBusy, value); }
    public string? UpdateStatusMessage { get => _updateStatusMessage; private set => SetProperty(ref _updateStatusMessage, value); }
    public double UpdateProgressPercent { get => _updateProgressPercent; private set => SetProperty(ref _updateProgressPercent, value); }
    public bool IsUpdateDownloading { get => _isUpdateDownloading; private set => SetProperty(ref _isUpdateDownloading, value); }

    /// <summary>現在のバージョン表示。<see cref="Views.AboutView"/>と同じ取得方法（実行アセンブリのバージョン）。</summary>
    public string CurrentVersionText => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "不明";

    private void InitializeUpdateFeature(
        AppPaths appPaths, IExternalLinkLauncher? externalLinks,
        IReleaseFeed? releaseFeed, IUpdateDownloader? updateDownloader)
    {
        _externalLinks = externalLinks ?? new NullExternalLinkLauncher();
        _releaseFeed = releaseFeed ?? new GitHubReleaseFeed();
        _updateDownloader = updateDownloader ?? new HttpUpdateDownloader();
        _updateChecker = new UpdateChecker(_releaseFeed, new UpdateCheckStateStore(appPaths));
        _updateInstallPipeline = new UpdateInstallPipeline(_updateDownloader);

        CheckForUpdateNowCommand = new AsyncRelayCommand(
            () => CheckForUpdateAsync(isManual: true), () => !_isUpdateBusy, context: "更新の確認");
    }

    /// <summary>
    /// <see cref="Views.StartupCoordinator.StartAsync"/>から、メインウィンドウ表示後に
    /// fire-and-forgetで呼ばれる（要件: 通信は非同期で行い起動をブロックしないこと）。
    /// 設定がオフ、または前回確認から24時間未満なら通信自体を行わない
    /// （<see cref="UpdateChecker.CheckOnStartupAsync"/>参照）。
    /// </summary>
    public async Task CheckForUpdateOnStartupIfDueAsync()
    {
        if (!_updateCheckOnStartup) return;
        await CheckForUpdateAsync(isManual: false).ConfigureAwait(true);
    }

    private async Task CheckForUpdateAsync(bool isManual)
    {
        IsUpdateBusy = true;
        UpdateStatusMessage = "更新を確認しています…";
        try
        {
            var userAgent = $"Graft/{CurrentVersionText}";
            var result = isManual
                ? await _updateChecker.CheckNowAsync(_updateCheckUrl, CurrentVersionText, userAgent, CancellationToken.None).ConfigureAwait(true)
                : await _updateChecker.CheckOnStartupAsync(_updateCheckUrl, CurrentVersionText, userAgent, CancellationToken.None).ConfigureAwait(true);

            switch (result.Status)
            {
                case UpdateCheckStatus.NotDue:
                    UpdateStatusMessage = $"現在のバージョン: {CurrentVersionText}";
                    break;
                case UpdateCheckStatus.Failed:
                    // 要件: 通信の失敗は握りつぶして「確認できなかった」で済ませ、起動を妨げない。
                    // 手動確認時は押した本人が状況を知りたいはずなので、理由を画面に残す。
                    UpdateStatusMessage = isManual
                        ? result.ErrorMessage
                        : $"現在のバージョン: {CurrentVersionText}";
                    break;
                case UpdateCheckStatus.UpToDate:
                    UpdateStatusMessage = $"最新版です（現在のバージョン: {CurrentVersionText}）。";
                    break;
                case UpdateCheckStatus.UpdateAvailable:
                    UpdateStatusMessage = $"新しいバージョン {result.Release!.TagName} が利用可能です（現在: {CurrentVersionText}）。";
                    await OfferUpdateAsync(result.Release!).ConfigureAwait(true);
                    break;
            }
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    /// <summary>
    /// 見つかった更新を案内し、同意が得られれば<see cref="RunUpdateAsync"/>へ進む。
    /// データ保存先移行完了ダイアログの「再起動」ボタンと同じ<see
    /// cref="IDialogService.ShowActionMessageAsync"/>（単一アクションボタン。×で閉じれば
    /// 「今回はしない」として扱う）を使う。
    /// </summary>
    private async Task OfferUpdateAsync(GitHubReleaseInfo release)
    {
        var proceed = await _dialogService.ShowActionMessageAsync(
            "更新の確認",
            $"新しいバージョン {release.TagName} が利用可能です（現在: {CurrentVersionText}）。" +
            $"ダウンロードして更新しますか？{Environment.NewLine}{Environment.NewLine}リリースページ: {release.HtmlUrl}",
            "今すぐ更新")
            .ConfigureAwait(true);
        if (!proceed) return;

        await RunUpdateAsync(release).ConfigureAwait(true);
    }

    private async Task RunUpdateAsync(GitHubReleaseInfo release)
    {
        // 指示書の最重要事項: Program Files 等へ書き込めない場合は自動更新をあきらめ、
        // 手動更新の案内（リリースページを開く）へ誘導する。既存のCanWriteToBaseDirectory
        // （課題1の書き込み権限確認）をそのまま再利用する。
        if (!_appPaths.CanWriteToBaseDirectory())
        {
            var openReleasePage = await _dialogService.ShowActionMessageAsync(
                "自動更新できません",
                $"データ保存先（{_appPaths.BaseDirectory}）へ書き込めないため、自動更新できませんでした。" +
                "Program Files 等、書き込みが制限されたフォルダに置かれている可能性があります。" +
                "リリースページから配布物をダウンロードし、手動で置き換えてください。",
                "リリースページを開く")
                .ConfigureAwait(true);
            if (openReleasePage && !string.IsNullOrEmpty(release.HtmlUrl))
            {
                _externalLinks.Open(release.HtmlUrl);
            }
            return;
        }

        var asset = release.FindAssetByNameSuffix(UpdateFiles.WindowsAssetNameSuffix);
        if (asset is null)
        {
            await _dialogService.ShowMessageAsync(
                "更新できません", "このリリースにWindows版の配布物（-win-x64.zip）が見つかりませんでした。").ConfigureAwait(true);
            return;
        }

        IsUpdateBusy = true;
        IsUpdateDownloading = true;
        UpdateProgressPercent = 0;
        UpdateStatusMessage = "ダウンロードしています…";
        _updateDownloadCts = new CancellationTokenSource();
        try
        {
            var workDir = Path.Combine(Path.GetTempPath(), "GraftUpdate", Guid.NewGuid().ToString("N"));
            var progress = new Progress<double>(p => UpdateProgressPercent = Math.Round(p * 100, 1));

            var installResult = await _updateInstallPipeline
                .RunAsync(asset, _appPaths.BaseDirectory, workDir, progress, _updateDownloadCts.Token)
                .ConfigureAwait(true);

            if (!installResult.Success)
            {
                UpdateStatusMessage = DescribeInstallFailure(installResult);
                if (installResult.Status != UpdateInstallStatus.Cancelled)
                {
                    await _dialogService.ShowMessageAsync("更新に失敗しました", UpdateStatusMessage!).ConfigureAwait(true);
                }
                return;
            }

            await FinishInstallAndRequestRestartAsync(release).ConfigureAwait(true);
        }
        finally
        {
            IsUpdateBusy = false;
            IsUpdateDownloading = false;
            _updateDownloadCts?.Dispose();
            _updateDownloadCts = null;
        }
    }

    /// <summary>
    /// ファイルの置き換えまでが成功した後の後始末。未保存確認 → 再起動可否確認 →
    /// 「再起動」ボタン確認 → <see cref="RestartRequested"/>、の順に進める。
    /// </summary>
    private async Task FinishInstallAndRequestRestartAsync(GitHubReleaseInfo release)
    {
        UpdateStatusMessage = "更新ファイルの準備ができました。";

        // 要件: 更新には再起動が伴うため保存を促す（既存の終了時処理と同種の確認の流用）。
        if (ConfirmUnsavedDocumentsAsync is { } confirmUnsaved)
        {
            var confirmed = await confirmUnsaved().ConfigureAwait(true);
            if (!confirmed)
            {
                UpdateStatusMessage = "更新ファイルの準備は完了しましたが、保存の確認でキャンセルされたため再起動していません。" +
                    "次回Graftを起動したときに反映されます。";
                return;
            }
        }

        if (!AppRestart.CanRestart())
        {
            await _dialogService.ShowMessageAsync("再起動できません",
                "更新ファイルの準備はできましたが、実行ファイルの場所を特定できず自動的に再起動できませんでした。" +
                "手動でGraftを再起動してください。")
                .ConfigureAwait(true);
            return;
        }

        var restartConfirmed = await _dialogService.ShowActionMessageAsync(
            "更新の準備ができました",
            $"バージョン {release.TagName} の準備ができました。再起動して更新を完了しますか？",
            "今すぐ再起動")
            .ConfigureAwait(true);
        if (!restartConfirmed) return;

        RestartRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>ダウンロード中の「中断」ボタン用。</summary>
    public void CancelUpdateDownload() => _updateDownloadCts?.Cancel();

    private static string DescribeInstallFailure(UpdateInstallResult result) => result.Status switch
    {
        UpdateInstallStatus.Cancelled => "更新を中断しました。",
        UpdateInstallStatus.DownloadFailed => $"ダウンロードに失敗しました。{result.ErrorMessage}",
        UpdateInstallStatus.ChecksumUnavailable => result.ErrorMessage ?? "配布物の検証情報が取得できませんでした。",
        UpdateInstallStatus.ChecksumMismatch => result.ErrorMessage ?? "配布物の検証に失敗しました。",
        UpdateInstallStatus.UnexpectedZipContents => result.ErrorMessage ?? "配布物の中身が想定と異なっていました。",
        UpdateInstallStatus.InstallFailed => $"ファイルの置き換えに失敗しました。{result.ErrorMessage}",
        _ => result.ErrorMessage ?? "更新に失敗しました。",
    };
}
