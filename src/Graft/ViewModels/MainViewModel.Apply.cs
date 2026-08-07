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
        // MainViewModel.Hooks.cs（同ファイルグループ）へ委譲する。
        if (project is not null && project.PostApplyHooks.Count > 0)
        {
            await RunPostApplyHooksAsync(project, result.Value.Revision).ConfigureAwait(true);
        }

        await _dialogs.ShowMessageAsync("適用が完了しました", $"r{result.Value.Revision} として記録しました。").ConfigureAwait(true);
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
