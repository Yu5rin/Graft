using Graft.Core;
using Graft.Core.PromptFocus;
using Graft.Features;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// コンテキスト収集ViewModelのうち、依頼の観点（チップ）の選択を持つ部分
/// （仕様書_ブックの情報と依頼の観点 6.2）。本体が大きいため分割ファイルにしている。
///
/// 【ここが選択の持ち主である理由】依頼文コピー（<see cref="PromptCopyViewModel"/>）とコンテキスト収集の窓は、
/// すでに同じ <see cref="ContextCollectViewModel"/> を共有して「選んだファイル」を一致させている。
/// 観点の選択も同じ入れ物に置けば、2つの画面で選択が共有される（片方で選べばもう片方にも出る）ことが、
/// 同期の仕組みを足さずに自然に成り立つ。
/// </summary>
public sealed partial class ContextCollectViewModel
{
    /// <summary>
    /// 選択の記憶を保存するまでの間隔。仕様書6.2「選択が変わってから500ms後にまとめて1回」。
    /// チップを続けて何個か押す間は保存せず、止まってから1回だけ書く（projects.json の書き込みを減らす）。
    /// </summary>
    private const int FocusPersistDebounceMs = 500;

    private readonly PromptFocusStore _focusStore;
    private readonly IUiTimer _focusPersistTimer;
    private bool _focusPersistPending;
    private bool _focusRestored;

    /// <summary>依頼の観点のチップの選択状態。依頼文コピーとコンテキスト収集の窓が同じものを見る。</summary>
    public PromptFocusSelectionViewModel Focus { get; }

    /// <summary>
    /// 観点の一覧（自作の分）を <c>focus.json</c> から読み直し、保存済みの選択を復元する。
    /// プロジェクトを開いたとき・窓やドロップダウンを開くたびに呼ぶ（設定のタブで観点を足したり消したりした
    /// 結果を、再起動なしで反映するため）。ファイルが読めなくても組み込みの観点は必ず出る。
    ///
    /// 【記憶の読み直し】プロジェクト一覧が持つ <see cref="Project"/> は、別の操作で保存された最新の記憶を
    /// 反映していないことがある（プロジェクトを切り替えて戻ったとき、一覧側は古い値のまま）。そのため最初の1回は
    /// ストアから最新の <see cref="ProjectOverrides.PromptFocusIds"/> を読み直す。利用者がすでにチップを
    /// 触っているときは、その操作を上書きしない。
    /// </summary>
    public async Task ReloadFocusAsync(CancellationToken ct = default)
    {
        try
        {
            var loaded = await _focusStore.LoadAsync(ct).ConfigureAwait(true);

            if (!_focusRestored)
            {
                _focusRestored = true;
                if (!Focus.ChangedByUser)
                {
                    var projects = await _projectStore.LoadAsync(ct).ConfigureAwait(true);
                    var latest = projects.IsSuccess
                        ? projects.Value.FirstOrDefault(p => p.Id == _project.Id)?.Overrides.PromptFocusIds
                        : null;
                    if (latest is not null && !Focus.ChangedByUser) Focus.ReplaceSelection(latest);
                }
            }

            // 読み込みに失敗したときは組み込みだけで並べ、一覧に無い Id の掃除はしない
            // （失敗を理由に、自作の観点を選んでいた記憶まで消さないため）。
            //
            // 【「成功」でも、読めていないことがある】 JsonFileStore.ReadWithRecoveryAsync は、他のアプリが掴んでいて読めない
            // （E402）ときも、壊れていて退避した（E404）ときも、「警告つきの成功＋既定値（自作が 0 件）」を返す。
            // これを確定した一覧として扱うと、自作の観点を選んでいたプロジェクトで、選択（Id）が一覧に無いものとして
            // 捨てられ、projects.json に書かれてしまう（ウイルス対策ソフトが一瞬掴んだだけで、全プロジェクトの選択が外れる）。
            // 警告に E402・E404 があるときは、確定していない一覧として扱う。
            var readIncompletely = loaded.Issues.Any(i => i.Code is ErrorCode.E402 or ErrorCode.E404);
            var isFinal = loaded.IsSuccess && !readIncompletely;
            var catalog = loaded.IsSuccess ? loaded.Value : PromptFocusStore.BuiltIns;
            if (Focus.SetCatalog(catalog, isFinal)) QueueFocusPersist();
            UpdateApproxTokenEstimate();
        }
        catch (OperationCanceledException)
        {
            // 窓を閉じるなどで中断された。何もしない。
        }
        catch (Exception ex)
        {
            // 観点が読めないことで依頼文の作成まで止めない。想定外の例外は記録だけ残す。
            SafeHandler.OnUnexpected?.Invoke("依頼の観点の読み込み", ex);
        }
    }

    private void OnFocusChanged(object? sender, EventArgs e)
    {
        // チップの切り替えで、概算トークン数（窓の表示）も変わる。
        UpdateApproxTokenEstimate();
        QueueFocusPersist();
    }

    private void QueueFocusPersist()
    {
        _focusPersistPending = true;
        _focusPersistTimer.Restart();
    }

    private void OnFocusPersistTick()
    {
        _focusPersistTimer.Stop();
        if (!_focusPersistPending) return;
        _focusPersistPending = false;
        _ = PersistFocusAsync();
    }

    /// <summary>
    /// 選択を <see cref="ProjectOverrides.PromptFocusIds"/> へ保存する。<see cref="PersistFileStatesAsync"/> と同じ理由
    /// （古いスナップショットでの丸ごと上書きが履歴番号を巻き戻した実機不具合）で、変えるのはこの項目だけにし、
    /// <see cref="ProjectStore.UpdateAsync"/> で「読み込み→この項目だけ差し替え→保存」を不可分に行う。
    /// </summary>
    private async Task PersistFocusAsync()
    {
        try
        {
            var ids = Focus.IdsToPersist;
            var result = await _projectStore.UpdateAsync(
                    _project.Id,
                    p => p with { Overrides = p.Overrides with { PromptFocusIds = ids } })
                .ConfigureAwait(true);
            if (result.IsSuccess) _project = result.Value with { IsDisconnected = _project.IsDisconnected };
        }
        catch (Exception ex)
        {
            SafeHandler.OnUnexpected?.Invoke("依頼の観点の選択の保存", ex);
        }
    }

    /// <summary>
    /// 選択中の観点を依頼文用の文にする。何も選んでいなければ空（依頼文は従来と同じ）。
    /// 依頼文コピーとコンテキスト収集の出力が、この1か所を通る。
    /// </summary>
    /// <remarks>
    /// ブックの情報が<b>実際に</b>依頼文に入るとき（<see cref="BuildWorkbookInfoText"/> が空でないとき）だけ、
    /// VBA の前提の最終行を「ブックの情報に載っているものだけを使う」へ切り替える。入らないのにそう書くと、
    /// 存在しない「ブックの情報」を AI に指さしてしまう（Q1 の申し送り）。
    /// </remarks>
    public string BuildFocusText() => Focus.ComposeText(hasWorkbookInfo: BuildWorkbookInfoText().Length > 0);
}
