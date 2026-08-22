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
/// 通信は「起動時に更新を確認する」設定がオンのときの起動時チェック（<see
/// cref="CheckForUpdateOnStartupAsync"/>、<see cref="StartupCoordinator.StartAsync"/>から
/// のみ呼ばれる。v1.0.12から、起動のたびに必ず1回発生する。かつては前回確認から24時間
/// 未満なら通信しない絞り込みがあったが、チェックボックスの文言「起動時に更新を確認する」
/// と実態が食い違っていたため廃止した）と「今すぐ更新を確認」ボタン（<see
/// cref="CheckForUpdateNowCommand"/>）でのみ発生し、それ以外の経路は無い。
/// </summary>
public sealed partial class SettingsViewModel
{
    private IReleaseFeed _releaseFeed = null!;
    private IUpdateDownloader _updateDownloader = null!;
    private IExternalLinkLauncher _externalLinks = null!;
    private UpdateChecker _updateChecker = null!;
    private UpdateInstallPipeline _updateInstallPipeline = null!;
    private UpdateCheckStateStore _updateCheckStateStore = null!;
    private CancellationTokenSource? _updateDownloadCts;

    private bool _isUpdateBusy;
    private string? _updateStatusMessage;
    private double _updateProgressPercent;
    private bool _isUpdateDownloading;
    private DateTimeOffset? _updateLastCheckedAt;

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

    /// <summary>ダウンロード中に表示する「中断」ボタン。</summary>
    public RelayCommand CancelUpdateDownloadCommand { get; private set; } = null!;

    public bool IsUpdateBusy { get => _isUpdateBusy; private set => SetProperty(ref _isUpdateBusy, value); }
    public string? UpdateStatusMessage { get => _updateStatusMessage; private set => SetProperty(ref _updateStatusMessage, value); }
    public double UpdateProgressPercent { get => _updateProgressPercent; private set => SetProperty(ref _updateProgressPercent, value); }
    public bool IsUpdateDownloading { get => _isUpdateDownloading; private set => SetProperty(ref _isUpdateDownloading, value); }

    /// <summary>現在のバージョン表示。<see cref="Views.AboutView"/>と同じ取得方法（実行アセンブリのバージョン）。</summary>
    public string CurrentVersionText => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "不明";

    /// <summary>
    /// 機能追加（v1.0.11・「起動時に更新チェックされているか分からない」への対応）:
    /// 実際に通信して確認した直近の日時（<see cref="UpdateCheckState.LastCheckedAt"/>と同じ値。
    /// 起動時チェック・手動確認いずれも含み、設定オフでのスキップでは更新されない）。
    /// 一度も確認していなければnull。
    /// </summary>
    public DateTimeOffset? UpdateLastCheckedAt
    {
        get => _updateLastCheckedAt;
        private set => SetProperty(ref _updateLastCheckedAt, value, () => OnPropertyChanged(nameof(UpdateLastCheckedText)));
    }

    /// <summary>
    /// 「バージョン情報」タブに常時表示する「最終確認: yyyy/MM/dd HH:mm」文言（未確認なら
    /// 「未確認」）。指示書どおり、確認したのに何も起きない（＝最新だった）のか、そもそも
    /// 確認していないのかを利用者が区別できるようにするための表示。
    /// </summary>
    public string UpdateLastCheckedText => _updateLastCheckedAt is { } at
        ? $"最終確認: {at.LocalDateTime:yyyy/MM/dd HH:mm}"
        : "最終確認: 未確認";

    private void InitializeUpdateFeature(
        AppPaths appPaths, IExternalLinkLauncher? externalLinks,
        IReleaseFeed? releaseFeed, IUpdateDownloader? updateDownloader)
    {
        _externalLinks = externalLinks ?? new NullExternalLinkLauncher();
        _releaseFeed = releaseFeed ?? new GitHubReleaseFeed();
        _updateDownloader = updateDownloader ?? new HttpUpdateDownloader();
        // UpdateLastCheckedTextの初期表示（RefreshUpdateLastCheckedAsync）用に、UpdateChecker内部と
        // 同じupdate-check.jsonを読むストアをここでも保持する（UpdateChecker自身は状態を
        // 外部へ公開しないため）。書き込みはUpdateChecker側だけが行い、ここでは読み込み専用。
        _updateCheckStateStore = new UpdateCheckStateStore(appPaths);
        _updateChecker = new UpdateChecker(_releaseFeed, _updateCheckStateStore);
        _updateInstallPipeline = new UpdateInstallPipeline(_updateDownloader);

        CheckForUpdateNowCommand = new AsyncRelayCommand(
            () => CheckForUpdateAsync(isManual: true), () => !_isUpdateBusy, context: "更新の確認");
        CancelUpdateDownloadCommand = new RelayCommand(CancelUpdateDownload, () => _isUpdateDownloading);
    }

