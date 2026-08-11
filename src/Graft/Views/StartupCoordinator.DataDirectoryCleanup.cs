using Graft.Infra;

namespace Graft.Views;

/// <summary>
/// <see cref="StartupCoordinator"/> の分割ファイル（1ファイル400行上限のため）。
///
/// 機能3: データ保存先の切り替え（「移動」）に伴う「後始末待ち」の起動時処理
/// （<see cref="DataDirectoryMigrator.RunPendingCleanup"/>）の結果を、確定した
/// <see cref="Logger"/>へ記録する。実際の取り込み直し・削除処理そのものは
/// <see cref="Infra.DataDirectoryMigrator"/>（純粋なロジック・単体テスト対象）が担い、
/// ここではログへの記録のみを行う（<see cref="StartAsync"/>から、Logger生成直後・
/// 各ストアがファイルを開くより前に呼ばれる。呼び出し箇所のコメント参照）。
/// </summary>
public sealed partial class StartupCoordinator
{
    /// <summary>
    /// <see cref="DataDirectoryMigrator.RunPendingCleanup"/>の結果をLoggerへ記録する。
    /// 取り込み直しに失敗した場合（<see cref="DataDirectoryMigrator.PendingCleanupResult.
    /// MigrateFailed"/>）は、次回起動でもう一度試みられることも合わせて記録し、失敗を
    /// 握りつぶさない（附録A.4の方針）。
    /// </summary>
    private void LogPendingCleanupOutcome(DataDirectoryMigrator.PendingCleanupOutcome outcome)
    {
        switch (outcome.Result)
        {
            case DataDirectoryMigrator.PendingCleanupResult.NoMarker:
                // 大半の起動はこれ（前回、保存先の切り替えを行っていない）。記録しない。
                return;

            case DataDirectoryMigrator.PendingCleanupResult.NothingToDo:
                _logger?.Info("startup",
                    $"データ保存先の後始末: 対象の旧保存先が既に無いため何もしませんでした（{outcome.OldDirectory}）。");
                return;

            case DataDirectoryMigrator.PendingCleanupResult.Completed:
                _logger?.Info("startup",
                    outcome.Detail is null
                        ? $"データ保存先の後始末: 旧保存先（{outcome.OldDirectory}）を取り込み直し、削除しました。"
                        : $"データ保存先の後始末: 旧保存先（{outcome.OldDirectory}）を取り込み直しましたが、{outcome.Detail}");
                return;

            case DataDirectoryMigrator.PendingCleanupResult.MigrateFailed:
                // 削除は行っていない・マーカーも残したままのため、次回起動でもう一度試みられる。
                _logger?.Warn("startup",
                    $"データ保存先の後始末: 旧保存先（{outcome.OldDirectory}）から現在の保存先への" +
                    $"取り込み直しに失敗したため、削除は行いませんでした。次回起動時にもう一度試みます。詳細: {outcome.Detail}");
                return;
        }
    }
}
