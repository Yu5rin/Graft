using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaRibbonPackageWriter"/>（ブックの customUI の部品の差し替え・追加と、原子的な置き換え）のテスト。
/// 仕様書_ブックの情報と依頼の観点 16.7・16.15。小さな .xlsm をテストの中で組み立てて書き換え、読み取り（<see cref="VbaRibbonPackageReader"/>）
/// で往復することまで確かめる。
/// </summary>
public class VbaRibbonPackageWriterTests
{
    private const string Ns14 = "http://schemas.microsoft.com/office/2009/07/customui";
    private const string Ns12 = "http://schemas.microsoft.com/office/2006/01/customui";

    private static string Ribbon(string label, string ns = Ns14)
        => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n"
           + $"<customUI xmlns=\"{ns}\"><ribbon><tabs><tab id=\"tabG\" label=\"{label}\"><group id=\"grpG\" label=\"G\">"
           + "<button id=\"btnG\" label=\"押す\" size=\"large\" imageMso=\"HappyFace\" onAction=\"OnG\"/></group></tab></tabs></ribbon></customUI>\r\n";

    private static (VbaRibbonWriteResult Result, byte[] Output) Write(byte[] source, params VbaRibbonWriteRequest[] requests)
    {
        using var input = new MemoryStream(source, writable: false);
        using var output = new MemoryStream();
        var result = VbaRibbonPackageWriter.Write(input, output, requests);
        return (result, output.ToArray());
    }

    private static VbaRibbonReadResult Read(byte[] zip)
    {
        using var stream = new MemoryStream(zip);
        return VbaRibbonPackageReader.ReadFromStream(stream);
    }

    private static byte[] Entry(byte[] zip, string name)
        => RibbonPackageBuilder.ReadEntries(zip).Single(e => e.Name == name).Data;

    [Fact(DisplayName = "既存の customUI14 の差し替え: _rels/.rels と [Content_Types].xml は 1 バイトも変わらず、読み取りで新しい本文に戻る")]
    public void 既存の差し替え()
    {
        var source = RibbonPackageBuilder.ExcelWorkbook(customUi14: Ribbon("前"));
        var after = Ribbon("後");

        var (result, output) = Write(source, new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, after));

