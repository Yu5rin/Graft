using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;

namespace Graft.ViewModels;

/// <summary>
/// ブックの情報を Excel から取る処理（仕様書_ブックの情報と依頼の観点 5.4）。<c>MainViewModel</c> が VBA プロジェクトの
/// ときだけ <see cref="ContextCollectViewModel"/> に渡す。
/// </summary>
/// <param name="views">
/// シートごとの見せ方（全体の出し方を掛けた後のもの）。<b>セルの値を読むのは、値が渡る見せ方のシートだけ</b>
/// （それ以外のシートでは、セルを読む COM 呼び出しは 1 回も行われない）。
/// </param>
/// <param name="ct">取り消し。</param>
/// <returns>
/// 取得結果。<b>null は「取得しない」</b>（設定 <c>Vba.CollectWorkbookInfo</c> が false で、Excel を呼んでいない）。
/// 失敗は <see cref="GraftResult{T}"/> の失敗で返し、例外にしない（収集を止めないため）。
/// </returns>
public delegate Task<GraftResult<VbaWorkbookInfoSnapshot>?> VbaWorkbookInfoFetchAsync(
    VbaSheetViewSet views, CancellationToken ct);

/// <summary>
/// ブックの情報の窓口。取得そのもの（<see cref="FetchAsync"/>）に加えて、行を出すか・行数の既定といった
/// 「その時点の設定とプロジェクトの状態」を、呼び出しのたびに読める形で持つ
/// （設定画面での変更を、プロジェクトを選び直さなくても反映するため。<c>CreateVbaContextPull</c> と同じ作法）。
/// </summary>
public sealed class VbaWorkbookInfoSource
{
    /// <summary>VBA 連携が有効で、この環境で使え、VBA プロジェクトか。false なら行そのものを出さない。</summary>
    public required Func<bool> IsAvailable { get; init; }

    /// <summary>
    /// 「見出し＋N 行」の N の既定（<c>Vba.WorkbookInfoSampleRows</c>）。見せ方を選んだときの最初の行数と、
    /// v0.4 より前の「値の見本」がオンだったプロジェクトを「全シート 見出し＋N 行」へ移すときの N になる。
    /// </summary>
    public required Func<int> SampleRows { get; init; }

    /// <summary>Excel から取得する。</summary>
    public required VbaWorkbookInfoFetchAsync FetchAsync { get; init; }

    /// <summary>「全部」「範囲を指定」の最大行数（<c>Vba.WorkbookInfoAllMaxRows</c>）。確認ダイアログと案内文に出す。未配線なら既定。</summary>
    public Func<int>? AllMaxRows { get; init; }

    /// <summary>「全部」「範囲を指定」の最大列数（<c>Vba.WorkbookInfoAllMaxColumns</c>）。未配線なら既定。</summary>
    public Func<int>? AllMaxColumns { get; init; }

    /// <summary>
    /// 設定 <c>Vba.CollectWorkbookInfo</c> がいまオンか。<b>依頼文を作るたびに</b>読み直す。
    /// 取得（<see cref="FetchAsync"/>）の契機は収集の窓を開いたときなどに限られ、依頼文のクイックコピーは一覧が残っていれば
    /// 取り直さない。そのため設定でオフにした直後でも、直前に取った結果が依頼文へ出続けてしまった
    /// （オフにしたのに社外秘かもしれない内容が出るのは意図に反する）。出力の直前にここを見て、オフなら空にする。
    /// 未配線（null）のときは常にオンとして扱う（従来どおり）。
    /// </summary>
    public Func<bool>? IsCollecting { get; init; }

    /// <summary>
    /// 依頼文に載せるブックの情報の最大文字数（<c>Vba.WorkbookInfoMaxChars</c>）を、呼び出しのたびに返す。
    /// <c>ContextCollectViewModel</c> が作成時に持った設定のコピーは、設定画面で変えても古いままなので
    /// （行数は <see cref="SampleRows"/> で即時に効くのに、上限だけ選び直すまで効かなかった）、ここから読む。
    /// 未配線（null）のときは作成時の設定を使う。
    /// </summary>
    public Func<int>? MaxChars { get; init; }

    /// <summary>
    /// 選択肢つきの確認ダイアログ（<c>VbaChoiceWindow</c>）を出して、選ばれたボタンの識別子を返す
    /// （値が渡る見せ方を選ぶ確認に使う）。未配線の環境（テスト、画面なし）では null で、確認は「やめる」と同じ結果になる
    /// （社外秘が AI へ渡る操作を、確認なしでは通さない）。
    /// </summary>
    public Func<VbaChoiceRequest, Task<string>>? ChooseAsync { get; init; }

    /// <summary>
    /// <b>いま紐付いているブックのフルパス</b>（一覧の最新の <c>VbaLink.WorkbookFullName</c>）を、呼び出しのたびに返す。
    /// 収集のモデルは作成時の <c>VbaLink</c>（見せ方の記録を含む）を持ち続けるので、別のブックへ結び直されても、
    /// 作り直されない限り古い見せ方のまま新しいブックの値を読んでしまう。取得と出力の直前に、取得に使ったブックのパスと
    /// これを突き合わせ、違えば取得結果と見せ方を捨てる。未配線（null）のときは突き合わせない。
    /// </summary>
    public Func<string?>? CurrentWorkbookFullName { get; init; }
}

