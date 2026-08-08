using Graft.Core;
using Graft.Features;
using Graft.Infra;

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
    /// 課題3: 自動コミットの成否をlogs/&lt;日付&gt;.logへ記録するためのロガー。起動処理
    /// （StartupCoordinator）が生成する<see cref="Logger"/>をShellWindowと同じ流儀
    /// （コンストラクタ引数ではなく、生成後に設定するnullableプロパティ）で受け取る。
    /// コンストラクタへ新たな依存を追加すると、本体のテスト用ビルダ（BuildShellViewModel等）
    /// すべてに影響が及ぶため、この程度の付随的なログ出力のためだけにそこまでの変更は見送った
    /// （未設定＝nullでも動作は変わらず、単にログへ残らないだけ）。
    /// </summary>
    public Logger? Logger { get; set; }

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
    /// そのため画面には一切出さず静かに諦めるが、「コミットされない」と相談を受けたときに
    /// 原因を追えるよう、理由を区別してLoggerへ記録する（課題3）。
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
        if (paths.Count == 0)
        {
            // コミット対象が無い（ApplyCommandの実行条件上ここには来ないはずだが念のため）。
            Logger?.Warn("git-auto-commit", "コミット対象が無いためスキップしました", revision: manifest.Revision);
            return;
        }

        // git add / git commit を試みる前に前提条件を確認する。CommitAsync自身は
        // 「gitが無い」も「リポジトリでない」も同じ形の失敗（"git add に失敗しました: ..."）で
        // 返ってくるため、ログで理由を区別するにはここで先に見分ける必要がある。
        var preflight = await _gitIntegration.CheckCommitPreflightAsync(project.Root).ConfigureAwait(true);
        switch (preflight)
        {
            case GitCommitPreflight.GitCommandNotFound:
                Logger?.Warn("git-auto-commit", "gitコマンドが見つからないためスキップしました", revision: manifest.Revision);
                return;
            case GitCommitPreflight.NotARepository:
                Logger?.Warn("git-auto-commit", $"「{project.DisplayName}」はgitリポジトリではないためスキップしました", revision: manifest.Revision);
                return;
        }

        // 6.6/17章: コミットメッセージは"type: summary"形式（GitIntegration.CommitAsync）。
        // 履歴（history.jsonl・back/配下）とGitログを見比べられるよう、リビジョン番号を添える。
        var summary = string.IsNullOrWhiteSpace(manifest.Summary) ? "Graftによる変更" : manifest.Summary;
        var message = $"{summary} (r{manifest.Revision})";

        var result = await _gitIntegration.CommitAsync(project.Root, manifest.Type, message, paths).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            // 上のpreflightで「gitが無い」「リポジトリでない」は既に除外済みのため、ここに来る
            // 失敗はそれ以外の理由（コミット対象の差分が実際には無かった、gitのuser.name/
            // user.emailが未設定、pre-commitフックの失敗等）としてまとめて記録する。
            var detail = result.Issues.FirstOrDefault()?.Detail ?? "詳細不明のエラー";
            Logger?.Warn("git-auto-commit", $"コミットに失敗しました: {detail}", revision: manifest.Revision);
            return;
        }

        Logger?.Info("git-auto-commit", $"コミットしました: {result.Value}", revision: manifest.Revision);
    }
}
