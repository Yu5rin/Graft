using Avalonia.Threading;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="StartupCoordinator"/> の分割ファイル（1ファイル400行上限のため）。
/// 起動時検証（13.1/6.3/15章/4.10）と、その結果の通知・終了処理を担う。
/// v2.0のWPF版からの移植（19章 L3）。UIスレッドへの復帰は
/// <see cref="Dispatcher.UIThread"/> を使い、Win32 P/Invokeは持たない
/// （多重起動時の既存ウィンドウ前面化は <see cref="ISingleInstanceGuard"/> の責務）。
/// </summary>
public sealed partial class StartupCoordinator
{
    // ------------------------------------------------------------------
    // 起動時検証（13.1/6.3/15章/4.10）。back/配下の走査を伴うため非同期・バックグラウンドで
    // 行い、UIの表示（1秒以内、17章）をブロックしない。結果が出てから通知する。
    // ------------------------------------------------------------------

    private async Task RunStartupValidationAsync(
        ProjectStore projectStore, RevisionStore revisionStore, IDialogService dialogService,
        RevisionRestorer revisionRestorer, List<GraftIssue> issues, Task initialWatchSignal)
    {
        var loaded = await projectStore.LoadAsync().ConfigureAwait(false);
        // issuesはUIスレッド側（ExplorerViewModel.WatchStartCompletedHandler、不具合4対応）からも
        // 追加されうる共有リストのため、ここでの追加もロックで保護する。
        lock (issues) issues.AddRange(loaded.Issues);

        var validated = await projectStore.ValidateAsync(loaded.Value).ConfigureAwait(false);
        lock (issues) issues.AddRange(validated.Issues);

        var reconciled = await ReconcileRevisionsAsync(projectStore, revisionStore, validated.Value)
            .ConfigureAwait(false);
        var inProgress = await CollectInProgressAsync(revisionStore, reconciled).ConfigureAwait(false);

        if (_logger is not null)
        {
            await _logger.CleanupOldLogsAsync().ConfigureAwait(false);
        }
        if (_patchQueue is not null)
        {
            var queueIssues = (await _patchQueue.LoadAsync().ConfigureAwait(false)).Issues;
            lock (issues) issues.AddRange(queueIssues);
        }

        // 不具合4対応: プロジェクトが1件以上あれば、起動直後の自動選択でExplorerViewModelが
        // ファイル監視の開始を試みるはず。その結果（成功・失敗）が届くまでレポート確定を待つ。
        // ここで待たずに確定すると、この検証（back/配下の走査等で重い）より先に監視開始の
        // 試行が終わっているとは限らず、タイミング次第で監視失敗の警告を取りこぼす
        // （実機検証で実際に発生を確認したレース。ExplorerViewModel.WatchStartCompletedHandler
        // のコメント参照）。5秒のタイムアウトは、何らかの理由でOnLoaded経由の初期化が
        // ここまで辿り着かない場合に起動時レポート自体が出せなくなるのを防ぐための保険。
        if (loaded.Value.Count > 0)
        {
            await Task.WhenAny(initialWatchSignal, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }

        StartupReport report;
        lock (issues)
        {
            report = new StartupReport
            {
                Issues = new List<GraftIssue>(issues),
                InProgressRevisions = inProgress,
                IsFirstLaunch = !OnboardingWindow.HasCompleted(_appPaths),
            };
        }
        _logger?.Info("startup", "起動時検証を完了しました");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // ここまでで起動時に検出した問題を集め終えたとみなし、以降のファイル監視失敗は
            // ExplorerViewModel自身の即時ダイアログへ戻す（不具合4対応。StartAsync参照）。
            // WatchStartCompletedHandlerの読み書きはUIスレッドに閉じるよう、ここ（UIスレッドへの
            // 復帰後）でリセットする。
            if (_shellViewModel is not null)
            {
                _shellViewModel.Explorer.WatchStartCompletedHandler = null;
            }
            return PresentReportAsync(report, dialogService, revisionRestorer);
        }).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Project>> ReconcileRevisionsAsync(
        ProjectStore projectStore, RevisionStore revisionStore, IReadOnlyList<Project> projects)
    {
        var updated = new List<Project>(projects.Count);
        var changed = false;
        foreach (var project in projects)
        {
            var maxRevision = await revisionStore.DetectMaxRevisionAsync(project.Id).ConfigureAwait(false);
            var reconciled = maxRevision.IsSuccess ? ProjectStore.ReconcileRevision(project, maxRevision.Value) : project;
            changed |= reconciled.NextRevision != project.NextRevision;
            updated.Add(reconciled);
        }

        if (changed)
        {
            await projectStore.SaveAsync(updated).ConfigureAwait(false);
        }
        return updated;
    }