/// <summary>
/// コンテキスト収集ViewModelのうち、ブックの情報（機能 A）を持つ部分。本体が大きいため分割ファイルにしている。
/// <para>
/// 【取得結果はメモリにだけ持つ】 セルの値が社外秘を含みうるので、ディスクへは一切書かない
/// （仕様書付録A-6。<c>projects.json</c> に保存するのは「出し方」と「シートごとの見せ方」の選択だけ）。
/// 破棄（<see cref="Dispose"/>）でも手放す。
/// </para>
/// <para>
/// 【Excel に繋がらなくても止めない】 取得の失敗は警告にして続ける。前回成功した分が同じセッションにあれば
/// 「取得日時つき・最新を読めていません」の注記つきで使い、無ければ何も出さない（見出しだけが残らない）。
/// </para>
/// <para>
/// 【値を渡す選択は、確認して、変えたら直ちに反映する】 値が渡る見せ方（見出し＋N 行・全部・範囲を指定）を選ぶときは
/// 確認ダイアログを出す（既定は「やめる」）。値が渡らない見せ方へ戻したとき、全体を構成だけ・出さないへ戻したときは、
/// メモリ上の値を直ちに捨て、プレビュー欄も空にする（1.2.0 で直した不具合と同じ注意）。
/// </para>
/// </summary>
public sealed partial class ContextCollectViewModel
{
    private readonly VbaWorkbookInfoSource? _workbookInfoSource;
    private VbaWorkbookInfoSnapshot? _workbookSnapshot;
    private bool _workbookSnapshotIsStale;

    // 【取得の世代】 取得（Excel への問い合わせ）は時間がかかり、その間に利用者が見せ方を変えて次の取得が走りうる。
    // 古い取得の結果が、あとから新しい指示の結果の上に格納されると、オフにしたはずの数式つきの値などが戻ってくる。
    // 取得を始めるたびに番号を進め、戻ってきた時点で最新の世代でなければ結果を捨てる。
    private int _fetchGeneration;

    // 取得結果（_workbookSnapshot）を読んだブックのパス。いまの紐付けと違えば、別のブックの内容なので捨てる。
    private string? _snapshotWorkbookPath;

    /// <summary>ブックの情報の行（コンテキスト収集の窓。VBA プロジェクトのときだけ表示される）。</summary>
    public VbaWorkbookInfoRowViewModel WorkbookInfo { get; private set; } = null!;

