using System.Runtime.InteropServices;
using Graft.Core;
using Graft.Core.Vba;

namespace Graft.Platform.Windows;

// ブックの探索・前提条件の検査（仕様 5.4）・一覧・読み取り。
// 規約: dynamic の呼び出しはプロパティを連鎖させず、受けた COM オブジェクトは必ず session.Own に渡す
// （ComScope のクラスコメント参照）。dynamic 式の結果は (object) や (int) へ明示的に変換してから使う。
public sealed partial class WindowsVbaBridge
{
    /// <summary>ブックと VBProject を開いた結果。<see cref="Issue"/> が非 null なら、それ以外は null。</summary>
    private sealed record OpenedProject(object? Workbook, object? Project, GraftIssue? Issue, bool IsReadOnly = false);

    // ------------------------------------------------------------------
    // 一覧
    // ------------------------------------------------------------------

    private GraftResult<IReadOnlyList<VbaWorkbookInfo>> ListWorkbooksCore(CancellationToken abandoned)
    {
        using var session = NewSession(abandoned);
        using var snapshot = ComRunningObjectTable.Enumerate();

        var infos = new List<VbaWorkbookInfo>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < snapshot.DisplayNames.Count; i++)
        {
            if (session.IsAbandoned) break;
            var info = DescribeWorkbook(session, snapshot, i);
            if (info is null) continue;

            // 同じブックが ROT に別名のモニカで二重に載ることがあるので、パスで重複を除く。
            // 同じパスのブックを2つの Excel プロセスで開いた場合は、書き込める側（読み取り専用でない側）を残す。
            var key = VbaMirrorFolderName.NormalizePath(info.FullName);
            if (seen.TryGetValue(key, out var existing))
            {
                if (infos[existing].IsReadOnly && !info.IsReadOnly) infos[existing] = info;
                continue;
            }

            seen[key] = infos.Count;
            infos.Add(info);
        }

