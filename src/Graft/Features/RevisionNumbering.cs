using Graft.Core;

namespace Graft.Features;

/// <summary>
/// 適用・「ここまで戻す」が新しいリビジョンに付ける番号を、projects.jsonのnextRevisionと
/// 実体（back/配下のフォルダ・history.jsonl）の両方から決める。
///
/// 【なぜ必要か（実機で起きた不具合）】 番号の出どころは長らくprojects.jsonのnextRevisionだけだった。
/// ところがコンテキスト収集画面の保存が古いスナップショットでprojects.jsonを丸ごと上書きし、
/// r42まで適用したあとにnextRevisionが36へ戻った。次の適用はr36を使い、BackupManagerは
/// <c>r{N}_{yyyyMMdd_HHmmss}</c> というタイムスタンプつきの名前でフォルダを作るため
/// 「同じ番号・時刻違い」のフォルダが衝突せずに2つできた（r36〜r42が2組、さらにr37…r44と続いた）。
/// 原因の書き戻しは直したが、nextRevisionが何らかの理由で実体より小さくなっても
/// 重複を作らないよう、番号を決めるときに実体の最大番号+1も必ず見る（多重の防御）。
///
/// 使う実体の最大値は <see cref="RevisionStore.DetectMaxRevisionAsync"/> で、起動時の補正
/// （<see cref="ProjectStore.ReconcileRevision"/>）と同じ値。フォルダに加えてhistory.jsonlも
/// 見るので、フォルダが後から消えても番号を再利用しない（仕様書13.1の方針と同じ）。
/// </summary>
public static class RevisionNumbering
{
    /// <summary>
    /// 次に付ける番号の見込みを返す（何も消費しない）。ドライラン時の文脈・ログ・プレビューに
    /// 出す番号を、実際に払い出す番号（<see cref="ReserveAsync"/>）と揃えるために使う。
    /// 画面が持つ古いスナップショット（<paramref name="snapshotNextRevision"/>）、projects.jsonの
    /// 最新値、実体の最大値+1のうち最大のものを返す。
    /// </summary>
    public static async Task<int> PeekNextAsync(
        ProjectStore projectStore, RevisionStore revisionStore, string projectId, int snapshotNextRevision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(revisionStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var next = snapshotNextRevision;

        var loaded = await projectStore.LoadAsync(ct).ConfigureAwait(false);
        var latest = loaded.Value.FirstOrDefault(p => p.Id == projectId);
        if (latest is not null) next = Math.Max(next, latest.NextRevision);

        var max = await revisionStore.DetectMaxRevisionAsync(projectId, ct).ConfigureAwait(false);
        if (max.IsSuccess) next = Math.Max(next, max.Value + 1);

        return next;
    }

    /// <summary>
    /// 新しいリビジョンの番号を払い出し、projects.jsonのnextRevisionを「払い出した番号+1」へ進める。
    /// 払い出す番号は <c>max(projects.jsonのnextRevision, 実体の最大番号+1, minimumRevision)</c>。
    /// 払い出しと進行はProjectStoreのゲートの中で不可分に行われるため、並行する他の払い出しとも
    /// 重ならず、記録されるnextRevisionと実際に使う番号が食い違うこともない。
    /// 成功・失敗を問わず番号を消費する既存の方針（<see cref="ProjectStore.ConsumeNextRevisionAsync(string, CancellationToken)"/>
    /// のコメント）はそのまま守る。
    /// </summary>
    /// <param name="minimumRevision">
    /// 呼び出し側が既に見込んでいる番号（ドライラン時点の <see cref="PeekNextAsync"/> の結果など）。
    /// 見込みより小さい番号を払い出さないための下限で、見込みから動いていなければ同じ番号になる。
    /// </param>
    public static async Task<GraftResult<int>> ReserveAsync(
        ProjectStore projectStore, RevisionStore revisionStore, string projectId, int minimumRevision = 0,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(revisionStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var floor = minimumRevision;
        var max = await revisionStore.DetectMaxRevisionAsync(projectId, ct).ConfigureAwait(false);
        if (max.IsSuccess) floor = Math.Max(floor, max.Value + 1);

        return await projectStore.ConsumeNextRevisionAsync(projectId, floor, ct).ConfigureAwait(false);
    }
}
