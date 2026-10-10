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

    /// <summary>
    /// 直前の確認が成功したかどうか（true=最新かどうかを判定できた、false=通信・解析に失敗した）。
    /// <para>
    /// 実機不具合対応: 以前は<see cref="LastCheckedAt"/>しか持たず、確認が3回連続で失敗しても
    /// 画面には「最終確認: 2026/09/06 06:26」とだけ出ていた（失敗はログのwarnにしか残らない）。
    /// これは「確認した＝最新だった」と読めてしまい、オフラインが続くと利用者は何日でも
    /// 更新が止まっていることに気づけない。成否を併記できるようにこの項目を足した。
    /// </para>
    /// <para>
    /// null は「不明」を表す。この項目が無かった頃のupdate-check.jsonを読んだ場合がこれにあたり、
    /// 表示側（<see cref="Graft.ViewModels.SettingsViewModel.UpdateLastCheckedText"/>）は
    /// 従来どおり日時だけを出す。憶測で「成功」と書いてしまわないため、既定値は true にしない。
    /// </para>
    /// </summary>
    public bool? LastCheckSucceeded { get; init; }

    /// <summary>
    /// 「確認なしで自動更新する」（<see cref="Infra.UpdateSettings.AutoInstall"/>）が失敗した版の
    /// タグ（例: "v1.0.25"）。失敗の通知を「同じ版では1回だけ」にするための記録で、無ければnull。
    ///
    /// 【なぜ持つか】 自動更新は利用者が見ていない裏で動くため、失敗しても次の起動でまた同じ版を
    /// 取りに行って同じ理由で失敗しうる（例: ウイルス対策ソフトが入れ替えを止めている）。そのたびに
    /// ステータスバーへ通知すると、直せない失敗を毎回見せることになる。ログ（update）には毎回
    /// 残し、利用者への通知だけをこの記録で1回に絞る。次の版が出れば別の値になるので、
    /// 新しい版の失敗は改めて通知される。
    /// 【なぜここ（update-check.json）か】 settings.jsonは利用者が編集する「設定」であり、
    /// 内部状態を混ぜない方針（<see cref="Infra.UpdateSettings"/>のクラスコメント参照）。
    /// </summary>
    public string? AutoInstallFailedTag { get; init; }
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

    /// <summary>
    /// 現在の内容を読み、<paramref name="change"/>で変更した値を書き戻す。
    ///
    /// 【なぜ追加したか】 以前の保存は「確認日時と成否だけを持つ新しい状態」で丸ごと上書きしていた。
    /// 項目を増やした（<see cref="UpdateCheckState.AutoInstallFailedTag"/>）あと、確認のたびに
    /// 他の項目が消えてしまうのを防ぐため、書き込みは読み→変更→書きの形にそろえる。
    /// 書き込み元は起動時の確認の直列な流れだけで、同時に2か所から書くことは無い。
    /// </summary>
    public async Task UpdateAsync(Func<UpdateCheckState, UpdateCheckState> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var current = await LoadAsync(ct).ConfigureAwait(false);
        await SaveAsync(change(current), ct).ConfigureAwait(false);
    }
}
