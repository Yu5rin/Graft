using Graft.Features;
using Graft.Infra;

namespace Graft.Views;

/// <summary>
/// <see cref="StartupCoordinator"/>の分割ファイル。異常系点検「低」6件目（一時ファイル・
/// 退避ファイルの掃除が無い）の対応: <see cref="TempFileCleanup"/>を起動時に呼び出す配線のみを
/// 担う（判定・削除そのもののロジックは<see cref="TempFileCleanup"/>側の純粋な静的メソッドに
/// 寄せてあり、単体テストもそちらで完結する）。
/// </summary>
public sealed partial class StartupCoordinator
{
    /// <summary>
    /// <see cref="TempFileCleanup"/>による一時ファイル・退避ファイルの掃除をバックグラウンドで
    /// 実行する。<see cref="StartAsync"/>本体からはawaitせず<c>_ = ...</c>で切り離して呼ぶため、
    /// このメソッド自身が投げる例外は誰にも観測されず（.NETの既定動作では“fire-and-forget”
    /// タスクの例外は握りつぶされずFinalizerスレッドで再スローされうる）アプリを不安定にしうる。
    /// そのため内部の失敗はすべてこのメソッド内で握りつぶし、ログにのみ記録する
    /// （Logger.SafeCleanupAsyncと同じ方針）。
    /// </summary>
    private static async Task RunTempFileCleanupInBackgroundAsync(AppPaths appPaths, ProjectStore projectStore, Logger? logger)
    {
        try
        {
            // UIスレッドを一切塞がないよう、ディレクトリ列挙・削除はスレッドプールへ逃がす。
            var (removedTemp, removedBackups) = await Task.Run(async () =>
            {
                var removedTempCount = TempFileCleanup.CleanupBaseDirectoryTempFiles(appPaths.BaseDirectory);

                var removedBackupCount = 0;
                var projectsResult = await projectStore.LoadAsync().ConfigureAwait(false);
                if (projectsResult.IsSuccess)
                {
                    foreach (var project in projectsResult.Value)
                    {
                        removedBackupCount += TempFileCleanup.CleanupProjectBackupFiles(project.Root);
                    }
                }

                return (removedTempCount, removedBackupCount);
            }).ConfigureAwait(false);

            if (removedTemp > 0 || removedBackups > 0)
            {
                logger?.Info("cleanup",
                    $"起動時の後始末: 一時ファイル{removedTemp}件・プロジェクト内の退避ファイル{removedBackups}件を削除しました。");
            }
        }
        catch (Exception ex)
        {
            // バックグラウンドタスクのため、ここで捕まえ損ねるとアプリの安定性に関わる
            // （クラスコメント参照）。掃除自体は次回起動時にも再試行されるため、失敗しても
            // 起動・実行中の操作には一切影響させない。
            logger?.Warn("cleanup", $"起動時の一時ファイル・退避ファイルの掃除に失敗しました: {ex.Message}");
        }
    }
}
