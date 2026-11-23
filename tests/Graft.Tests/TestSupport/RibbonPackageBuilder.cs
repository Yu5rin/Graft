using System.IO.Compression;
using System.Text;

namespace Graft.Tests.TestSupport;

/// <summary>
/// リボン（customUI）の読み取りのテスト用に、小さな .xlsm 相当の zip（Open XML パッケージ）をその場で組み立てる。
/// 本物の Excel のブックは使わない（CI で用意できず、中身の食い違いを意図して作りたいため）。
/// <c>tests/Graft.UiTests</c> にもリンクして使う（同じ組み立てを 2 か所に書かない）。
/// </summary>
public static class RibbonPackageBuilder
{
    /// <summary>新しい形式（customUI14.xml）の関係の種類。</summary>
    public const string RelNew = "http://schemas.microsoft.com/office/2007/relationships/ui/extensibility";

    /// <summary>古い形式（customUI.xml）の関係の種類。</summary>
    public const string RelOld = "http://schemas.microsoft.com/office/2006/relationships/ui/extensibility";

    private const string RelsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>テストで読ませる、典型的なリボンの XML（タブ 1・グループ 1・コントロール 3 つ。コールバック 5 種）。</summary>
    public const string SampleRibbon =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
        + "<customUI xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\" onLoad=\"RibbonLoaded\">\n"
        + "  <ribbon>\n"
        + "    <tabs>\n"
        + "      <tab id=\"tabMain\" label=\"業務\">\n"
        + "        <group id=\"grpA\" label=\"操作\">\n"
        + "          <button id=\"btnRun\" label=\"実行\" size=\"large\" imageMso=\"HappyFace\" onAction=\"OnRun\" getEnabled=\"GetEnabled\"/>\n"
        + "          <button idMso=\"Copy\" onAction=\"Module1.OnCopy\"/>\n"
        + "          <dropDown id=\"ddKind\" label=\"種類\" onAction=\"OnPick\" getItemCount=\"CountItems\">\n"
        + "            <item id=\"i1\" label=\"A\"/>\n"
        + "          </dropDown>\n"
        + "        </group>\n"
        + "      </tab>\n"
        + "    </tabs>\n"
        + "  </ribbon>\n"
        + "</customUI>\n";

    /// <summary>zip を組み立てる。<paramref name="entries"/> は（部品名, 中身）。</summary>
    public static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var output = entry.Open();
                output.Write(data, 0, data.Length);
            }
        }

        return stream.ToArray();
    }

    /// <summary>関係の一覧（<c>_rels/.rels</c>）の XML。<paramref name="relationships"/> は（種類, Target）。</summary>
    public static string Rels(params (string Type, string Target)[] relationships)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        sb.Append("<Relationships xmlns=\"").Append(RelsNamespace).Append("\">\n");
        sb.Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>\n");
        var id = 2;
        foreach (var (type, target) in relationships)
            sb.Append("<Relationship Id=\"rId").Append(id++).Append("\" Type=\"").Append(type).Append("\" Target=\"").Append(target).Append("\"/>\n");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    /// <summary>
    /// 小さな .xlsm 相当のパッケージ。<paramref name="customUi14"/> があれば <c>customUI/customUI14.xml</c>、
    /// <paramref name="customUiOld"/> があれば <c>customUI/customUI.xml</c> を、それぞれの関係の種類で足す。
    /// </summary>
    public static byte[] Workbook(string? customUi14 = null, string? customUiOld = null)
    {
        var rels = new List<(string, string)>();
        var entries = new List<(string, byte[])>
        {
            ("[Content_Types].xml", Utf8("<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>")),
            ("xl/workbook.xml", Utf8("<workbook/>")),
        };
        if (customUi14 is not null)
        {
            rels.Add((RelNew, "customUI/customUI14.xml"));
            entries.Add(("customUI/customUI14.xml", Utf8(customUi14)));
        }

        if (customUiOld is not null)
        {
            rels.Add((RelOld, "customUI/customUI.xml"));
            entries.Add(("customUI/customUI.xml", Utf8(customUiOld)));
        }

        entries.Add(("_rels/.rels", Utf8(Rels(rels.ToArray()))));
        return Zip(entries.ToArray());
    }

    /// <summary>UTF-8（BOM なし）のバイト列。</summary>
    public static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
}
