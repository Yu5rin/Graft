namespace Graft.Core.Vba;

/// <summary>
/// 実行中オブジェクトテーブル（ROT）の表示名とブックのパスに関する純ロジック（仕様書_VBA連携 5.2）。
/// COM に触れる部分（<c>WindowsVbaBridge</c>）から、判定だけをここへ切り出して Linux の CI で検証する。
/// <para>
/// 【なぜ表示名で先に絞るか】 ROT には Excel 以外のオブジェクト（Word の文書、各種サービスのモニカ）も
/// 大量に登録されている。表示名（ファイルモニカならフルパス）が Excel のブックの拡張子で終わるものだけを
/// 候補にすれば、無関係なオブジェクトに <c>GetObject</c> を呼ばずに済み、COM の呼び出しが減る。
/// </para>
/// </summary>
public static class VbaRotNames
{
    // 仕様 5.2: .xlsm / .xlsb / .xlam / .xls / .xlsx で終わるものを候補にする。
    private static readonly string[] WorkbookExtensions = { ".xlsm", ".xlsb", ".xlam", ".xls", ".xlsx" };

    // マクロ（VBA プロジェクト）を保存できない形式（仕様 5.4 の E806）。.xls は旧形式だがマクロを持てる。
    private static readonly string[] NoMacroExtensions = { ".xlsx", ".xltx" };

    /// <summary>ROT の表示名がブックのファイル名（拡張子）で終わっているか。</summary>
    public static bool IsWorkbookDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        var name = displayName.Trim();
        return WorkbookExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ファイル名が、VBA のプロジェクトにできるブック（.xlsm／.xlsb／.xlam／.xls）か。プロジェクト一覧へのドラッグ＆ドロップで、
    /// ブックを親フォルダ登録ではなく VBA のプロジェクトとして扱うかの判定に使う。「Excel ブックから追加」の一覧の候補と同じ拡張子で、
    /// マクロを保存できない形式（.xlsx）は含まない。
    /// </summary>
    public static bool IsVbaWorkbookFile(string? path) => IsWorkbookDisplayName(path) && !IsNoMacroFormat(path);

    /// <summary>ファイル名が、マクロを保存できないブック（.xlsx）か。落とされても VBA のプロジェクトにできないと知らせるための判定。</summary>
    public static bool IsNoMacroWorkbookFile(string? path) => IsWorkbookDisplayName(path) && IsNoMacroFormat(path);

    /// <summary>
    /// <c>FullName</c> が URL か（OneDrive／SharePoint 上のブック。仕様 5.2 により非対応）。
    /// </summary>
    public static bool IsUrlLocation(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return false;
        var name = fullName.TrimStart();
        return name.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>マクロを保存できない形式（.xlsx など）か。</summary>
    public static bool IsNoMacroFormat(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return false;
        var name = fullName.Trim();
        return NoMacroExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 2つのブックのパスが同じものを指すか。大文字小文字を区別せず、<c>.</c>／<c>..</c> や区切り文字の
    /// 違いを正規化して比べる。UNC パスとドライブ文字の違いは解決しない（同じブックを別パスで開いた場合は
    /// 別物として扱う。この制限は取扱説明書に書く。仕様 5.2）。
    /// </summary>
    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(VbaMirrorFolderName.NormalizePath(a), VbaMirrorFolderName.NormalizePath(b), StringComparison.Ordinal);
    }

    /// <summary>
    /// ROT の 1 項目が、<b>いま開いているブック</b>を指しているか（<paramref name="applicationWorkbookNames"/> は、その項目から辿った
    /// Excel の <c>Application.Workbooks</c>〔と保護ビューのウィンドウ〕にある各ブックのフルパス。項目のオブジェクトが死んでいて
    /// 辿れなかったときは null）。
    /// <para>
    /// 【なぜ ROT の表示名だけで「開いている」と決めないか】 実機（2026-11-24、Microsoft 365 Excel 16.0 ビルド 20625・64bit）で、
    /// <c>Workbook.Close</c> のあと 10 秒たっても、ブックが ROT に残り続けることを 5 回とも確かめた（Windows実機検証手順 6-34 の 5-b）。
    /// ROT の列挙だけで開いているかを決めると、閉じたブックを「開いている」と誤判定する（書き換えを E819 で止める・バッジが誤る・
    /// 閉じたあとの確認で「開いたまま」と止める・開き直しのあとの確認で「開いた」と成功扱いにする）。ROT は「候補を見つける」ためだけに使い、
    /// 開いているかは、その候補が属する Excel の <c>Workbooks</c> に同じフルパスのブックが実在するかで決める。
    /// </para>
    /// </summary>
    public static bool IsLiveEntry(IEnumerable<string?>? applicationWorkbookNames, string workbookFullName)
    {
        if (applicationWorkbookNames is null) return false;
        return applicationWorkbookNames.Any(name => SamePath(name, workbookFullName));
    }

    /// <summary>
    /// ROT の表示名の並びから、紐付けたブックと同じパスのものの添字を返す（複数あれば全部。
    /// 同じパスのブックを2つの Excel プロセスで開いた場合に、読み取り専用側の判断を呼び出し側ができるように）。
    /// </summary>
    public static IReadOnlyList<int> FindMatches(IReadOnlyList<string?> displayNames, string workbookFullName)
    {
        ArgumentNullException.ThrowIfNull(displayNames);
        var result = new List<int>();
        for (var i = 0; i < displayNames.Count; i++)
        {
            var name = displayNames[i];
            if (IsWorkbookDisplayName(name) && SamePath(name, workbookFullName)) result.Add(i);
        }

        return result;
    }
}
