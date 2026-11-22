using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// ブックの情報を Excel から取る処理（仕様書_ブックの情報と依頼の観点 5.4）。<c>MainViewModel</c> が VBA プロジェクトの
/// ときだけ <see cref="ContextCollectViewModel"/> に渡す。
/// </summary>
/// <param name="sampleValues">値の見本を読むか（オプトイン。false なら見本を読む COM 呼び出しは 1 回も行われない）。</param>
/// <param name="includeFormulas">見本に数式も含めるか。</param>
/// <param name="ct">取り消し。</param>
/// <returns>
/// 取得結果。<b>null は「取得しない」</b>（設定 <c>Vba.CollectWorkbookInfo</c> が false で、Excel を呼んでいない）。
/// 失敗は <see cref="GraftResult{T}"/> の失敗で返し、例外にしない（収集を止めないため）。
/// </returns>
public delegate Task<GraftResult<VbaWorkbookInfoSnapshot>?> VbaWorkbookInfoFetchAsync(
    bool sampleValues, bool includeFormulas, CancellationToken ct);

/// <summary>
/// ブックの情報の窓口。取得そのもの（<see cref="FetchAsync"/>）に加えて、行を出すか・見本の行数といった
/// 「その時点の設定とプロジェクトの状態」を、呼び出しのたびに読める形で持つ
/// （設定画面での変更を、プロジェクトを選び直さなくても反映するため。<c>CreateVbaContextPull</c> と同じ作法）。
/// </summary>
public sealed class VbaWorkbookInfoSource
{
    /// <summary>VBA 連携が有効で、この環境で使え、VBA プロジェクトか。false なら行そのものを出さない。</summary>
    public required Func<bool> IsAvailable { get; init; }

    /// <summary>確認ダイアログと注意文に出す、値の見本の行数（<c>Vba.WorkbookInfoSampleRows</c>）。</summary>
    public required Func<int> SampleRows { get; init; }

    /// <summary>Excel から取得する。</summary>
    public required VbaWorkbookInfoFetchAsync FetchAsync { get; init; }
}

/// <summary>
/// コンテキスト収集ViewModelのうち、ブックの情報（機能 A）を持つ部分。本体が大きいため分割ファイルにしている。
/// <para>
/// 【取得結果はメモリにだけ持つ】 値の見本が社外秘を含みうるので、ディスクへは一切書かない
/// （仕様書付録A-6。<c>projects.json</c> に保存するのは「出し方」と「見本を出すか」の選択だけ）。
/// 破棄（<see cref="Dispose"/>）でも手放す。
/// </para>
/// <para>
/// 【Excel に繋がらなくても止めない】 取得の失敗は警告にして続ける。前回成功した分が同じセッションにあれば
/// 「取得日時つき・最新を読めていません」の注記つきで使い、無ければ何も出さない（見出しだけが残らない）。
/// </para>
/// </summary>
public sealed partial class ContextCollectViewModel
{
    private readonly VbaWorkbookInfoSource? _workbookInfoSource;
    private VbaWorkbookInfoSnapshot? _workbookSnapshot;
    private bool _workbookSnapshotIsStale;

    /// <summary>ブックの情報の行（コンテキスト収集の窓。VBA プロジェクトのときだけ表示される）。</summary>
    public VbaWorkbookInfoRowViewModel WorkbookInfo { get; private set; } = null!;

    /// <summary>
    /// ブックの情報の状態・見本の選択・取得結果が変わったときに発火する（トークン推定・依頼文の再計算の契機）。
    /// 依頼文コピーが購読する。
    /// </summary>
    public event EventHandler? WorkbookInfoChanged;

    /// <summary>直近のブックの情報の取得で出た警告（E815 や取得失敗）。無ければ null。</summary>
    public string? WorkbookInfoWarning { get; private set; }

    /// <summary>
    /// 窓を開かない経路（依頼文コピー）が自分の画面に出す、Excel 関連の警告（取り込みの警告とブックの情報の警告を
    /// 1 行に連結したもの。仕様書5.6）。無ければ null。
    /// </summary>
    public string? VbaWarning => CombineWarnings(VbaPullWarning, WorkbookInfoWarning);

