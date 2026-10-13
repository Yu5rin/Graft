using Graft.Infra;
using Graft.Platform;

namespace Graft.Views;

/// <summary>
/// <see cref="StartupCoordinator"/> の分割ファイル（1ファイル400行上限のため）。
///
/// 1.0.26: data フォルダへの自動移行（<see cref="PortableDataLayout.Resolve"/>）の結果を、
/// 確定した <see cref="Logger"/> へ記録し、問題があれば利用者へ知らせる。移行そのものは
/// Logger 生成前に終わっているので、ここでは記録と通知だけを行う。
///
/// 【通知を繰り返さない】移せない理由（書き込み権限、容量不足など）は毎回の起動で同じになりうる。
/// そのたびに大きなダイアログを出すと邪魔なので、同じ理由は一度だけ出す
/// （<see cref="PortableDataLayout.TryMarkNoticeShown"/>）。ログには毎回 Warn で残す。再試行は毎回行う。
/// </summary>
public sealed partial class StartupCoordinator
{
    private async Task ReportPortableDataOutcomeAsync(IDialogService dialogService)
    {
        var outcome = _portableDataOutcome;
        if (outcome is null) return;

        switch (outcome.Result)
        {
            case PortableDataResult.PortableReady:
            case PortableDataResult.CustomLocation:
                return;

            case PortableDataResult.MigratedFromLegacy:
            case PortableDataResult.MigratedFromUserFolder:
                _logger?.Info("startup",
                    $"データを {outcome.BaseDirectory} へ移しました（元: {outcome.SourceDirectory}）。" +
                    "設定・プロジェクト一覧・履歴・バックアップ・ログを移し、元の場所の既知のデータを削除しました。" +
                    (outcome.Detail is null ? string.Empty : " " + outcome.Detail));
                return;
        }

        _logger?.Warn("startup", $"データ保存先: {outcome.Result}: {outcome.Detail}（使用中: {outcome.BaseDirectory}）");
        if (!outcome.ShouldNotifyUser) return;

        var key = $"{outcome.Result}|{outcome.Detail}";
        if (!PortableDataLayout.TryMarkNoticeShown(outcome.BaseDirectory, key)) return;

        var message = outcome.Result switch
        {
            PortableDataResult.BothHaveData or PortableDataResult.UserFolderAndDataConflict =>
                outcome.Detail + Environment.NewLine + "不要な方を手動で整理してください。",
            _ => "データを実行ファイルのフォルダの data フォルダへ移せなかったため、これまでの場所のまま起動しました。" +
                 Environment.NewLine + outcome.Detail + Environment.NewLine +
                 "データは失われていません。次回の起動時にもう一度試みます（同じ理由の通知は繰り返しません。詳細はログにあります）。",
        };
        await dialogService.ShowMessageAsync("Graft", message).ConfigureAwait(true);
    }
}
