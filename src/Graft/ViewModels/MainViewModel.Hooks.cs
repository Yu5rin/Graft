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

    /// <summary>ダイアログへ載せるフック出力の行数。</summary>
    private const int HookOutputTailLines = 5;

    /// <summary>ダイアログへ載せるフック出力の1行あたりの上限文字数。</summary>
    private const int HookOutputLineMaxChars = 200;

    /// <summary>
    /// フック失敗ダイアログの本文を組み立てる。
    ///
    /// 【実機不具合対応】 以前は「・ビルド: 終了コード 1」のように<b>終了コードしか</b>出して
    /// いなかった。<see cref="HookRunner"/>は標準出力・標準エラーを
    /// <see cref="HookResult.Output"/>へちゃんと集めているのに、画面には一切出しておらず、
    /// 利用者は「何が失敗したのか」を知る手段が無かった（ログにも出力そのものは残らない）。
    /// 出力の末尾数行を併記する。コンパイルエラーやテストの失敗は末尾に出るのが普通で、
    /// 「次に何を直せばよいか」はたいていこの数行で分かる。
    ///
    /// 【全文を出さない理由】 ビルド出力は数千行になりうる。ダイアログへ全文を載せると
    /// 読めないうえ、画面外へあふれてボタンにも届かなくなる。行数（<see cref="HookOutputTailLines"/>）と
    /// 1行の長さ（<see cref="HookOutputLineMaxChars"/>）の両方で必ず抑える。全文が必要な場合は
    /// フックのコマンド側でログファイルへ書き出してもらう想定
    /// （manifest.jsonへは保存しない。理由は<see cref="HookResult.Output"/>のコメント参照）。
    /// </summary>
    private static string BuildHookFailureMessage(IReadOnlyList<HookResult> failed)
    {
        var blocks = failed.Select(f =>
        {
            var head = f.TimedOut
                ? $"・{f.Name}: タイムアウトしました"
                : $"・{f.Name}: 終了コード {f.ExitCode}";
            var tail = SummarizeHookOutput(f.Output);
            return tail is null ? head : $"{head}{Environment.NewLine}{tail}";
        });
        return string.Join(Environment.NewLine, blocks);
    }

    /// <summary>
    /// フックの出力から末尾の数行を取り出し、字下げして返す。出力が空なら null
    /// （「出力はありません」と書いても利用者にできることは無く、行数を食うだけのため）。
    /// </summary>
    private static string? SummarizeHookOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var lines = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        if (lines.Count == 0) return null;

        var tail = lines.Skip(Math.Max(0, lines.Count - HookOutputTailLines)).ToList();
        var body = tail.Select(l =>
        {
            var trimmed = l.TrimEnd();
            if (trimmed.Length > HookOutputLineMaxChars) trimmed = trimmed[..HookOutputLineMaxChars] + "…";
            return $"    {trimmed}";
        });

        // 何行のうちの何行かを明示する。これが無いと、抜粋なのか出力の全部なのかが分からず、
        // 「これで全部のはずなのに原因が書いていない」という誤解を招く。
        var header = lines.Count > tail.Count
            ? $"  出力の末尾{tail.Count}行（全{lines.Count}行）:"
            : "  出力:";
        return header + Environment.NewLine + string.Join(Environment.NewLine, body);
    }
}