        return GraftResult<IReadOnlyList<VbaWorkbookInfo>>.Ok(
            infos.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static VbaWorkbookInfo? DescribeWorkbook(Session s, ComRunningObjectTable.Snapshot snapshot, int index)
    {
        var displayName = snapshot.DisplayNames[index];
        var rotObject = s.Own(snapshot.GetObject(index));
        if (rotObject is null) return null;

        object? obj;
        string fullName;
        string name;
        bool readOnly;
        try
        {
            // 閉じたのに ROT に残っている古い項目は、一覧に出さない（実機検証 6-34 の 5-b。FindWorkbook の説明を参照）。
            obj = ResolveLiveWorkbook(s, rotObject, displayName);
            if (obj is null) return null;

            dynamic wb = obj;
            fullName = s.Call(() => (string)wb.FullName);
            name = s.Call(() => (string)wb.Name);
            readOnly = s.Call(() => (bool)wb.ReadOnly);
        }
        catch (ComBusyException)
        {
            // セル編集中などで応答しない Excel。表示名しか分からないので、理由つきで無効にして見せる。
            return new VbaWorkbookInfo(displayName, Path.GetFileName(displayName), VbaWorkbookAvailability.Unresponsive,
                "Excel が応答しません（セルの編集中・ダイアログの表示中など）。確定または閉じてから、もう一度開いてください。", false);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // ブックではないオブジェクト、または取得の直後に閉じられた。一覧には出さない。
            return null;
        }

        if (VbaRotNames.IsUrlLocation(fullName))
        {
            return new VbaWorkbookInfo(fullName, name, VbaWorkbookAvailability.UrlLocation,
                VbaWorkbookAvailabilityIssues.UrlLocationReason, readOnly);
        }

        if (VbaRotNames.IsNoMacroFormat(fullName))
        {
            return new VbaWorkbookInfo(fullName, name, VbaWorkbookAvailability.NoMacroFormat,
                "マクロを保存できない形式（.xlsx）です。.xlsm で保存し直してください。", readOnly);
        }

        if (IsInProtectedView(s, obj, fullName))
        {
            return new VbaWorkbookInfo(fullName, name, VbaWorkbookAvailability.ProtectedView,
                VbaWorkbookAvailabilityIssues.ProtectedViewReason, readOnly);
        }

        return ProbeVbProject(s, obj, fullName, name, readOnly);
    }

    /// <summary>VBProject に触れるか・ロックされていないかを調べて、利用可否を決める。</summary>
    private static VbaWorkbookInfo ProbeVbProject(Session s, object workbook, string fullName, string name, bool readOnly)
    {
        try
        {
            dynamic wb = workbook;
            var vbp = s.Own(s.Call(() => (object)wb.VBProject));
            if (vbp is null) return NotTrusted(fullName, name, readOnly);

            dynamic project = vbp;
            var protection = s.Call(() => (int)project.Protection);
            return VbaProjectState.IsLocked(protection)
                ? new VbaWorkbookInfo(fullName, name, VbaWorkbookAvailability.Locked,
                    "VBA プロジェクトがパスワードで保護されています。VBE でロックを解除してください。", readOnly)
                : new VbaWorkbookInfo(fullName, name, VbaWorkbookAvailability.Available, null, readOnly);
        }
        catch (ComBusyException)
        {
            return new VbaWorkbookInfo(fullName, name, VbaWorkbookAvailability.Unresponsive,
                "Excel が応答しません（セルの編集中・ダイアログの表示中など）。", readOnly);
        }
        catch (COMException ex) when (VbaComHResult.ClassifyVbProjectAccess(ex.HResult) == ErrorCode.E802)
        {
            return NotTrusted(fullName, name, readOnly);
        }
        catch (COMException ex)
        {
            // トラストセンターの設定（既知の値）以外の失敗。原因を決めつけず、HRESULT を添えて見せる。
            return new VbaWorkbookInfo(fullName, name, VbaWorkbookAvailability.Unresponsive,
                $"Excel と通信できませんでした（0x{ex.HResult:X8}）。Excel を確認して、もう一度開いてください。", readOnly);
        }
    }

    private static VbaWorkbookInfo NotTrusted(string fullName, string name, bool readOnly)
        => new(fullName, name, VbaWorkbookAvailability.AccessNotTrusted,
            "VBA プロジェクト オブジェクト モデルへのアクセスが許可されていません。トラストセンターで許可してください。", readOnly);

    /// <summary>
    /// 保護ビューで開いているか。保護ビューのブックは VBProject に触れない（実機で要確認の推測。付録B）。
    /// <c>Application.ProtectedViewWindows</c> のどれかの <c>Workbook</c> が同じパスなら保護ビューとみなす。
    /// 調べられなければ（古い Excel などで例外）、保護ビューではないものとして続ける。
    /// </summary>
    private static bool IsInProtectedView(Session s, object workbook, string fullName)
    {
        try
        {
            dynamic wb = workbook;
            var appObj = s.Own(s.Call(() => (object)wb.Application));
            if (appObj is null) return false;

            dynamic app = appObj;
            var windowsObj = s.Own(s.Call(() => (object)app.ProtectedViewWindows));
            if (windowsObj is null) return false;

            dynamic windows = windowsObj;
            var count = s.Call(() => (int)windows.Count);
            for (var i = 1; i <= count; i++)
            {
                var windowObj = s.Own(s.Call(() => (object)windows.Item(i)));
                if (windowObj is null) continue;
                dynamic window = windowObj;
                var inner = s.Own(s.Call(() => (object)window.Workbook));
                if (inner is null) continue;
                dynamic innerWorkbook = inner;
                var innerName = s.Call(() => (string)innerWorkbook.FullName);
                if (VbaRotNames.SamePath(innerName, fullName)) return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // ブックと VBProject を開く（前提条件の検査。仕様 5.4）
    // ------------------------------------------------------------------

    /// <summary>
    /// ROT から候補を見つけ、<b>その Excel の <c>Workbooks</c> に実在するブックだけ</b>を返す。見つからなければ null（E804）。Excel を起動しない。
    /// <para>
    /// 【ROT の項目を信用しない理由】 実機（2026-11-24。Windows実機検証手順 6-34 の 5-b）で、<c>Workbook.Close</c> から 10 秒たっても
    /// ブックが ROT に残り続けた（5 回とも）。ROT に載っているだけでは「開いている」と言えないので、候補ごとに
    /// <see cref="ResolveLiveWorkbook"/> で確かめ、実在しない項目（閉じたのに残った古い項目）は読み飛ばす。
    /// 返すのは ROT のオブジェクトではなく <c>Workbooks</c> から取り直したブックで、閉じて開き直したあとに古い項目と新しい項目が
    /// 並んでいても、死んだ方のブックを掴まない。
    /// </para>
    /// <para>
    /// 【古い項目から開き直してしまう心配】 ここで ROT に使うのは <c>IRunningObjectTable.GetObject</c>（登録済みのオブジェクトを取り出すだけ）と、
    /// 取り出したあとの <c>Application</c>・<c>Workbooks</c> の読み取りだけで、ファイルを開く操作は 1 つも無い。
    /// モニカを束ねてファイルを開く <c>Marshal.BindToMoniker</c>・<c>IMoniker.BindToObject</c> は使っていない
    /// （<c>ComRunningObjectTable</c> の冒頭と、<c>VbaRotNoReopenGuardTests</c> がソースで固定している）。
    /// </para>
    /// </summary>
    private static object? FindWorkbook(Session s, string fullName)
    {
        using var snapshot = ComRunningObjectTable.Enumerate();
        var matches = VbaRotNames.FindMatches(snapshot.DisplayNames.Cast<string?>().ToList(), fullName);

        object? chosen = null;
        foreach (var index in matches)
        {
            var rotObject = s.Own(snapshot.GetObject(index));
            if (rotObject is null) continue;

            var obj = ResolveLiveWorkbook(s, rotObject, fullName);
            if (obj is null) continue; // 閉じたのに ROT に残っている古い項目。

            chosen ??= obj;
            try
            {
                // 同じパスのブックを2つの Excel プロセスで開いている場合は、書き込める側を選ぶ。
                dynamic wb = obj;
                if (!s.Call(() => (bool)wb.ReadOnly))
                {
                    return obj;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // 読み取り専用かどうか分からないものは、後回しにする。
            }
        }

        return chosen;
    }

    /// <summary>
    /// ROT の項目（<paramref name="rotObject"/>）が、いま開いているブックを指しているかを確かめ、開いていればその <b>Workbook</b> を返す
    /// （閉じたのに残った項目なら null）。判定は <see cref="VbaRotNames.IsLiveEntry"/> と同じ考え方: 項目から <c>Application</c> を辿り、
    /// <c>Application.Workbooks</c> に同じフルパスのブックがあれば、<b>そちらのオブジェクト</b>を返す（古い項目の死んだオブジェクトは返さない）。
    /// 列挙に出ないアドイン（.xlam）は名前で取り直す（<see cref="FindInWorkbooks"/>。1.3.1 で 1.3.0 の退行を直した）。
    /// <c>Workbooks</c> に無くても、保護ビューのウィンドウにあるなら開いているので、ROT のオブジェクトを返す（保護ビューは既存の判定で止める）。
    /// <para>
    /// 閉じたブックのオブジェクトから <c>Application</c> を辿れない（切断された・無効）ときは「実在しない」。Excel が拒否し続けた
    /// （<see cref="ComBusyException"/>）ときは、ここで握りつぶさず呼び出し側へ伝える（E805。実在するかどうかを推測で決めない）。
    /// </para>
    /// </summary>
    private static object? ResolveLiveWorkbook(Session s, object rotObject, string fullName)
    {
        try
        {
            dynamic stale = rotObject;
            var appObj = s.Own(s.Call(() => (object)stale.Application));
            if (appObj is null) return null;

            var found = FindInWorkbooks(s, appObj, fullName);
            if (found is not null) return found;

            return IsInProtectedView(s, rotObject, fullName) ? rotObject : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // 閉じたブックの項目（切断されたオブジェクト）、または確かめている最中に閉じられた。開いているとは言えない。
            return null;
        }
    }

    /// <summary>
    /// 指した Excel（<paramref name="appObject"/>）の <c>Workbooks</c> から、フルパスが <paramref name="fullName"/> と同じブックを取り出す
    /// （無ければ null。見つけたブックは session の持ち物で、呼び出し側が不要になったら <c>s.Scope.Release</c> する）。
    /// <para>
    /// 【2段で探す理由】 1.3.0 で「閉じたのに ROT に残った項目を出さない」ために <c>Workbooks</c> の列挙（<c>Count</c>／<c>Item(i)</c>）だけで
    /// 実在を決めたところ、<b>アドイン（.xlam）が一覧から消える退行</b>になった。<c>.xlam</c> は <c>Workbooks</c> の列挙に出ない
    /// （仕様 5.2「.xlam は Workbooks に出ないが ROT には出る」）。そこで、列挙で見つからなかったときは <c>Workbooks.Item(ファイル名)</c> で
    /// 取り直す。名前での取得は、アドインでも効く。
    /// </para>
    /// <para>
    /// 【1.3.0 の対策（実機検証 6-34 の 5-b）は保たれる】 閉じたアドインの古い ROT 項目は、名前では取れない（Item が COMException）ので「実在しない」になる。
    /// 名前で取れても <c>FullName</c> が違う（別のフォルダの同名ブック）なら採らない。取得が拒否され続けたとき
    /// （<see cref="ComBusyException"/>）は握りつぶさず呼び出し側へ伝える（実在するかを推測で決めない）。
    /// </para>
    /// </summary>
    private static object? FindInWorkbooks(Session s, object appObject, string fullName)
    {
        dynamic app = appObject;
        var workbooksObj = s.Own(s.Call(() => (object)app.Workbooks));
        if (workbooksObj is null) return null;

        dynamic workbooks = workbooksObj;
        var count = s.Call(() => (int)workbooks.Count);
        for (var i = 1; i <= count; i++)
        {
            var bookObj = s.Own(s.Call(() => (object)workbooks.Item(i)));
            if (bookObj is null) continue;

            dynamic book = bookObj;
            if (VbaRotNames.SamePath(s.Call(() => (string)book.FullName), fullName)) return bookObj;
            s.Scope.Release(bookObj);
        }

        // 列挙に出ないアドイン（.xlam）を、名前で取り直す。
        var fileName = VbaRibbonRewriteText.FileNameOf(fullName);
        if (fileName.Length == 0) return null;

        object? byName;
        try
        {
            byName = s.Own(s.Call(() => (object)workbooks.Item(fileName)));
        }
        catch (COMException)
        {
            // 見つからない（DISP_E_BADINDEX など）。閉じたアドインの古い ROT 項目もここで「実在しない」になる。
            return null;
        }

        if (byName is null) return null;

        dynamic named = byName;
        if (VbaRotNames.SamePath(s.Call(() => (string)named.FullName), fullName)) return byName;
        s.Scope.Release(byName);
        return null;
    }

    /// <summary>
    /// 前提条件を順に検査してブックと VBProject を返す。違反は問題として返す（E806／E804／E802／E803、
    /// 書き込みなら E813）。
    /// </summary>
    private OpenedProject OpenProject(Session s, string fullName, bool requireDesignMode)
    {
        if (VbaRotNames.IsNoMacroFormat(fullName))
        {
            return Failed(GraftIssue.Of(ErrorCode.E806, $"{fullName}: マクロを保存できない形式です。", path: fullName));
        }

        // 選択画面と同じ判定で、OneDrive／SharePoint 上のブックと保護ビューのブックを弾く。弾かないと、追加した後に
        // そうなったブックは VBProject の取得で失敗して、トラストセンターの案内（E802）と誤診される。
        if (VbaRotNames.IsUrlLocation(fullName))
        {
            return Failed(VbaWorkbookAvailabilityIssues.ToIssue(VbaWorkbookAvailability.UrlLocation, fullName)!);
        }

        var workbook = FindWorkbook(s, fullName);
        if (workbook is null)
        {
            return Failed(GraftIssue.Of(ErrorCode.E804, $"次のブックが開いていません: {fullName}", path: fullName));
        }

        if (IsInProtectedView(s, workbook, fullName))
        {
            return Failed(VbaWorkbookAvailabilityIssues.ToIssue(VbaWorkbookAvailability.ProtectedView, fullName)!);
        }

        dynamic wb = workbook;
        bool readOnly = s.Call(() => (bool)wb.ReadOnly);

        object? vbp;
        try
        {
            vbp = s.Own(s.Call(() => (object)wb.VBProject));
        }
        catch (COMException ex) when (!ComRetry.IsRetryable(ex))
        {
            // 取得で COMException になるのは「アクセスを信頼していない」が典型（仕様 5.4）。切断なら E801。
            var code = VbaComHResult.ClassifyVbProjectAccess(ex.HResult);
            return Failed(code == ErrorCode.E801
                ? ComFailures.FromException(ex)
                : GraftIssue.Of(ErrorCode.E802, $"0x{ex.HResult:X8}: {ex.Message}", path: fullName));
        }

        if (vbp is null)
        {
            return Failed(GraftIssue.Of(ErrorCode.E802, "VBProject を取得できませんでした。", path: fullName));
        }

        dynamic project = vbp;
        var protection = s.Call(() => (int)project.Protection);
        if (VbaProjectState.IsLocked(protection))
        {
            return Failed(GraftIssue.Of(ErrorCode.E803, $"{fullName}: VBA プロジェクトがパスワードで保護されています。", path: fullName));
        }

        if (requireDesignMode)
        {
            // マクロの実行中・中断中に書き込むとコードが壊れる、あるいは呼び出しが戻らない見込み。
            // 1件も書かずに止める（仕様 5.4）。
            var mode = s.Call(() => (int)project.Mode);
            if (!VbaProjectState.IsDesignMode(mode))
            {
                return Failed(GraftIssue.Of(ErrorCode.E813, $"{fullName}: VBProject.Mode={mode}（デザインモードではありません）。", path: fullName));
            }
        }

        return new OpenedProject(workbook, vbp, null, readOnly);
    }

    private static OpenedProject Failed(GraftIssue issue) => new(null, null, issue);

    // ------------------------------------------------------------------
    // 読み取り
    // ------------------------------------------------------------------

    private GraftResult<IReadOnlyList<VbaComponentSnapshot>> ReadComponentsCore(
        string fullName, IReadOnlyCollection<string>? names, CancellationToken abandoned)
    {
        using var session = NewSession(abandoned);
        var opened = OpenProject(session, fullName, requireDesignMode: false);
        if (opened.Issue is not null) return GraftResult<IReadOnlyList<VbaComponentSnapshot>>.Fail(opened.Issue);

        dynamic project = opened.Project!;
        var comps = session.Own(session.Call(() => (object)project.VBComponents));
        if (comps is null) return GraftResult<IReadOnlyList<VbaComponentSnapshot>>.Fail(ErrorCode.E801, "VBComponents を取得できませんでした。");

        dynamic components = comps;
        var count = session.Call(() => (int)components.Count);
        var wanted = names is null ? null : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        var result = new List<VbaComponentSnapshot>();
        var issues = new List<GraftIssue>();
        Dictionary<string, string>? captions = null;

        for (var i = 1; i <= count; i++)
        {
            if (session.IsAbandoned) break;

            // 1件ごとに解放する（100 モジュール分の RCW を溜めない）。
            using var item = new ComScope();
            var comp = item.Own(session.Call(() => (object)components.Item(i)));
            if (comp is null) continue;

            dynamic component = comp;
            var componentName = session.Call(() => (string)component.Name);
            if (wanted is not null && !wanted.Contains(componentName)) continue;

            var type = session.Call(() => (int)component.Type);
            var kind = VbaModuleKinds.FromComType(type);
            if (kind is null)
            {
                // ActiveX デザイナなど。同期の対象外で、一覧で「未対応」と見せる（仕様 6.1）。
                issues.Add(GraftIssue.Of(
                    ErrorCode.E810,
                    $"コンポーネント「{componentName}」は未対応の種別（Type={type}）のため、同期の対象外です。",
                    severity: Severity.Warning));
                continue;
            }

            var cm = item.Own(session.Call(() => (object)component.CodeModule));
            if (cm is null) continue;

            dynamic codeModule = cm;
            var lines = session.Call(() => (int)codeModule.CountOfLines);
            var code = lines > 0 ? session.Call(() => (string)codeModule.Lines(1, lines)) : string.Empty;

            string? caption = null;
            if (kind == VbaModuleKind.Document)
            {
                captions ??= ReadSheetCaptions(session, opened.Workbook!);
                captions.TryGetValue(componentName, out caption);
            }

            result.Add(new VbaComponentSnapshot(componentName, kind.Value, code, caption));
        }

        return GraftResult<IReadOnlyList<VbaComponentSnapshot>>.Ok(result, issues);
    }

    /// <summary>
    /// シートのコード名（<c>Sheet1</c>）→ 表示名（「集計」）。ドキュメントモジュールのファイル名はコード名なので、
    /// 利用者が見分けるための補足としてだけ使う。取れなくても同期には影響しないので、失敗は握りつぶす。
    /// </summary>
    private static Dictionary<string, string> ReadSheetCaptions(Session s, object workbookObject)
    {
        dynamic workbook = workbookObject;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var sheetsObj = s.Own(s.Call(() => (object)workbook.Sheets));
            if (sheetsObj is null) return map;

            dynamic sheets = sheetsObj;
            var count = s.Call(() => (int)sheets.Count);
            for (var i = 1; i <= count; i++)
            {
                using var item = new ComScope();
                var sheetObj = item.Own(s.Call(() => (object)sheets.Item(i)));
                if (sheetObj is null) continue;
                dynamic sheet = sheetObj;
                var codeName = s.Call(() => (string)sheet.CodeName);
                var displayName = s.Call(() => (string)sheet.Name);
                map[codeName] = displayName;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // 表示名は補足情報。
        }

        return map;
    }
}
