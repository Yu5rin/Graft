using Graft.Core.Update;
using Graft.Infra;

namespace Graft.ViewModels;

/// <summary>確認なしの自動更新が、ステータスバーに出す通知の種別。</summary>
public enum AutoUpdateNoticeKind
{
    /// <summary>入れ替えが済んだ。「今すぐ再起動」で切り替えられる。</summary>
    Installed,

    /// <summary>失敗した。いまの版のまま動いている。</summary>
    Failed,
}

/// <summary>
/// ステータスバーに出す自動更新の通知。
/// </summary>
/// <param name="Kind">種別。</param>
/// <param name="Text">ステータスバーに出す1行。</param>
/// <param name="Detail">ツールチップに出す詳しい説明。</param>
public sealed record AutoUpdateNotice(AutoUpdateNoticeKind Kind, string Text, string Detail);

/// <summary>
/// <see cref="SettingsViewModel"/> の分割ファイル（1ファイル400行上限）。
///
/// 機能追加（1.0.25）: 「確認なしで自動更新する」（<see cref="UpdateAutoInstall"/>）。
/// 起動時の更新確認で新しい版が見つかり、この設定がオンのとき、確認ダイアログを出さずに裏で
/// ダウンロード・検証・入れ替えまで進める。ダウンロードから入れ替えまでの実処理は、確認ダイアログ
/// 経由の更新と同じ<c>ExecuteInstallAsync</c>・<see cref="UpdateInstallPipeline"/>を通る
/// （重複実装しない。ここに書くのは、進めてよいかの判断・完了／失敗の知らせ方・再起動の入口だけ）。
///
/// 【勝手に再起動しない理由】 入れ替えが済んでも、実行中の版はそのまま動き続ける（実行中のexeは
/// 名前を変えて退避しただけで、読み込み済みの内容は変わらない）。作業の途中で画面が消えて
/// 再起動することが、確認なしの更新でいちばん困る副作用なので、再起動は利用者が選ぶまで行わない。
/// 選ばなくても、次に起動したときに新しい版になる（退避したファイルの掃除は
/// <see cref="PendingUpdateCleanup"/>が次回起動時に行う）。
///
/// 【知らせ方】 ダイアログは作業を止めるので使わず、ステータスバーに1行の通知を出す
/// （<see cref="CurrentAutoUpdateNotice"/>、StartupCoordinatorが<see cref="AutoUpdateNoticeChanged"/>を
/// ShellViewModelへ橋渡しする）。適用直後の「元に戻す」通知のような数秒で消える通知にしないのは、
/// 起動直後に裏で終わるため、席を外していると見逃すから。利用者が閉じるまで、または再起動まで残す。
/// </summary>
public sealed partial class SettingsViewModel
{
    private string? _installedPendingRestartTag;
    private AutoUpdateNotice? _currentAutoUpdateNotice;

    /// <summary>
    /// 入れ替え先のフォルダ（実行ファイルのあるフォルダ）を決める関数。既定は
    /// <see cref="AppRestart.TryResolveExecutableDirectory"/>で、本番の挙動はこれ以外にない。
    /// テスト（Graft.UiTests）が、テストホスト自身のフォルダを書き換えずに入れ替えの経路を
    /// 通せるよう、一時フォルダを返す関数へ差し替えられるようにしてある
    /// （<see cref="UpdatePlatform"/>と同じ作法）。
    /// </summary>
    internal Func<string?> InstallDirectoryResolver { get; set; } = () => AppRestart.TryResolveExecutableDirectory();

    /// <summary>
    /// いまステータスバーに出すべき自動更新の通知。無ければnull。値が変わるたびに
    /// <see cref="AutoUpdateNoticeChanged"/>が発火する。
    /// </summary>
    public AutoUpdateNotice? CurrentAutoUpdateNotice
    {
        get => _currentAutoUpdateNotice;
        private set
        {
            if (!SetProperty(ref _currentAutoUpdateNotice, value)) return;
            AutoUpdateNoticeChanged?.Invoke(this, value);
        }
    }

    /// <summary>
    /// <see cref="CurrentAutoUpdateNotice"/>が変わった（出た・消えた）。引数が新しい通知で、消えたときはnull。
    /// SettingsViewModelはShellViewModelより先に生成され、互いを直接知らない（通知の表示は
    /// ShellViewModelのステータスバー）ため、StartupCoordinatorがこのイベントで橋渡しする。
    /// </summary>
    public event EventHandler<AutoUpdateNotice?>? AutoUpdateNoticeChanged;