    /// <summary>
    /// <see cref="Views.StartupCoordinator.StartAsync"/>から、メインウィンドウ表示後に
    /// fire-and-forgetで呼ばれる（要件: 通信は非同期で行い起動をブロックしないこと）。
    /// 「起動時に更新を確認する」設定がオフなら通信自体を行わない。オンなら、v1.0.12から
    /// 起動のたびに必ず通信する（<see cref="UpdateChecker.CheckOnStartupAsync"/>参照。
    /// かつて存在した「前回確認から24時間未満ならスキップ」という絞り込みは、設定画面の
    /// チェックボックスの文言が最初から「起動時に更新を確認する」であり実態と食い違って
    /// いたため廃止し、文言どおり毎回確認するようにした）。
    ///
    /// 機能追加（v1.0.11・「起動時に更新チェックされているか分からない」への対応）: 設定オフで
    /// 通信そのものを行わなかった場合も、ここでその旨をログへ残す（<see
    /// cref="CheckForUpdateAsync"/>側は実際に<see cref="_updateChecker"/>を呼んだ場合しか
    /// ログを残せないため、この早期returnの分だけここで個別に記録する）。
    /// </summary>
    /// <param name="isRestartLaunch">
    /// 仕様変更（v1.0.12・利用者からの追加要望）: このプロセスがテーマ変更・データ保存先の
    /// 移動・自動更新の適用後などでGraft自身が自己再起動して起動したものかどうか
    /// （<see cref="Infra.AppRestart.IsRestartLaunch"/>で起動引数から判定し、<see
    /// cref="Views.StartupCoordinator"/>経由でここへ渡される）。「起動のたびに確認する」は
    /// あくまで利用者が自分の意思でGraftを起動したときの話であり、Graftが自分の都合で
    /// 再起動しただけのときまで確認しに行く理由が無いため、trueなら設定に関わらず
    /// 通信そのものを行わない。連続再起動でGitHub APIへ無駄に何度も問い合わせる事態も、
    /// この除外だけで自然に防げるため、別途時間ベースの間隔ガードは設けていない
    /// （時間ガードは「利用者の意思での起動かどうか」という本質と無関係に短期間の起動
    /// すべてを一律スキップしてしまい、例えば手動で素早く再起動したい場合にも確認できなく
    /// なってしまう。自己再起動かどうかで判定する方が設計として素直）。
    /// </param>
    public async Task CheckForUpdateOnStartupAsync(bool isRestartLaunch)
    {
        if (isRestartLaunch)
        {
            Logger?.Info("update", "起動時の更新確認: 自己再起動のためスキップしました。");
            return;
        }

        if (!_updateCheckOnStartup)
        {
            Logger?.Info("update", "起動時の更新確認: 「起動時に更新を確認する」設定がオフのためスキップしました。");
            return;
        }

        await CheckForUpdateAsync(isManual: false).ConfigureAwait(true);
    }