        result.Succeeded.Should().BeTrue(result.Problem);
        result.RelationshipsChanged.Should().BeFalse();
        result.ContentTypesChanged.Should().BeFalse();
        result.Parts.Should().ContainSingle().Which.Should().Be(new VbaRibbonWrittenPart(VbaRibbonFormat.Office2010, "customUI/customUI14.xml", false));
        Entry(output, "_rels/.rels").Should().Equal(Entry(source, "_rels/.rels"));
        Entry(output, "[Content_Types].xml").Should().Equal(Entry(source, "[Content_Types].xml"));
        var read = Read(output);
        read.Status.Should().Be(VbaRibbonReadStatus.Ok);
        read.Parts.Should().ContainSingle().Which.Xml.Should().Be(after);
    }

    [Fact(DisplayName = "customUI の無いブックへの追加: 関係を 1 つ足し（Id は重ならない）、xml の既定があれば [Content_Types].xml は変えない")]
    public void 新規の追加()
    {
        var source = RibbonPackageBuilder.ExcelWorkbook();
        var xml = Ribbon("新規");

        var (result, output) = Write(source, new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, xml));

        result.Succeeded.Should().BeTrue(result.Problem);
        result.RelationshipsChanged.Should().BeTrue();
        result.ContentTypesChanged.Should().BeFalse("<Default Extension=\"xml\" ContentType=\"application/xml\"/> があるので足さない");
        result.Parts.Should().ContainSingle().Which.AddedRelationship.Should().BeTrue();
        var rels = Encoding.UTF8.GetString(Entry(output, "_rels/.rels"));
        var before = Encoding.UTF8.GetString(Entry(source, "_rels/.rels"));
        rels.Should().Be(before.Replace("</Relationships>",
            "<Relationship Id=\"rIdGraft1\" Type=\"http://schemas.microsoft.com/office/2007/relationships/ui/extensibility\" Target=\"customUI/customUI14.xml\"/></Relationships>"),
            "既存の関係は順序も属性もそのまま、閉じタグの直前に 1 つ差し込むだけ");
        Entry(output, "[Content_Types].xml").Should().Equal(Entry(source, "[Content_Types].xml"));
        RibbonPackageBuilder.ReadEntries(output).Last().Name.Should().Be("customUI/customUI14.xml", "足す部品は最後に置く");
        Read(output).Parts.Should().ContainSingle().Which.Xml.Should().Be(xml);
    }

    [Fact(DisplayName = "xml の既定が無いブックへの追加: その部品の Override を足す")]
    public void Overrideを足す()
    {
        var source = RibbonPackageBuilder.ExcelWorkbook(xmlDefault: false);

        var (result, output) = Write(source, new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("x")));

        result.ContentTypesChanged.Should().BeTrue();
        Encoding.UTF8.GetString(Entry(output, "[Content_Types].xml"))
            .Should().EndWith("<Override PartName=\"/customUI/customUI14.xml\" ContentType=\"application/xml\"/></Types>");
    }

    [Fact(DisplayName = "関係の Id が既に rIdGraft1 なら rIdGraft2 を使う")]
    public void Idが重ならない()
    {
        var rels = RibbonPackageBuilder.Rels(("http://example.com/other", "x.xml")).Replace("rId2", "rIdGraft1");
        var source = RibbonPackageBuilder.Zip(
            ("[Content_Types].xml", RibbonPackageBuilder.Utf8("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>")),
            ("_rels/.rels", RibbonPackageBuilder.Utf8(rels)),
            ("xl/workbook.xml", RibbonPackageBuilder.Utf8("<workbook/>")));

        var (result, output) = Write(source, new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("x")));

        result.Succeeded.Should().BeTrue(result.Problem);
        Encoding.UTF8.GetString(Entry(output, "_rels/.rels")).Should().Contain("Id=\"rIdGraft2\"").And.Contain("Id=\"rIdGraft1\"");
    }

    [Fact(DisplayName = "別名の部品（customUI/ribbon.xml）: 関係が指す部品へ書き、既定の名前の部品は作らない")]
    public void 別名の部品()
    {
        var source = RibbonPackageBuilder.Zip(
            ("[Content_Types].xml", RibbonPackageBuilder.Utf8("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>")),
            ("_rels/.rels", RibbonPackageBuilder.Utf8(RibbonPackageBuilder.Rels((RibbonPackageBuilder.RelNew, "/customUI/ribbon.xml")))),
            ("customUI/ribbon.xml", RibbonPackageBuilder.Utf8(Ribbon("前"))));

        var (result, output) = Write(source, new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("後")));

        result.Parts.Should().ContainSingle().Which.PartName.Should().Be("customUI/ribbon.xml");
        RibbonPackageBuilder.ReadEntries(output).Select(e => e.Name).Should().NotContain("customUI/customUI14.xml");
        Read(output).Parts.Should().ContainSingle().Which.Xml.Should().Be(Ribbon("後"));
    }

    [Fact(DisplayName = "両形式: 片方は差し替え、片方は新規。どちらも読み取りで往復する")]
    public void 両形式()
    {
        var source = RibbonPackageBuilder.ExcelWorkbook(customUi14: Ribbon("前"));
        var new14 = Ribbon("14");
        var newOld = Ribbon("12", Ns12);

        var (result, output) = Write(source,
            new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, new14), new VbaRibbonWriteRequest(VbaRibbonFormat.Office2007, newOld));

        result.Succeeded.Should().BeTrue(result.Problem);
        result.Parts.Should().HaveCount(2);
        var read = Read(output);
        read.Parts.Single(p => p.IsOffice2010Format).Xml.Should().Be(new14);
        read.Parts.Single(p => !p.IsOffice2010Format).Xml.Should().Be(newOld);
    }

    [Fact(DisplayName = "触っていない項目（画像と画像の関係を含む）は、名前・順序・中身・更新日時が保たれ、無圧縮の項目は無圧縮のまま")]
    public void ほかの項目を保つ()
    {
        var png = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray();
        byte[] source;
        var stamp = new DateTimeOffset(2024, 5, 6, 7, 8, 10, TimeSpan.Zero);
        using (var stream = new MemoryStream())
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Add(string name, byte[] data, CompressionLevel level)
                {
                    var e = archive.CreateEntry(name, level);
                    e.LastWriteTime = stamp;
                    using var s = e.Open();
                    s.Write(data, 0, data.Length);
                }

                Add("[Content_Types].xml", RibbonPackageBuilder.Utf8("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/></Types>"), CompressionLevel.Optimal);
                Add("_rels/.rels", RibbonPackageBuilder.Utf8(RibbonPackageBuilder.Rels((RibbonPackageBuilder.RelNew, "customUI/customUI14.xml"))), CompressionLevel.Optimal);
                Add("xl/vbaProject.bin", Enumerable.Range(0, 20000).Select(i => (byte)(i % 7)).ToArray(), CompressionLevel.Optimal);
                Add("customUI/customUI14.xml", RibbonPackageBuilder.Utf8(Ribbon("前")), CompressionLevel.Optimal);
                Add("customUI/_rels/customUI14.xml.rels", RibbonPackageBuilder.Utf8("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"logo\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"images/logo.png\"/></Relationships>"), CompressionLevel.Optimal);
                Add("customUI/images/logo.png", png, CompressionLevel.NoCompression);
            }

            source = stream.ToArray();
        }

        var (result, output) = Write(source, new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("後")));

        result.Succeeded.Should().BeTrue(result.Problem);
        var before = RibbonPackageBuilder.ReadEntries(source);
        var after = RibbonPackageBuilder.ReadEntries(output);
        after.Select(e => e.Name).Should().Equal(before.Select(e => e.Name));
        foreach (var (name, data) in before.Where(e => e.Name != "customUI/customUI14.xml"))
            after.Single(e => e.Name == name).Data.Should().Equal(data, name);

        using var outStream = new MemoryStream(output);
        using var outArchive = new ZipArchive(outStream, ZipArchiveMode.Read);
        var image = outArchive.GetEntry("customUI/images/logo.png")!;
        image.CompressedLength.Should().Be(image.Length, "無圧縮の項目は無圧縮のまま");
        outArchive.GetEntry("xl/vbaProject.bin")!.LastWriteTime.Should().Be(stamp);

        using var src = new MemoryStream(source);
        using var dst = new MemoryStream(output);
        VbaRibbonPackageWriter.Verify(src, dst, new[] { new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("後")) }, result)
            .Should().BeNull();
    }

    [Fact(DisplayName = "照合: 書いたあとで、触っていない項目の中身が変わっていれば（CRC-32 の違い）理由を返す")]
    public void 照合で改ざんを見つける()
    {
        var source = RibbonPackageBuilder.ExcelWorkbook(customUi14: Ribbon("前"));
        var request = new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("後"));
        var (result, output) = Write(source, request);
        var tampered = RibbonPackageBuilder.Zip(RibbonPackageBuilder.ReadEntries(output)
            .Select(e => e.Name == "xl/styles.xml" ? (e.Name, RibbonPackageBuilder.Utf8("<styleSheet/>")) : e).ToArray());

        using var src = new MemoryStream(source);
        using var dst = new MemoryStream(tampered);
        VbaRibbonPackageWriter.Verify(src, dst, new[] { request }, result).Should().Contain("xl/styles.xml");
    }

    [Fact(DisplayName = "照合: customUI の部品が書いた本文と違えば理由を返す")]
    public void 照合で本文の違いを見つける()
    {
        var source = RibbonPackageBuilder.ExcelWorkbook(customUi14: Ribbon("前"));
        var request = new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("後"));
        var (result, output) = Write(source, request);

        using var src = new MemoryStream(source);
        using var dst = new MemoryStream(output);
        VbaRibbonPackageWriter.Verify(src, dst, new[] { request with { Xml = Ribbon("別") } }, result)
            .Should().Contain("ミラーの本文と違います");
    }

    [Fact(DisplayName = "本文の先頭の BOM の文字は除いて UTF-8（BOM なし）で書く（読み取りとの往復で 1 文字ずれない）")]
    public void BOMを除く()
    {
        var source = RibbonPackageBuilder.ExcelWorkbook();
        var xml = Ribbon("x");

        var (_, output) = Write(source, new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, "\uFEFF" + xml));

        Entry(output, "customUI/customUI14.xml").Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        Read(output).Parts.Single().Xml.Should().Be(xml);
    }

    [Fact(DisplayName = "BOM つきの _rels/.rels に足しても BOM を保つ")]
    public void BOMつきの関係の一覧()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(RibbonPackageBuilder.Utf8(RibbonPackageBuilder.Rels())).ToArray();
        var source = RibbonPackageBuilder.Zip(
            ("[Content_Types].xml", RibbonPackageBuilder.Utf8("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>")),
            ("_rels/.rels", withBom));

        var (result, output) = Write(source, new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("x")));

        result.Succeeded.Should().BeTrue(result.Problem);
        Entry(output, "_rels/.rels").Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        Read(output).Status.Should().Be(VbaRibbonReadStatus.Ok);
    }

    [Fact(DisplayName = "展開後の合計が上限を超えたら TooLarge で止める（zip 爆弾への備え）")]
    public void 展開後の上限()
    {
        var source = RibbonPackageBuilder.Zip(
            ("[Content_Types].xml", RibbonPackageBuilder.Utf8("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>")),
            ("_rels/.rels", RibbonPackageBuilder.Utf8(RibbonPackageBuilder.Rels())),
            ("xl/big.bin", new byte[300_000]));
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();

        var result = VbaRibbonPackageWriter.Write(input, output,
            new[] { new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("x")) }, maxTotalExpandedBytes: 100_000);

        result.Status.Should().Be(VbaRibbonWriteStatus.TooLarge);
    }

    [Fact(DisplayName = "壊れた zip・暗号化（OLE）・電子署名つき・パッケージの外を指す関係は、書かずに理由を返す")]
    public void 書かないブック()
    {
        var request = new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("x"));
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.Concat(new byte[600]).ToArray();
        var signed = RibbonPackageBuilder.Zip(
            ("[Content_Types].xml", RibbonPackageBuilder.Utf8("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>")),
            ("_rels/.rels", RibbonPackageBuilder.Utf8(RibbonPackageBuilder.Rels())),
            ("_xmlsignatures/sig1.xml", RibbonPackageBuilder.Utf8("<Signature/>")));
        var outside = RibbonPackageBuilder.Zip(
            ("[Content_Types].xml", RibbonPackageBuilder.Utf8("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>")),
            ("_rels/.rels", RibbonPackageBuilder.Utf8(RibbonPackageBuilder.Rels((RibbonPackageBuilder.RelNew, "../evil.xml")))));

        Write(Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray(), request).Result.Status.Should().Be(VbaRibbonWriteStatus.Corrupt);
        Write(ole, request).Result.Status.Should().Be(VbaRibbonWriteStatus.Encrypted);
        Write(signed, request).Result.Status.Should().Be(VbaRibbonWriteStatus.Signed);
        Write(outside, request).Result.Status.Should().Be(VbaRibbonWriteStatus.InvalidTarget);
    }

    [Fact(DisplayName = "予行（Inspect）: 正しいブックは Ok、電子署名つきは Signed、暗号化は Encrypted")]
    public void 予行()
    {
        using var ok = new MemoryStream(RibbonPackageBuilder.ExcelWorkbook());
        VbaRibbonPackageWriter.Inspect(ok).Status.Should().Be(VbaRibbonWriteStatus.Ok);

        using var signed = new MemoryStream(RibbonPackageBuilder.Zip(
            ("[Content_Types].xml", RibbonPackageBuilder.Utf8("<Types/>")),
            ("_rels/.rels", RibbonPackageBuilder.Utf8(RibbonPackageBuilder.Rels())),
            ("_xmlsignatures/origin.sigs", Array.Empty<byte>())));
        VbaRibbonPackageWriter.Inspect(signed).Status.Should().Be(VbaRibbonWriteStatus.Signed);

        using var ole = new MemoryStream(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0 });
        VbaRibbonPackageWriter.Inspect(ole).Status.Should().Be(VbaRibbonWriteStatus.Encrypted);
    }

    [Fact(DisplayName = "出力のストリームが途中で失敗しても、入力のブックには何も書かない")]
    public void 出力の失敗()
    {
        var source = RibbonPackageBuilder.ExcelWorkbook(customUi14: Ribbon("前"));
        var copy = source.ToArray();
        using var input = new MemoryStream(source, writable: false);
        using var output = new FailingStream(failAfterBytes: 200);

        var act = () => VbaRibbonPackageWriter.Write(input, output, new[] { new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("後")) });

        act.Should().Throw<IOException>();
        source.Should().Equal(copy);
    }

    [Fact(DisplayName = "ファイルの書き換え: 一時ファイル → 照合 → 置き換えで成功し、一時ファイルは残らない")]
    public void ファイルの書き換え()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("book");
        var path = Path.Combine(dir, "計画.xlsm");
        File.WriteAllBytes(path, RibbonPackageBuilder.ExcelWorkbook());

        var result = VbaRibbonPackageWriter.RewriteFile(path, new[] { new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("x")) });

        result.Succeeded.Should().BeTrue(result.Problem);
        VbaRibbonPackageReader.Read(path).Parts.Should().ContainSingle().Which.Xml.Should().Be(Ribbon("x"));
        Directory.GetFiles(dir).Should().ContainSingle("一時ファイル（~graft-…tmp）は消える");
    }

    [Fact(DisplayName = "ファイルの書き換え: 失敗（壊れたブック）では元のファイルが 1 バイトも変わらず、一時ファイルも残らない")]
    public void ファイルの書き換えの失敗()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("book");
        var path = Path.Combine(dir, "壊れた.xlsm");
        var broken = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();
        File.WriteAllBytes(path, broken);

        var result = VbaRibbonPackageWriter.RewriteFile(path, new[] { new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("x")) });

        result.Status.Should().Be(VbaRibbonWriteStatus.Corrupt);
        File.ReadAllBytes(path).Should().Equal(broken);
        Directory.GetFiles(dir).Should().ContainSingle();
    }

    [Fact(DisplayName = "ファイルの書き換え: 置き換えの直前に元のファイルが変わったら、上書きせずに止める（SourceChanged）")]
    public void 途中で元が変わる()
    {
        using var ws = new TempWorkspace();
        var dir = ws.CreateDirectory("book");
        var path = Path.Combine(dir, "計画.xlsm");
        File.WriteAllBytes(path, RibbonPackageBuilder.ExcelWorkbook());
        var changed = RibbonPackageBuilder.ExcelWorkbook(sheetText: "別のプログラムが保存した内容");

        var result = VbaRibbonPackageWriter.RewriteFile(
            path, new[] { new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("x")) },
            beforeReplace: () =>
            {
                File.WriteAllBytes(path, changed);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            });

        result.Status.Should().Be(VbaRibbonWriteStatus.SourceChanged);
        File.ReadAllBytes(path).Should().Equal(changed);
        Directory.GetFiles(dir).Should().ContainSingle();
    }

    [Fact(DisplayName = "ファイルの書き換え: .xls は対象外（LegacyFormat）。ファイルを開かない")]
    public void 古い形式()
    {
        VbaRibbonPackageWriter.RewriteFile("C:/nothing/old.xls", new[] { new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("x")) })
            .Status.Should().Be(VbaRibbonWriteStatus.LegacyFormat);
    }

    [Fact(DisplayName = "同じ形式を 2 つ渡すのは呼び出しの誤り（ArgumentException）")]
    public void 同じ形式を2つ()
    {
        using var input = new MemoryStream(RibbonPackageBuilder.ExcelWorkbook());
        using var output = new MemoryStream();
        var request = new VbaRibbonWriteRequest(VbaRibbonFormat.Office2010, Ribbon("x"));

        var act = () => VbaRibbonPackageWriter.Write(input, output, new[] { request, request });

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>指定のバイト数を書いたら IOException を投げる出力（ディスクがいっぱいになった、などの代役）。</summary>
    private sealed class FailingStream : MemoryStream
    {
        private readonly int _failAfterBytes;

        public FailingStream(int failAfterBytes) => _failAfterBytes = failAfterBytes;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > _failAfterBytes) throw new IOException("ディスクがいっぱいです（テスト）。");
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > _failAfterBytes) throw new IOException("ディスクがいっぱいです（テスト）。");
            base.Write(buffer);
        }
    }
}
