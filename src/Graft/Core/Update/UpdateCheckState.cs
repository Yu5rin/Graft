using Graft.Infra;

namespace Graft.Core.Update;

/// <summary>
/// 更新確認の内部状態（<c>update-check.json</c>、<see cref="AppPaths.UpdateCheckStateFilePath"/>）。
/// settings.jsonとは別ファイル（<see cref="Infra.UpdateSettings"/>のクラスコメント参照）。
/// </summary>
public sealed record UpdateCheckState
{
    /// <summary>
    /// 前回、実際に通信して確認した日時（起動時チェック・手動確認いずれも含む）。未確認ならnull。
    /// 「バージョン情報」タブの「最終確認」表示にのみ使う（v1.0.12から、起動時チェックの
    /// 絞り込みには使わなくなった。<see cref="UpdateChecker.CheckOnStartupAsync"/>参照）。
    /// </summary>
    public DateTimeOffset? LastCheckedAt { get; init; }
}

/// <summary><see cref="UpdateCheckState"/>の読み書き。他の内部状態と同じ<see cref="JsonFileStore"/>を使う。</summary>
public sealed class UpdateCheckStateStore
{
    private readonly string _path;
    private readonly JsonFileStore _store = new();

    public UpdateCheckStateStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = paths.UpdateCheckStateFilePath;
    }

    /// <summary>読み込みに失敗（破損・未作成）した場合は既定値（未確認）を返す。</summary>
    public async Task<UpdateCheckState> LoadAsync(CancellationToken ct = default)
    {
        var result = await _store.ReadWithRecoveryAsync(_path, () => new UpdateCheckState(), ct: ct).ConfigureAwait(false);
        return result.Value;
    }

    public Task SaveAsync(UpdateCheckState state, CancellationToken ct = default)
        => _store.WriteAsync(_path, state, ct: ct);
}