    private static async Task<IReadOnlyList<InProgressRevisionIssue>> CollectInProgressAsync(
        RevisionStore revisionStore, IReadOnlyList<Project> projects)
    {
        var result = new List<InProgressRevisionIssue>();
        foreach (var project in projects.Where(p => !p.IsDisconnected))
        {
            var found = await revisionStore.FindInProgressAsync(project.Id).ConfigureAwait(false);
            if (found.IsSuccess && found.Value.Count > 0)
            {
                result.Add(new InProgressRevisionIssue
                {
                    ProjectId = project.Id,
                    ProjectName = project.DisplayName,
                    ProjectRoot = project.Root,
                    Revisions = found.Value,
                });
            }
        }
        return result;
    }

    // ------------------------------------------------------------------
    // 検証結果の通知（UIスレッド上で実行）
    // ------------------------------------------------------------------

    private static async Task PresentReportAsync(
        StartupReport report, IDialogService dialogService, RevisionRestorer revisionRestorer)
    {
        var summary = report.BuildIssuesSummaryText();
        if (!string.IsNullOrEmpty(summary))
        {
            await dialogService.ShowMessageAsync("起動時の確認事項", summary).ConfigureAwait(true);
        }

        foreach (var issue in report.InProgressRevisions)
        {
            await OfferRollbackAsync(issue, dialogService, revisionRestorer).ConfigureAwait(true);
        }
    }

    /// <summary>6.3/E403: 中途半端な適用状態を通知し、承諾された場合のみロールバックを実行する。</summary>
    private static async Task OfferRollbackAsync(
        InProgressRevisionIssue issue, IDialogService dialogService, RevisionRestorer revisionRestorer)
    {
        var confirmed = await dialogService
            .ConfirmAsync("未完了の適用を検出しました", StartupReport.BuildRollbackPrompt(issue))
            .ConfigureAwait(true);
        if (!confirmed) return;

        foreach (var revision in issue.Revisions)
        {
            var restored = await revisionRestorer
                .RestoreAsync(issue.ProjectId, issue.ProjectRoot, revision, force: true)
                .ConfigureAwait(true);
            if (restored.IsSuccess)
            {
                await MarkRolledBackAsync(revision).ConfigureAwait(true);
            }
        }
    }

    /// <summary>ロールバック後、manifest.jsonのstatusを更新し次回起動時に再度提案されないようにする。</summary>
    private static async Task MarkRolledBackAsync(RevisionSummary revision)
    {
        if (!revision.IsRestorable) return;

        var manifestPath = Path.Combine(revision.FolderPath, "manifest.json");
        var rolledBack = revision.Manifest with { Status = RevisionStatus.RolledBack };
        await new JsonFileStore().WriteAsync(manifestPath, rolledBack, JsonFileStore.DefaultOptions)
            .ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // 終了処理（4.10 パッチキューの保存を含む）
    // ------------------------------------------------------------------

    /// <summary>
    /// 課題1（バグ修正）: 呼び出し側（<see cref="App.OnShutdownRequested"/>）が
    /// UIスレッドを同期ブロックせずawaitするようになったため、ここのConfigureAwait(true)は
    /// 安全（UIスレッドは塞がれておらず、継続をディスパッチャ経由で普通に受け取れる）。
    /// 以前は呼び出し側が<c>.GetAwaiter().GetResult()</c>でUIスレッドを同期的にブロックして
    /// おり、その状態でConfigureAwait(true)の継続をUIスレッドへ戻そうとしたためデッドロックし、
    /// ×で閉じてもプロセスが終了しない不具合の直接の原因になっていた。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_patchQueue is not null)
        {
            await _patchQueue.SaveAsync().ConfigureAwait(true);
        }
        _shellViewModel?.Dispose();
        _messageBridge?.Dispose();
        _platform.Hotkeys.Dispose();
        _platform.Clipboard.Dispose();
        _platform.Tray.Dispose();
        _platform.Theme.Dispose();
        _platform.SingleInstance.Dispose();
        if (_logger is not null)
        {
            await _logger.DisposeAsync().ConfigureAwait(true);
        }
    }
}
