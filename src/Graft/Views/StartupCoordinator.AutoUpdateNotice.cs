using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="StartupCoordinator"/> の分割ファイル。「確認なしで自動更新する」の結果を知らせる
/// ステータスバーの通知（<see cref="ShellViewModel"/>側の表示）と、通知の状態を持つ
/// <see cref="SettingsViewModel"/>との橋渡し。
///
/// SettingsViewModelはShellViewModelより先に生成され、互いを直接知らない設計のため
/// （クリップボード監視やグローバルホットキーの表示もStartupCoordinatorが橋渡しする）、
/// 配線はここに置く。起動時の更新確認の呼び出しより前に必ず行うこと（確認は起動直後に
/// 裏で走り、通知が出るのは早ければ数秒後のため）。
/// </summary>
public sealed partial class StartupCoordinator
{
    private void WireAutoUpdateNotice(SettingsViewModel settings, ShellViewModel shell)
    {
        settings.AutoUpdateNoticeChanged += (_, notice) => shell.SetUpdateNotice(notice);
        shell.UpdateNoticeDismissRequested += (_, _) => settings.DismissAutoUpdateNotice();
        shell.UpdateRestartRequested += (_, _) =>
        {
            // 未保存の確認ダイアログを待つ非同期処理。例外が出ても握りつぶさずログに残す
            // （起動時の更新確認の呼び出しと同じ作法）。
            _ = settings.RestartForInstalledUpdateAsync().ContinueWith(
                task => _logger?.Error("update", $"自動更新の完了通知からの再起動に失敗しました: {task.Exception!.GetBaseException()}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        };
    }
}
