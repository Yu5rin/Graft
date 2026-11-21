using System.Diagnostics;
using System.IO;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル（1ファイル400行上限のため）。
/// 適用（ApplyAsync）本体・取り消し（UndoLastAsync）、および仕様書4.8/7章
/// 「接ぎ木との連携」のうち、適用前チェック（未保存確認）と適用後の再読込通知を担う。
/// MainViewModelはUI層（AvalonEdit・エディタタブ）の型を知らないため、実際に
/// エディタと結ぶのはShellViewModel（<see cref="BeforeApplyAsync"/>/<see cref="AfterApplyAsync"/>
/// へデリゲートを設定する。附録A）。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 4.8/7章 適用前チェック用フック。ドライラン開始時に対象ファイルの絶対パス一覧を渡して呼ぶ。
    /// Cancelledが返るとドライランを中止する。Savedは「保存が起きた」ことを伝える
    /// （適用の側はこれを見て解析し直す。<see cref="SaveTargetsAndReanalyzeAsync"/>）。
    /// </summary>
    public Func<IReadOnlyList<string>, Task<TargetSaveOutcome>>? BeforeApplyAsync { get; set; }

    /// <summary>
    /// 4.8/7章 適用後フック。適用が成功したら、書き換わったファイルの絶対パス一覧を渡して必ず呼ぶ。
    /// </summary>
    public Func<IReadOnlyList<string>, Task>? AfterApplyAsync { get; set; }

    /// <summary>
    /// 課題1（設定 Settings.ShowPreview）: 書き込み前の差分確認。MainViewModelはWindowの型を
    /// 知らないため、実際の表示はShellWindowが購読して<see cref="Views.ApplyPreviewWindow"/>を
    /// 開く形で行う（BeforeApplyAsync/AfterApplyAsyncと同じ「Viewへ委譲する」設計）。
    /// 購読者が居ない場合（他画面からMainViewModelだけを使うテスト等）は、フック未設定時と
    /// 同様に確認済みとして素通りさせる。
    /// </summary>
    public event EventHandler<ApplyPreviewRequestedEventArgs>? ApplyPreviewRequested;

    /// <summary>
    /// 適用処理（要約確認〜書き込み〜適用後フック〜Git自動コミット）が実行中かどうか。
    /// アプリを閉じる操作が、書き込みの途中でプロセスを終わらせてしまわないよう、
    /// 終わるまで待つために参照する（ShellWindow.OnClosing）。
    /// </summary>
    public bool IsApplyInProgress => _isApplyInProgress;

    /// <summary>ApplyCommandの実体。要約入力・確認ダイアログを経て本適用し、6.5適用後フックを実行する。</summary>
    private async Task ApplyAsync()
    {
        if (_dryRun is not { } dryRun || _lastContext is not { } context) return;

        // 課題1: この一連の処理（要約確認〜適用〜適用後フック〜Git自動コミット完了まで）の間、
        // UpdateSettingsからの_settings差し替えを保留させる（MainViewModel.UpdateSettings
        // 参照）。設定画面はApplyCommand実行中もモーダルではなく開けるため、その間に
        // ユーザーが安全機構・マッチング・Git連携等を変更しても、この回の適用は開始時点の
        // 設定のまま最後まで一貫して動かす。finallyで必ず解除し、保留中の変更があれば
        // ここでまとめて反映する。
        _isApplyInProgress = true;
        try
        {
            await ApplyCoreAsync(dryRun, context).ConfigureAwait(true);
        }
        finally
        {
            _isApplyInProgress = false;
            ResetVbaApplyChoices(); // 「ファイルだけ適用する」・上書きの承認はこの適用だけの選択（MainViewModel.VbaApply.cs）。
            if (_pendingSettings is { } pending)
            {
                _pendingSettings = null;
                ApplySettingsNow(pending);
            }
        }
    }

    private async Task ApplyCoreAsync(DryRunResult dryRun, ApplyContext context)
    {
        var updatedPlans = Blocks.Select(b => b.Plan with { IsSelected = b.IsSelected }).ToList();
        var updatedDryRun = dryRun with { Plans = updatedPlans };

        // 実機不具合対応: ApplyCommand自体はドライラン時点の適用可能件数（チェックの状態を
        // 見ない）で有効・無効を決めているため、失敗したブロックしか無い、またはユーザーが
        // 適用可能なブロックのチェックを全部外した場合でも実行できてしまう。そのまま
        // ApplyEngine.ApplyAsyncへ渡すと、書き換えは1件も無いのに「rNとして記録しました」という
        // 空のリビジョンだけが作られてしまう（履歴・世代管理・リビジョン番号を無駄に消費するうえ、
        // 利用者からは何が起きたのか分からない）。チェック済みかつ適用可能なブロックが1件も無い
        // 場合は、ここで案内を出して適用処理自体を始めない。
        if (!updatedPlans.Any(p => p.IsSelected && p.CanApply))
        {
            await _dialogs
                .ShowMessageAsync("適用対象がありません", "チェックが付いている、適用可能な変更がありません。適用する変更にチェックを付けてください。")
                .ConfigureAwait(true);
            return;
        }

        // Excel VBA 連携（仕様書_VBA連携 7.3-2）: VBA プロジェクトでは、DELETE／RENAME（E814）と CP932 で表せない文字（E811）を
        // 含むブロックにチェックが付いている間は適用を始めない。外せば、ほかのブロックは適用できる。コア（ApplyEngine）は
        // 変えず、確認・プレビューの前に画面側で止める。VBA プロジェクトでなければ何もしない。
        var vbaProject = ProjectPane.SelectedItem?.Project;
        if (await VbaGuardBlocksApplyAsync(vbaProject, updatedPlans).ConfigureAwait(true)) return;

        // チェックの組み合わせが成り立たない場合（RENAMEを外して移動先への変更だけ残した、FULLを外して
        // それを前提にした変更だけ残した、等）は、要約入力や確認ダイアログを通した後で中止になるより前に、
        // ここで理由と直し方を案内する。適用エンジンも書き込み前に同じ判定で止める
        // （ApplyEngine.CheckSelection参照）ので、ここは早めに知らせるための二重の備え。
        var selectionIssues = ApplyEngine.CheckSelection(updatedPlans);
        if (selectionIssues.Count > 0)
        {
            await _dialogs
                .ShowMessageAsync("このチェックの組み合わせでは適用できません",
                    string.Join(Environment.NewLine + Environment.NewLine, selectionIssues.Select(i => i.Detail ?? i.ToDisplayText())))
                .ConfigureAwait(true);
            return;
        }

        // 未保存の編集の確認と保存は、要約入力・確認ダイアログ・プレビューより前に行う（C3）。
        // 解析して差分を見ている間に対象をエディタで直し、保存しないまま適用すると、適用は
        // ディスクの内容に対して行われ、直後の再読込でエディタの未保存の編集が消えていた。
        // 保存を拒んだ場合は適用を始めない。保存が起きるとディスクの内容が解析時から変わり、
        // 適用エンジンが一致位置の食い違い（E301）で中止するため、ここで解析し直してから進める
        // （MainViewModel.ApplySave.cs）。確認ダイアログやプレビューより前に置くのは、利用者が
        // 見る差分を保存後の内容に対するものにするため。
        //
        // 書き込みの直前（確認のあと）にもう一度確かめる処理は置かない。確認ダイアログ
        // （AvaloniaDialogService.ShowModal）も適用前プレビュー（ApplyPreviewWindow.ShowDialog）も、
        // オーナーウィンドウの入力を塞ぐモーダルで、その間にエディタを編集することはできないため、
        // ここで保存した状態が書き込みまで保たれる。
        var saved = await SaveTargetsAndReanalyzeAsync(dryRun, context, updatedPlans).ConfigureAwait(true);
        if (saved is null) return;
        var reanalyzed = false;
        if (!ReferenceEquals(saved.Value.DryRun, dryRun))
        {
            reanalyzed = true;
            dryRun = saved.Value.DryRun;
            context = saved.Value.Context;
            updatedPlans = Blocks.Select(b => b.Plan with { IsSelected = b.IsSelected }).ToList();
            updatedDryRun = dryRun with { Plans = updatedPlans };
        }

        // Excel VBA 連携（7.3-3）: 解析から「適用」までの間に Excel 側が変わっていないかを、書き込みの前にもう一度確かめる。
        // 取り込んで解析し直すときは、保存後の再解析と同じ仕組み（チェック状態の引き継ぎ込み）を使う。
        var rechecked = await RecheckVbaBeforeApplyAsync(dryRun, context, updatedPlans).ConfigureAwait(true);
        if (rechecked is null) return;
        if (!ReferenceEquals(rechecked.Value.DryRun, dryRun))
        {
            reanalyzed = true;
            dryRun = rechecked.Value.DryRun;
            context = rechecked.Value.Context;
            updatedPlans = Blocks.Select(b => b.Plan with { IsSelected = b.IsSelected }).ToList();
            updatedDryRun = dryRun with { Plans = updatedPlans };
        }

        // 再解析で結果が差し替わっていたら、適用できない変更が新しく現れていないかを確かめ直す。
        if (reanalyzed && await VbaGuardBlocksApplyAsync(vbaProject, updatedPlans).ConfigureAwait(true)) return;

        if (_settings.RequireSummary && string.IsNullOrWhiteSpace(updatedDryRun.Patch.Meta.Summary))
        {
            var input = await _dialogs.PromptAsync("要約を入力", "この適用の概要を入力してください。", null).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(input)) return;
            var patchWithSummary = updatedDryRun.Patch with { Meta = updatedDryRun.Patch.Meta with { Summary = input } };
            updatedDryRun = updatedDryRun with { Patch = patchWithSummary };
        }

        // 課題1: 「適用前にプレビューを表示する」設定。有効なら、テキストだけの確認
        // （「N件を適用します。よろしいですか？」）の代わりに、DiffView/DiffViewModelで
        // 実際の差分を見ながら可否を判断できる専用ダイアログを挟む（v1.5仕様書6.8）。
        // 無効なら、確認自体は残しつつ差分確認は挟まない（現在の挙動のまま）。
        // 接ぎ木パネルの「プレビュー」ボタン（PreviewCommand＝ドライランの再実行）とは別物であり、
        // 互いの役割は重複しない（ApplyPreviewRequestedのコメント参照）。
        var confirmed = _settings.ShowPreview
            ? await RequestApplyPreviewAsync(updatedDryRun).ConfigureAwait(true)
            : await _dialogs
                .ConfirmAsync("適用の確認", $"{updatedDryRun.ApplicableCount}件を適用します。よろしいですか？")
                .ConfigureAwait(true);
        if (!confirmed) return;

        var project = ProjectPane.SelectedItem?.Project;

        State = CenterPaneState.Loading;
        // 実機不具合対応: 適用処理（書き込み失敗を含む）がログに一切残っていなかったため、
        // 成否・所要時間・付随する警告（SafeFileWriterが検出した退避方式の使用や検証やり直し
        // 等）を必ずlogs/へ記録する。ApplyEngine.ApplyAsync自体はUIに依存しないためLoggerを
        // 引き回せず、呼び出し元であるここで記録する（MainViewModel.Git.csのLogger運用に倣う）。
        var stopwatch = Stopwatch.StartNew();

        // 不具合2対応: 適用を試みるにあたり、成功・失敗を問わずnextRevisionを消費して
        // projects.jsonへ永続化する（消費しないと次回も同じ番号が付与され続ける）。
        // 失敗時にも消費する理由はProjectStore.ConsumeNextRevisionAsyncのコメント参照。
        // ProjectPane.LoadAsync（下）より前に行い、再読込結果へ確実に反映させる。
        //
        // 履歴番号が巻き戻って同じ番号のバックアップが2つできた実機不具合への対応として、
        // 消費は以前の「適用の直後」から「適用の直前」へ移し、払い出した番号をそのまま適用に使う。
        // 払い出す番号はprojects.jsonのnextRevisionと実体の最大番号+1の大きい方（RevisionNumbering）
        // なので、nextRevisionが何らかの理由で戻っていても既存のフォルダとは重ならない。
        // 払い出しと「記録されるnextRevision（払い出した番号+1）」は同じ操作で決まるため、
        // 適用に使った番号と記録が食い違うこともない。事前に払い出すことの副作用は、適用が
        // 例外で途中終了した場合にも番号が消費されることだが、それは「失敗しても消費する」
        // 既存の方針（欠番を許容する）と同じ向きである。
        context = await ReserveRevisionNumberAsync(context).ConfigureAwait(true);
        var result = await _applyEngine.ApplyAsync(updatedDryRun, context).ConfigureAwait(true);
        stopwatch.Stop();

        if (!result.IsSuccess)
        {
            CenterError = result.Errors.FirstOrDefault();
            Logger?.Error("apply", CenterError?.ToDisplayText() ?? "不明なエラーで適用に失敗しました",
                revision: context.Revision, durationMs: stopwatch.ElapsedMilliseconds);
            State = CenterPaneState.Error;
            return;
        }

        LogApplySuccess(result.Value, result.Issues, stopwatch.ElapsedMilliseconds);
        // 不具合3対応: パッチ適用が成功しリビジョンが記録された時点で「最後に適用した日時」を
        // 更新する（ProjectPane.LoadAsyncより前に行い、直後の並べ替えに反映させる）。更新ロジック
        // 自体はProjectStore.MarkAppliedAsyncの1箇所に集約している。失敗しても適用結果自体は
        // 成功しているためエラー扱いにはせず、ログにのみ残す。
        await MarkProjectAppliedAsync(context.ProjectId).ConfigureAwait(true);
        await NotifyFilesRewrittenAsync(context.ProjectRoot, result.Value).ConfigureAwait(true); // 4.8/7章: 再読込フック。
        FinalizeApplyFromQueueIfNeeded(); // 4.10: キュー結合適用時はキューを空にする（MainViewModel.Queue.cs）。
        // 失敗ブロックが残った適用では、完了ダイアログが「理由は接ぎ木パネルの各ブロックに赤字で
        // 残っています」と案内する。その前に一覧を空にしてしまうと理由も「修正を依頼」も失われるため、
        // 失敗ブロックだけを残す（MainViewModel.PartialApply.cs）。全件成功なら従来どおり空にする。
        if (updatedDryRun.FailedCount > 0) KeepOnlyFailedBlocks(updatedDryRun);
        else DiscardCurrentPatch();
        await ProjectPane.LoadAsync().ConfigureAwait(true);
        if (project is not null) await History.LoadAsync(project.Id, project.Root).ConfigureAwait(true);

        // 6.5: 適用後フック。実行とonFailure（ignore/warn/offerRollback/autoRollback）の分岐は
        // MainViewModel.Hooks.cs（同ファイルグループ）へ委譲する。戻り値はロールバックを
        // 実際に試みたかどうか（下のGit自動コミットの可否判断に使う。MainViewModel.Git.cs参照）。
        //
        // Excel VBA 連携（仕様書_VBA連携 7.3-4）: Excel への自動 Push は、ここ（履歴の読み込みの後・フックの前）で行う。
        // NotifyFilesRewrittenAsync の直後では履歴の一覧がまだ古い。Push の失敗で適用を取り消した場合は、
        // rolledBack と同じ扱いにして、フックも Git 自動コミットも行わない。
        var rolledBack = false;
        var vbaUndone = false;
        string? vbaNoticeDetail = null;
        if (project is not null && IsVbaActive(project) && project.Vba!.AutoPushAfterApply)
        {
            (vbaUndone, vbaNoticeDetail) = await PushVbaAfterApplyAsync(project, result.Value).ConfigureAwait(true);
            rolledBack = vbaUndone;
        }

        // 未反映・削除保留の件数のバッジを数え直す（仕様書_VBA連携 7.6。Excel には接続しない。自動反映をオフにしている
        // プロジェクトでも、この適用でミラーが変わって「未反映」が増えるため、Push の有無にかかわらず呼ぶ）。
        if (project is not null && IsVbaActive(project)) ProjectPane.RequestVbaBadgeRefresh(checkWorkbookOpen: false);

        if (!rolledBack && project is not null && project.PostApplyHooks.Count > 0)
        {
            rolledBack = await RunPostApplyHooksAsync(project, result.Value.Revision).ConfigureAwait(true);
        }

        // 7.5: Git自動コミット。ロールバックされた変更をコミットしてしまわないよう、
        // 必ずフックの結果を確認した後に行う（MainViewModel.Git.csのコメント参照）。
        if (project is not null && !rolledBack)
        {
            await TryAutoCommitAfterApplyAsync(project, result.Value).ConfigureAwait(true);
        }

        // 実機で確認された指摘1: 既定のマウス操作だと「貼り付けて解析」→「適用」→プレビュー窓の「適用」→
        // 完了ダイアログの「OK」で4クリックかかり、しかもOKを押した瞬間にステータスバーへ
        // 「rN として適用しました — 元に戻す」（ShowApplyUndoNotice、下記）が出る。同じ「適用が
        // 完了した」という情報を、押しても何も増えないモーダルとステータスバーの二重で見せて
        // いたことになる。失敗ブロックが0件（＝完全に成功）のときはダイアログを省略し、
        // ステータスバー通知1本に一本化する。一方、失敗ブロックが1件以上残っているときは
        // 「何が適用できなかったか」を伝える価値があるため、従来どおりダイアログで案内する
        // （通知の文言はrNのみで、失敗件数までは含められないため）。
        //
        // 追加対応（実機点検）: 「（1件は適用できませんでした）」だけでは理由がどこにも書いて
        // おらず、利用者は次に何を見ればよいか分からなかった。失敗したブロックの理由（赤字）は
        // 接ぎ木パネルにそのまま残っているので、そこへ誘導する一言を必ず添える。
        if (updatedDryRun.FailedCount > 0 && !vbaUndone)
        {
            var completionMessage =
                $"r{result.Value.Revision} として適用しました。（{updatedDryRun.FailedCount}件は適用できませんでした）" +
                $"{Environment.NewLine}適用できなかった{updatedDryRun.FailedCount}件だけを接ぎ木パネルに残しています。" +
                "理由は各ブロックに赤字で出ています。" +
                $"「修正を依頼」で、その{updatedDryRun.FailedCount}件の修正依頼文をコピーできます。" +
                $"{Environment.NewLine}不要なら「破棄」で消せます。";
            await _dialogs.ShowMessageAsync("適用が完了しました", completionMessage).ConfigureAwait(true);
        }

        // 機能2: 適用直後の「元に戻す」通知（MainViewModel.ApplyUndoNotice.cs）。完了ダイアログを
        // 出す場合は、それを閉じた後に出す（ダイアログ表示中はステータスバーが見えず、閉じるまでの
        // 間に数秒の表示時間を消費してしまうため）。「適用が失敗した場合や、リビジョンが記録され
        // なかった場合には出さない」仕様のため、ここまで到達している時点で成功は確定しているが、
        // 念のためentriesが1件も無い（実質的に何も変更されなかった）ケースは対象外にする。
        // Push の失敗で取り消した適用には、「元に戻す」を出さない（すでに取り消してある）。
        if (result.Value.Entries.Count > 0 && !vbaUndone) ShowApplyUndoNotice(result.Value.Revision, vbaNoticeDetail);
    }

    /// <summary>
    /// 実機不具合対応: 適用成功時にlogs/へ記録する。SafeFileWriter・世代整理・history.jsonl追記
    /// 等が返したWarning/Info（例: 退避方式を使った、書き込み直後の検証でやり直した）は、
    /// 適用そのものは成功しているためダイアログでは出さないが、後から原因を追えるよう
    /// ログには必ず残す。
    /// </summary>
    private void LogApplySuccess(RevisionManifest manifest, IReadOnlyList<GraftIssue> issues, long durationMs)
    {
        Logger?.Info("apply", $"r{manifest.Revision} として適用しました（{manifest.Entries.Count}件）",
            revision: manifest.Revision, durationMs: durationMs);

        foreach (var issue in issues)
        {
            if (issue.Severity == Severity.Warning)
            {
                Logger?.Warn("apply", issue.ToDisplayText(), revision: manifest.Revision, targetPath: issue.Path);
            }
            else if (issue.Severity == Severity.Info)
            {
                Logger?.Info("apply", issue.ToDisplayText(), revision: manifest.Revision, targetPath: issue.Path);
            }
        }
    }

    /// <summary>
    /// 不具合2対応: 適用の直前に呼ぶ。projects.jsonのnextRevisionを1つ進めて永続化し、
    /// 今回の適用で実際に使う番号を入れた文脈を返す。
    /// 消費するかどうかを成功/失敗で分岐しない理由は<see cref="ProjectStore.ConsumeNextRevisionAsync(string, CancellationToken)"/>
    /// のコメント参照。番号の決め方（nextRevisionと実体の最大番号+1の大きい方）は
    /// <see cref="RevisionNumbering.ReserveAsync"/>のコメント参照。ドライラン時の見込み
    /// （<see cref="ApplyContext.Revision"/>）を下限に渡すので、見込みから動いていなければ
    /// プレビューやログに出た番号と同じになる。
    /// </summary>
    private async Task<ApplyContext> ReserveRevisionNumberAsync(ApplyContext context)
    {
        var reserved = await RevisionNumbering
            .ReserveAsync(_projectStore, _revisionStore, context.ProjectId, context.Revision)
            .ConfigureAwait(true);
        if (reserved.IsSuccess)
        {
            if (reserved.Value != context.Revision)
            {
                // ドライランの見込みから動いていた（その間に別の操作が番号を進めた）。
                // 使う番号を払い出された番号へ改め、食い違いが分かるようログに残す。
                Logger?.Info("apply",
                    $"履歴番号が見込みのr{context.Revision}からr{reserved.Value}に変わりました" +
                    "（解析の後に別の操作が番号を進めたため）",
                    revision: reserved.Value);
            }
            return context with { Revision = reserved.Value };
        }

        // projects.jsonへの書き込み不可等、想定外の状況。適用結果自体はここでは
        // Fail扱いにしない（次回起動時のReconcileRevisionsAsyncが実体フォルダの最大値から
        // 補正するため、番号がずれたままでも致命的にはならない）。ログにだけ残し、
        // 見込みの番号のまま続行する。
        SafeHandler.OnUnexpected?.Invoke(
            "履歴番号の更新",
            new InvalidOperationException(
                reserved.Errors.FirstOrDefault()?.Detail ?? "不明なエラーでprojects.jsonを更新できませんでした"));
        return context;
    }

    /// <summary>
    /// 不具合3対応: <see cref="ProjectStore.MarkAppliedAsync"/>を呼ぶ薄いラッパー。失敗しても
    /// 適用結果自体はここでは失敗にしない理由は<see cref="ReserveRevisionNumberAsync"/>と同じ。
    /// </summary>
    private async Task MarkProjectAppliedAsync(string projectId)
    {
        var marked = await _projectStore.MarkAppliedAsync(projectId, DateTimeOffset.Now).ConfigureAwait(true);
        if (!marked.IsSuccess)
        {
            SafeHandler.OnUnexpected?.Invoke(
                "最終適用日時の更新",
                new InvalidOperationException(
                    marked.Errors.FirstOrDefault()?.Detail ?? "不明なエラーでprojects.jsonを更新できませんでした"));
        }
    }

    /// <summary>
    /// UndoCommand（Ctrl+Z、および機能2のステータスバー「元に戻す」通知）の実体。
    /// 最新リビジョンを取り消す。どちらの経路で呼ばれても、もう有効ではない適用直後の通知を
    /// 出しっぱなしにしないよう、まず消す（DismissApplyUndoNotice＝MainViewModel.ApplyUndoNotice.cs）。
    /// </summary>
    private async Task UndoLastAsync()
    {
        if (IsVbaPushing) return; // Excel へ Push している最中は取り消さない（MainViewModel.VbaApply.cs）。
        DismissApplyUndoNotice();
        var undone = await History.UndoLatestAsync().ConfigureAwait(true);
        if (!undone) await _dialogs.ShowMessageAsync("取り消せません", "取り消せる直前の適用がありません。").ConfigureAwait(true);
    }

    /// <summary>RunDryRunAsyncの冒頭から呼ぶ。フック未設定時は常にtrue。</summary>
    private async Task<bool> ConfirmTargetsSavedAsync(string projectRoot)
    {
        if (BeforeApplyAsync is null) return true;
        return await BeforeApplyAsync(ResolveTargetFullPaths(projectRoot)).ConfigureAwait(true) != TargetSaveOutcome.Cancelled;
    }

    /// <summary>ApplyAsync成功直後から呼ぶ。フック未設定時は何もしない。</summary>
    private async Task NotifyFilesRewrittenAsync(string projectRoot, RevisionManifest manifest)
    {
        if (AfterApplyAsync is null) return;

        var files = manifest.Entries
            .Select(entry => Path.Combine(projectRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar)))
            .Distinct()
            .ToList();
        await AfterApplyAsync(files).ConfigureAwait(true);
    }

    /// <summary>_currentPatchのブロックから対象ファイルの絶対パス一覧（重複除去）を求める。</summary>
    private IReadOnlyList<string> ResolveTargetFullPaths(string projectRoot)
    {
        if (_currentPatch is null) return Array.Empty<string>();
        return _currentPatch.Blocks
            .Select(b => Path.Combine(projectRoot, b.Path.Replace('/', Path.DirectorySeparatorChar)))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// 課題1: <see cref="ApplyPreviewRequested"/>を発火し、Viewがダイアログを閉じるまで待つ。
    /// 実際に書き込まれる対象（チェック済みかつ適用可能なブロック）だけを渡す。
    /// 購読者が居ない場合はBeforeApplyAsync等と同じ流儀で確認済み（true）として素通りさせる。
    /// </summary>
    private Task<bool> RequestApplyPreviewAsync(DryRunResult updatedDryRun)
    {
        if (ApplyPreviewRequested is null) return Task.FromResult(true);

        var plansToApply = updatedDryRun.Plans.Where(p => p.IsSelected && p.CanApply).ToList();
        var args = new ApplyPreviewRequestedEventArgs(plansToApply, _settings, _ui);
        ApplyPreviewRequested.Invoke(this, args);
        return args.Completion.Task;
    }
}

/// <summary>
/// 課題1: <see cref="MainViewModel.ApplyPreviewRequested"/>の引数。Viewは
/// <see cref="Views.ApplyPreviewWindow"/>を表示し、閉じたら結果（適用するかどうか）を
/// <see cref="Completion"/>へ書き込む。
/// </summary>
public sealed class ApplyPreviewRequestedEventArgs : EventArgs
{
    public ApplyPreviewRequestedEventArgs(IReadOnlyList<BlockPlan> plansToApply, Settings settings, IUiServices ui)
    {
        PlansToApply = plansToApply ?? throw new ArgumentNullException(nameof(plansToApply));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    /// <summary>実際に書き込まれる対象（チェック済みかつ適用可能なブロック）。</summary>
    public IReadOnlyList<BlockPlan> PlansToApply { get; }

    /// <summary>diff表示（DiffViewModel）の構築に使う現在の設定。</summary>
    public Settings Settings { get; }

    /// <summary>diff表示（DiffViewModel）の構築に使うUI機能一式。</summary>
    public IUiServices Ui { get; }

    /// <summary>Viewがダイアログを閉じたら結果（適用する＝true／キャンセル＝false）を書き込む。</summary>
    public TaskCompletionSource<bool> Completion { get; } = new();
}
