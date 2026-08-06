using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="StartupCoordinator"/> の分割ファイル（1ファイル400行上限のため）。
/// 起動時検証（13.1/6.3/15章/4.10）と、その結果の通知・終了処理を担う。
/// </summary>
public sealed partial class StartupCoordinator
{
    // ------------------------------------------------------------------
    // 起動時検証（13.1/6.3/15章/4.10）。back/配下の走査を伴うため非同期・バックグラウンドで
    // 行い、UIの表示（1秒以内、17章）をブロックしない。結果が出てから通知する。
    // ------------------------------------------------------------------

    private async Task RunStartupValidationAsync(
        ProjectStore projectStore, RevisionStore revisionStore, IDialogService dialogService,
        RevisionRestorer revisionRestorer, List<GraftIssue> issues, Dispatcher dispatcher)
    {
        var loaded = await projectStore.LoadAsync().ConfigureAwait(false);
        issues.AddRange(loaded.Issues);

        var validated = await projectStore.ValidateAsync(loaded.Value).ConfigureAwait(false);
        issues.AddRange(validated.Issues);

        var reconciled = await ReconcileRevisionsAsync(projectStore, revisionStore, validated.Value).ConfigureAwait(false);
        var inProgress = await CollectInProgressAsync(revisionStore, reconciled).ConfigureAwait(false);

        if (_logger is not null)
        {
            await _logger.CleanupOldLogsAsync().ConfigureAwait(false);
        }
        if (_patchQueue is not null)
        {
            issues.AddRange((await _patchQueue.LoadAsync().ConfigureAwait(false)).Issues);
        }

        var report = new StartupReport
        {
            Issues = issues,
            InProgressRevisions = inProgress,
            IsFirstLaunch = !OnboardingWindow.HasCompleted(_appPaths),
        };
        _logger?.Info("startup", "起動時検証を完了しました");

        await dispatcher.InvokeAsync(() => _ = PresentReportAsync(report, dialogService, revisionRestorer)).Task
            .ConfigureAwait(false);
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
                    ProjectName = project.Name,
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

    private async Task PresentReportAsync(StartupReport report, IDialogService dialogService, RevisionRestorer revisionRestorer)
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
    private async Task OfferRollbackAsync(
        InProgressRevisionIssue issue, IDialogService dialogService, RevisionRestorer revisionRestorer)
    {
        var confirmed = await dialogService
            .ConfirmAsync("未完了の適用を検出しました", StartupReport.BuildRollbackPrompt(issue))
            .ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

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
        if (!revision.IsRestorable)
        {
            return;
        }
        var manifestPath = Path.Combine(revision.FolderPath, "manifest.json");
        var rolledBack = revision.Manifest with { Status = RevisionStatus.RolledBack };
        await new JsonFileStore().WriteAsync(manifestPath, rolledBack, JsonFileStore.DefaultOptions).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------
    // 終了処理（4.10 パッチキューの保存を含む）
    // ------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        if (_patchQueue is not null)
        {
            await _patchQueue.SaveAsync().ConfigureAwait(true);
        }
        _shellViewModel?.Dispose();
        _hotkeyManager?.Dispose();
        _clipboardWatcher?.Dispose();
        _trayIcon?.Dispose();
        _guard?.Dispose();
        if (_logger is not null)
        {
            await _logger.DisposeAsync().ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------
    // Win32 P/Invoke（6.8 多重起動防止: 既存ウィンドウの前面表示）
    // ------------------------------------------------------------------

    private const int SwRestore = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
