using System.IO;
using Graft.Infra;

namespace Graft.Core;

/// <summary>
/// <see cref="RevisionStore"/> の分割ファイル（1ファイル400行上限のため）。
/// 確定済みリビジョンの状態（<see cref="RevisionStatus"/>）の書き換えを担う。
/// </summary>
public sealed partial class RevisionStore
{
    /// <summary>
    /// リビジョンの状態を書き換える。manifest.json（正本）と、history.jsonl の該当行の両方を更新する。
    ///
    /// 【なぜ history.jsonl も直すか】 履歴の一覧は実体フォルダの manifest を優先するが、フォルダが外部から
    /// 消えたときは history.jsonl の記録から一覧を作る（仕様書13.1）。こちらが success のまま残ると、
    /// フォルダを失ったあとに「取り消し済み」が「成功」へ戻って見える。history.jsonl に該当行が無い
    /// （確定されなかった in_progress の適用）場合は何も起きない。
    ///
    /// manifest.json の更新に失敗したら失敗を返す（状態が変わっていないので、呼び出し側が利用者へ伝える）。
    /// history.jsonl の更新に失敗しただけなら、正本は更新済みなので Warning を付けて成功にする。
    /// </summary>
    public async Task<GraftResult<bool>> SetStatusAsync(
        string projectId, int revision, string status, CancellationToken ct = default)
    {
        var projectDir = _paths.GetProjectBackupDirectory(projectId);
        var folder = FindRevisionFolder(projectDir, revision);
        if (folder is null)
        {
            return GraftResult<bool>.Fail(ErrorCode.E405, $"履歴 {revision} の実体が見つかりません", path: projectDir);
        }

        var manifestPath = Path.Combine(folder, "manifest.json");
        RevisionManifest Fallback() => CreateFallbackManifest(projectId, revision, DateTimeOffset.Now);
        var readResult = await _jsonStore
            .ReadWithRecoveryAsync<RevisionManifest>(manifestPath, Fallback, JsonFileStore.DefaultOptions, ct)
            .ConfigureAwait(false);

        var updated = readResult.Value with { Status = status };
        try
        {
            await _jsonStore.WriteAsync(manifestPath, updated, JsonFileStore.DefaultOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<bool>.Fail(ErrorCode.E401, $"manifest.json の更新に失敗しました: {ExceptionMessages.Describe(ex)}", path: manifestPath);
        }

        var issues = new List<GraftIssue>(readResult.Issues);
        try
        {
            await _revisionIndex
                .RewriteAsync(projectId, e => e.Revision == revision ? e with { Status = status } : e, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(GraftIssue.Of(
                ErrorCode.E404, $"history.jsonl の更新に失敗しました: {ExceptionMessages.Describe(ex)}",
                path: _revisionIndex.GetIndexPath(projectId), severity: Severity.Warning));
        }

        return GraftResult<bool>.Ok(true, issues);
    }
}
