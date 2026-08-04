using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// 起動時検証で検出した「未完了のまま残ったリビジョン」1プロジェクト分（仕様書6.3・E403）。
/// 実際のロールバック実行は <see cref="Core.RevisionRestorer"/> を使い、ユーザーが承諾した
/// 場合のみ行う（<see cref="Views.StartupCoordinator"/> の責務）。
/// </summary>
public sealed record InProgressRevisionIssue
{
    /// <summary>対象プロジェクトのID。</summary>
    public required string ProjectId { get; init; }

    /// <summary>対象プロジェクトの表示名。</summary>
    public required string ProjectName { get; init; }

    /// <summary>対象プロジェクトルートの絶対パス。</summary>
    public required string ProjectRoot { get; init; }

    /// <summary>status: "in_progress" のまま残っているリビジョン（新しい順）。</summary>
    public required IReadOnlyList<RevisionSummary> Revisions { get; init; }
}

/// <summary>
/// 起動時検証（<see cref="Views.StartupCoordinator"/>）が収集した結果をUIへ渡すためのモデル。
/// 仕様書13.1（データ破損・リビジョン不整合）・6.3（中断復帰）・15章（ログ削除）・
/// 4.10（パッチキュー復元）・8.10/8.12（ホットキー・クリップボード監視の登録失敗）に対応する。
/// back/ 配下の走査は重いため（17章）非同期・バックグラウンドで行い、本レポートは
/// その完了後にまとめて届く。UIの初期表示自体はこのレポートを待たない。
/// </summary>
public sealed class StartupReport
{
    /// <summary>
    /// settings.json/projects.json の破損・不正値・未接続プロジェクト・ホットキー登録失敗（E601）等、
    /// 通知すべき問題の一覧。致命的でない Warning/Info を含む。
    /// </summary>
    public IReadOnlyList<GraftIssue> Issues { get; init; } = Array.Empty<GraftIssue>();

    /// <summary>status: in_progress のまま残っているリビジョン（プロジェクト単位）。ロールバック提案の対象。</summary>
    public IReadOnlyList<InProgressRevisionIssue> InProgressRevisions { get; init; } = Array.Empty<InProgressRevisionIssue>();

    /// <summary>初回起動かどうか（8.15章のオンボーディング表示判定に使う）。</summary>
    public bool IsFirstLaunch { get; init; }

    /// <summary>通知すべき問題が1件もないかどうか。</summary>
    public bool IsClean => Issues.Count == 0 && InProgressRevisions.Count == 0;

    /// <summary>
    /// Issues のうち、ユーザーへ表示するに値するもの（Info以外）だけを抽出する。
    /// Info は通知ダイアログを煩雑にしないため表示対象から除く。
    /// </summary>
    public IReadOnlyList<GraftIssue> NotifiableIssues
        => Issues.Where(i => i.Severity != Severity.Info).ToList();

    /// <summary>通知ダイアログ用の本文を組み立てる。1件も無い場合は空文字列を返す。</summary>
    public string BuildIssuesSummaryText()
    {
        var notifiable = NotifiableIssues;
        if (notifiable.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, notifiable.Select(i => "・" + i.ToDisplayText()));
    }

    /// <summary>指定プロジェクトのロールバック提案文を組み立てる。</summary>
    public static string BuildRollbackPrompt(InProgressRevisionIssue issue)
    {
        var revisions = string.Join("、", issue.Revisions.Select(r => $"r{r.Manifest.Revision}"));
        return $"プロジェクト「{issue.ProjectName}」で前回の適用が完了しないまま終了しています（{revisions}）。" +
               "中途半端な状態のまま残っている可能性があります。適用前の状態へロールバックしますか？";
    }
}
