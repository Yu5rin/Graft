using System.IO;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;

namespace Graft.Features;

/// <summary>
/// 同期基準（<see cref="VbaSyncState"/>）の保存と読み込み（仕様書_VBA連携 4.3）。
/// 保存先は <c>&lt;データ保存先&gt;/vba-sync/&lt;プロジェクトId&gt;.json</c>
/// （<see cref="AppPaths.GetVbaSyncFilePath"/>）。
/// <para>
/// 【ミラーの中ではなくデータ保存先に置く理由】 ミラーのルートに管理ファイルを置くと、エクスプローラーや
/// コンテキスト収集に混ざる。履歴（<c>back/&lt;プロジェクトId&gt;/</c>）と同じく、Id をキーにした
/// アプリのデータとして持つ。
/// </para>
/// <para>
/// 書き込みは <see cref="JsonFileStore"/>（一時ファイル → 置き換え）を使うので、書きかけのファイルは残らない。
/// 壊れた JSON は <see cref="JsonFileStore"/> の共通復旧手順（<c>.corrupt.&lt;日時&gt;</c> へ退避して空の状態から
/// やり直す）に従う。その場合は次の同期の三方比較が「記録なし」から始まり、本文が同じなら基準を作り直し、
/// 違えば衝突として利用者に選んでもらう（本文を黙って上書きしない側へ倒れる）。
/// </para>
/// </summary>
public sealed class VbaSyncStateStore
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _store = new();

    public VbaSyncStateStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>
    /// 同期基準を読む。ファイルが無ければ空の状態（<paramref name="workbookFullName"/> を持つ）を返す。
    /// 壊れていれば退避して空の状態を返す（警告つき）。記録されているブックが <paramref name="workbookFullName"/> と
    /// 別のものなら、その記録は使わず空の状態を返す（警告つき）。
    /// </summary>
    public async Task<GraftResult<VbaSyncState>> LoadAsync(
        string projectId, string workbookFullName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var result = await _store
            .ReadWithRecoveryAsync(
                _paths.GetVbaSyncFilePath(projectId),
                () => new VbaSyncState { WorkbookFullName = workbookFullName },
                JsonFileStore.DefaultOptions,
                ct)
            .ConfigureAwait(false);

        // JSON 上で modules が null と書かれていても落ちないよう、読み込み直後に整える。
        // キーの大文字小文字は区別しない（ミラーのファイル名の比較に合わせる）。
        var state = result.Value;
        var normalized = new Dictionary<string, VbaModuleState>(StringComparer.OrdinalIgnoreCase);
        if (state.Modules is not null)
        {
            foreach (var (key, value) in state.Modules)
            {
                if (value is not null) normalized[key] = value;
            }
        }

        // 別のブックの同期状態は使わない。任意のフォルダを「既存のミラーを使う」で別のブックに結び付けると、前のブックの
        // 基準（ミラーと Excel のハッシュ）で三方比較してしまい、新しいブックの内容を「Excel 側だけが変わった」と
        // 判定して、確認なしにミラーへ取り込んだ（取り込みと反映の向きが基準と食い違う）。使わなければ「記録なし」の
        // 初回の突き合わせになり、本文が同じなら基準を作り直し、違えば衝突として利用者に選んでもらう
        // （黙って上書きしない側へ倒れる。壊れた JSON を退避したときと同じ扱い）。
        if (!string.IsNullOrEmpty(state.WorkbookFullName) && !string.IsNullOrEmpty(workbookFullName)
            && !VbaRotNames.SamePath(state.WorkbookFullName, workbookFullName))
        {
            var issues = new List<GraftIssue>(result.Issues)
            {
                GraftIssue.Of(
                    ErrorCode.E812,
                    $"このミラーの同期の記録は別のブック（{state.WorkbookFullName}）のものだったため使いません。" +
                    "ファイルと Excel の違いは、すべて衝突として確認します。別のブックへ結び付け直すときは、" +
                    "プロジェクトを削除して「Excel ブックから追加」で「作り直す」を選ぶと、迷わず揃います。",
                    severity: Severity.Warning),
            };
            return GraftResult<VbaSyncState>.Ok(new VbaSyncState { WorkbookFullName = workbookFullName }, issues);
        }

        return GraftResult<VbaSyncState>.Ok(state with { Modules = normalized }, result.Issues);
    }

    /// <summary>同期基準を保存する。</summary>
    public Task SaveAsync(string projectId, VbaSyncState state, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(state);
        return _store.WriteAsync(_paths.GetVbaSyncFilePath(projectId), state, JsonFileStore.DefaultOptions, ct);
    }

    /// <summary>
    /// 同期基準のファイルを削除する（無ければ何もしない）。プロジェクトの削除で「ミラーフォルダと同期状態も
    /// 削除する」を選んだときに使う。書き込み失敗時の控え（<c>.rescue/</c>）は、Excel から失われた本文を
    /// 含みうるので、ここでは消さない。
    /// </summary>
    public void Delete(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var path = _paths.GetVbaSyncFilePath(projectId);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// プロジェクトの Id が変わったとき（<c>ProjectStore.RelocateAsync</c> や保存先の移行。Id は Root から
    /// 導くので Root を変えると変わる）に、同期基準のファイルと控えのフォルダを新しい Id へ付け替える。
    /// 履歴（<c>back/&lt;旧Id&gt;/</c>）を新しい Id へ移すのと同じ理由で、移さないと「同期の記録が無い
    /// プロジェクト」に見えて、次の同期が全モジュールを初回の食い違い扱いにしてしまう。
    /// 旧 Id のファイルが無ければ何もしない。新 Id に既にあるファイルは、旧 Id の記録で置き換える。
    /// </summary>
    public void Move(string oldProjectId, string newProjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newProjectId);
        if (string.Equals(oldProjectId, newProjectId, StringComparison.Ordinal)) return;

        var oldFile = _paths.GetVbaSyncFilePath(oldProjectId);
        if (File.Exists(oldFile))
        {
            var newFile = _paths.GetVbaSyncFilePath(newProjectId);
            Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
            File.Move(oldFile, newFile, overwrite: true);
        }

        var oldRescue = _paths.GetVbaRescueDirectory(oldProjectId);
        var newRescue = _paths.GetVbaRescueDirectory(newProjectId);
        if (Directory.Exists(oldRescue) && !Directory.Exists(newRescue))
        {
            Directory.Move(oldRescue, newRescue);
        }
    }
}
