using System.IO;
using Graft.Core;

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
    /// falseが返るとドライランを中止する。
    /// </summary>
    public Func<IReadOnlyList<string>, Task<bool>>? BeforeApplyAsync { get; set; }

    /// <summary>
    /// 4.8/7章 適用後フック。適用が成功したら、書き換わったファイルの絶対パス一覧を渡して必ず呼ぶ。
    /// </summary>
    public Func<IReadOnlyList<string>, Task>? AfterApplyAsync { get; set; }

    /// <summary>ApplyCommandの実体。要約入力・確認ダイアログを経て本適用し、6.5適用後フックを実行する。</summary>
    private async Task ApplyAsync()
    {
        if (_dryRun is null || _lastContext is null) return;

        var updatedPlans = Blocks.Select(b => b.Plan with { IsSelected = b.IsSelected }).ToList();
        var updatedDryRun = _dryRun with { Plans = updatedPlans };

        if (_settings.RequireSummary && string.IsNullOrWhiteSpace(updatedDryRun.Patch.Meta.Summary))
        {
            var input = await _dialogs.PromptAsync("要約を入力", "このリビジョンの概要を入力してください。", null).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(input)) return;
            var patchWithSummary = updatedDryRun.Patch with { Meta = updatedDryRun.Patch.Meta with { Summary = input } };
            updatedDryRun = updatedDryRun with { Patch = patchWithSummary };
        }

        var confirmed = await _dialogs
            .ConfirmAsync("適用の確認", $"{updatedDryRun.ApplicableCount}件を適用します。よろしいですか？")
            .ConfigureAwait(true);
        if (!confirmed) return;

        var project = ProjectPane.SelectedItem?.Project;

        State = CenterPaneState.Loading;
        var result = await _applyEngine.ApplyAsync(updatedDryRun, _lastContext).ConfigureAwait(true);

        // 不具合2対応: 適用を試みた直後に、成功・失敗を問わずnextRevisionを消費して
        // projects.jsonへ永続化する（消費しないと次回も同じ番号が付与され続ける）。
        // 失敗時にも消費する理由はProjectStore.ConsumeNextRevisionAsyncのコメント参照。
        // ProjectPane.LoadAsync（下）より前に行い、再読込結果へ確実に反映させる。
        await ConsumeRevisionNumberAsync(_lastContext.ProjectId).ConfigureAwait(true);

        if (!result.IsSuccess)
        {
            CenterError = result.Errors.FirstOrDefault();
            State = CenterPaneState.Error;
            return;
        }

        await NotifyFilesRewrittenAsync(_lastContext.ProjectRoot, result.Value).ConfigureAwait(true); // 4.8/7章: 再読込フック。
        FinalizeApplyFromQueueIfNeeded(); // 4.10: キュー結合適用時はキューを空にする（MainViewModel.Queue.cs）。
        DiscardCurrentPatch();
        await ProjectPane.LoadAsync().ConfigureAwait(true);
        if (project is not null) await History.LoadAsync(project.Id, project.Root).ConfigureAwait(true);

        // 6.5: 適用後フック。実行とonFailure（ignore/warn/offerRollback/autoRollback）の分岐は
        // MainViewModel.Hooks.cs（同ファイルグループ）へ委譲する。戻り値はロールバックを
        // 実際に試みたかどうか（下のGit自動コミットの可否判断に使う。MainViewModel.Git.cs参照）。
        var rolledBack = false;
        if (project is not null && project.PostApplyHooks.Count > 0)
        {
            rolledBack = await RunPostApplyHooksAsync(project, result.Value.Revision).ConfigureAwait(true);
        }

        // 7.5: Git自動コミット。ロールバックされた変更をコミットしてしまわないよう、
        // 必ずフックの結果を確認した後に行う（MainViewModel.Git.csのコメント参照）。
        if (project is not null && !rolledBack)
        {
            await TryAutoCommitAfterApplyAsync(project, result.Value).ConfigureAwait(true);
        }

        await _dialogs.ShowMessageAsync("適用が完了しました", $"r{result.Value.Revision} として記録しました。").ConfigureAwait(true);
    }

    /// <summary>
    /// 不具合2対応: 適用を試みた直後に呼ぶ。projects.jsonのnextRevisionを1つ進めて永続化する。
    /// 消費するかどうかを成功/失敗で分岐しない理由は<see cref="ProjectStore.ConsumeNextRevisionAsync"/>
    /// のコメント参照。
    /// </summary>
    private async Task ConsumeRevisionNumberAsync(string projectId)
    {
        var consumed = await _projectStore.ConsumeNextRevisionAsync(projectId).ConfigureAwait(true);
        if (!consumed.IsSuccess)
        {
            // projects.jsonへの書き込み不可等、想定外の状況。適用結果自体はここでは
            // Fail扱いにしない（次回起動時のReconcileRevisionsAsyncが実体フォルダの最大値から
            // 補正するため、番号がずれたままでも致命的にはならない）。ログにだけ残す。
            SafeHandler.OnUnexpected?.Invoke(
                "リビジョン番号の更新",
                new InvalidOperationException(
                    consumed.Errors.FirstOrDefault()?.Detail ?? "不明なエラーでprojects.jsonを更新できませんでした"));
        }
    }

    /// <summary>UndoCommand（Ctrl+Z）の実体。最新リビジョンを取り消す。</summary>
    private async Task UndoLastAsync()
    {
        var undone = await History.UndoLatestAsync().ConfigureAwait(true);
        if (!undone) await _dialogs.ShowMessageAsync("取り消せません", "取り消し可能な直前のリビジョンがありません。").ConfigureAwait(true);
    }

    /// <summary>RunDryRunAsyncの冒頭から呼ぶ。フック未設定時は常にtrue。</summary>
    private async Task<bool> ConfirmTargetsSavedAsync(string projectRoot)
    {
        if (BeforeApplyAsync is null) return true;
        return await BeforeApplyAsync(ResolveTargetFullPaths(projectRoot)).ConfigureAwait(true);
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
}
