using System.Diagnostics;
using Avalonia.Threading;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Themes;
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
        // Dispatcher.UIThreadは遅延生成・スレッド非安全な静的プロパティで、headlessテストでは
        // テストごとの一瞬の再構築の窓に別スレッドから読まれると壊れたインスタンスが
        // キャッシュされてしまう（DocumentSessionクラス冒頭のコメント参照）。このメソッドは
        // StartAsync側から`_ = RunStartupValidationAsync(...)`と投げっぱなしで呼ばれ（起動を
        // 待たせないため）、直後にConfigureAwait(false)でスレッドプールへ移るため、
        // まだ呼び出し元のUIスレッドにいるこの時点で捕捉しておく。
        var ui = Dispatcher.UIThread;

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

        // 依頼1（E705）: 日本語グリフを描画できるフォントが環境に1つも無いかを確認する。
        // 18章「起動から操作可能まで1秒以内」への影響: この検証（RunStartupValidationAsync）
        // 自体がStartAsync側でウィンドウ表示（window.Show()、「操作可能まで」計測点）より
        // 後ろに投げっぱなし（fire-and-forget）で開始されるバックグラウンド処理のため、
        // ここに何を足しても起動の体感速度には一切影響しない。実測でもJapaneseFontAvailability.
        // HasJapaneseCapableFont自体は数ms（TryMatchCharacterの初回呼び出しでフォント一覧の
        // 構築が走るのが支配的）で完了することを確認済み（実装ノート参照）。
        lock (issues)
        {
            if (!JapaneseFontAvailability.HasJapaneseCapableFont())
            {
                issues.Add(GraftIssue.Of(ErrorCode.E705,
                    "UI・コードいずれのフォントフォールバック列（Themes/Tokens.axaml）を辿っても、" +
                    "日本語（ひらがな・カタカナ・漢字）を描画できるフォントが見つかりませんでした。",
                    severity: Severity.Warning));
            }
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

        await ui.InvokeAsync(() =>
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

    /// <summary>
    /// 不具合調査（利用者報告「使い方を学ぶ終了後にProjectが消える」）で判明した、書き込みの
    /// 競合を避けるための実装。
    ///
    /// 【以前の実装の問題】以前はここで全プロジェクトの補正結果をまとめてリストへ組み立て、
    /// 1件でも補正が必要なら<see cref="ProjectStore.SaveAsync"/>でリスト全体を1回だけ書き戻して
    /// いた。しかしそのリストの元（引数<paramref name="projects"/>）は、このメソッドの呼び出しより
    /// 前——具体的には<see cref="RunStartupValidationAsync"/>の冒頭、起動直後——に読み込んだ
    /// スナップショットである。この起動時検証はback/配下の実体走査を伴うため数百ms〜数秒かかる
    /// ことがあり（クラスコメント参照）、その完了を待たずに画面上のチュートリアル
    /// （<c>Graft.Views.ShellWindow</c>のShellWindow.Tutorial.cs）がサンプルプロジェクトの
    /// 登録・適用・削除を並行して行うと、リスト全体の書き戻しがそれらの結果を古いスナップショットで
    /// 上書きしてしまう（サンプルが復活して残り続ける、または直前に登録された実プロジェクトが
    /// 消える、といった実害につながりうる）。
    ///
    /// 【対応】1件でも補正が必要な場合、そのプロジェクト単体だけを<see cref="ProjectStore.
    /// UpdateAsync"/>で更新する。<see cref="ProjectStore.UpdateAsync"/>は書き込みの直前に
    /// 自前で<see cref="ProjectStore.LoadAsync"/>し直してから該当IDのプロジェクトだけを書き換えて
    /// 保存するため、「読んでから書くまでの隙間」が1回のUpdateAsync呼び出し分（ミリ秒オーダー）まで
    /// 縮まり、他の操作の結果を巻き戻す実害を無くせる。対象プロジェクトが既に削除されている場合
    /// （並行してチュートリアルのサンプルが片付けられた等）はUpdateAsyncが失敗を返すだけで、
    /// 例外にはならない（補正の必要も無くなっているため無視してよい）。
    /// </summary>
    private static async Task<IReadOnlyList<Project>> ReconcileRevisionsAsync(
        ProjectStore projectStore, RevisionStore revisionStore, IReadOnlyList<Project> projects)
    {
        var updated = new List<Project>(projects.Count);
        foreach (var project in projects)
        {
            var maxRevision = await revisionStore.DetectMaxRevisionAsync(project.Id).ConfigureAwait(false);
            var reconciled = maxRevision.IsSuccess ? ProjectStore.ReconcileRevision(project, maxRevision.Value) : project;
            updated.Add(reconciled);

            if (reconciled.NextRevision != project.NextRevision)
            {
                var targetRevision = reconciled.NextRevision;
                await projectStore
                    .UpdateAsync(project.Id, p => p with { NextRevision = targetRevision })
                    .ConfigureAwait(false);
            }
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
    ///
    /// 課題1（ログ）: 後始末そのもの（パッチキューの保存・各プラットフォームサービスの破棄）が
    /// 完了したことと、終了処理全体（<see cref="ShellWindow.ShutdownStartedAt"/>から
    /// ここまで）にかかった時間を記録する。起動側の「操作可能まで N ms」と対になる形。
    /// 後始末の途中で例外が飛んでもここで必ず捕捉し、Error levelで記録したうえで
    /// 呼び出し元へは正常終了として返す（＝再スローしない）。後始末の失敗で
    /// <see cref="App.OnShutdownRequested"/>側の<c>desktop.Shutdown()</c>呼び出しに
    /// 到達できなくなり、二度と終了できなくなる事態を避けるため。
    ///
    /// ロガーの破棄順序: ロガー自身への書き込みは、破棄対象の中で最後まで使うため
    /// <see cref="Logger.DisposeAsync"/>は必ず一番最後に呼ぶ（このメソッドの他の行より前で
    /// 呼んではならない）。<see cref="Logger"/>はキューへの書き込みが完了済みチャネルに対しては
    /// 例外を投げず黙って無視する作りのため、万一この順序を誤っても即座にクラッシュはしないが、
    /// 直後のログが記録されず診断できなくなる。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        try
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

            LogCleanupCompleted(stopwatch.ElapsedMilliseconds, error: null);
        }
        catch (Exception ex)
        {
            // 後始末の一部（パッチキューの保存やOS資源の解放）が失敗しても、プロセスは
            // 必ず終了できなければならない（課題2）。ここで再スローしない。
            LogCleanupCompleted(stopwatch.ElapsedMilliseconds, error: ex);
        }
        finally
        {
            // ロガーは他の後始末すべてが終わった後、一番最後に破棄する（上記コメント参照）。
            if (_logger is not null)
            {
                // v1.0.7実機不具合対応: 起動から今までに握りつぶした想定外の例外の集計
                // （SuppressedExceptionTracker）を、1回以上発生した種類だけshutdownログへ
                // 残す。ロガーを破棄する直前（＝これ以上ログを書けなくなる前）に必ず行う。
                SuppressedExceptionTracker.Shared.LogSummary(_logger);
                await _logger.DisposeAsync().ConfigureAwait(true);
            }
        }
    }

    /// <summary>後始末の完了・終了処理全体の所要時間を記録する。3秒を超えたら異常として警告する。</summary>
    private void LogCleanupCompleted(long cleanupElapsedMs, Exception? error)
    {
        if (error is not null)
        {
            _logger?.Error("shutdown", $"後始末に失敗しました（{cleanupElapsedMs} ms経過）: {error}");
        }
        else if (cleanupElapsedMs > 3000)
        {
            _logger?.Warn("shutdown", $"後始末の完了に時間がかかりました: {cleanupElapsedMs} ms");
        }
        else
        {
            _logger?.Info("shutdown", $"後始末が完了しました: {cleanupElapsedMs} ms");
        }

        // 起動側の「操作可能まで N ms」（StartupCoordinator.StartAsync）と対になる形で、
        // ウィンドウを閉じてからここまでの終了処理全体の所要時間も記録する。トレイへ
        // 隠しただけの場合やウィンドウを一切作らなかった場合（多重起動検出）はnullのまま。
        if (MainWindow?.ShutdownStartedAt is { } startedAt)
        {
            var totalMs = (long)(DateTime.Now - startedAt).TotalMilliseconds;
            _logger?.Info("shutdown", $"終了処理を完了しました。終了処理全体で {totalMs} ms かかりました。");
        }
    }
}
