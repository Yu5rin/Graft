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

    /// <summary>
    /// Excel が開ける最小の .xlsm（Open XML として正しいもの。シート 1 枚・VBA プロジェクトなし）。リボンの書き換え（16.7）のテストと、
    /// 実機検証 1〜4 の検証用のブックの元にする。<see cref="Workbook"/> は読み取りのテスト用で <c>[Content_Types].xml</c> が空のため、
    /// Excel では開けない。こちらは <c>[Content_Types].xml</c>・ブック・シート・スタイル・文書のプロパティをそろえる。
    /// <para>
    /// 【項目の並び】 Excel が書くブックと同じく <c>[Content_Types].xml</c> を先頭に置く（パッケージの規則ではないが、ツールによっては
    /// 先頭を前提にするため。書き換えが順序を保つことの確かめにもなる）。<paramref name="customUi14"/>・<paramref name="customUiOld"/> が
    /// あれば、関係と部品を足す（Custom UI Editor と同じ部品名）。
    /// </para>
    /// </summary>
    /// <param name="customUi14">新しい形式の XML（無ければ足さない）。</param>
    /// <param name="customUiOld">古い形式の XML（無ければ足さない）。</param>
    /// <param name="xmlDefault"><c>&lt;Default Extension="xml" ContentType="application/xml"/&gt;</c> を置くか（Excel が書くブックには通常ある）。</param>
    /// <param name="sheetText">A1 に置く文字（検証用のブックで、どのブックかを目で見分けるため）。</param>
    public static byte[] ExcelWorkbook(string? customUi14 = null, string? customUiOld = null, bool xmlDefault = true, string sheetText = "Graft リボン検証")
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string officeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var types = new StringBuilder();
        types.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n");
        types.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        types.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        if (xmlDefault) types.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        types.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.ms-excel.sheet.macroEnabled.main+xml\"/>");
        types.Append("<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        types.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        types.Append("<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>");
        // xml の既定が無いブックでも開けるよう、customUI 以外の .xml 部品にはすべて Override を書いておく。
        types.Append("<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
        types.Append("</Types>");

        var rels = new StringBuilder();
        rels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n");
        rels.Append("<Relationships xmlns=\"").Append(RelsNamespace).Append("\">");
        rels.Append("<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>");
        rels.Append("<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>");
        rels.Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>");
        if (customUi14 is not null) rels.Append("<Relationship Id=\"rId4\" Type=\"").Append(RelNew).Append("\" Target=\"customUI/customUI14.xml\"/>");
        if (customUiOld is not null) rels.Append("<Relationship Id=\"rId5\" Type=\"").Append(RelOld).Append("\" Target=\"customUI/customUI.xml\"/>");
        rels.Append("</Relationships>");

        var entries = new List<(string, byte[])>
        {
            ("[Content_Types].xml", Utf8(types.ToString())),
            ("_rels/.rels", Utf8(rels.ToString())),
            ("docProps/app.xml", Utf8("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Application>Graft test</Application></Properties>")),
            ("docProps/core.xml", Utf8("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:creator>Graft</dc:creator></cp:coreProperties>")),
            ("xl/workbook.xml", Utf8($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<workbook xmlns=\"{main}\" xmlns:r=\"{officeRel}\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>")),
            ("xl/_rels/workbook.xml.rels", Utf8("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Relationships xmlns=\"" + RelsNamespace + "\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
                + "</Relationships>")),
            ("xl/worksheets/sheet1.xml", Utf8($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<worksheet xmlns=\"{main}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(sheetText)}</t></is></c></row></sheetData></worksheet>")),
            ("xl/styles.xml", Utf8($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<styleSheet xmlns=\"{main}\">"
                + "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
                + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
                + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
                + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                + "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>"
                + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>"
                + "</styleSheet>")),
        };
        if (customUi14 is not null) entries.Add(("customUI/customUI14.xml", Utf8(customUi14)));
        if (customUiOld is not null) entries.Add(("customUI/customUI.xml", Utf8(customUiOld)));
        return Zip(entries.ToArray());
    }

    /// <summary>zip の項目を（名前, 中身）の一覧で読む（順序どおり）。</summary>
    public static List<(string Name, byte[] Data)> ReadEntries(byte[] zip)
    {
        using var stream = new MemoryStream(zip);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var result = new List<(string, byte[])>();
        foreach (var entry in archive.Entries)
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            result.Add((entry.FullName, output.ToArray()));
        }

        return result;
    }

    /// <summary>UTF-8（BOM なし）のバイト列。</summary>
    public static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
}
