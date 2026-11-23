using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Graft.Core;
using Graft.Core.Vba;

namespace Graft.Platform.Windows;

// リボンの書き換えのための閉じ開き（仕様書_ブックの情報と依頼の観点 16.6。段階 F4）。
// 規約は WindowsVbaBridge.Read.cs と同じ: dynamic の呼び出しはプロパティを連鎖させず、受けた COM オブジェクトは必ず session.Own に渡す。
//
// 【実機で要確認のまま実装している前提】（付録B。tools/Test-ExcelReopen.ps1 の結果で直す箇所）
//  - Workbook.Save / Close(SaveChanges:=False) / Workbooks.Open を COM から呼んだとき、Workbook_BeforeSave・BeforeClose・Workbook_Open が
//    動き、Auto_Open は動かない（16.12）。動く・動かないはこのコードの分岐を変えないが、確認ダイアログの文面（VbaRibbonRewriteText.ExcelNotes）が変わる。
//  - 【実機で確認済み（2026-11-24）】 Workbook.Close のあと、ブックは ROT に 10 秒以上残る（5 回とも。6-34 の 5-b）。Application.Workbooks からは
//    Close が戻った時点で消える。そこで「開いているか」は ROT だけで決めず、必ず Workbooks に実在するかで決める
//    （FindWorkbook・ResolveLiveWorkbook。閉じたことの確認 IsStillOpen も同じ）。待ちは足さない。
//  - 開き直しで保護ビューや「マクロを有効にする」のバーが出るか（16.12。出るなら Workbooks.Open が戻らない・マクロが動かない、のどちらかになる）。
//  - 【実機で確認済み（2026-11-24）】 アクティブなシート・通常ウィンドウの位置と大きさ・最大化は、Excel が自分で戻す（3-a〜3-c）。
//    戻らないのは前面のブックだけ（3-d）で、ここだけを Graft が戻す（VbaWorkbookViewRestore）。
public sealed partial class WindowsVbaBridge
{
    /// <summary>
    /// 開き直しの券で持っている Excel の参照（券の Id → GIT のクッキー）。
    /// <para>
    /// 【RCW ではなく GIT（Global Interface Table）に持つ理由】 (1) 同じ COM オブジェクトの RCW は 1 つを共有するので、ほかの呼び出し
    /// （<c>IsInProtectedView</c> などが取る <c>wb.Application</c>）が <see cref="ComScope"/> で <c>FinalReleaseComObject</c> すると、
    /// 持ち越した参照まで無効になる。(2) RCW は作ったスレッド（アパートメント）に結び付くが、COM の専用スレッドは時間切れで作り直され
    /// うる（<see cref="StaWorker"/>）。閉じてから開き直すまでに作り直されても、GIT からなら新しいスレッドで正しく取り出せる。
    /// GIT に登録した参照は Excel のプロセスを生かし続けるので、成功・失敗・時間切れ・取り消し・Graft の終了のすべてで必ず外す
    /// （<see cref="ReleaseHeld"/>）。
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, HeldTicket> _held = new(StringComparer.Ordinal);

    private sealed record HeldTicket(uint Cookie, string WorkbookFullName);

    private static readonly Guid ClsidStdGlobalInterfaceTable = new("00000323-0000-0000-C000-000000000046");
    private static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");

    // ------------------------------------------------------------------
    // IVbaBridge（16.6 の 3 つと、券を捨てる 1 つ）
    // ------------------------------------------------------------------

