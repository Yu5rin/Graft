namespace Graft.Core.Vba;

/// <summary>
/// ブックの情報（機能 A。仕様書_ブックの情報と依頼の観点 4.3）を読むときの指示。
/// すべて「読み取り方」だけを表す値で、COM のオブジェクトは持たない。
/// </summary>
/// <param name="IncludeReferences">参照設定（<c>VBProject.References</c>）を読むか。</param>
/// <param name="SampleRows">値の見本の行数。<b>0 なら値の見本を読む COM 呼び出しを一切行わない</b>
/// （既定オフのオプトイン。社外秘を Graft のメモリにも載せないため）。</param>
/// <param name="IncludeFormulas">値の見本に数式も含めるか（<paramref name="SampleRows"/> が 0 のときは無意味）。</param>
/// <param name="MaxColumns">見出し・見本の最大列数。</param>
/// <param name="MaxCellChars">1 セルの最大文字数（超えたら <c>…</c> つきで切る）。</param>
/// <param name="MaxSheets">読むシートの最大数。</param>
/// <param name="Budget">
/// シートの合間に見る「ここまでで打ち切る」時間。<c>Vba.CallTimeoutMs</c> の 2/3 を渡す。
/// 呼び出し全体の時間切れ（E805）より手前で、読めた分だけを返すための余裕。
/// </param>
public sealed record VbaWorkbookInfoOptions(
    bool IncludeReferences, int SampleRows, bool IncludeFormulas,
    int MaxColumns, int MaxCellChars, int MaxSheets, TimeSpan Budget);

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
/// <param name="HeaderRow">使用範囲の先頭行（最大 <c>MaxColumns</c> 列）。</param>
/// <param name="SampleRows">値の見本。読んでいなければ空。</param>
/// <param name="OmittedColumns">
/// <c>MaxColumns</c> を超えて切った右側の列数（仕様書のレコードに無い追加項目。「…（他 N 列）」の N。
/// 既定 0。見出しの右側を切ったことを AI に伝えるために持つ）。
/// </param>
public sealed record VbaSheetInfo(
    string Name, string CodeName, VbaSheetVisibility Visibility,
    string? UsedRange,
    IReadOnlyList<string> HeaderRow,
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    int OmittedColumns = 0);

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
    /// <summary>内容も出す（既定）。シート・テーブル・名前・先頭行の見出し・参照設定（値の見本はオプトインのときだけ）。</summary>
    Full,

    /// <summary>構成だけ。シート・テーブル・名前付き範囲だけで、見出し・参照設定・値の見本は出さない。</summary>
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
