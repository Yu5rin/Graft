namespace Graft.Core.Vba;

/// <summary>
/// customUI の形式（仕様書_ブックの情報と依頼の観点 16.3）。ミラーのファイル名・名前空間・関係の種類・既定の部品名が、
/// 形式ごとに 1 つに決まる。
/// </summary>
public enum VbaRibbonFormat
{
    /// <summary>
    /// 新しい形式（Office 2010 以降）。ミラーは <c>CustomUI/customUI14.xml</c>、名前空間は
    /// <c>http://schemas.microsoft.com/office/2009/07/customui</c>、関係の種類は <c>…/2007/relationships/ui/extensibility</c>。
    /// </summary>
    Office2010,

    /// <summary>
    /// 古い形式（Office 2007）。ミラーは <c>CustomUI/customUI.xml</c>、名前空間は
    /// <c>http://schemas.microsoft.com/office/2006/01/customui</c>、関係の種類は <c>…/2006/relationships/ui/extensibility</c>。
    /// </summary>
    Office2007,
}

/// <summary>ミラーのリボンのファイル 1 つを読み解いた結果。</summary>
/// <param name="Format">形式。</param>
/// <param name="RelativePath">正規の相対パス（<c>CustomUI/customUI14.xml</c>。区切りは <c>/</c>、綴りは正規の大文字小文字）。</param>
public sealed record VbaRibbonMirrorPath(VbaRibbonFormat Format, string RelativePath);

/// <summary>
/// ミラーのリボンのファイル（<c>CustomUI/customUI14.xml</c>・<c>CustomUI/customUI.xml</c>）のパスの解釈と、
/// 形式ごとの定数（仕様書_ブックの情報と依頼の観点 16.3）。
/// <para>
/// 【ファイル名を形式で固定する理由】 パッケージの中の部品名は、関係の <c>Target</c> しだいで <c>customUI/ribbon.xml</c> のような
/// 別名になりうる。部品名をそのままミラーのファイル名にすると、どちらの形式かが中身を読むまで分からず、同じ形式のファイルが
/// 2 つ並ぶこともありうる。ミラー側は形式ごとに固定の 2 つの名前にし、実際の部品名は同期状態に持つ（付録A-32）。
/// </para>
/// <para>
/// 【<see cref="VbaMirrorLayout"/> と分けた理由】 <see cref="VbaModuleKind"/> は COM の <c>VBComponent.Type</c> と 1 対 1 で、
/// CP932 の検査や名前の規則まで「VBE の中のコード」を前提にしている。リボンは相手がファイルで、書き方も検査も違うので、
/// 種別を足して既存の全経路に分岐を入れるより、小さな専用の部品に分ける（付録A-31）。
/// </para>
/// <para>
/// 【大文字小文字を区別しない理由】 Windows のファイルシステムは大文字小文字を区別せず、<see cref="VbaMirrorLayout"/> も同じ扱い。
/// <c>customui/CUSTOMUI14.XML</c> を別物にすると、同じ部品を二重に同期してしまう。解釈の結果は常に正規の綴りに直す。
/// </para>
/// </summary>
public static class VbaRibbonMirrorLayout
{
    /// <summary>ミラーの中のリボンのフォルダ名（利用者の指定どおり。パッケージの中は <c>customUI/</c>）。</summary>
    public const string FolderName = "CustomUI";

    /// <summary>新しい形式のファイル名。</summary>
    public const string Office2010FileName = "customUI14.xml";

    /// <summary>古い形式のファイル名。</summary>
    public const string Office2007FileName = "customUI.xml";

    /// <summary>新しい形式（2009/07）の名前空間。</summary>
    public const string Office2010Namespace = "http://schemas.microsoft.com/office/2009/07/customui";

    /// <summary>古い形式（2006/01）の名前空間。</summary>
    public const string Office2007Namespace = "http://schemas.microsoft.com/office/2006/01/customui";