    public Task<GraftResult<VbaWorkbookRewriteState>> InspectWorkbookForRewriteAsync(string workbookFullName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookFullName);
        return InvokeAsync(token => InspectForRewriteCore(workbookFullName, token), ct);
    }

    public async Task<GraftResult<VbaWorkbookReopenTicket>> SaveAndCloseWorkbookAsync(string workbookFullName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookFullName);

        // 券の Id を先に決めて仕事へ渡す。時間切れで結果を受け取れなかったときに、遅れて作られた券（GIT の参照）を Id で外すため。
        var id = Guid.NewGuid().ToString("N");
        var timeoutMs = Volatile.Read(ref _saveOpenTimeoutMs);
        try
        {
            return await InvokeAsync(
                token => SaveAndCloseCore(workbookFullName, id, token),
                ct,
                onTimeout: () =>
                {
                    // 結果を待つのをやめた。仕事が遅れて完了して券を作っていれば（作るのがこのあとでも、仕事の側が見捨てられたことを見て）外す。
                    ReleaseHeld(id);
                    return GraftResult<VbaWorkbookReopenTicket>.Fail(GraftIssue.Of(ErrorCode.E805,
                        $"ブックの保存と閉じる処理が {timeoutMs / 1000d:0.#} 秒以内に終わりませんでした。Excel にダイアログ（保存の確認・互換性の警告・ブックのマクロのフォームなど）が出ていないか確かめてください。",
                        path: workbookFullName));
                },
                timeoutMs: timeoutMs).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ReleaseHeld(id);
            throw;
        }
    }

    public async Task<GraftResult<VbaWorkbookReopenOutcome>> ReopenWorkbookAsync(VbaWorkbookReopenTicket ticket, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var timeoutMs = Volatile.Read(ref _saveOpenTimeoutMs);
        try
        {
            return await InvokeAsync(
                token => ReopenCore(ticket, token),
                ct,
                onTimeout: () => GraftResult<VbaWorkbookReopenOutcome>.Fail(GraftIssue.Of(ErrorCode.E822,
                    $"ブックを開き直す処理が {timeoutMs / 1000d:0.#} 秒以内に終わりませんでした。Excel にダイアログ（ブックのマクロのフォーム、リンクの更新、修復の確認など）が出ていないか確かめてください。",
                    path: ticket.WorkbookFullName)),
                timeoutMs: timeoutMs).ConfigureAwait(false);
        }
        finally
        {
            // 券は成功・失敗・時間切れのどれでも捨てる（16.6）。仕事が始まっていれば仕事の側で外し済みで、ここは何もしない。
            // 始まる前に取り消された・待ち行列で見捨てられたときに、ここで外す。
            ReleaseHeld(ticket.Id);
        }
    }

    public Task DiscardReopenTicketAsync(VbaWorkbookReopenTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ReleaseHeld(ticket.Id);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // 状態を読む
    // ------------------------------------------------------------------

    private GraftResult<VbaWorkbookRewriteState> InspectForRewriteCore(string fullName, CancellationToken abandoned)
    {
        using var session = NewSession(abandoned);
        var workbook = FindWorkbook(session, fullName);
        return GraftResult<VbaWorkbookRewriteState>.Ok(workbook is null
            ? VbaWorkbookRewriteState.NotOpen
            : ReadRewriteState(session, workbook, fullName));
    }

    /// <summary>
    /// 止めるブックの判定の材料を読む。版によって無いプロパティ（<c>AutoSaveOn</c> は Office 2016 の途中から）は false として読む
    /// （付録B「late binding で取れる。無い版では例外」）。拒否され続けた（<see cref="ComBusyException"/>）ときは握りつぶさず、
    /// 呼び出し全体を E805 にする（セル編集中に閉じない）。
    /// </summary>
    private static VbaWorkbookRewriteState ReadRewriteState(Session s, object workbook, string fullName)
    {
        dynamic wb = workbook;
        var actualName = s.Call(() => (string)wb.FullName);
        var saved = s.Call(() => (bool)wb.Saved);
        var readOnly = s.Call(() => (bool)wb.ReadOnly);
        var isAddin = TryReadBool(s, () => (bool)wb.IsAddin);
        var shared = TryReadBool(s, () => (bool)wb.MultiUserEditing);
        var autoSave = TryReadBool(s, () => (bool)wb.AutoSaveOn);
        var protectedView = IsInProtectedView(s, workbook, fullName);
        return new VbaWorkbookRewriteState(
            IsOpen: true, Saved: saved, ReadOnly: readOnly, IsAddin: isAddin, MultiUserEditing: shared, AutoSaveOn: autoSave,
            IsProtectedView: protectedView, IsUrlLocation: VbaRotNames.IsUrlLocation(actualName), ProjectMode: TryReadProjectMode(s, workbook));
    }

    private static bool TryReadBool(Session s, Func<bool> read)
    {
        try
        {
            return s.Call(read);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>VBProject.Mode</c>。VBA プロジェクトに触れない（トラストセンター・ロック）ときは null（E813 の検査を省いて進む。16.6 の 1）。
    /// ロック中でも <c>Mode</c> を読めるかは実機で要確認（付録B）。読めなければ null に倒す。
    /// </summary>
    private static int? TryReadProjectMode(Session s, object workbook)
    {
        try
        {
            dynamic wb = workbook;
            var vbp = s.Own(s.Call(() => (object)wb.VBProject));
            if (vbp is null) return null;
            dynamic project = vbp;
            return s.Call(() => (int)project.Mode);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // 保存して閉じる（16.6 の 1〜5）
    // ------------------------------------------------------------------

    private GraftResult<VbaWorkbookReopenTicket> SaveAndCloseCore(string fullName, string ticketId, CancellationToken abandoned)
    {
        var name = VbaRibbonRewriteText.FileNameOf(fullName);
        using var session = NewSession(abandoned);
        var workbook = FindWorkbook(session, fullName);
        if (workbook is null)
        {
            return GraftResult<VbaWorkbookReopenTicket>.Fail(ErrorCode.E820,
                $"「{name}」は、閉じる前に Excel で開いていませんでした（すでに閉じられた見込み）。ブックのファイルは変えていません。", path: fullName);
        }

        // 1. 閉じる直前にもう一度、止めるブックとマクロの実行中を見る（確認のあいだに読み取り専用で開き直された、などを最後に止める）。
        var state = ReadRewriteState(session, workbook, fullName);
        if (VbaWorkbookRewriteGuard.Check(state, fullName) is { } stop) return GraftResult<VbaWorkbookReopenTicket>.Fail(stop);

        // 2. 見た目を控える（どれも「できれば」。取れなければ戻さないだけ）。
        var view = CaptureView(session, workbook, fullName);

        dynamic wb = workbook;
        var appObj = session.Own(session.Call(() => (object)wb.Application));
        if (appObj is null)
        {
            return GraftResult<VbaWorkbookReopenTicket>.Fail(ErrorCode.E820,
                $"「{name}」を開いている Excel を取得できませんでした。ブックは Excel で開いたままです。ブックのファイルは変えていません。", path: fullName);
        }

        // 3. Saved に関係なく必ず保存する（16.6「Workbook.Saved の限界」・16.16-3 の決定）。DisplayAlerts は触らない（互換性の警告などを
        //    利用者に見せずに通さない。16.12）。保存できなければ、ここで止まる（ブックは開いたまま）。
        try
        {
            session.Call(() => { wb.Save(); return 0; });
        }
        catch (COMException ex) when (!ComRetry.IsRetryable(ex))
        {
            return GraftResult<VbaWorkbookReopenTicket>.Fail(ErrorCode.E820,
                $"「{name}」を保存できませんでした（0x{ex.HResult:X8}: {ex.Message}）。ブックは Excel で開いたままです。ブックのファイルは変えていません。",
                path: fullName);
        }

        // 保存に時間がかかって呼び出し側が待つのをやめていたら、閉じない（利用者には「時間切れ」と伝わっている。そのあとで黙って閉じると、
        // 何が起きたか分からなくなる）。保存は済んでいるので、利用者の作業は失われない。
        if (session.IsAbandoned)
        {
            return GraftResult<VbaWorkbookReopenTicket>.Fail(ErrorCode.E805, "時間切れのため、保存のあとブックを閉じずに止めました。", path: fullName);
        }

        // 4. 閉じる。保存した直後なので失うものは無い。SaveChanges に false を渡すのは、再計算などで Saved が False に戻っても
        //    「保存しますか」で止まらないようにするため。
        try
        {
            session.Call(() => { wb.Close(false); return 0; });
        }
        catch (COMException ex) when (!ComRetry.IsRetryable(ex))
        {
            return GraftResult<VbaWorkbookReopenTicket>.Fail(ErrorCode.E820,
                $"「{name}」を保存しましたが、閉じられませんでした（0x{ex.HResult:X8}: {ex.Message}）。ブックは保存済みで、Excel で開いたままです。ブックのファイルは変えていません。",
                path: fullName);
        }

        // 5. 閉じたことを確かめる。Workbook_BeforeClose が Cancel = True にした・ほかのブックの VBA から参照されている、などで残っていれば E820。
        if (IsStillOpen(session, appObj, fullName))
        {
            return GraftResult<VbaWorkbookReopenTicket>.Fail(ErrorCode.E820,
                $"「{name}」を保存して閉じようとしましたが、ブックが開いたままです（ブックのマクロ〔Workbook_BeforeClose〕が閉じるのを取り消した、またはほかのブックから参照されている見込み）。ブックは保存済みで、Excel で開いたままです。ブックのファイルは変えていません。",
                path: fullName);
        }

        // 閉じた。開き直しに使う Application の参照を GIT に預け、券を返す。
        uint cookie;
        try
        {
            cookie = RegisterInGlobalTable(appObj);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
        {
            // 預けられなければ、同じ Excel で開き直せない。閉じたことは事実なので、券なしの失敗にせず、開き直せない券として返す
            // （呼び出し側は開き直しで E822 になり、「Excel で開く」を案内する）。
            return GraftResult<VbaWorkbookReopenTicket>.Ok(new VbaWorkbookReopenTicket(ticketId, fullName, view));
        }

        _held[ticketId] = new HeldTicket(cookie, fullName);
        if (abandoned.IsCancellationRequested)
        {
            // 呼び出し側は時間切れで結果を捨てている。券は誰にも渡らないので、ここで外す（onTimeout の側と、どちらが先でも 1 回だけ外れる）。
            ReleaseHeld(ticketId);
        }

        return GraftResult<VbaWorkbookReopenTicket>.Ok(new VbaWorkbookReopenTicket(ticketId, fullName, view));
    }

    /// <summary>
    /// 閉じる前の見た目（16.6「保存して閉じる」の 2）。控えるのは、このブックがアクティブだったか、そうでなければそのとき前面にあったブックだけ
    /// （アクティブなシートとウィンドウの状態は Excel が自分で戻す。<see cref="VbaWorkbookViewState"/>）。分からなければ「アクティブだった」と
    /// みなし、ほかのブックを前面へ動かさない。
    /// </summary>
    private static VbaWorkbookViewState CaptureView(Session s, object workbook, string fullName)
    {
        dynamic wb = workbook;
        var wasActive = true;
        string? previous = null;

        try
        {
            var appObj = s.Own(s.Call(() => (object)wb.Application));
            if (appObj is not null)
            {
                dynamic app = appObj;
                var activeObj = s.Own(s.Call(() => (object)app.ActiveWorkbook));
                if (activeObj is not null)
                {
                    dynamic active = activeObj;
                    var activeName = s.Call(() => (string)active.FullName);
                    wasActive = VbaRotNames.SamePath(activeName, fullName);
                    previous = wasActive ? null : activeName;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // 分からなければ「アクティブだった」とみなす（ほかのブックを前面へ動かさない）。
        }

        return new VbaWorkbookViewState(wasActive, previous);
    }

    /// <summary>
    /// 閉じたあとも、閉じた Excel の <c>Workbooks</c> にブックが残っているか。
    /// <para>
    /// 【<c>Workbooks</c> を根拠にする理由】 実機で、閉じたブックが ROT に 10 秒以上残ることを確かめた（6-34 の 5-b。5 回とも）。
    /// ROT で決めると、閉じたのに「開いたまま（E820）」と誤って止めてしまう。<c>Workbooks</c> は <c>Close</c> が戻った時点で正しいので、
    /// これを根拠にする。<c>Workbooks</c> を数えられないときの代わりの確認（<see cref="FindWorkbook"/>）も、ROT の項目をそのまま信用せず
    /// 項目の Excel の <c>Workbooks</c> で確かめる（数えられない状況では確かめも通らず「閉じた」と見る。閉じた判断が外れていても、
    /// 段階 7 の排他で開く検査が、ブックのファイルを書き換える前に最後に止める）。
    /// </para>
    /// </summary>
    private static bool IsStillOpen(Session s, object appObject, string fullName)
    {
        try
        {
            dynamic app = appObject;
            var workbooksObj = s.Own(s.Call(() => (object)app.Workbooks));
            if (workbooksObj is not null)
            {
                dynamic workbooks = workbooksObj;
                var count = s.Call(() => (int)workbooks.Count);
                for (var i = 1; i <= count; i++)
                {
                    using var item = new ComScope();
                    var bookObj = item.Own(s.Call(() => (object)workbooks.Item(i)));
                    if (bookObj is null) continue;
                    dynamic book = bookObj;
                    if (VbaRotNames.SamePath(s.Call(() => (string)book.FullName), fullName)) return true;
                }

                return false;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // Workbooks を数えられない。ROT の項目からの確かめ（FindWorkbook）に任せる。
        }

        return FindWorkbook(s, fullName) is not null;
    }

    // ------------------------------------------------------------------
    // 開き直す（16.6「開き直す」）
    // ------------------------------------------------------------------

    private GraftResult<VbaWorkbookReopenOutcome> ReopenCore(VbaWorkbookReopenTicket ticket, CancellationToken abandoned)
    {
        var fullName = ticket.WorkbookFullName;
        var name = VbaRibbonRewriteText.FileNameOf(fullName);
        if (!_held.TryRemove(ticket.Id, out var held))
        {
            return GraftResult<VbaWorkbookReopenOutcome>.Fail(ErrorCode.E822,
                $"「{name}」を閉じた Excel を覚えていないため、開き直せません。Excel でブックを開いてください。", path: fullName);
        }

        using var session = NewSession(abandoned);
        object? appObj;
        try
        {
            appObj = session.Own(GetFromGlobalTable(held.Cookie));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
        {
            return GraftResult<VbaWorkbookReopenOutcome>.Fail(ErrorCode.E822,
                $"「{name}」を閉じた Excel が見つかりません（Excel が終了した見込み。0x{ex.HResult:X8}）。Excel でブックを開いてください。", path: fullName);
        }
        finally
        {
            // 取り出したら GIT からは外す。開き直しのあいだは session の参照が Excel を生かす。
            RevokeFromGlobalTable(held.Cookie);
        }

        try
        {
            dynamic app = appObj!;
            var workbooksObj = session.Own(session.Call(() => (object)app.Workbooks));
            if (workbooksObj is null)
            {
                return GraftResult<VbaWorkbookReopenOutcome>.Fail(ErrorCode.E822,
                    $"「{name}」を閉じた Excel からブックの一覧を取れませんでした。Excel でブックを開いてください。", path: fullName);
            }

            // 1. Workbooks.Open(Filename) だけ。ほかの引数は渡さない（リンクの更新などは Excel の既定のまま。利用者が手で開くのと同じ）。
            //    Marshal.BindToMoniker や Process.Start は使わない（別の Excel で開いたり、利用者の Excel と別のプロセスになったりするため）。
            dynamic workbooks = workbooksObj;
            var openedObj = session.Own(session.Call(() => (object)workbooks.Open(fullName)));
            if (openedObj is null)
            {
                return GraftResult<VbaWorkbookReopenOutcome>.Fail(ErrorCode.E822,
                    $"「{name}」を開き直せませんでした（Excel がブックを返しませんでした）。Excel でブックを開いてください。", path: fullName);
            }

            // 2. 見た目を戻す（どれも「できれば」。失敗しても警告にもしない。覚え書きにだけ残す）。
            var notes = RestoreView(session, appObj!, ticket.View, fullName);
            return GraftResult<VbaWorkbookReopenOutcome>.Ok(new VbaWorkbookReopenOutcome(notes.Count == 0, notes));
        }
        catch (ComBusyException)
        {
            return GraftResult<VbaWorkbookReopenOutcome>.Fail(ErrorCode.E822,
                $"「{name}」を開き直そうとしましたが、Excel が受け付けませんでした（セルの編集中・ダイアログの表示中など）。Excel でブックを開いてください。", path: fullName);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            var hresult = ex is COMException com ? com.HResult : ex.HResult;
            var why = ex is COMException && VbaComHResult.Classify(hresult) == VbaComFailure.Disconnected
                ? "Excel が終了していた見込みです"
                : ex.Message;
            return GraftResult<VbaWorkbookReopenOutcome>.Fail(ErrorCode.E822,
                $"「{name}」を開き直せませんでした（0x{hresult:X8}: {why}）。Excel でブックを開いてください。", path: fullName);
        }
    }

    /// <summary>
    /// 見た目を戻す（<see cref="VbaWorkbookViewRestore"/> の決め方どおり）。戻すのは、閉じる前にアクティブでなかったブックの
    /// 場合の「前にアクティブだったブック」だけ（開き直したブックが前面になるため。実機の 3-d）。アクティブなシート・ウィンドウは
    /// Excel が自分で戻す（3-a〜3-c）ので触れない。戻せなかった項目を返す。
    /// </summary>
    private static List<string> RestoreView(Session s, object appObject, VbaWorkbookViewState view, string fullName)
    {
        var notes = new List<string>();
        dynamic app = appObject;

        if (VbaWorkbookViewRestore.ReactivatesPrevious(view, fullName))
        {
            try
            {
                var workbooksObj = s.Own(s.Call(() => (object)app.Workbooks));
                dynamic workbooks = workbooksObj!;
                var count = s.Call(() => (int)workbooks.Count);
                var found = false;
                for (var i = 1; i <= count && !found; i++)
                {
                    var bookObj = s.Own(s.Call(() => (object)workbooks.Item(i)));
                    if (bookObj is null) continue;
                    dynamic book = bookObj;
                    if (!VbaRotNames.SamePath(s.Call(() => (string)book.FullName), view.PreviouslyActiveWorkbook)) continue;
                    s.Call(() => { book.Activate(); return 0; });
                    found = true;
                }

                if (!found) notes.Add("前にアクティブだったブックが見つからないため、前面へ戻しませんでした。");
            }
            catch (Exception ex) when (ex is COMException or ComBusyException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                notes.Add($"前にアクティブだったブックを前面へ戻せませんでした（{ex.GetType().Name}）。");
            }
        }

        return notes;
    }

    // ------------------------------------------------------------------
    // GIT（Global Interface Table）
    // ------------------------------------------------------------------

    /// <summary>
    /// 券の参照を外す（何度呼んでも 1 回だけ外れる）。GIT はどのアパートメントからでも使えるので、COM の専用スレッドが固まっていても
    /// 呼び出し元のスレッドで外せる（GIT の RCW を毎回その場で作るのはこのため。専用スレッドで作った RCW を別のスレッドから使うと、
    /// 固まったスレッドへの呼び出しを待って止まりうる）。
    /// </summary>
    private void ReleaseHeld(string ticketId)
    {
        if (_held.TryRemove(ticketId, out var held)) RevokeFromGlobalTable(held.Cookie);
    }

    private void ReleaseAllHeldTickets()
    {
        foreach (var id in _held.Keys.ToList()) ReleaseHeld(id);
    }

    private static IGlobalInterfaceTable CreateGlobalTable()
    {
        var type = Type.GetTypeFromCLSID(ClsidStdGlobalInterfaceTable, throwOnError: true)!;
        return (IGlobalInterfaceTable)Activator.CreateInstance(type)!;
    }

    private static uint RegisterInGlobalTable(object comObject)
    {
        var table = CreateGlobalTable();
        try
        {
            var iid = IidIDispatch;
            table.RegisterInterfaceInGlobal(comObject, ref iid, out var cookie);
            return cookie;
        }
        finally
        {
            Marshal.ReleaseComObject(table);
        }
    }

    private static object GetFromGlobalTable(uint cookie)
    {
        var table = CreateGlobalTable();
        try
        {
            var iid = IidIDispatch;
            table.GetInterfaceFromGlobal(cookie, ref iid, out var value);
            return value;
        }
        finally
        {
            Marshal.ReleaseComObject(table);
        }
    }

    private static void RevokeFromGlobalTable(uint cookie)
    {
        try
        {
            var table = CreateGlobalTable();
            try
            {
                table.RevokeInterfaceFromGlobal(cookie);
            }
            finally
            {
                Marshal.ReleaseComObject(table);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException or ArgumentException)
        {
            // 外し済み・プロセスの終了の間際など。残っても、Graft のプロセスの終了で COM が片付ける。
        }
    }
}

/// <summary>COM の Global Interface Table（<c>IGlobalInterfaceTable</c>）。アパートメントをまたいでインターフェイスの参照を預ける。</summary>
[ComImport]
[Guid("00000146-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[SupportedOSPlatform("windows")]
internal interface IGlobalInterfaceTable
{
    void RegisterInterfaceInGlobal([MarshalAs(UnmanagedType.IUnknown)] object pUnk, [In] ref Guid riid, out uint pdwCookie);

    void RevokeInterfaceFromGlobal(uint dwCookie);

    void GetInterfaceFromGlobal(uint dwCookie, [In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}
