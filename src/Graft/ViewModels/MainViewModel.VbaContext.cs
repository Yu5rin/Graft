using System.IO;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// コンテキスト収集が走査の前に呼ぶ「Excel から最新を取り込む」処理（仕様書_VBA連携 7.2）。
/// </summary>
/// <param name="showConflict">
/// 衝突ダイアログを出す窓口。コンテキスト収集の窓が開いている間は、その窓を親にして出す窓口が入る
/// （モーダルの裏にメインウィンドウ親のダイアログが回ると操作できなくなるため）。null なら
/// <c>MainViewModel</c> 既定の窓口（メインウィンドウが親）で出す。
/// </param>
/// <param name="ct">取り消し。</param>
/// <returns>取り込めなかったときの警告文。取り込めた、または対象外なら null。</returns>
public delegate Task<string?> VbaContextPullAsync(
    Func<VbaConflictRequest, Task<VbaConflictDecision?>>? showConflict, CancellationToken ct);

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル。コンテキスト収集の自動 Pull（仕様書_VBA連携 7.2）。
/// <para>
/// 【経路は <c>ContextCollectViewModel</c> の入口に集める】 窓を開く経路だけでなく、依頼文コピー
/// （<c>PromptCopyViewModel</c>。<c>{{files}}</c>・「選んだファイルも付ける」・グローバルホットキー）も同じ
/// 収集の入口を通る。そこで取り込みを窓ではなく ViewModel の側に置き、窓を開かない経路でも古い本文が
/// AI に渡らないようにしている。
/// </para>
/// <para>
/// 【止めない】 Excel に繋がらない・応答しない・衝突を「中止」したときも、収集は止めない。
/// ミラーの現在の内容で続け、警告を 1 件返す。コンテキスト収集は AI へ渡す前の下ごしらえで、Excel が
/// 閉じているだけで手が止まると不便なため。未保存の編集の確認と、取り込みの履歴を残さない扱いは、
/// 手動の取込・解析前の取込と同じ <see cref="VbaSyncService.PullAsync"/> の流れに従う。
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// 指定のプロジェクトがコンテキスト収集の前に Excel を取り込む処理を作る。VBA プロジェクトでなければ null
    /// （通常のファイルプロジェクトの挙動は従来どおり。呼び出しも増えない）。設定は呼び出しのたびに読む
    /// （設定画面での変更を、プロジェクトを選び直さなくても反映するため）。
    /// </summary>
    private VbaContextPullAsync? CreateVbaContextPull(Project project)
    {
        if (project.Vba is null) return null;
        return (showConflict, ct) => PullVbaForContextAsync(project, showConflict, ct);
    }

    private async Task<string?> PullVbaForContextAsync(
        Project project, Func<VbaConflictRequest, Task<VbaConflictDecision?>>? showConflict, CancellationToken ct)
    {
        // 一覧を読み直した後の最新の Project（VbaLink）を使う。Id・Root・ブックのパスは変わらないが、念のため。
        var current = ProjectPane.Items.FirstOrDefault(i => i.Project.Id == project.Id)?.Project ?? project;
        if (!IsVbaActive(current) || !_settings.Vba.PullOnContextOpen) return null;

        var result = await VbaSync.PullAsync(current, ct: ct).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            Logger?.Warn("vba-pull", "コンテキスト収集の前の Excel からの取り込みに失敗しました。ミラーの現在の内容で続けます。", targetPath: current.Root);
            // 理由は、コードと見出しだけを添える（詳細の長い文は、警告の行に収まらないため。全文はログと、手動の取込で見られる）。
            var firstError = result.Issues.FirstOrDefault(i => i.Severity == Severity.Error);
            var reason = firstError is null ? null : $"{firstError.Code}: {ErrorCatalog.SummaryOf(firstError.Code)}";
            return reason is null
                ? "Excel の最新を取り込めていません。Graft 側（ミラー）の現在の内容で続けます。"
                : $"Excel の最新を取り込めていません。Graft 側（ミラー）の現在の内容で続けます（{reason}）";
        }

        var pulled = result.Value;
        foreach (var skipped in pulled.Skipped)
        {
            Logger?.Warn("vba", $"未保存の編集があるため、Excel からの取り込みをスキップしました: {skipped}", targetPath: Path.Combine(current.Root, skipped));
        }

        if (pulled.Conflicts.Count > 0)
        {
            var request = new VbaConflictRequest(VbaConflictScene.ContextCollect, pulled.ConflictItems);
            var ask = showConflict ?? ShowVbaConflictAsync;
            var decision = (ask is null ? null : await ask(request).ConfigureAwait(true)) ?? request.DefaultDecision;
            if (decision.Aborted)
            {
                return "Excel の最新を取り込めていません（Excel 側とファイル側の両方が変更されているため）: " +
                       string.Join("、", pulled.ConflictItems.Select(c => c.RelativePath));
            }

            var adopt = decision.PathsWith(VbaConflictChoice.AdoptExcel);
            if (adopt.Count > 0)
            {
                var adopted = await VbaSync.PullAsync(current, adopt, adoptConflicts: adopt, ct: ct).ConfigureAwait(true);
                if (!adopted.IsSuccess) return "Excel の最新を取り込めていません。Graft 側（ミラー）の現在の内容で続けます。";
            }

            var overwrite = decision.PathsWith(VbaConflictChoice.OverwriteExcel);
            if (overwrite.Count > 0)
            {
                var pushed = await VbaSync.PushAsync(current, overwrite, overwrite, ct).ConfigureAwait(true);
                if (!pushed.IsSuccess) return "Graft 側の内容を Excel へ反映できませんでした。Graft 側（ミラー）の現在の内容で続けます。";
            }

            // 衝突を解決した（または取り込んだ）ので、保存時の自動反映が付けた「衝突 N」の印を見直す
            // （バッジを数え直すと、未反映でなくなったモジュールの印が外れる）。
            ProjectPane.RequestVbaBadgeRefresh(checkWorkbookOpen: false);
        }

        return null;
    }
}
