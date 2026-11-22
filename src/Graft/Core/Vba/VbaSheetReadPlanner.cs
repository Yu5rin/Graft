namespace Graft.Core.Vba;

/// <summary>
/// 1 枚のシートで「どこを読むか」の計画（<see cref="VbaSheetReadPlanner.Plan"/> の結果）。
/// COM に触れない値なので、Windows のブリッジと Fake が同じ計画を使える。
/// </summary>
/// <param name="Mode">見せ方（値を読まないときは <see cref="VbaSheetViewMode.StructureOnly"/> か <see cref="VbaSheetViewMode.None"/>）。</param>
/// <param name="StartRow">読む範囲の先頭行（1 始まり）。<see cref="RowCount"/> が 0 のときは無意味。</param>
/// <param name="StartColumn">読む範囲の先頭列。</param>
/// <param name="RowCount">読む行数。0 なら、セルを読む COM 呼び出しを行わない。</param>
/// <param name="ColumnCount">読む列数。</param>
/// <param name="TotalRows">切る前の行数（「全部」は使用範囲、「範囲を指定」は使用範囲との共通部分）。</param>
/// <param name="TotalColumns">切る前の列数。</param>
/// <param name="Requested">「範囲を指定」で指定された範囲（正規化済み）。</param>
/// <param name="OutsideUsedRange">指定の範囲が使用範囲に掛からなかった。</param>
public sealed record VbaSheetReadPlan(
    VbaSheetViewMode Mode, int StartRow, int StartColumn, int RowCount, int ColumnCount,
    int TotalRows, int TotalColumns, string? Requested, bool OutsideUsedRange)
{
    /// <summary>セルを読むか。</summary>
    public bool ReadsCells => RowCount > 0 && ColumnCount > 0;

    /// <summary>読まない計画。</summary>
    public static VbaSheetReadPlan None(VbaSheetViewMode mode) => new(mode, 1, 1, 0, 0, 0, 0, null, false);
}

/// <summary>
/// シートの見せ方・使用範囲・上限から、読む範囲を決める純ロジック（仕様書_ブックの情報と依頼の観点 5.5）。
/// <para>
/// 【ここへ出す理由】 「全部」の上限（500 行・50 列）や「範囲を指定」と使用範囲の共通部分の計算を、COM を呼ぶ
/// <c>WindowsVbaBridge.Info</c> の中に置くと、Linux の CI で確かめられない。計画だけを純ロジックにして固め、
/// ブリッジは計画のとおりに読むだけにする（Fake も同じ計画を使うので、テストの結果が実機の読み方とずれない）。
/// </para>
/// <para>
/// 【読む前に切る】 上限を超える範囲は、<b>読んでから切るのではなく、読む範囲そのものを小さくする</b>。
/// 100 万行の列を指定されても、Excel から 500 行だけを 1 回で受ける。
/// </para>
/// </summary>
public static class VbaSheetReadPlanner
{
    /// <summary>
    /// 計画を立てる。
    /// </summary>
    /// <param name="used">使用範囲（読めなかった・空のシートは null）。null なら何も読まない。</param>
    /// <param name="view">このシートの見せ方。</param>
    /// <param name="maxColumns">「見出し＋N 行」の最大列数（<c>Vba.WorkbookInfoMaxColumns</c>）。</param>
    /// <param name="allMaxRows">「全部」「範囲を指定」の最大行数。</param>
    /// <param name="allMaxColumns">「全部」「範囲を指定」の最大列数。</param>
    public static VbaSheetReadPlan Plan(VbaA1Range? used, VbaSheetView view, int maxColumns, int allMaxRows, int allMaxColumns)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (used is not { } bounds || !view.PassesValues) return VbaSheetReadPlan.None(view.Mode);

