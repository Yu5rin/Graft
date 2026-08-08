using System.IO;
using Graft.Infra;

namespace Graft.Core;

/// <summary>
/// <see cref="RevisionStore"/> の分割ファイル（1ファイル400行上限のため）。
/// 仕様書6.5 適用後フックの実行結果をmanifest.jsonへ書き戻す処理を担う。
/// </summary>
public sealed partial class RevisionStore
{
    /// <summary>
    /// 適用後フック（仕様書6.5）の実行結果を、確定済みmanifest.jsonへ書き戻す。
    /// フックは適用（<see cref="BackupSession.CompleteAsync"/>）が完了した後に実行するため、
    /// 一度確定したmanifestを読み直して <see cref="RevisionManifest.Hooks"/> のみを更新する。
    /// </summary>
    public async Task<GraftResult<bool>> RecordHookResultsAsync(
        string projectId, int revision, IReadOnlyList<HookResult> hooks, CancellationToken ct = default)
    {
        var projectDir = _paths.GetProjectBackupDirectory(projectId);
        var folder = FindRevisionFolder(projectDir, revision);
        if (folder is null)
        {
            return GraftResult<bool>.Fail(ErrorCode.E405, $"リビジョン {revision} の実体が見つかりません", path: projectDir);
        }

        var manifestPath = Path.Combine(folder, "manifest.json");
        RevisionManifest Fallback() => CreateFallbackManifest(projectId, revision, DateTimeOffset.Now);
        var readResult = await _jsonStore
            .ReadWithRecoveryAsync<RevisionManifest>(manifestPath, Fallback, JsonFileStore.DefaultOptions, ct)
            .ConfigureAwait(false);

        var updated = readResult.Value with { Hooks = hooks };
        try
        {
            await _jsonStore.WriteAsync(manifestPath, updated, JsonFileStore.DefaultOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GraftResult<bool>.Fail(ErrorCode.E401, $"manifest.json の更新に失敗しました: {ExceptionMessages.Describe(ex)}", path: manifestPath);
        }

        return GraftResult<bool>.Ok(true, readResult.Issues);
    }
}