    /// <summary>
    /// <c>CustomUI/</c> の中の、リボンの 2 ファイル以外に付ける理由（E810 の詳細）。画像を置いても反映されないことを、ここで伝える
    /// （仕様書 16.3「E810 との関係」）。
    /// </summary>
    public const string OtherFileReason = "CustomUI/ に置けるのは customUI14.xml と customUI.xml だけです（画像は扱いません）。";

    /// <summary>形式ごとのミラーの正規の相対パス（<c>CustomUI/customUI14.xml</c>）。</summary>
    public static string ToRelativePath(VbaRibbonFormat format) => FolderName + "/" + FileName(format);

    /// <summary>形式ごとのミラーのファイル名。</summary>
    public static string FileName(VbaRibbonFormat format)
        => format == VbaRibbonFormat.Office2010 ? Office2010FileName : Office2007FileName;

    /// <summary>形式ごとの、customUI のルート要素の名前空間。</summary>
    public static string Namespace(VbaRibbonFormat format)
        => format == VbaRibbonFormat.Office2010 ? Office2010Namespace : Office2007Namespace;

    /// <summary>形式ごとの、パッケージの関係（<c>_rels/.rels</c>）の種類。読み取り（15.2）と同じ値。</summary>
    public static string RelationshipType(VbaRibbonFormat format)
        => format == VbaRibbonFormat.Office2010 ? VbaRibbonPackageReader.RelationshipTypeNew : VbaRibbonPackageReader.RelationshipTypeOld;

    /// <summary>
    /// 新規に足すときの部品名（<c>customUI/customUI14.xml</c>。先頭の <c>/</c> は付けない）。Custom UI Editor や Office の
    /// 慣例と同じ名前にしておくと、利用者が別のツールで開いたときにも迷わない。
    /// </summary>
    public static string DefaultPartName(VbaRibbonFormat format) => "customUI/" + FileName(format);

    /// <summary>読み取り結果の部品（<see cref="VbaRibbonRawPart.IsOffice2010Format"/>）から形式を決める。</summary>
    public static VbaRibbonFormat FormatOf(VbaRibbonRawPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return part.IsOffice2010Format ? VbaRibbonFormat.Office2010 : VbaRibbonFormat.Office2007;
    }

    /// <summary>
    /// ミラーの相対パスが、リボンの 2 ファイルのどちらかなら読み解く。区切りは <c>/</c>・<c>\</c> のどちらも受け付け、先頭の
    /// <c>./</c> は無視する。大文字小文字は区別しない。<c>CustomUI/</c> の中のほかのファイル・入れ子・ミラーの外を指すパスは false。
    /// </summary>
    public static bool TryParse(string? relativePath, out VbaRibbonMirrorPath? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        // ミラーの外（先頭の / や \、ドライブ名、..）を指すものは受け付けない。VbaMirrorLayout.TryParse と同じ判定。
        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\')) return false;
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".").ToArray();
        if (segments.Length != 2 || segments.Contains("..")) return false;
        if (segments[0].Length >= 2 && segments[0][1] == ':') return false;
        if (!string.Equals(segments[0], FolderName, StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var format in Enum.GetValues<VbaRibbonFormat>())
        {
            if (string.Equals(segments[1], FileName(format), StringComparison.OrdinalIgnoreCase))
            {
                result = new VbaRibbonMirrorPath(format, ToRelativePath(format));
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>CustomUI/</c> の中（入れ子を含む）を指すパスか。<see cref="TryParse"/> が通らないものに <see cref="OtherFileReason"/> を
    /// 付けて E810 にするための判定（16.3。段階 F2 で <see cref="VbaMirrorLayout.CollectUnsupported"/> が使う）。
    /// </summary>
    public static bool IsUnderRibbonFolder(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".").ToArray();
        return segments.Length >= 2 && string.Equals(segments[0], FolderName, StringComparison.OrdinalIgnoreCase);
    }
}
