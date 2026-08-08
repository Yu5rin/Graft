using Graft.Core;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル（1ファイル400行上限のため）。
/// 仕様書7.5 Git連携の「適用後の自動コミット」を配線する。<see cref="GitIntegration.CommitAsync"/>は
/// 実装済みだったが呼び出し元が存在せず、設定画面の<see cref="Graft.Infra.GitSettings.AutoCommit"/>
/// をオンにしても何も起きない状態だった（設定が実際の挙動と食い違っていた）。
/// </summary>
public sealed partial class MainViewModel
{
    private readonly GitIntegration _gitIntegration = new();

    /// <summary>
    /// ApplyAsync成功直後から呼ぶ。<see cref="Graft.Infra.GitSettings.AutoCommit"/>が有効なときだけ
    /// 実際にコミットする。
    ///
    /// 実行順序について: このメソッドはMainViewModel.Apply.csのApplyAsyncから、
    /// 6.5適用後フック（<see cref="RunPostApplyHooksAsync"/>）の**後**に、その戻り値
    /// （ロールバックを試みたかどうか）を確認したうえで呼ぶこと。適用後フックにはテストを
    /// 走らせて失敗時に自動ロールバックする機能があり、ロールバックされた変更を先にコミット
    /// してしまうと、Git履歴にだけ「テストに失敗したはずの変更」が残ってしまう。
    /// ロールバックが試みられた（＝成功でファイルが元に戻った、または失敗して状態が不確実に
    /// なった）場合は、いずれのケースでもコミットしないのが安全なため、呼び出し元で
    /// スキップの判断をしてもらう設計にしている（このメソッド自身はロールバックの有無を知らない）。
    ///
    /// 失敗時の扱い: gitリポジトリでない・gitコマンドが見つからない・コミット対象が無い等は
    /// いずれも日常的に起こりうる状況であり、AutoCommitをオンにした利用者に適用のたびに
    /// エラーダイアログを見せると煩わしいだけで得るものが無い。加えて適用（ファイルの書き換え）
    /// 自体は既に成功しているため、コミットの失敗を理由に適用全体を失敗扱いにするのも誤り。
    /// そのためここでの失敗は静かに諦める（呼び出し元・利用者への通知は行わない）。
    /// MainViewModelにはログ出力用の依存が無く（Loggerはアプリ起動処理・View層のみが保持する）、
    /// この程度の付随的な失敗のためだけにコンストラクタへ新たな依存を追加するのは見送った
    /// （ログに残せないこと自体は実機確認・課題3の報告で明記する）。
    /// </summary>
    private async Task TryAutoCommitAfterApplyAsync(Project project, RevisionManifest manifest)
    {
        if (!_settings.Git.AutoCommit) return;

        // RENAMEは移動元・移動先の両方をステージしないと、移動元の削除がコミットに含まれない。
        var paths = manifest.Entries
            .SelectMany<RevisionEntry, string>(e => e.RenamedFrom is null
                ? new[] { e.Path }
                : new[] { e.RenamedFrom, e.Path })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0) return; // コミット対象が無い（ApplyCommandの実行条件上ここには来ないはずだが念のため）。

        // 6.6/17章: コミットメッセージは"type: summary"形式（GitIntegration.CommitAsync）。
        // 履歴（history.jsonl・back/配下）とGitログを見比べられるよう、リビジョン番号を添える。
        var summary = string.IsNullOrWhiteSpace(manifest.Summary) ? "Graftによる変更" : manifest.Summary;
        var message = $"{summary} (r{manifest.Revision})";

        // 戻り値は意図的に無視する。理由は本メソッドのコメント参照（静かに諦める設計）。
        _ = await _gitIntegration.CommitAsync(project.Root, manifest.Type, message, paths).ConfigureAwait(true);
    }
}
