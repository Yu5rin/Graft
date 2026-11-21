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
        var obj = s.Own(snapshot.GetObject(index));
        if (obj is null) return null;

        string fullName;
        string name;
        bool readOnly;
        try
        {
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
                "OneDrive／SharePoint 上のブックは使えません。ローカルのフォルダへ保存してから開き直してください。", readOnly);
        }

        if (VbaRotNames.IsNoMacroFormat(fullName))
        {
            return new VbaWorkbookInfo(fullName, name, VbaWorkbookAvailability.NoMacroFormat,
                "マクロを保存できない形式（.xlsx）です。.xlsm で保存し直してください。", readOnly);
        }

        if (IsInProtectedView(s, obj, fullName))
        {
            return new VbaWorkbookInfo(fullName, name, VbaWorkbookAvailability.ProtectedView,
                "保護ビューで開いています。「編集を有効にする」を押してから、もう一度開いてください。", readOnly);
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
        catch (COMException)
        {
            return new VbaWorkbookInfo(fullName, name, VbaWorkbookAvailability.Unresponsive,
                "Excel との接続が切れました。", readOnly);
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

    /// <summary>ROT から、紐付けたブックを探す。見つからなければ null（E804）。Excel を起動しない。</summary>
    private static object? FindWorkbook(Session s, string fullName)
    {
        using var snapshot = ComRunningObjectTable.Enumerate();
        var matches = VbaRotNames.FindMatches(snapshot.DisplayNames.Cast<string?>().ToList(), fullName);

        object? chosen = null;
        foreach (var index in matches)
        {
            var obj = s.Own(snapshot.GetObject(index));
            if (obj is null) continue;

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
    /// 前提条件を順に検査してブックと VBProject を返す。違反は問題として返す（E806／E804／E802／E803、
    /// 書き込みなら E813）。
    /// </summary>
    private OpenedProject OpenProject(Session s, string fullName, bool requireDesignMode)
    {
        if (VbaRotNames.IsNoMacroFormat(fullName))
        {
            return Failed(GraftIssue.Of(ErrorCode.E806, $"{fullName}: マクロを保存できない形式です。", path: fullName));
        }

        var workbook = FindWorkbook(s, fullName);
        if (workbook is null)
        {
            return Failed(GraftIssue.Of(ErrorCode.E804, $"次のブックが開いていません: {fullName}", path: fullName));
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