    private async Task CheckForUpdateAsync(bool isManual)
    {
        // ログ文言・UpdateStatusMessageの両方で使う「どちらの経路からの確認か」の表示名。
        var trigger = isManual ? "手動の更新確認（今すぐ更新を確認）" : "起動時の更新確認";

        IsUpdateBusy = true;
        UpdateStatusMessage = "更新を確認しています…";
        try
        {
            var userAgent = $"Graft/{CurrentVersionText}";
            var result = isManual
                ? await _updateChecker.CheckNowAsync(_updateCheckUrl, CurrentVersionText, userAgent, CancellationToken.None).ConfigureAwait(true)
                : await _updateChecker.CheckOnStartupAsync(_updateCheckUrl, CurrentVersionText, userAgent, CancellationToken.None).ConfigureAwait(true);

            // 機能追加（v1.0.11）: 「バージョン情報」タブの「最終確認」表示を、実際に
            // update-check.jsonへ書き込まれた値（今回の日時）に同期させる。
            // UpdateChecker.CheckNowAsyncは通信の成否に関わらずLastCheckedAtを先に更新する契約
            // （そのクラスのコメント参照）のため、Failedでも「確認しようとした」事実がここに
            // 反映される。
            await RefreshUpdateLastCheckedAsync().ConfigureAwait(true);

            switch (result.Status)
            {
                case UpdateCheckStatus.Failed:
                    // 要件: 通信の失敗は握りつぶして「確認できなかった」で済ませ、起動を妨げない。
                    // 手動確認時は押した本人が状況を知りたいはずなので、理由を画面に残す。
                    // 起動時の自動確認での失敗は、UpToDate等と同じく画面には何も表示しない
                    // （利用者が見ていない場面での通信結果を、わざわざ画面上に出す必要は無い。
                    // 詳細はログに残る）。
                    UpdateStatusMessage = isManual ? result.ErrorMessage : null;
                    Logger?.Warn("update", $"{trigger}: 通信に失敗しました（{result.ErrorMessage}）。");
                    break;
                case UpdateCheckStatus.UpToDate:
                    UpdateStatusMessage = $"最新版です（現在のバージョン: {CurrentVersionText}）。";
                    Logger?.Info("update", $"{trigger}: 確認しました。最新版です（現在: {CurrentVersionText}）。");
                    break;
                case UpdateCheckStatus.UpdateAvailable:
                    UpdateStatusMessage = $"新しいバージョン {result.Release!.TagName} が利用可能です（現在: {CurrentVersionText}）。";
                    Logger?.Info("update", $"{trigger}: 確認しました。新しいバージョンが見つかりました（{result.Release!.TagName}、現在: {CurrentVersionText}）。");
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
        // 不具合修正（自動更新が「データ保存先をユーザーフォルダへ移動」済みの環境で
        // 必ず失敗していた件）: 以前はここで_appPaths.BaseDirectory（データ保存先。
        // datapath.txtポインタがあれば%APPDATA%\Graftを指す）を「実行ファイルのフォルダ」
        // として使っていたが、両者は別物であり、移行済み環境では実際には存在しない
        // フォルダのGraft.exeを退避しようとして必ず失敗していた（詳しい経緯は
        // AppRestart.TryResolveExecutableDirectoryのXMLコメント参照）。
        // インストール先は必ず「実行ファイルが実際に置かれているフォルダ」を使う。
        var installDirectory = AppRestart.TryResolveExecutableDirectory();
        if (installDirectory is null)
        {
            await _dialogService.ShowMessageAsync(
                "自動更新できません",
                "実行ファイルの場所を特定できなかったため、自動更新できませんでした。" +
                "リリースページから配布物をダウンロードし、手動で置き換えてください。")
                .ConfigureAwait(true);
            return;
        }

        // 指示書の最重要事項: Program Files 等へ書き込めない場合は自動更新をあきらめ、
        // 手動更新の案内（リリースページを開く）へ誘導する。書き込み可否は「実際に
        // ファイルを置き換える場所」＝installDirectoryに対して確認する（上記のとおり
        // データ保存先とは別物になりうるため。既存のAppPaths.CanWriteToDirectory
        // （課題1の書き込み権限確認）をディレクトリ引数化して再利用する）。
        if (!AppPaths.CanWriteToDirectory(installDirectory))
        {
            var openReleasePage = await _dialogService.ShowActionMessageAsync(
                "自動更新できません",
                $"実行ファイルのフォルダ（{installDirectory}）へ書き込めないため、自動更新できませんでした。" +
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
            var workDir = Path.Combine(Path.GetTempPath(), UpdateFiles.WorkDirectoryRootName, Guid.NewGuid().ToString("N"));
            var progress = new Progress<double>(p => UpdateProgressPercent = Math.Round(p * 100, 1));

            var installResult = await _updateInstallPipeline
                .RunAsync(asset, installDirectory, workDir, progress, _updateDownloadCts.Token)
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

    /// <summary>
    /// 機能追加（v1.0.11）: update-check.jsonから<see cref="UpdateCheckState.LastCheckedAt"/>を
    /// 読み直し、<see cref="UpdateLastCheckedAt"/>（ひいては<see cref="UpdateLastCheckedText"/>）へ
    /// 反映する。<see cref="SettingsViewModel.InitializeAsync"/>（画面を開いた直後の初期表示）と
    /// <see cref="CheckForUpdateAsync"/>（確認のたびの更新）の両方から呼ばれる。
    /// </summary>
    private async Task RefreshUpdateLastCheckedAsync(CancellationToken ct = default)
    {
        var state = await _updateCheckStateStore.LoadAsync(ct).ConfigureAwait(true);
        UpdateLastCheckedAt = state.LastCheckedAt;
    }

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
