namespace Graft.Core.Vba;

/// <summary>
/// ブックの情報（機能 A。仕様書_ブックの情報と依頼の観点 4.3・5.5）を読むときの指示。
/// すべて「読み取り方」だけを表す値で、COM のオブジェクトは持たない。
/// </summary>
/// <param name="IncludeReferences">参照設定（<c>VBProject.References</c>）を読むか。</param>
/// <param name="Views">
/// シートごとの見せ方（v0.4）。<b>セルの値を読むのは、値が渡る見せ方（見出し＋N 行・全部・範囲を指定）のシートだけ</b>
/// （構成だけ・出さないのシートでは、セルを読む COM 呼び出しを 1 回も行わない。社外秘を Graft のメモリにも載せないため）。
/// 「出さない」のシートは、テーブルも読まない（出力に出ないので）。
/// </param>
/// <param name="MaxColumns">「見出し＋N 行」の最大列数。</param>
/// <param name="MaxCellChars">1 セルの最大文字数（超えたら <c>…</c> つきで切る）。</param>
/// <param name="MaxSheets">読むシートの最大数。</param>
/// <param name="Budget">
/// シートの合間に見る「ここまでで打ち切る」時間。<c>Vba.CallTimeoutMs</c> の 2/3 を渡す。
/// 呼び出し全体の時間切れ（E805）より手前で、読めた分だけを返すための余裕。
/// </param>
/// <param name="AllMaxRows">「全部」「範囲を指定」で 1 シートから読む最大の行数（<c>Vba.WorkbookInfoAllMaxRows</c>）。</param>
/// <param name="AllMaxColumns">「全部」「範囲を指定」で 1 シートから読む最大の列数（<c>Vba.WorkbookInfoAllMaxColumns</c>）。</param>
public sealed record VbaWorkbookInfoOptions(
    bool IncludeReferences, VbaSheetViewSet Views,
    int MaxColumns, int MaxCellChars, int MaxSheets, TimeSpan Budget,
    int AllMaxRows = VbaSheetViewLimits.DefaultAllMaxRows,
    int AllMaxColumns = VbaSheetViewLimits.DefaultAllMaxColumns);

/// <summary>シートの表示状態（<c>Worksheet.Visible</c>）。</summary>
public enum VbaSheetVisibility
{
    /// <summary>表示（<c>xlSheetVisible</c> = -1）。</summary>
    Visible,

    /// <summary>非表示（<c>xlSheetHidden</c> = 0。利用者が「再表示」で戻せる）。</summary>
    Hidden,

    /// <summary>VBE からしか戻せない非表示（<c>xlSheetVeryHidden</c> = 2）。</summary>
    VeryHidden,
}

/// <summary>
/// ブックの情報の読み取り結果。<b>メモリにだけ持ち、ディスクへは書かない</b>
/// （値の見本が社外秘を含みうるため。仕様書付録A-6）。
/// </summary>
/// <param name="WorkbookName">ブックのファイル名。</param>
/// <param name="ReadAt">読み取った日時。</param>
/// <param name="Sheets">シート（グラフシートは含まない）。</param>
/// <param name="Tables">テーブル（ListObject）。</param>
/// <param name="Names">名前付き範囲。</param>
/// <param name="References">参照設定。読めなかったときは空で、理由は <paramref name="Omitted"/> にある。</param>
/// <param name="Omitted">上限・時間切れ・権限で省いたものの説明（警告 E815 の元）。</param>
public sealed record VbaWorkbookInfoSnapshot(
    string WorkbookName, DateTimeOffset ReadAt,
    IReadOnlyList<VbaSheetInfo> Sheets, IReadOnlyList<VbaTableInfo> Tables,
    IReadOnlyList<VbaNamedRangeInfo> Names, IReadOnlyList<VbaReferenceInfo> References,
    IReadOnlyList<string> Omitted);

/// <summary>シート 1 枚。</summary>
/// <param name="Name">表示名。</param>
/// <param name="CodeName">コード名（<c>Sheet1</c> など。ドキュメントモジュールのファイル名と対応する）。</param>
/// <param name="Visibility">表示状態。</param>
/// <param name="UsedRange">使用範囲（<c>A1:F120</c>）。空のシートは null。</param>
/// <param name="HeaderRow">見出し＋N 行のシートの、使用範囲の先頭行（最大 <c>MaxColumns</c> 列）。読んでいなければ空。</param>
/// <param name="SampleRows">見出し＋N 行のシートの、見出しに続く N 行の値。読んでいなければ空。</param>
/// <param name="OmittedColumns">
/// <c>MaxColumns</c> を超えて切った右側の列数（「…（他 N 列）」の N。見出しの右側を切ったことを AI に伝えるために持つ）。
/// </param>
/// <param name="Block">「全部」「範囲を指定」で読んだ値のかたまり。読んでいなければ null。</param>
public sealed record VbaSheetInfo(
    string Name, string CodeName, VbaSheetVisibility Visibility,
    string? UsedRange,
    IReadOnlyList<string> HeaderRow,
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    int OmittedColumns = 0,
    VbaSheetBlock? Block = null);

