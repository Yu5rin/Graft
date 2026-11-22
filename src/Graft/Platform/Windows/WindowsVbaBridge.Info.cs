using System.Diagnostics;
using System.Runtime.InteropServices;
using Graft.Core;
using Graft.Core.Vba;

namespace Graft.Platform.Windows;

// ブックの情報（機能 A。仕様書_ブックの情報と依頼の観点 5.5）の読み取り。書き込みはしない。
// 規約は他の分割ファイルと同じ: dynamic の呼び出しはプロパティを連鎖させず、受けた COM オブジェクトは必ず Own に渡す
// （ComScope のクラスコメント参照）。dynamic 式の結果は (object) や (int) へ明示的に変換してから使う。
//
// 【Linux では実行できない】 COM の呼び出し順・解放・Value2 の戻り値の型は、実機でしか確かめられない
// （仕様書付録B・11.3 の 1〜7、10）。ここに無い判定（Value2 の配列→文字列の表、列の文字、名前の分解、
// 時間の予算の判定）は Core/Vba の VbaWorkbookInfoReadRules・VbaCellText に出してテストしてある。
public sealed partial class WindowsVbaBridge
{
    /// <inheritdoc />
    public Task<GraftResult<VbaWorkbookInfoSnapshot>> ReadWorkbookInfoAsync(
        string workbookFullName, VbaWorkbookInfoOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookFullName);
        ArgumentNullException.ThrowIfNull(options);
        return InvokeAsync(token => ReadWorkbookInfoCore(workbookFullName, options, token), ct);
    }

    private GraftResult<VbaWorkbookInfoSnapshot> ReadWorkbookInfoCore(
        string fullName, VbaWorkbookInfoOptions options, CancellationToken abandoned)
    {
        using var session = NewSession(abandoned);
        var watch = Stopwatch.StartNew();

        // 前提検査は OpenProject の「VBProject まで」を除いた分。シート・テーブル・名前は VBProject に依存しないので、
        // トラストセンターがオフ（E802）でも読める。VBProject に触れるのは参照設定だけで、そこだけ失敗を許す（下）。
        if (VbaRotNames.IsUrlLocation(fullName))
        {
            return GraftResult<VbaWorkbookInfoSnapshot>.Fail(
                VbaWorkbookAvailabilityIssues.ToIssue(VbaWorkbookAvailability.UrlLocation, fullName)!);
        }

        var workbook = FindWorkbook(session, fullName);
        if (workbook is null)
        {
            return GraftResult<VbaWorkbookInfoSnapshot>.Fail(
                GraftIssue.Of(ErrorCode.E804, $"次のブックが開いていません: {fullName}", path: fullName));
        }

        if (IsInProtectedView(session, workbook, fullName))
        {
            return GraftResult<VbaWorkbookInfoSnapshot>.Fail(
                VbaWorkbookAvailabilityIssues.ToIssue(VbaWorkbookAvailability.ProtectedView, fullName)!);
        }

        dynamic wb = workbook;
        var workbookName = session.Call(() => (string)wb.Name);

        var omitted = new List<string>();
        var sheets = new List<VbaSheetInfo>();
        var tables = new List<VbaTableInfo>();
        ReadSheets(session, workbook, options, watch, sheets, tables, omitted);

        var names = session.IsAbandoned ? new List<VbaNamedRangeInfo>() : ReadNames(session, workbook, omitted);

        // 参照設定は、時間の予算を使い切っていたら読まない（シートの読み取りで予算を食った後に、さらに COM を呼ばない）。
        var references = new List<VbaReferenceInfo>();
        if (options.IncludeReferences && !session.IsAbandoned
            && !VbaWorkbookInfoReadRules.IsOverBudget(watch.Elapsed, options.Budget))
        {
            ReadReferences(session, workbook, references, omitted);
        }

        return GraftResult<VbaWorkbookInfoSnapshot>.Ok(new VbaWorkbookInfoSnapshot(
            workbookName, DateTimeOffset.Now, sheets, tables, names, references, omitted));
    }

    // ------------------------------------------------------------------
    // シート（シートごとの見せ方に応じた値・テーブル）
    // ------------------------------------------------------------------

    private static void ReadSheets(
        Session s, object workbook, VbaWorkbookInfoOptions options, Stopwatch watch,
        List<VbaSheetInfo> sheets, List<VbaTableInfo> tables, List<string> omitted)
    {
        dynamic wb = workbook;
        // Worksheets はグラフシートを含まない（Sheets は含む）。ダイアログシートも対象外にしたいので Worksheets を使う。
        var sheetsObj = s.Own(s.Call(() => (object)wb.Worksheets));
        if (sheetsObj is null) return;

        dynamic worksheets = sheetsObj;
        var total = s.Call(() => (int)worksheets.Count);
        var limit = Math.Min(total, Math.Max(1, options.MaxSheets));

        for (var i = 1; i <= limit; i++)
        {
            if (s.IsAbandoned) return;

            // シートの合間で時間の予算を見る。1 枚ごとの読み取りは小さい一括読み取りなので、合間で打ち切れば
            // 呼び出し全体の時間切れ（E805）より手前で、読めた分を返せる。
            if (VbaWorkbookInfoReadRules.IsOverBudget(watch.Elapsed, options.Budget))
            {
                omitted.Add(VbaWorkbookInfoReadRules.OmittedByBudgetMessage(SheetNameAt(s, worksheets, i)));
                return;
            }

            // 1 枚ごとに解放する（100 枚分の RCW を溜めない）。
            using var item = new ComScope();
            string sheetName = $"（{i} 枚目）";
            // シート本体（見出し・見本）を一覧に足したあとで、テーブルの読み取りだけが失敗することがある。
            // その場合に「シートは読めませんでした」と伝えると、AI は一覧にあるシートを「無い」と誤解しかねない
            // （実際は読めている）。何が読めなかったかを正確に言うために、足したかどうかを覚えておく。
            var sheetAdded = false;
            try
            {
                var sheetObj = item.Own(s.Call(() => (object)worksheets.Item(i)));
                if (sheetObj is null) continue;

                dynamic sheet = sheetObj;
                sheetName = s.Call(() => (string)sheet.Name);
                var info = ReadSheet(s, item, sheetObj, sheetName, options);
                sheets.Add(info);
                sheetAdded = true;
                // 「出さない」のシートは、テーブルも出力に出ない。読まない（Excel への問い合わせを増やさない）。
                if (options.Views.For(info.CodeName).Mode != VbaSheetViewMode.None)
                {
                    ReadTables(s, item, sheetObj, sheetName, options, tables);
                }
            }
            catch (ComBusyException) when (sheets.Count > 0)
            {
                // 1 枚以上読めているなら、全体を失敗にせず、読めた分を返す（全く読めていなければ E805 のまま）。
                omitted.Add(VbaWorkbookInfoReadRules.OmittedByBudgetMessage(sheetName));
                return;
            }
            catch (COMException ex) when (VbaComHResult.Classify(ex.HResult) == VbaComFailure.Other)
            {
                // この 1 枚だけ読めなかった（保護・破損など）。他のシートは続ける。
                omitted.Add(VbaWorkbookInfoReadRules.OmittedSheetMessage(sheetName, sheetAdded, ex.HResult));
            }
        }

        if (total > limit)
        {
            omitted.Add(VbaWorkbookInfoReadRules.OmittedByMaxSheetsMessage(total - limit, limit));
        }
    }

    private static string SheetNameAt(Session s, dynamic worksheets, int index)
    {
        // 予算切れのメッセージ用に、最初に読まなかったシートの名前だけを取る。取れなければ番号で代える。
        try
        {
            using var item = new ComScope();
            var sheetObj = item.Own(s.Call(() => (object)worksheets.Item(index)));
            if (sheetObj is null) return $"{index} 枚目";
            dynamic sheet = sheetObj;
            return s.Call(() => (string)sheet.Name);
        }
        catch (Exception ex) when (ex is COMException or ComBusyException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return $"{index} 枚目";
        }
    }

    private static VbaSheetInfo ReadSheet(Session s, ComScope item, object sheetObject, string sheetName, VbaWorkbookInfoOptions options)
    {
        dynamic sheet = sheetObject;
        var codeName = s.Call(() => (string)sheet.CodeName);
        var visible = s.Call(() => (int)sheet.Visible);
        var visibility = VbaWorkbookInfoReadRules.VisibilityFromExcel(visible);

        var usedObj = item.Own(s.Call(() => (object)sheet.UsedRange));
        if (usedObj is null) return new VbaSheetInfo(sheetName, codeName, visibility, null, Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

        dynamic used = usedObj;
        var rowsObj = item.Own(s.Call(() => (object)used.Rows));
        var columnsObj = item.Own(s.Call(() => (object)used.Columns));
        if (rowsObj is null || columnsObj is null) return new VbaSheetInfo(sheetName, codeName, visibility, null, Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

        dynamic rows = rowsObj;
        dynamic columns = columnsObj;
        var rowCount = s.Call(() => (int)rows.Count);
        var columnCount = s.Call(() => (int)columns.Count);

        // 空のシートは UsedRange が A1 だけで Value2 が Empty。使用範囲 null として、見出しも見本も読まない。
        if (rowCount == 1 && columnCount == 1)
        {
            var only = s.Call(() => (object?)used.Value2);
            if (only is null)
            {
                return new VbaSheetInfo(sheetName, codeName, visibility, null, Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());
            }
        }

        var address = s.Call(() => (string)used.Address(false, false));
        var structure = new VbaSheetInfo(sheetName, codeName, visibility, address, Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

        // 【セルの値を読むのは、値が渡る見せ方のシートだけ】 構成だけ・出さないのシートでは、ここから先の COM 呼び出しを
        // 1 回も行わない（社外秘を Graft のメモリにも載せない。読む範囲の計画は Core の VbaSheetReadPlanner が立てる）。
        var view = options.Views.For(codeName);
        var plan = VbaSheetReadPlanner.Plan(
            VbaA1Range.FromAddress(address), view, options.MaxColumns, options.AllMaxRows, options.AllMaxColumns);
        if (plan.Mode is not (VbaSheetViewMode.HeaderRows or VbaSheetViewMode.All or VbaSheetViewMode.Range)) return structure;
        if (!plan.ReadsCells) return VbaSheetReadPlanner.Assemble(structure, plan, Array.Empty<IReadOnlyList<string>>(), null);

        // 計画の範囲（先頭のセルから行数×列数）を、セルの値 1 回（数式を含めるときは数式も 1 回）で受ける。
        // 使用範囲そのものを Offset/Resize しないのは、使用範囲がシートの最終行まである場合に範囲外で失敗するのと、
        // 「範囲を指定」で使用範囲の途中から読むため。Cells.Item(行, 列) から Resize する形に統一している。
        var startRow = plan.StartRow;
        var startColumn = plan.StartColumn;
        var readRows = plan.RowCount;
        var readColumns = plan.ColumnCount;
        var cellsObj = item.Own(s.Call(() => (object)sheet.Cells));
        if (cellsObj is null) return structure;

        dynamic cells = cellsObj;
        var topObj = item.Own(s.Call(() => (object)cells.Item(startRow, startColumn)));
        if (topObj is null) return structure;

        dynamic top = topObj;
        var blockObj = item.Own(s.Call(() => (object)top.Resize(readRows, readColumns)));
        if (blockObj is null) return structure;

        dynamic block = blockObj;
        var raw = s.Call(() => (object?)block.Value2);
        var values = VbaWorkbookInfoReadRules.ToGrid(raw, options.MaxCellChars);
        IReadOnlyList<IReadOnlyList<string>>? withFormulas = null;
        if (view.IncludeFormulas)
        {
            var formulas = s.Call(() => (object?)block.Formula);
            withFormulas = VbaWorkbookInfoReadRules.ToGrid(raw, formulas, options.MaxCellChars);
        }

        return VbaSheetReadPlanner.Assemble(structure, plan, values, withFormulas);
    }

    private static void ReadTables(
        Session s, ComScope item, object sheetObject, string sheetName, VbaWorkbookInfoOptions options, List<VbaTableInfo> tables)
    {
        if (tables.Count >= VbaWorkbookInfoReadRules.MaxTables) return;

        dynamic sheet = sheetObject;
        var listObjectsObj = item.Own(s.Call(() => (object)sheet.ListObjects));
        if (listObjectsObj is null) return;

        dynamic listObjects = listObjectsObj;
        var count = s.Call(() => (int)listObjects.Count);
        for (var j = 1; j <= count && tables.Count < VbaWorkbookInfoReadRules.MaxTables; j++)
        {
            using var tableScope = new ComScope();
            var tableObj = tableScope.Own(s.Call(() => (object)listObjects.Item(j)));
            if (tableObj is null) continue;

            dynamic table = tableObj;
            var name = s.Call(() => (string)table.Name);
            var rangeObj = tableScope.Own(s.Call(() => (object)table.Range));
            if (rangeObj is null) continue;

            dynamic range = rangeObj;
            var address = s.Call(() => (string)range.Address(false, false));

            // 列名は HeaderRowRange.Value2 を 1 回で読む。見出し行を非表示にしたテーブルでは HeaderRowRange が
            // 取れない（COMException または null）ので、列名なしで続ける。
            var columns = new List<string>();
            try
            {
                var headerObj = tableScope.Own(s.Call(() => (object)table.HeaderRowRange));
                if (headerObj is not null)
                {
                    dynamic headerRange = headerObj;
                    var raw = s.Call(() => (object?)headerRange.Value2);
                    var grid = VbaWorkbookInfoReadRules.ToGrid(raw, options.MaxCellChars);
                    if (grid.Count > 0)
                    {
                        columns.AddRange(grid[0].Where(c => c.Length > 0).Take(VbaWorkbookInfoReadRules.MaxTableColumns));
                    }
                }
            }
            catch (COMException ex) when (VbaComHResult.Classify(ex.HResult) == VbaComFailure.Other)
            {
                // 列名は補足情報。取れなくても表の存在は伝える。
            }

            tables.Add(new VbaTableInfo(name, sheetName, address, columns));
        }
    }

    // ------------------------------------------------------------------
    // 名前付き範囲・参照設定
    // ------------------------------------------------------------------

    private static List<VbaNamedRangeInfo> ReadNames(Session s, object workbook, List<string> omitted)
    {
        var result = new List<VbaNamedRangeInfo>();
        try
        {
            dynamic wb = workbook;
            var namesObj = s.Own(s.Call(() => (object)wb.Names));
            if (namesObj is null) return result;

            dynamic names = namesObj;
            var count = s.Call(() => (int)names.Count);
            for (var i = 1; i <= count; i++)
            {
                if (s.IsAbandoned) break;
                if (result.Count >= VbaWorkbookInfoReadRules.MaxNames)
                {
                    omitted.Add($"名前付き範囲が多いため、先頭の {VbaWorkbookInfoReadRules.MaxNames} 件だけを読みました。");
                    break;
                }

                using var item = new ComScope();
                var nameObj = item.Own(s.Call(() => (object)names.Item(i)));
                if (nameObj is null) continue;

                dynamic name = nameObj;
                // 非表示の名前は Excel 自身が内部で使うもの。利用者が作った名前ではないので除く。
                if (!s.Call(() => (bool)name.Visible)) continue;

                var raw = s.Call(() => (string)name.Name);
                if (VbaWorkbookInfoReadRules.IsInternalName(raw)) continue;

                var refersTo = s.Call(() => (string)name.RefersTo);
                // シート限定の名前は "Sheet1!税率" の形で返るので、Parent を引かずに名前から分ける。
                var (scope, bare) = VbaWorkbookInfoReadRules.SplitName(raw);
                result.Add(new VbaNamedRangeInfo(bare, refersTo, scope));
            }
        }
        catch (COMException ex) when (VbaComHResult.Classify(ex.HResult) == VbaComFailure.Other)
        {
            // 途中で失敗したときは、読めた分を返している。「読めませんでした」と言うと、一覧にある名前まで無いように聞こえる。
            omitted.Add(VbaWorkbookInfoReadRules.OmittedNamesMessage(result.Count, ex.HResult));
        }

        return result;
    }

    private static void ReadReferences(Session s, object workbook, List<VbaReferenceInfo> references, List<string> omitted)
    {
        try
        {
            dynamic wb = workbook;
            var vbp = s.Own(s.Call(() => (object)wb.VBProject));
            if (vbp is null)
            {
                omitted.Add(VbaWorkbookInfoReadRules.OmittedReferencesMessage);
                return;
            }

            dynamic project = vbp;
            // ロック中（E803）でも References は読めない見込み（実機で要確認。付録B）。読めなければ省く。
            if (VbaProjectState.IsLocked(s.Call(() => (int)project.Protection)))
            {
                omitted.Add(VbaWorkbookInfoReadRules.OmittedReferencesMessage);
                return;
            }

            var refsObj = s.Own(s.Call(() => (object)project.References));
            if (refsObj is null) return;

            dynamic refs = refsObj;
            var count = s.Call(() => (int)refs.Count);
            for (var i = 1; i <= count; i++)
            {
                if (s.IsAbandoned) break;
                if (references.Count >= VbaWorkbookInfoReadRules.MaxReferences)
                {
                    omitted.Add($"参照設定が多いため、先頭の {VbaWorkbookInfoReadRules.MaxReferences} 件だけを読みました。");
                    break;
                }

                using var item = new ComScope();
                var refObj = item.Own(s.Call(() => (object)refs.Item(i)));
                if (refObj is null) continue;

                dynamic reference = refObj;
                var name = s.Call(() => (string)reference.Name);
                var description = SafeString(() => s.Call(() => (string)reference.Description));
                var builtIn = s.Call(() => (bool)reference.BuiltIn);
                var broken = SafeBool(() => s.Call(() => (bool)reference.IsBroken));
                // 参照切れの FullPath は取れないことがある。取れなければ null（パスなし）。
                var fullPath = SafeString(() => s.Call(() => (string)reference.FullPath));
                references.Add(new VbaReferenceInfo(name, description ?? string.Empty, fullPath, builtIn, broken));
            }
        }
        catch (COMException)
        {
            // トラストセンターがオフ（E802）・ロック・その他。参照設定だけを省き、シート・テーブル・名前は返す。
            omitted.Add(VbaWorkbookInfoReadRules.OmittedReferencesMessage);
        }
    }

    private static string? SafeString(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static bool SafeBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (COMException)
        {
            return false;
        }
    }
}