    private void InitializeWorkbookInfo(Project project, VbaWorkbookInfoSource? source)
    {
        // 行の初期値は、前回までに保存した選択（VbaLink）。VBA プロジェクトでなければ既定のまま、行は出さない。
        var link = project.Vba;
        var cycle = new AsyncRelayCommand(CycleWorkbookInfoLevelAsync, context: "ブックの情報の出し方の切り替え");
        var toggle = new AsyncRelayCommand(ToggleWorkbookSampleValuesAsync, context: "値の見本の切り替え");
        WorkbookInfo = new VbaWorkbookInfoRowViewModel(cycle, toggle);
        WorkbookInfo.Level = VbaWorkbookInfoLevels.Parse(link?.WorkbookInfoState);
        WorkbookInfo.SampleValues = link?.WorkbookInfoSampleValues ?? false;
        WorkbookInfo.IncludeFormulas = link?.WorkbookInfoIncludeFormulas ?? false;
        WorkbookInfo.SampleRows = source?.SampleRows() ?? 5;
        WorkbookInfo.IsVisible = source is not null && source.IsAvailable();
    }

    /// <summary>
    /// ブックの情報を Excel から取る。取る契機は Pull（<see cref="PullFromExcelAsync"/>）の直後と同じ
    /// （コンテキスト収集を開いたとき・依頼文コピーのとき）。取れなくても例外にせず、警告にして続ける。
    /// <para>
    /// 取らない条件（いずれも Excel を呼ばない）: VBA プロジェクトでない・機能が無効・この環境で使えない、
    /// 設定 <c>Vba.CollectWorkbookInfo</c> が false、行の状態が「出さない」。
    /// </para>
    /// </summary>
    public async Task FetchWorkbookInfoAsync(CancellationToken ct = default)
    {
        WorkbookInfoWarning = null;
        var source = _workbookInfoSource;
        var available = source is not null && source.IsAvailable();
        WorkbookInfo.IsVisible = available;
        if (!available)
        {
            _workbookSnapshot = null;
            _workbookSnapshotIsStale = false;
            WorkbookInfo.StatusText = string.Empty;
            return;
        }

        WorkbookInfo.SampleRows = source!.SampleRows();
        if (WorkbookInfo.Level == VbaWorkbookInfoLevel.Hidden)
        {
            WorkbookInfo.StatusText = "出さない設定のため、Excel には問い合わせていません。";
            return;
        }

        // 値の見本を読むのは「内容も出す」で、かつ見本をオンにしているときだけ。それ以外は 0 行（読まない）で呼ぶ。
        var sampleValues = WorkbookInfo.SampleValues && WorkbookInfo.Level == VbaWorkbookInfoLevel.Full;
        GraftResult<VbaWorkbookInfoSnapshot>? result;
        try
        {
            result = await source.FetchAsync(sampleValues, WorkbookInfo.IncludeFormulas, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 取得の想定外の失敗でも、依頼文の作成は止めない。記録だけ残して、取得失敗として扱う。
            SafeHandler.OnUnexpected?.Invoke("ブックの情報の取得", ex);
            result = GraftResult<VbaWorkbookInfoSnapshot>.Fail(ErrorCode.E801, "ブックの情報の取得中に想定外の問題が起きました。");
        }

        ApplyWorkbookInfoResult(result);
        PublishVbaWarningToStatus();
        WorkbookInfoChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyWorkbookInfoResult(GraftResult<VbaWorkbookInfoSnapshot>? result)
    {
        if (result is null)
        {
            // 設定で取得をオフにしている。前回の結果も使わない（オフにしたのに古い内容が出続けるのは意図に反する）。
            _workbookSnapshot = null;
            _workbookSnapshotIsStale = false;
            WorkbookInfo.StatusText = "設定（VBA 連携）でブックの情報の取得をオフにしています。";
            return;
        }

        if (result.IsSuccess)
        {
            _workbookSnapshot = result.Value;
            _workbookSnapshotIsStale = false;
            var at = result.Value.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            if (result.Value.Omitted.Count > 0)
            {
                // E815（警告）: 読めた分を使い、省いたものを伝える。
                WorkbookInfoWarning = $"E815: {ErrorCatalog.SummaryOf(ErrorCode.E815)}（{string.Join(" ", result.Value.Omitted)}）";
                WorkbookInfo.StatusText = $"取得: {at}（一部のみ）";
            }
            else
            {
                WorkbookInfo.StatusText = $"取得: {at}";
            }

            return;
        }

        // 取得そのものの失敗（E804 開いていない・E805 応答なし・E801 通信失敗など）。元のコードを警告として添える。
        var first = result.Issues.FirstOrDefault(i => i.Severity == Severity.Error) ?? result.Issues.FirstOrDefault();
        var reason = first is null ? "原因不明" : $"{first.Code}: {ErrorCatalog.SummaryOf(first.Code)}";
        if (_workbookSnapshot is not null)
        {
            _workbookSnapshotIsStale = true;
            var at = _workbookSnapshot.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            WorkbookInfoWarning = $"ブックの情報を最新にできていません。{at} に取得した内容で続けます（{reason}）";
            WorkbookInfo.StatusText = $"最新を読めていません（取得日時: {at}）";
        }
        else
        {
            WorkbookInfoWarning = $"ブックの情報を取得できていません。ブックの情報なしで続けます（{reason}）";
            WorkbookInfo.StatusText = "取得できませんでした。";
        }
    }

    /// <summary>
    /// 取り込みの警告とブックの情報の警告を、ステータス欄の 1 行にまとめる。
    /// （<see cref="PullFromExcelAsync"/>・<see cref="FetchWorkbookInfoAsync"/> のあとで呼ぶ。）
    /// </summary>
    private void PublishVbaWarningToStatus()
    {
        var combined = VbaWarning;
        if (combined is not null) StatusMessage = combined;
    }

    private static string? CombineWarnings(string? first, string? second)
        => first is null ? second : second is null ? first : first + " " + second;

    /// <summary>
    /// 依頼文・収集の出力に載せる、ブックの情報の文。載せないときは空。
    /// 載せない条件: 行が出ていない（VBA でない・機能が無効・環境が非対応）、「出さない」、
    /// 取得に失敗して前回の成功分も無い。
    /// </summary>
    public string BuildWorkbookInfoText()
    {
        if (_workbookSnapshot is null || !WorkbookInfo.IsVisible || WorkbookInfo.Level == VbaWorkbookInfoLevel.Hidden)
        {
            return string.Empty;
        }

        return VbaWorkbookInfoFormatter.Format(
            _workbookSnapshot,
            WorkbookInfo.Level,
            includeSamples: WorkbookInfo.SampleValues && WorkbookInfo.Level == VbaWorkbookInfoLevel.Full,
            maxChars: _settings.Vba.WorkbookInfoMaxChars,
            isStale: _workbookSnapshotIsStale);
    }

    /// <summary>行のクリック: 内容も出す → 構成だけ → 出さない → 内容も出す、と巡る。選んだ状態は保存する。</summary>
    public async Task CycleWorkbookInfoLevelAsync()
    {
        var before = WorkbookInfo.Level;
        var next = VbaWorkbookInfoLevels.Next(before);
        WorkbookInfo.Level = next;
        await PersistWorkbookInfoAsync(link => link with { WorkbookInfoState = next.ToString() }).ConfigureAwait(true);

        // 「出さない」から戻したときは、取得結果が無い（問い合わせていなかった）ので取り直す。
        // 取り直しは次の収集の契機を待たずに、ここで済ませる（行の状態が変わったその場で出力に反映するため）。
        if (before == VbaWorkbookInfoLevel.Hidden && next != VbaWorkbookInfoLevel.Hidden)
        {
            await RefreshWorkbookInfoOnDemandAsync().ConfigureAwait(true);
        }
        else
        {
            AfterWorkbookInfoSelectionChanged();
        }
    }

    /// <summary>
    /// 値の見本のオン・オフ。<b>オンにするときは確認ダイアログを出す</b>（仕様書5.2。個人情報・社外秘が AI へ渡りうるため）。
    /// 断られたら何も変えない。オフにしたら、メモリ上の見本も直ちに捨てる。
    /// </summary>
    public async Task ToggleWorkbookSampleValuesAsync()
    {
        if (!WorkbookInfo.SampleValues)
        {
            var rows = WorkbookInfo.SampleRows;
            var confirmed = await _dialogs.ConfirmAsync(
                "値の見本を AI に渡しますか",
                $"各シートの先頭 {rows} 行の値が、依頼文に含まれ、AI に渡ります。" + Environment.NewLine
                + "個人情報や社外秘が含まれていないか確認してください。").ConfigureAwait(true);
            if (!confirmed) return;

            WorkbookInfo.SampleValues = true;
            await PersistWorkbookInfoAsync(link => link with { WorkbookInfoSampleValues = true }).ConfigureAwait(true);
            // 見本を読むため、取り直す（オフの間は見本を読んでいない）。
            await RefreshWorkbookInfoOnDemandAsync().ConfigureAwait(true);
            return;
        }

        WorkbookInfo.SampleValues = false;
        await PersistWorkbookInfoAsync(link => link with { WorkbookInfoSampleValues = false }).ConfigureAwait(true);
        DropSamplesFromMemory();
        AfterWorkbookInfoSelectionChanged();
    }

    /// <summary>値の見本に数式も含めるか（既定オフ）。切り替えたら保存して、見本を取り直す。</summary>
    public async Task SetWorkbookIncludeFormulasAsync(bool value)
    {
        if (WorkbookInfo.IncludeFormulas == value) return;
        WorkbookInfo.IncludeFormulas = value;
        await PersistWorkbookInfoAsync(link => link with { WorkbookInfoIncludeFormulas = value }).ConfigureAwait(true);
        if (WorkbookInfo.SampleValues) await RefreshWorkbookInfoOnDemandAsync().ConfigureAwait(true);
    }

    /// <summary>オフにした見本を、メモリ上の取得結果からも消す（ディスクには元々書いていない）。</summary>
    private void DropSamplesFromMemory()
    {
        if (_workbookSnapshot is null) return;
        _workbookSnapshot = _workbookSnapshot with
        {
            Sheets = _workbookSnapshot.Sheets.Select(s => s with { SampleRows = Array.Empty<IReadOnlyList<string>>() }).ToArray(),
        };
    }

    private async Task RefreshWorkbookInfoOnDemandAsync()
    {
        try
        {
            await FetchWorkbookInfoAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 窓を閉じるなどで中断された。
        }

        AfterWorkbookInfoSelectionChanged();
    }

    private void AfterWorkbookInfoSelectionChanged()
    {
        UpdateApproxTokenEstimate();
        WorkbookInfoChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// <see cref="VbaLink"/> の項目だけを <see cref="ProjectStore.UpdateAsync"/> で差し替えて保存する
    /// （<see cref="PersistFocusAsync"/> と同じ理由。古いスナップショットで丸ごと上書きして履歴番号を巻き戻さない）。
    /// VBA プロジェクトでなければ何もしない。
    /// </summary>
    private async Task PersistWorkbookInfoAsync(Func<VbaLink, VbaLink> change)
    {
        if (_project.Vba is null) return;
        try
        {
            var result = await _projectStore.UpdateAsync(
                    _project.Id,
                    p => p.Vba is null ? p : p with { Vba = change(p.Vba) })
                .ConfigureAwait(true);
            if (result.IsSuccess) _project = result.Value with { IsDisconnected = _project.IsDisconnected };
        }
        catch (Exception ex)
        {
            SafeHandler.OnUnexpected?.Invoke("ブックの情報の設定の保存", ex);
        }
    }
}
