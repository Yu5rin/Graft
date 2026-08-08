using Graft.Core;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル（1ファイル400行上限のため）。
/// 仕様書6.5 適用後フックの実行結果を受けて、ignore/warn/offerRollback/autoRollbackの
/// 4種類の挙動に分岐する。実行自体は<see cref="HookRunner"/>へ委譲し、ロールバックは
/// 新規リビジョンを作らない既存の巻き戻し処理（<see cref="RevisionRestorer"/>、仕様書7.3。
/// <see cref="HistoryPaneViewModel.UndoLatestAsync"/>相当）を再利用する。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// ApplyAsync成功直後・履歴反映後に呼ぶ。フック未設定のプロジェクトでは呼ばれない。
    /// 戻り値はロールバックを実際に試みたかどうか（成功・失敗を問わない）。呼び出し元
    /// （MainViewModel.Apply.cs）はこれを見て、Git自動コミット（MainViewModel.Git.cs）を
    /// 行ってよいかを判断する——ロールバックが試みられた場合、書き戻された内容は
    /// このリビジョンの変更ではなくなっている（成功時）か状態が不確実になっている（失敗時）ため、
    /// どちらのケースでもこのリビジョンの変更としてコミットしてはならない。
    /// </summary>
    private async Task<bool> RunPostApplyHooksAsync(Project project, int revision)
    {
        // HookRunner.RunAsyncは実行失敗も個々のHookResultへ詰めて返すため、常に成功で返る。
        var run = await _hookRunner.RunAsync(project, _settings.Hooks.TimeoutSec).ConfigureAwait(true);
        var results = run.Value;

        // manifest.jsonへの記録に失敗しても、以降のonFailure分岐は続行する
        // （記録の失敗自体はフックの成否と無関係な副作用のため）。
        await _revisionStore.RecordHookResultsAsync(project.Id, revision, results).ConfigureAwait(true);

        var failed = results.Where(r => r.ExitCode != 0).ToList();
        if (failed.Count == 0) return false;

        var actionByName = project.PostApplyHooks.ToDictionary(h => h.Name, h => h.OnFailure);
        var actions = failed.Select(f => actionByName.GetValueOrDefault(f.Name, HookFailureAction.Warn)).ToList();

        if (actions.Contains(HookFailureAction.AutoRollback))
        {
            await RollbackAfterHookFailureAsync(project, revision, failed, askFirst: false).ConfigureAwait(true);
            return true; // 確認なしのロールバックは必ず実施される。
        }

        if (actions.Contains(HookFailureAction.OfferRollback))
        {
            return await RollbackAfterHookFailureAsync(project, revision, failed, askFirst: true).ConfigureAwait(true);
        }

        if (actions.Contains(HookFailureAction.Warn))
        {
            await _dialogs.ShowMessageAsync("適用後フックが失敗しました", BuildHookFailureMessage(failed)).ConfigureAwait(true);
        }
        // ignore: manifestへの記録のみ行い、UIへは通知しない。
        return false;
    }

    /// <summary>
    /// offerRollback（<paramref name="askFirst"/>=true）は確認ダイアログを表示してから、
    /// autoRollback（false）は確認なしに、直前の状態（適用したリビジョンの巻き戻し）へ復元する。
    /// 戻り値はロールバックを実際に試みたかどうか。offerRollbackで利用者が「いいえ」を選んだ
    /// 場合はロールバック自体が行われない（変更はそのまま残る）ため false を返す。
    /// </summary>
    private async Task<bool> RollbackAfterHookFailureAsync(
        Project project, int revision, IReadOnlyList<HookResult> failed, bool askFirst)
    {
        var detail = BuildHookFailureMessage(failed);
        if (askFirst)
        {
            var confirmed = await _dialogs
                .ConfirmAsync("適用後フックが失敗しました", $"{detail}{Environment.NewLine}{Environment.NewLine}直前の状態へロールバックしますか？")
                .ConfigureAwait(true);
            if (!confirmed) return false;
        }

        var summary = await _revisionStore.ReadAsync(project.Id, revision).ConfigureAwait(true);
        if (!summary.IsSuccess)
        {
            await _dialogs.ShowMessageAsync("ロールバックに失敗しました",
                $"リビジョンr{revision}が見つからないため、手動で履歴から復元してください。").ConfigureAwait(true);
            return true; // 復元は試みた（失敗した）。状態が不確実なため呼び出し元はコミットしてはならない。
        }

        var restored = await _revisionRestorer
            .RestoreAsync(project.Id, project.Root, summary.Value, force: true)
            .ConfigureAwait(true);

        var title = askFirst ? "ロールバックしました" : "フック失敗のため自動的にロールバックしました";
        if (restored.IsSuccess)
        {
            await _dialogs.ShowMessageAsync(title, detail).ConfigureAwait(true);
        }
        else
        {
            await _dialogs.ShowMessageAsync("ロールバックに失敗しました",
                string.Join(Environment.NewLine, restored.Errors.Select(i => i.ToDisplayText()))).ConfigureAwait(true);
        }

        await History.LoadAsync(project.Id, project.Root).ConfigureAwait(true);
        return true;
    }

    private static string BuildHookFailureMessage(IReadOnlyList<HookResult> failed)
    {
        var lines = failed.Select(f => f.TimedOut
            ? $"・{f.Name}: タイムアウトしました"
            : $"・{f.Name}: 終了コード {f.ExitCode}");
        return string.Join(Environment.NewLine, lines);
    }
}
