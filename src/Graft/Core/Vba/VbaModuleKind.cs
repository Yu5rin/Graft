namespace Graft.Core.Vba;

/// <summary>
/// VBA コンポーネントの種別（仕様書_VBA連携 6.1）。ミラーフォルダの置き場所と拡張子、
/// COM の <c>VBComponent.Type</c> の値との対応は <see cref="VbaModuleKinds"/> に集める。
/// </summary>
public enum VbaModuleKind
{
    /// <summary>標準モジュール（vbext_ct_StdModule = 1）。</summary>
    Standard,

    /// <summary>クラスモジュール（vbext_ct_ClassModule = 2）。</summary>
    Class,

    /// <summary>ドキュメントモジュール＝ThisWorkbook と各シート（vbext_ct_Document = 100）。新規作成できない。</summary>
    Document,

    /// <summary>ユーザーフォームのコード部分（vbext_ct_MSForm = 3）。デザイン（.frx）は対象外で、新規作成もできない。</summary>
    Form,
}

/// <summary>
/// <see cref="VbaModuleKind"/> と、COM の Type 値・ミラーのフォルダ名・拡張子の対応表。
/// <para>
/// 【なぜ列挙体の値に COM の値を直接使わないか】 COM の値（1・2・3・100）は飛び飛びで、
/// ActiveX デザイナ（11）のように「同期の対象外」にする値もある。列挙体を COM の値と結びつけると、
/// 対象外の値を表す場所に困る。対応はここの1箇所に集め、未対応の値は null で返して呼び出し側に
/// 「未対応」として扱わせる。
/// </para>
/// </summary>
public static class VbaModuleKinds
{
    /// <summary>
    /// COM の <c>VBComponent.Type</c> の値から種別を求める。同期の対象外の値
    /// （ActiveX デザイナ = 11 など）は null（一覧で「未対応」と表示する）。
    /// </summary>
    public static VbaModuleKind? FromComType(int comType) => comType switch
    {
        1 => VbaModuleKind.Standard,
        2 => VbaModuleKind.Class,
        3 => VbaModuleKind.Form,
        100 => VbaModuleKind.Document,
        _ => null,
    };

    /// <summary>種別に対応する COM の <c>VBComponent.Type</c> の値。</summary>
    public static int ToComType(VbaModuleKind kind) => kind switch
    {
        VbaModuleKind.Standard => 1,
        VbaModuleKind.Class => 2,
        VbaModuleKind.Form => 3,
        VbaModuleKind.Document => 100,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知のモジュール種別です。"),
    };

    /// <summary>ミラー直下のフォルダ名（仕様 4.2）。</summary>
    public static string FolderName(VbaModuleKind kind) => kind switch
    {
        VbaModuleKind.Standard => "Modules",
        VbaModuleKind.Class => "Classes",
        VbaModuleKind.Form => "Forms",
        VbaModuleKind.Document => "Documents",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知のモジュール種別です。"),
    };

    /// <summary>ミラーのファイルの拡張子（ドット付き）。クラスとドキュメントはどちらも <c>.cls</c>。</summary>
    public static string Extension(VbaModuleKind kind) => kind switch
    {
        VbaModuleKind.Standard => ".bas",
        VbaModuleKind.Class => ".cls",
        VbaModuleKind.Form => ".frm",
        VbaModuleKind.Document => ".cls",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知のモジュール種別です。"),
    };

    /// <summary>
    /// Graft が Excel 側に新規作成できる種別か。標準モジュールとクラスモジュールだけ。
    /// ドキュメントモジュール（シート）とフォームは、Excel 側に先に存在していないと作れない（仕様 1.3・6.1）。
    /// </summary>
    public static bool CanCreateOnExcel(VbaModuleKind kind) => kind is VbaModuleKind.Standard or VbaModuleKind.Class;
}