    /// <summary>通知を閉じる（ステータスバーの「×」）。</summary>
    public void DismissAutoUpdateNotice() => CurrentAutoUpdateNotice = null;

    /// <summary>
    /// 同じ版の入れ替えが、この起動中にすでに済んでいて、再起動だけが済んでいないか。
    /// 実行中の版は再起動まで古いままなので、更新を確認し直すと同じ版が「新しい」と判定される。
    /// </summary>
    private bool IsInstalledAndWaitingForRestart(GitHubReleaseInfo release)
        => _installedPendingRestartTag is not null
            && string.Equals(_installedPendingRestartTag, release.TagName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 完了通知の「今すぐ再起動」。確認ダイアログ経由の更新と同じ前提の確認
    /// （未保存の編集の確認 → 再起動できる環境か）を通ってから、再起動を要求する。
    /// 利用者がボタンを押したこと自体が再起動の意思表示なので、「再起動しますか？」の
    /// 確認ダイアログは重ねて出さない。未保存の確認でキャンセルされたら再起動せず、
    /// 通知は残す（もう一度押せる）。
    /// </summary>
    public async Task RestartForInstalledUpdateAsync()
    {
        if (!await ConfirmRestartPreconditionsAsync().ConfigureAwait(true)) return;

        Logger?.Info("update", "自動更新: 完了通知の「今すぐ再起動」が押されました。");
        RestartRequested?.Invoke(this, new RestartRequestedEventArgs(RestartReason.AutoUpdateNotice));
    }

    /// <summary>
    /// 見つかった版を、確認なしで入れ替える。戻り値は「この版の扱いをここで終えたか」で、
    /// falseなら従来の確認ダイアログ（<see cref="OfferUpdateAsync"/>）へ進む。
    /// <list type="bullet">
    /// <item>false: <see cref="AutoUpdatePolicy"/>が確認なしを許さない場合と、入れ替え先の
    /// 決定・書き込みの確認（<see cref="ResolveInstallPlan"/>）で問題があった場合。どちらも
    /// ダイアログに戻した理由をupdateログにinfoで残す。</item>
    /// <item>true: 入れ替えが成功した、利用者が中断した、失敗した（失敗はいまの版のまま続ける）。</item>
    /// </list>
    /// </summary>
    private async Task<bool> TryAutoInstallAsync(GitHubReleaseInfo release)
    {
        var tag = release.TagName;

        var decision = AutoUpdatePolicy.Evaluate(release, CurrentVersionText, UpdatePlatform, _updateCheckUrl);
        if (!decision.CanProceed)
        {
            LogAutoInstallFallback(tag, decision.Reason!);
            return false;
        }

        // 実行ファイルのフォルダの特定・書き込み・配布物の選択は、確認ダイアログ経由の更新と
        // 同じResolveInstallPlanで調べる。問題があれば、そのダイアログ側がこれまでどおり
        // 利用者に事情を説明して手動更新へ誘導する。
        var plan = ResolveInstallPlan(release);
        if (plan.Problem != InstallPlanProblem.None)
        {
            LogAutoInstallFallback(tag, plan.Problem switch
            {
                InstallPlanProblem.NoInstallDirectory => "実行ファイルの場所を特定できないため",
                InstallPlanProblem.NotWritable => $"実行ファイルのフォルダ（{plan.InstallDirectory}）へ書き込めないため",
                _ => "このOS向けの配布物が見つからないため",
            });
            return false;
        }

        Logger?.Info("update",
            $"自動更新: 「確認なしで自動更新する」設定のため、確認ダイアログを出さずに {tag} のダウンロードと入れ替えを始めます" +
            "（SHA256の照合とダウンロード元ホストの検証、ZIP内容の検査は通常どおり行います。再起動はしません）。");

        UpdateInstallResult result;
        try
        {
            result = await ExecuteInstallAsync(release, plan).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 裏で動いているため、想定外の例外で起動時の確認ごと落とさない（利用者は何も
            // していない）。いまの版のまま続け、失敗として扱う。
            Logger?.Error("update", $"自動更新: {tag} の入れ替え中に想定外の例外が起きました: {ex}");
            UpdateStatusMessage = "自動更新に失敗しました。";
            result = new UpdateInstallResult(UpdateInstallStatus.InstallFailed, ex.Message);
        }

        if (result.Success)
        {
            await OnAutoInstallSucceededAsync(release).ConfigureAwait(true);
        }
        else if (result.Status == UpdateInstallStatus.Cancelled)
        {
            // 「中断」ボタン（バージョン情報タブ）を押したのは利用者本人。失敗として知らせない。
            Logger?.Info("update", $"自動更新: {tag} のダウンロードが中断されました。いまの版のまま続けます。");
        }
        else
        {
            await OnAutoInstallFailedAsync(release, result).ConfigureAwait(true);
        }

        return true;
    }

    private void LogAutoInstallFallback(string tag, string reason)
        => Logger?.Info("update",
            $"自動更新: {reason}、確認なしでは進めず、従来どおり確認ダイアログで案内します（{tag}）。");

    private async Task OnAutoInstallSucceededAsync(GitHubReleaseInfo release)
    {
        var version = VersionDisplay(release.TagName);
        UpdateStatusMessage = $"Graft を {version} に更新しました。次回の起動から新しい版になります。";
        Logger?.Info("update",
            $"自動更新: {release.TagName} の入れ替えが完了しました。再起動はしていません（次回の起動、または完了通知の「今すぐ再起動」で新しい版になります）。");

        // 成功したので、この版について過去に残した「失敗を通知済み」の記録は役目を終えた。
        await TryUpdateStateAsync(s => s.AutoInstallFailedTag is null ? s : s with { AutoInstallFailedTag = null })
            .ConfigureAwait(true);

        CurrentAutoUpdateNotice = new AutoUpdateNotice(
            AutoUpdateNoticeKind.Installed,
            $"Graft を {version} に更新しました。次回の起動から新しい版になります",
            "新しい版への入れ替えが済んでいます。「今すぐ再起動」で今すぐ切り替えられます" +
            "（未保存の変更があれば確認します）。再起動しなくても、次にGraftを起動したときから新しい版になります。");
    }

    private async Task OnAutoInstallFailedAsync(GitHubReleaseInfo release, UpdateInstallResult result)
    {
        var tag = release.TagName;
        var reason = UpdateStatusMessage ?? DescribeInstallFailure(result);
        Logger?.Warn("update",
            $"自動更新: {tag} の入れ替えに失敗しました（{result.Status}）。いまの版のまま動作を続けます。理由: {reason}");

        // 通知は同じ版につき1回だけ。ログには毎回残すが、直らない失敗（例: ウイルス対策ソフトが
        // 入れ替えを止めている）を起動のたびに見せない。記録を読めない・書けないときは、通知を
        // 出さないよりは出す側（失敗に気づけない方が困る）に倒す。
        var alreadyNotified = false;
        try
        {
            var state = await _updateCheckStateStore.LoadAsync().ConfigureAwait(true);
            alreadyNotified = string.Equals(state.AutoInstallFailedTag, tag, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger?.Warn("update", $"自動更新: 失敗の通知済みの記録を読めませんでした（{ex.GetType().Name}）。通知を出します。");
        }

        if (alreadyNotified)
        {
            Logger?.Info("update", $"自動更新: {tag} の失敗は前回までに通知済みのため、ステータスバーには出しません。");
            return;
        }

        await TryUpdateStateAsync(s => s with { AutoInstallFailedTag = tag }).ConfigureAwait(true);

        CurrentAutoUpdateNotice = new AutoUpdateNotice(
            AutoUpdateNoticeKind.Failed,
            "自動更新に失敗しました。設定の「今すぐ更新を確認」から手動で更新できます",
            $"{reason} Graft はいまの版のまま動いています。「設定」の「バージョン情報」にある" +
            "「今すぐ更新を確認」から、手動で更新できます。同じ版の失敗は、次の起動以降は通知しません。");
    }

    /// <summary>
    /// update-check.jsonの内部状態を更新する。失敗しても更新の流れは止めない（内部状態は
    /// 通知の重複を避ける補助にすぎない）。
    /// </summary>
    private async Task TryUpdateStateAsync(Func<UpdateCheckState, UpdateCheckState> change)
    {
        try
        {
            await _updateCheckStateStore.UpdateAsync(change).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger?.Warn("update", $"自動更新: 更新の内部状態（update-check.json）を保存できませんでした（{ex.GetType().Name}）。");
        }
    }

    /// <summary>タグ（"v1.0.25"）から、利用者に見せる版の表記（"1.0.25"）を作る。</summary>
    private static string VersionDisplay(string tag)
        => tag.Length > 1 && (tag[0] == 'v' || tag[0] == 'V') ? tag[1..] : tag;
}