/// <summary>
/// 「全部」「範囲を指定」で読んだ、1 枚のシートの値のかたまり（<b>メモリにだけ</b>持つ）。
/// 切った（上限で読まなかった）ことを AI に伝えられるよう、切る前の行数・列数も持つ。
/// </summary>
/// <param name="Mode"><see cref="VbaSheetViewMode.All"/> か <see cref="VbaSheetViewMode.Range"/>。</param>
/// <param name="Requested">
/// 指定された範囲（<see cref="VbaSheetViewMode.Range"/> のときだけ。正規化した <c>A1:F30</c>）。
/// 利用者が範囲を変えたあとに、古い取得結果の別の範囲が出てしまわないよう、出力の直前に突き合わせる。
/// </param>
/// <param name="StartRow">読んだ範囲の先頭行（1 始まり）。</param>
/// <param name="StartColumn">読んだ範囲の先頭列（1 始まり）。</param>
/// <param name="Rows">読んだ値（行×列。セル内の改行は空白に畳み済み。数式を含めるときは <c>=式 → 値</c>）。</param>
/// <param name="TotalRows">切る前の（対象の範囲の）行数。<see cref="Rows"/> の行数より多ければ、行を切っている。</param>
/// <param name="TotalColumns">切る前の列数。</param>
/// <param name="OutsideUsedRange">指定の範囲が使用範囲に全く掛からず、読む所が無かった。</param>
public sealed record VbaSheetBlock(
    VbaSheetViewMode Mode, string? Requested, int StartRow, int StartColumn,
    IReadOnlyList<IReadOnlyList<string>> Rows, int TotalRows, int TotalColumns, bool OutsideUsedRange = false)
{
    /// <summary>読んだ列数。</summary>
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);

    /// <summary>行を上限で切ったか。</summary>
    public bool RowsCut => Rows.Count < TotalRows;

    /// <summary>列を上限で切ったか。</summary>
    public bool ColumnsCut => ColumnCount < TotalColumns;

    /// <summary>実際に読んだ範囲（<c>A1:F30</c>）。何も読んでいなければ null。</summary>
    public string? Address => Rows.Count == 0 || ColumnCount == 0
        ? null
        : new VbaA1Range(StartRow, StartColumn, StartRow + Rows.Count - 1, StartColumn + ColumnCount - 1).ToA1();
}

/// <summary>テーブル（ListObject）1 つ。</summary>
public sealed record VbaTableInfo(string Name, string SheetName, string Range, IReadOnlyList<string> Columns);

/// <summary>名前付き範囲 1 つ。<paramref name="Scope"/> はブック全体なら null、シート限定ならシート名。</summary>
public sealed record VbaNamedRangeInfo(string Name, string RefersTo, string? Scope);

/// <summary>参照設定 1 つ（読み取りのみ）。</summary>
public sealed record VbaReferenceInfo(string Name, string Description, string? FullPath, bool IsBuiltIn, bool IsBroken);

/// <summary>
/// ブックの情報の出し方（3 状態。<c>ContextFileState</c> と同じ意味・同じ巡回順）。
/// <c>VbaLink.WorkbookInfoState</c> には名前の文字列で保存する。
/// </summary>
public enum VbaWorkbookInfoLevel
{
    /// <summary>内容も出す（既定）。シート・テーブル・名前・参照設定と、シートごとの見せ方（<see cref="VbaSheetView"/>）で選んだ値。</summary>
    Full,

    /// <summary>構成だけ。シート・テーブル・名前付き範囲だけで、参照設定・シートの値は出さない（「出さない」のシートは守る）。</summary>
    StructureOnly,

    /// <summary>出さない。Excel にも問い合わせない。</summary>
    Hidden,
}

/// <summary><see cref="VbaWorkbookInfoLevel"/> の文字列との往復。</summary>
public static class VbaWorkbookInfoLevels
{
    /// <summary>
    /// 保存された文字列を読む。null・空・不明な値は <see cref="VbaWorkbookInfoLevel.Full"/>
    /// （<c>ProjectOverrides.ContextFileStates</c> の不明な値を既定に倒すのと同じ。壊れた値で機能が消えないように）。
    /// </summary>
    public static VbaWorkbookInfoLevel Parse(string? text)
        => Enum.TryParse<VbaWorkbookInfoLevel>(text, ignoreCase: false, out var level) && Enum.IsDefined(level)
            ? level
            : VbaWorkbookInfoLevel.Full;

    /// <summary>クリックで巡回する次の状態（内容も出す → 構成だけ → 出さない → 内容も出す）。</summary>
    public static VbaWorkbookInfoLevel Next(VbaWorkbookInfoLevel level) => level switch
    {
        VbaWorkbookInfoLevel.Full => VbaWorkbookInfoLevel.StructureOnly,
        VbaWorkbookInfoLevel.StructureOnly => VbaWorkbookInfoLevel.Hidden,
        _ => VbaWorkbookInfoLevel.Full,
    };
}