    /// <summary>
    /// ブックの情報の状態・見せ方・取得結果が変わったときに発火する（トークン推定・依頼文の再計算の契機）。
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
        WorkbookInfo = new VbaWorkbookInfoRowViewModel(cycle);
        WorkbookInfo.Level = VbaWorkbookInfoLevels.Parse(link?.WorkbookInfoState);
        WorkbookInfo.DefaultHeaderRows = DefaultHeaderRowsFromSource(source);
        WorkbookInfo.AllMaxRows = source?.AllMaxRows?.Invoke() ?? VbaSheetViewLimits.DefaultAllMaxRows;
        WorkbookInfo.AllMaxColumns = source?.AllMaxColumns?.Invoke() ?? VbaSheetViewLimits.DefaultAllMaxColumns;
        WorkbookInfo.IsVisible = source is not null && source.IsAvailable();
    }

    private int DefaultHeaderRowsFromSource(VbaWorkbookInfoSource? source)
        => VbaSheetViewLimits.ClampHeaderRows(source?.SampleRows() ?? VbaSheetViewLimits.DefaultHeaderRows);

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
        var generation = ++_fetchGeneration;
        WorkbookInfoWarning = null;

        // 別のブックへ結び直されていたら、前のブックの取得結果と見せ方を先に捨てる（古い見せ方で新しいブックの値を読まない）。
        EnsureCurrentWorkbook();

        var source = _workbookInfoSource;
        var available = source is not null && source.IsAvailable();
        WorkbookInfo.IsVisible = available;
        if (!available)
        {
            _workbookSnapshot = null;
            _workbookSnapshotIsStale = false;
            _snapshotWorkbookPath = null;
            WorkbookInfo.StatusText = string.Empty;
            RebuildSheetRows();
            return;
        }

        WorkbookInfo.DefaultHeaderRows = DefaultHeaderRowsFromSource(source);
        WorkbookInfo.AllMaxRows = source!.AllMaxRows?.Invoke() ?? VbaSheetViewLimits.DefaultAllMaxRows;
        WorkbookInfo.AllMaxColumns = source.AllMaxColumns?.Invoke() ?? VbaSheetViewLimits.DefaultAllMaxColumns;
        if (WorkbookInfo.Level == VbaWorkbookInfoLevel.Hidden)
        {
            WorkbookInfo.StatusText = "出さない設定のため、Excel には問い合わせていません。";
            return;
        }

        // 値を読むのは、「内容も出す」で、かつ値が渡る見せ方のシートだけ（全体が構成だけなら、見せ方は構成だけに落ちる）。
        // 「何を指示して読んだか」（見せ方・ブックのパス）を覚えておき、戻ってきたときの現在と突き合わせる。
        var requestPath = source.CurrentWorkbookFullName?.Invoke();
        var requestViews = EffectiveSheetViews();
        GraftResult<VbaWorkbookInfoSnapshot>? result;
        try
        {
            result = await source.FetchAsync(requestViews, ct).ConfigureAwait(true);
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

        // 【古い世代の結果は捨てる】 待っている間に、より新しい取得が始まっていたら、その結果に任せる。
        if (generation != _fetchGeneration) return;

        // 待っている間に別のブックへ結び直されていたら、読んだ内容は前のブックのもの。格納せず、いまの紐付けへ合わせ直す。
        if (!IsSamePathOrUnknown(requestPath, source.CurrentWorkbookFullName?.Invoke()))
        {
            EnsureCurrentWorkbook();
            RebuildSheetRows();
            AfterWorkbookInfoSelectionChanged();
            return;
        }

        ApplyWorkbookInfoResult(result, requestPath, requestViews);
        var reconciled = ReconcileSheetViewsWithSnapshot();
        if (reconciled is not null)
        {
            // 記録の整理（表示名の食い違い・消えたシート）を保存へも反映する。メモリ上は済んでいる。保存に失敗しても、
            // 値が渡らない側へ倒した状態でそのまま続ける（利用者の操作の失敗ではないので、画面には知らせない）。
            var known = _workbookSnapshot!.KnownSheets;
            await PersistWorkbookInfoAsync(link => link.ReconcileSheetViews(known).Link, reportFailure: false).ConfigureAwait(true);
        }

        RebuildSheetRows();
        PublishVbaWarningToStatus();
        WorkbookInfoChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsSamePathOrUnknown(string? a, string? b)
        => string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || VbaRotNames.SamePath(a, b);

    /// <summary>
    /// <b>いま紐付いているブックと、保持している内容が合っているかを確かめ、違えば捨てる</b>。
    /// 結び直し（<c>ProjectPaneViewModel.AddFromWorkbookAsync</c>）では、同じプロジェクトを選んだまま別のブックへ結べる。
    /// このモデルは作成時の <c>VbaLink</c> と取得結果を持ち続けるので、何もしないと、前のブックの見せ方（例: Sheet1 は全部）の
    /// まま新しいブックの値を確認なしで読み、依頼文へ出し、次の操作で古い見せ方を保存へ書き戻してしまう。
    /// 取得・出力・操作の直前に呼ぶ。見せ方は、結び直しと同じ規則（<see cref="VbaLink.WithWorkbook"/>。全シート構成だけへ戻す）で捨てる。
    /// </summary>
    private void EnsureCurrentWorkbook()
    {
        var current = _workbookInfoSource?.CurrentWorkbookFullName?.Invoke();
        if (string.IsNullOrEmpty(current)) return;

        var changed = false;
        if (_project.Vba is { } vba && !VbaRotNames.SamePath(vba.WorkbookFullName, current))
        {
            _project = _project with { Vba = vba.WithWorkbook(current) };
            changed = true;
        }

        if (_workbookSnapshot is not null && _snapshotWorkbookPath is not null && !VbaRotNames.SamePath(_snapshotWorkbookPath, current))
        {
            changed = true;
        }

        if (!changed) return;
        _workbookSnapshot = null;
        _workbookSnapshotIsStale = false;
        _snapshotWorkbookPath = null;
        WorkbookInfo.StatusText = "ブックが結び直されたため、前のブックの情報を捨てました。";
        RebuildSheetRows();
    }

    private void ApplyWorkbookInfoResult(
        GraftResult<VbaWorkbookInfoSnapshot>? result, string? requestPath, VbaSheetViewSet requestViews)
    {
        if (result is null)
        {
            // 設定で取得をオフにしている。前回の結果も使わない（オフにしたのに古い内容が出続けるのは意図に反する）。
            _workbookSnapshot = null;
            _workbookSnapshotIsStale = false;
            _snapshotWorkbookPath = null;
            WorkbookInfo.StatusText = "設定（VBA 連携）でブックの情報の取得をオフにしています。";
            return;
        }

        if (result.IsSuccess)
        {
            // 【格納するときに、「取得に使った見せ方」と「いまの見せ方」を突き合わせる】 待っている間に、利用者が値を渡さない見せ方へ
            // 戻した・数式をオフにしたシートの値と数式は、格納しない（取得の指示は古いので、戻ってきた結果に値が入っている）。
            _workbookSnapshot = DropValuesNoLongerAllowed(result.Value, requestViews);
            _workbookSnapshotIsStale = false;
            _snapshotWorkbookPath = requestPath;
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
    /// 取得に使った見せ方（<paramref name="used"/>）では値が入っているが、<b>いまの見せ方では渡らない</b>シートの値と数式を落とす
    /// （値が渡らなくなった・数式をオフにした）。取得の待ち中の操作が、戻ってきた結果で打ち消されないようにする二重の鍵。
    /// </summary>
    private VbaWorkbookInfoSnapshot DropValuesNoLongerAllowed(VbaWorkbookInfoSnapshot snapshot, VbaSheetViewSet used)
    {
        var now = EffectiveSheetViews();
        return snapshot with
        {
            Sheets = snapshot.Sheets
                .Select(sheet =>
                {
                    var current = now.For(sheet.CodeName);
                    var formulasTurnedOff = used.For(sheet.CodeName).IncludeFormulas && !current.IncludeFormulas;
                    return !current.PassesValues || formulasTurnedOff ? WithoutValues(sheet) : sheet;
                })
                .ToArray(),
        };
    }

    private static VbaSheetInfo WithoutValues(VbaSheetInfo sheet) => sheet with
    {
        HeaderRow = Array.Empty<string>(),
        SampleRows = Array.Empty<IReadOnlyList<string>>(),
        OmittedColumns = 0,
        Block = null,
    };

    /// <summary>
    /// 取得結果のシートと、保存してある見せ方の記録を突き合わせる（<see cref="VbaLink.ReconcileSheetViews"/>）。
    /// コード名は同じでも表示名が食い違う（名前を変えた、または別のシートが同じコード名になった）シート、取得結果に無い
    /// （消えた）シートの、値が渡る見せ方の記録を落とす。メモリ上のこのモデルの記録と取得結果の値を直ちに合わせ、
    /// 保存への反映は呼び出し側が行う。変わらなければ null。
    /// </summary>
    private VbaSheetViewReconcileResult? ReconcileSheetViewsWithSnapshot()
    {
        if (_workbookSnapshot is null || _project.Vba is not { } vba) return null;
        var result = vba.ReconcileSheetViews(_workbookSnapshot.KnownSheets);
        if (!result.Changed) return null;

        _project = _project with { Vba = result.Link };
        foreach (var code in result.Renamed)
        {
            // 値は、落とした記録の見せ方（確認済みの）で読んでしまっている。メモリからも捨てる。
            DropValuesFromMemory(s => string.Equals(s.CodeName, code, StringComparison.OrdinalIgnoreCase));
        }

        if (result.Renamed.Count > 0)
        {
            var names = result.Renamed
                .Select(code => _workbookSnapshot.Sheets.FirstOrDefault(s => string.Equals(s.CodeName, code, StringComparison.OrdinalIgnoreCase))?.Name ?? code)
                .Select(name => $"「{name}」");
            var warning = $"シート{string.Join("", names)}は、記録していた表示名と違うため（名前を変えた、または別のシートになった）、"
                          + "値を渡す見せ方を「構成だけ」に戻しました。値を渡すには、見せ方を選び直してください（確認が出ます）。";
            WorkbookInfoWarning = CombineWarnings(WorkbookInfoWarning, warning);
        }

        return result;
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

    // ------------------------------------------------------------------
    // 出力
    // ------------------------------------------------------------------

    /// <summary>
    /// 依頼文・収集の出力に載せる、ブックの情報の文。載せないときは空。
    /// 載せない条件: 行が出ていない（VBA でない・機能が無効・環境が非対応）、「出さない」、
    /// 取得に失敗して前回の成功分も無い。
    /// </summary>
    public string BuildWorkbookInfoText()
    {
        // 別のブックへ結び直されていたら、前のブックの取得結果を出さない（出力の直前の突き合わせ）。
        EnsureCurrentWorkbook();
        if (_workbookSnapshot is null || !WorkbookInfo.IsVisible || WorkbookInfo.Level == VbaWorkbookInfoLevel.Hidden)
        {
            return string.Empty;
        }

        // 【出力の直前に、設定と環境の「いま」を引き直す】 結果を捨てるのは次の取得のときだけなので、ここで見ないと
        // 「設定で取得（または VBA 連携）をオフにしたのに、直前に取った内容が次のクイックコピーに出る」。
        // 行の表示（IsVisible）は取得のたびにしか更新されないため、窓口に直接聞く。
        var source = _workbookInfoSource;
        if (source is null || !source.IsAvailable() || source.IsCollecting?.Invoke() == false)
        {
            return string.Empty;
        }

        return VbaWorkbookInfoFormatter.Format(
            _workbookSnapshot,
            WorkbookInfo.Level,
            SavedSheetViews(),
            maxChars: source.MaxChars?.Invoke() ?? _settings.Vba.WorkbookInfoMaxChars,
            isStale: _workbookSnapshotIsStale);
    }

    /// <summary>保存されているシートごとの見せ方（移行つき。全体の出し方は掛けない）。</summary>
    private VbaSheetViewSet SavedSheetViews()
        => (_project.Vba ?? new VbaLink()).ResolveSheetViews(DefaultHeaderRowsFromSource(_workbookInfoSource));

    /// <summary>全体の出し方を掛けた、読み取りに使う見せ方。全体が構成だけなら、値は読まない。</summary>
    private VbaSheetViewSet EffectiveSheetViews() => SavedSheetViews().ForLevel(WorkbookInfo.Level);

    // ------------------------------------------------------------------
    // 全体の 3 状態
    // ------------------------------------------------------------------

    /// <summary>行のクリック: 内容も出す → 構成だけ → 出さない → 内容も出す、と巡る。選んだ状態は保存する。</summary>
    public async Task CycleWorkbookInfoLevelAsync()
    {
        var before = WorkbookInfo.Level;
        var next = VbaWorkbookInfoLevels.Next(before);
        WorkbookInfo.Level = next;
        if (!await PersistWorkbookInfoAsync(link => link with { WorkbookInfoState = next.ToString() }).ConfigureAwait(true))
        {
            // 保存できなかった。保存してある出し方（変わっていない）に、画面とメモリの値を合わせる。
            WorkbookInfo.Level = before;
            RebuildSheetRows();
            AfterWorkbookInfoSelectionChanged();
            return;
        }

        // 【構成だけ・出さないへ戻したら、メモリ上の値を直ちに捨てる】 全体が構成だけでも値は渡らないが、
        // 取得済みの値をメモリに持ち続けない（オフにしたら直ちに捨てる、という約束を、全体の切り替えにも守る）。
        if (next != VbaWorkbookInfoLevel.Full) DropValuesFromMemory();

        // 「出さない」から戻したときは、取得結果が無い（問い合わせていなかった）ので取り直す。
        // 取り直しは次の収集の契機を待たずに、ここで済ませる（行の状態が変わったその場で出力に反映するため）。
        if (before == VbaWorkbookInfoLevel.Hidden && next != VbaWorkbookInfoLevel.Hidden)
        {
            await RefreshWorkbookInfoOnDemandAsync().ConfigureAwait(true);
        }
        else
        {
            RebuildSheetRows();
            AfterWorkbookInfoSelectionChanged();
        }
    }

    // ------------------------------------------------------------------
    // シートごとの見せ方（仕様書 5.2）
    // ------------------------------------------------------------------

    /// <summary>
    /// シートの見せ方を変える。<b>値が渡る見せ方（見出し＋N 行・全部・範囲を指定）を新しく選ぶときは確認ダイアログを出す</b>
    /// （個人情報・社外秘が AI へ渡りうるため）。断られたら何も変えず false。
    /// 値が渡らない見せ方へ戻したら、そのシートの値をメモリから直ちに捨てる。
    /// </summary>
    /// <returns>変えた（または同じだった）なら true。確認を断られた・シートが無いなら false。</returns>
    public async Task<bool> SetSheetViewModeAsync(string codeName, VbaSheetViewMode mode)
    {
        var sheet = FindSheet(codeName);
        if (sheet is null) return false;

        var views = SavedSheetViews();
        var old = views.For(sheet.CodeName);
        if (old.Mode == mode) return true;

        // 前に選んだ行数・範囲の記録があれば引き継ぐ（見せ方を切り替えて戻しても入力が消えない）。
        // 値が渡る見せ方から来たのでなく、行数が既定の定数のままなら、設定の既定の N から始める。
        // 数式を含める選択は、値が渡る見せ方どうしの切り替えでだけ引き継ぐ（値が渡らない見せ方へ戻したら外す。
        // 次に値を渡す見せ方を選ぶときの確認で、利用者が知らないまま数式まで渡ることがないように）。
        var rows = old.HeaderRows == VbaSheetViewLimits.DefaultHeaderRows && !VbaSheetViewModes.IsValueMode(old.Mode)
            ? DefaultHeaderRowsFromSource(_workbookInfoSource)
            : old.HeaderRows;
        var next = old with
        {
            Mode = mode,
            HeaderRows = rows,
            IncludeFormulas = VbaSheetViewModes.IsValueMode(mode) && old.IncludeFormulas,
            // 値が渡らない見せ方へ戻すときは、範囲も消す。残すと、あとで「範囲を指定」を選び直した瞬間に、確認の文面（「このあと入力する
            // 範囲」）と食い違って、以前の範囲が入力なしで直ちに読まれた。
            Range = VbaSheetViewModes.IsValueMode(mode) ? old.Range : null,
        };

        if (VbaSheetViewModes.IsValueMode(mode) && !await ConfirmSheetValuesAsync(sheet, next).ConfigureAwait(true))
        {
            RebuildSheetRows();
            return false;
        }

        await ApplySheetViewAsync(sheet, old, next).ConfigureAwait(true);
        return true;
    }

    /// <summary>「見出し＋N 行」の N を変える（入力欄の文字）。誤りなら、その理由を返す（保存も取り直しもしない）。</summary>
    public async Task<string?> SetSheetHeaderRowsAsync(string codeName, string text)
    {
        // 全角の数字（５）も受け付ける（日本語入力のまま打つと全角になりやすい）。
        if (!VbaSheetViewLimits.TryParseHeaderRows(text, out var rows))
        {
            return $"0〜{VbaSheetViewLimits.MaxHeaderRows} の整数で入力してください（もっと多く見せたいときは「全部」か「範囲を指定」を使います）。";
        }

        await SetSheetHeaderRowsAsync(codeName, rows).ConfigureAwait(true);
        return null;
    }

    /// <summary>「見出し＋N 行」の N を変える。</summary>
    public async Task SetSheetHeaderRowsAsync(string codeName, int rows)
    {
        var sheet = FindSheet(codeName);
        if (sheet is null) return;

        var old = SavedSheetViews().For(sheet.CodeName);
        var next = old with { HeaderRows = VbaSheetViewLimits.ClampHeaderRows(rows) };
        if (next == old) return;
        await ApplySheetViewAsync(sheet, old, next).ConfigureAwait(true);
    }

    /// <summary>
    /// 「範囲を指定」の範囲を入れる。<b>検証に通らなければ、誤りの理由を返し、依頼文には入れない</b>
    /// （それまでの範囲も取り消し、そのシートの値は渡さない。画面に見えている入力と、実際に渡るものを食い違わせないため）。
    /// 通れば正規化して保存し、取り直す。
    /// </summary>
    /// <returns>誤りの理由。誤りが無ければ null。</returns>
    public async Task<string?> SetSheetRangeAsync(string codeName, string text)
    {
        var sheet = FindSheet(codeName);
        if (sheet is null) return null;

        var old = SavedSheetViews().For(sheet.CodeName);
        if (!VbaA1Range.TryParse(text, out var range, out var error))
        {
            if (old.Range is not null) await ApplySheetViewAsync(sheet, old, old with { Range = null }, keepDraft: true).ConfigureAwait(true);
            return error;
        }

        await ApplySheetViewAsync(sheet, old, old with { Range = range.ToA1() }, keepDraft: true).ConfigureAwait(true);
        return null;
    }

    /// <summary>数式も含めるか。オフにしたら、数式入りの値をメモリから直ちに捨てる。</summary>
    public async Task SetSheetIncludeFormulasAsync(string codeName, bool value)
    {
        var sheet = FindSheet(codeName);
        if (sheet is null) return;

        var old = SavedSheetViews().For(sheet.CodeName);
        var next = old with { IncludeFormulas = value };
        if (next == old) return;
        await ApplySheetViewAsync(sheet, old, next).ConfigureAwait(true);
    }

    /// <summary>
    /// 入力欄（範囲・見出しに続く行数）に打っている途中の文字を、すべて確定する。<b>コピー・ファイル保存・プレビュー・
    /// 依頼文のコピー・窓を閉じる直前</b>に呼ぶ。以前は、確定の契機がフォーカスを外したときと Enter だけ（LostFocus 頼み）で、
    /// 入力した直後にコピーやショートカットを押すと、確定と取り直しが間に合わず、画面の入力と違う範囲・行数で出力されうる。
    /// 返ったときは、それまでの確定（検証・保存・取り直し）がすべて済んでいる。
    /// </summary>
    public async Task CommitPendingInputsAsync()
    {
        foreach (var row in WorkbookInfo.Sheets.ToArray())
        {
            try
            {
                await row.CommitDraftsAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // 窓を閉じるなどで中断された。
            }
            catch (Exception ex)
            {
                // 入力の確定に失敗しても、コピー・保存を止めない（出る内容は、確定できた範囲のもの）。記録だけ残す。
                SafeHandler.OnUnexpected?.Invoke("ブックの情報の入力の確定", ex);
            }
        }
    }

    private VbaSheetInfo? FindSheet(string codeName)
    {
        // 操作の直前にも、紐付いているブックと合っているかを確かめる（結び直されたブックの古い見せ方を書き戻さない）。
        EnsureCurrentWorkbook();

        // コード名が空のシートは見せ方を記録できない（鍵が無い）ので、見せ方の操作の対象にしない。
        return string.IsNullOrEmpty(codeName)
            ? null
            : _workbookSnapshot?.Sheets.FirstOrDefault(s => string.Equals(s.CodeName, codeName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 見せ方を保存して反映する。値が渡らなくなったシートの値はメモリから捨て、読み直しが要るときだけ取り直す
    /// （値が渡る見せ方で、読む内容が変わったとき。「出さない」から戻したときは、テーブルを読んでいないので取り直す）。
    /// </summary>
    private async Task ApplySheetViewAsync(VbaSheetInfo sheet, VbaSheetView old, VbaSheetView next, bool keepDraft = false)
    {
        // 【保存側の最新を読み、対象のシートだけを差し替える】 このモデルが持つ見せ方の一覧（作成時のもの）で、保存側の全体を
        // 上書きしない。結び直し・他の経路での保存で保存側が変わっていても、巻き戻さない（変換は保存の臨界区間の中で、最新に対して行う）。
        var snapshot = _workbookSnapshot;
        var legacyRows = DefaultHeaderRowsFromSource(_workbookInfoSource);
        var saved = await PersistWorkbookInfoAsync(
            link => ReplaceSheetView(link, snapshot, sheet.CodeName, sheet.Name, next, legacyRows)).ConfigureAwait(true);
        if (!saved)
        {
            // 保存できなかった。保存してある見せ方（変わっていない）に、画面の表示とメモリの値を合わせる。
            RebuildSheetRows();
            AfterWorkbookInfoSelectionChanged();
            return;
        }

        // 値が渡らなくなった（構成だけ・出さない・範囲が空）なら、そのシートの値を直ちに捨てる。
        // 数式をオフにしたときも捨てる（値は「=式 → 値」の文字列に畳んで持っているので、あとから数式だけを取り除けない）。
        var formulasTurnedOff = old.IncludeFormulas && !next.IncludeFormulas;
        if (!next.PassesValues || formulasTurnedOff)
        {
            DropValuesFromMemory(s => string.Equals(s.CodeName, sheet.CodeName, StringComparison.OrdinalIgnoreCase));
        }

        var needsRead = (next.PassesValues && ReadsDifferently(old, next))
                        || (old.Mode == VbaSheetViewMode.None && next.Mode != VbaSheetViewMode.None);
        if (needsRead && WorkbookInfo.Level != VbaWorkbookInfoLevel.Hidden)
        {
            await RefreshWorkbookInfoOnDemandAsync(keepDraft).ConfigureAwait(true);
        }
        else
        {
            RebuildSheetRows(keepDraft);
            AfterWorkbookInfoSelectionChanged();
        }
    }

    /// <summary>
    /// 保存側の最新の <paramref name="link"/> に、1 枚のシートの見せ方を書き込んだ <see cref="VbaLink"/> を返す。
    /// 先に取得結果と記録を突き合わせ（<see cref="VbaLink.ReconcileSheetViews"/>。消えたシート・表示名が食い違うシートの
    /// 値が渡る記録を落とす）、そのうえで、このシートの記録を差し替える。このシートの記録には、いまの表示名を添える。
    /// </summary>
    private static VbaLink ReplaceSheetView(
        VbaLink link, VbaWorkbookInfoSnapshot? snapshot, string codeName, string sheetName, VbaSheetView next, int legacyRows)
    {
        if (snapshot is not null) link = link.ReconcileSheetViews(snapshot.KnownSheets).Link;
        var saved = MaterializeSheetViews(link, snapshot, link.ResolveSheetViews(legacyRows), out var names);
        names[codeName] = sheetName;
        return link.WithSheetViews(saved.With(codeName, next), names);
    }

    /// <summary>読む内容が変わるか（同じなら取り直さない）。</summary>
    private static bool ReadsDifferently(VbaSheetView old, VbaSheetView next)
    {
        if (!old.PassesValues || old.Mode != next.Mode) return true;
        if (old.IncludeFormulas != next.IncludeFormulas) return true;
        return next.Mode switch
        {
            VbaSheetViewMode.HeaderRows => old.HeaderRows != next.HeaderRows,
            VbaSheetViewMode.Range => !string.Equals(old.Range, next.Range, StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// 見せ方の記録を、取得済みの全シート分の明示的な記録にする。<b>v0.4 より前の「値の見本」からの移行</b>の要:
    /// 記録がまだ無い（<see cref="VbaLink.WorkbookInfoSheetViews"/> が null）プロジェクトで、全シートが
    /// 「見出し＋N 行（既存の N）」として読まれている間に、利用者が 1 枚だけ変えたとき、残りのシートの見せ方を
    /// ここで確定して保存する（確定しないと、保存と同時に旧項目をオフにするため、残りが構成だけに変わってしまう）。
    /// 確定した記録には、いまのシートの表示名を添える（<paramref name="names"/> に返す）。
    /// </summary>
    private static VbaSheetViewSet MaterializeSheetViews(
        VbaLink link, VbaWorkbookInfoSnapshot? snapshot, VbaSheetViewSet saved, out Dictionary<string, string> names)
    {
        names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (link.WorkbookInfoSheetViews is not null || snapshot is null) return saved;

        var records = saved.Overrides.ToList();
        foreach (var sheet in snapshot.KnownSheets)
        {
            if (string.IsNullOrEmpty(sheet.CodeName)) continue;
            names[sheet.CodeName] = sheet.Name;
            if (!saved.CodeNames.Contains(sheet.CodeName, StringComparer.OrdinalIgnoreCase))
            {
                records.Add(new KeyValuePair<string, VbaSheetView>(sheet.CodeName, saved.For(sheet.CodeName)));
            }
        }

        // 記録の無いシートの既定は、確定後は構成だけ（旧項目をオフにするので、既定が見出し＋N 行のままではなくなる）。
        return new VbaSheetViewSet(VbaSheetView.StructureOnly, records);
    }

    /// <summary>
    /// 値が渡る見せ方を選ぶときの確認。<b>ボタンを動詞にし、既定（Enter）を「やめる」にする</b>: OK／キャンセルで既定が OK だと、
    /// 文面を読まずに Enter を押しただけで、社外秘かもしれない値が AI に渡る設定になってしまう。何が起きるかが分かる
    /// ボタンの文言にし、何もせず閉じる側（Esc・×も同じ）を既定にする。
    /// </summary>
    private async Task<bool> ConfirmSheetValuesAsync(VbaSheetInfo sheet, VbaSheetView next)
    {
        var source = _workbookInfoSource;
        var maxRows = source?.AllMaxRows?.Invoke() ?? VbaSheetViewLimits.DefaultAllMaxRows;
        var maxColumns = source?.AllMaxColumns?.Invoke() ?? VbaSheetViewLimits.DefaultAllMaxColumns;
        var what = next.Mode switch
        {
            VbaSheetViewMode.HeaderRows => $"使用範囲の見出し行（1 行目）と、続く {next.HeaderRows} 行の値が",
            VbaSheetViewMode.All => $"使用範囲の値すべて（最大 {maxRows} 行・{maxColumns} 列）が",
            _ => next.Range is { } range
                ? $"指定済みの範囲 {range} の値が"
                : "このあと入力する範囲の値が",
        };
        var request = new VbaChoiceRequest(
            $"シート「{sheet.Name}」の値を AI に渡しますか",
            $"シート「{sheet.Name}」: {what}、依頼文に含まれ、AI に渡ります。" + Environment.NewLine
            + "個人情報や社外秘が含まれていないか確認してください。" + Environment.NewLine
            + "選んだ見せ方はプロジェクトごとに記憶されます（値そのものは保存しません）。",
            new[]
            {
                new VbaChoiceOption("give", "値を渡す"),
                new VbaChoiceOption("cancel", "やめる", IsDefault: true),
            });
        var choice = source?.ChooseAsync is { } choose
            ? await choose(request).ConfigureAwait(true)
            : request.Default.Id;
        return choice == "give";
    }

    // ------------------------------------------------------------------
    // メモリ上の値・画面の一覧
    // ------------------------------------------------------------------

    /// <summary>
    /// 値をメモリ上の取得結果から捨てる（ディスクには元々書いていない）。<paramref name="only"/> が null なら全シート。
    /// 構成（シート・テーブル・名前）は残す。
    /// </summary>
    private void DropValuesFromMemory(Func<VbaSheetInfo, bool>? only = null)
    {
        if (_workbookSnapshot is null) return;
        _workbookSnapshot = _workbookSnapshot with
        {
            Sheets = _workbookSnapshot.Sheets
                .Select(s => only is null || only(s) ? WithoutValues(s) : s)
                .ToArray(),
        };
    }

    /// <summary>
    /// 取得したシートの一覧を、画面の行（<see cref="VbaWorkbookInfoRowViewModel.Sheets"/>）へ映す。
    /// シートが消えた記録は保存には残るが、ここには出さない。同じコード名の行は使い回す（入力途中の欄を壊さない）。
    /// </summary>
    private void RebuildSheetRows(bool keepDraft = false)
    {
        var sheets = _workbookSnapshot?.Sheets ?? Array.Empty<VbaSheetInfo>();
        var views = SavedSheetViews();
        // コード名が重なる・空の行があっても例外にしない（ToDictionary は重複で例外になる）。先の行を使う。
        var existing = new Dictionary<string, VbaSheetViewRowViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var current in WorkbookInfo.Sheets)
        {
            if (current.CodeName.Length > 0) existing.TryAdd(current.CodeName, current);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<VbaSheetViewRowViewModel>();
        foreach (var sheet in sheets)
        {
            // 【コード名が空のシートは、見せ方を選べない行にする】 見せ方はコード名で記録するので、鍵が無い。常に構成だけで扱い、
            // 画面でも選べないと伝える（空の鍵で記録を引くと、先頭の 1 枚に当たってしまう）。同じコード名が重なる行も同様に、
            // 先の 1 枚だけを操作できる行にする。
            var selectable = sheet.CodeName.Length > 0 && seen.Add(sheet.CodeName);
            VbaSheetViewRowViewModel? row = null;
            if (selectable) existing.TryGetValue(sheet.CodeName, out row);
            row ??= new VbaSheetViewRowViewModel(this, sheet.Name, sheet.CodeName);
            row.Name = sheet.Name;
            row.IsSelectable = selectable;
            row.UsedRangeText = sheet.UsedRange ?? "（空）";
            var view = selectable ? views.For(sheet.CodeName) : VbaSheetView.StructureOnly;
            row.Apply(view, keepDraft);
            if (selectable && view.Mode == VbaSheetViewMode.Range && view.Range is null) row.ShowRangeRequired();
            row.NoteText = selectable
                ? DescribeSheetNote(sheet, view)
                : "コード名を読めない（または重なっている）シートのため、見せ方は選べません。構成だけで扱います。";
            rows.Add(row);
        }

        // 行の並びと中身が同じなら、コレクションを触らない（選択中の ComboBox などを再構築しない）。
        if (!rows.SequenceEqual(WorkbookInfo.Sheets))
        {
            WorkbookInfo.Sheets.Clear();
            foreach (var row in rows) WorkbookInfo.Sheets.Add(row);
        }

        WorkbookInfo.RaiseSheetStateChanged();
    }

    /// <summary>取得結果についての、シートごとの注記（切った行・列、範囲の外）。依頼文の注記と同じ事実を、画面にも出す。</summary>
    private static string DescribeSheetNote(VbaSheetInfo sheet, VbaSheetView view)
    {
        if (!view.PassesValues) return string.Empty;
        if (view.Mode == VbaSheetViewMode.HeaderRows)
        {
            return sheet.OmittedColumns > 0 ? $"右の {sheet.OmittedColumns} 列は、最大列数の設定で省いています。" : string.Empty;
        }

        if (sheet.Block is not { } block || block.Mode != view.Mode) return string.Empty;
        if (block.OutsideUsedRange) return $"指定の範囲は、このシートの使用範囲（{sheet.UsedRange ?? "空"}）の外です。値はありません。";
        if (block.RowsCut || block.ColumnsCut)
        {
            return $"{block.TotalRows} 行・{block.TotalColumns} 列のうち、{block.Rows.Count} 行・{block.ColumnCount} 列だけを渡しています。続きは「範囲を指定」で選べます。";
        }

        return string.Empty;
    }

    private async Task RefreshWorkbookInfoOnDemandAsync(bool keepDraft = false)
    {
        try
        {
            await FetchWorkbookInfoAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 窓を閉じるなどで中断された。
        }

        RebuildSheetRows(keepDraft);
        AfterWorkbookInfoSelectionChanged();
    }

    private void AfterWorkbookInfoSelectionChanged()
    {
        // 【プレビュー欄の古い出力を捨てる】 プレビュー（PreviewLines）は「プレビュー」を押したときの出力を持ち続ける。
        // 見せ方・数式・出し方を切り替えたあとも残ると、「オフにしたら直ちに捨てる」と案内した値が画面に出たままになる
        // （コピーや保存の出力には入らないが、画面を共有する・撮るときに見えてしまう）。切り替えたら空にし、
        // 見たければ押し直してもらう。
        PreviewLines = Array.Empty<PreviewLine>();
        UpdateApproxTokenEstimate();
        WorkbookInfoChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// <see cref="VbaLink"/> の項目だけを <see cref="ProjectStore.UpdateAsync"/> で差し替えて保存する
    /// （<see cref="PersistFocusAsync"/> と同じ理由。古いスナップショットで丸ごと上書きして履歴番号を巻き戻さない）。
    /// <paramref name="change"/> は保存側の<b>最新</b>の <see cref="VbaLink"/> に対して呼ばれる。
    /// VBA プロジェクトでなければ何もしない（成功扱い）。
    /// <para>
    /// 【失敗は画面に知らせる】 保存できなかったとき、以前は何も知らせなかったので、メモリの値は消えたのに見せ方の表示は
    /// 元に戻って見える、という食い違いが残った。<paramref name="reportFailure"/> が true なら、警告に出す。
    /// 呼び出し側は、false が返ったら、保存側（変わっていない）に画面とメモリを合わせる。
    /// </para>
    /// </summary>
    /// <returns>保存できた（または保存するものが無かった）なら true。</returns>
    private async Task<bool> PersistWorkbookInfoAsync(Func<VbaLink, VbaLink> change, bool reportFailure = true)
    {
        if (_project.Vba is null) return true;
        try
        {
            var result = await _projectStore.UpdateAsync(
                    _project.Id,
                    p => p.Vba is null ? p : p with { Vba = change(p.Vba) })
                .ConfigureAwait(true);
            if (result.IsSuccess)
            {
                _project = result.Value with { IsDisconnected = _project.IsDisconnected };
                return true;
            }
        }
        catch (Exception ex)
        {
            SafeHandler.OnUnexpected?.Invoke("ブックの情報の設定の保存", ex);
        }

        if (reportFailure)
        {
            WorkbookInfoWarning = CombineWarnings(
                WorkbookInfoWarning,
                "ブックの情報の設定を保存できませんでした。変更は反映せず、保存してある設定のままにしています。");
            PublishVbaWarningToStatus();
        }

        return false;
    }
}