        switch (view.Mode)
        {
            case VbaSheetViewMode.HeaderRows:
            {
                // 見出し 1 行＋続く N 行を、1 回の読み取りで受ける。列は MaxColumns まで（右を切った列数は呼び出し側が数える）。
                var rows = Math.Min(bounds.RowCount, 1 + VbaSheetViewLimits.ClampHeaderRows(view.HeaderRows));
                var cols = Math.Min(bounds.ColumnCount, Math.Max(1, maxColumns));
                return new VbaSheetReadPlan(
                    VbaSheetViewMode.HeaderRows, bounds.Row1, bounds.Column1, rows, cols,
                    bounds.RowCount, bounds.ColumnCount, null, false);
            }

            case VbaSheetViewMode.All:
                return Cap(VbaSheetViewMode.All, bounds, null, allMaxRows, allMaxColumns);

            case VbaSheetViewMode.Range:
            {
                // PassesValues が真なら Range は読める。念のため読めなければ何も読まない。
                if (!VbaA1Range.TryParse(view.Range, out var requested, out _)) return VbaSheetReadPlan.None(view.Mode);
                var inside = requested.Intersect(bounds);
                if (inside is not { } target)
                {
                    // 使用範囲の外（空のセルだけ）。何も読まず、「外だった」ことを伝える。
                    return new VbaSheetReadPlan(VbaSheetViewMode.Range, requested.Row1, requested.Column1, 0, 0, 0, 0, requested.ToA1(), true);
                }

                return Cap(VbaSheetViewMode.Range, target, requested.ToA1(), allMaxRows, allMaxColumns);
            }

            default:
                return VbaSheetReadPlan.None(view.Mode);
        }
    }

    private static VbaSheetReadPlan Cap(
        VbaSheetViewMode mode, VbaA1Range target, string? requested, int allMaxRows, int allMaxColumns)
    {
        var rows = Math.Min(target.RowCount, Math.Max(1, allMaxRows));
        var cols = Math.Min(target.ColumnCount, Math.Max(1, allMaxColumns));
        return new VbaSheetReadPlan(mode, target.Row1, target.Column1, rows, cols, target.RowCount, target.ColumnCount, requested, false);
    }

    /// <summary>
    /// 読んだ値（行×列の文字列。数式を含めるときは <paramref name="valuesWithFormulas"/> に <c>=式 → 値</c> 版）を、
    /// 計画に沿ってシートの情報へ詰める。ブリッジと Fake が同じ詰め方をするための共通部分。
    /// <list type="bullet">
    /// <item>見出し＋N 行: 先頭行は<b>数式なしの値</b>（見出しは数式を出さない。従来どおり）、続く行は数式つきがあればそちら。</item>
    /// <item>全部・範囲を指定: <see cref="VbaSheetBlock"/> にする。</item>
    /// </list>
    /// </summary>
    public static VbaSheetInfo Assemble(
        VbaSheetInfo structure, VbaSheetReadPlan plan,
        IReadOnlyList<IReadOnlyList<string>> values, IReadOnlyList<IReadOnlyList<string>>? valuesWithFormulas)
    {
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(values);

        if (plan.Mode == VbaSheetViewMode.HeaderRows)
        {
            if (!plan.ReadsCells || values.Count == 0) return structure;
            var body = (valuesWithFormulas ?? values).Skip(1).ToArray();
            return structure with
            {
                HeaderRow = values[0],
                SampleRows = body,
                OmittedColumns = Math.Max(0, plan.TotalColumns - plan.ColumnCount),
            };
        }

        if (plan.Mode is VbaSheetViewMode.All or VbaSheetViewMode.Range)
        {
            var rows = plan.ReadsCells ? (valuesWithFormulas ?? values) : Array.Empty<IReadOnlyList<string>>();
            return structure with
            {
                Block = new VbaSheetBlock(
                    plan.Mode, plan.Requested, plan.StartRow, plan.StartColumn, rows,
                    plan.TotalRows, plan.TotalColumns, plan.OutsideUsedRange),
            };
        }

        return structure;
    }
}
