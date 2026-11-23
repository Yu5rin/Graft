using FluentAssertions;
using Graft.Core.Vba;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaRibbonPackageReader"/>（保存済みブックの zip から customUI の部品を読む）のテスト。
/// 仕様書_ブックの情報と依頼の観点 15.2・15.8。小さな .xlsm 相当の zip をテスト内で組み立てて読む。
/// </summary>
public class VbaRibbonPackageReaderTests
{
    private static VbaRibbonReadResult ReadBytes(byte[] zip, VbaRibbonLimits? limits = null)
    {
        using var stream = new MemoryStream(zip);
        return VbaRibbonPackageReader.ReadFromStream(stream, limits);
    }

    [Fact(DisplayName = "customUI14.xml だけのブック: 新しい形式の部品として読める")]
    public void customUI14だけ()
    {
        var result = ReadBytes(RibbonPackageBuilder.Workbook(customUi14: RibbonPackageBuilder.SampleRibbon));

        result.Status.Should().Be(VbaRibbonReadStatus.Ok);
        var part = result.Parts.Should().ContainSingle().Subject;
        part.PartName.Should().Be("customUI/customUI14.xml");
        part.IsOffice2010Format.Should().BeTrue();
        part.Xml.Should().Be(RibbonPackageBuilder.SampleRibbon);
    }

    [Fact(DisplayName = "旧形式（customUI.xml）だけのブック: 古い形式の部品として読める")]
    public void 旧形式だけ()
    {
        var result = ReadBytes(RibbonPackageBuilder.Workbook(customUiOld: RibbonPackageBuilder.SampleRibbon));

        result.Status.Should().Be(VbaRibbonReadStatus.Ok);
        var part = result.Parts.Should().ContainSingle().Subject;
        part.PartName.Should().Be("customUI/customUI.xml");
        part.IsOffice2010Format.Should().BeFalse();
    }

    [Fact(DisplayName = "両方あれば両方読む")]
    public void 両方()
    {
        var result = ReadBytes(RibbonPackageBuilder.Workbook(
            customUi14: "<customUI xmlns=\"http://schemas.microsoft.com/office/2009/07/customui\"/>",
            customUiOld: "<customUI xmlns=\"http://schemas.microsoft.com/office/2006/01/customui\"/>"));

        result.Status.Should().Be(VbaRibbonReadStatus.Ok);
        result.Parts.Select(p => p.PartName).Should().BeEquivalentTo("customUI/customUI14.xml", "customUI/customUI.xml");
        result.ReadableParts.Should().HaveCount(2);
    }

    [Fact(DisplayName = "customUI が無いブック: リボンの設定は無い（NoRibbon）。壊れてはいない")]
    public void customUIが無い()
    {
        var result = ReadBytes(RibbonPackageBuilder.Workbook());

        result.Status.Should().Be(VbaRibbonReadStatus.NoRibbon);
        result.Parts.Should().BeEmpty();
    }

    [Fact(DisplayName = "壊れた zip: Corrupt。例外は投げない")]
    public void 壊れたzip()
    {
        var result = ReadBytes(Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray());

        result.Status.Should().Be(VbaRibbonReadStatus.Corrupt);
        result.Reason.Should().Contain("zip");
    }

    [Fact(DisplayName = "途中で切れた zip（中央ディレクトリが無い）も Corrupt")]
    public void 途中で切れたzip()
    {
        var zip = RibbonPackageBuilder.Workbook(customUi14: RibbonPackageBuilder.SampleRibbon);

        var result = ReadBytes(zip.Take(zip.Length / 2).ToArray());

        result.Status.Should().Be(VbaRibbonReadStatus.Corrupt);
    }

    [Fact(DisplayName = "_rels/.rels が無い zip は、Excel のブックの構造ではない（Corrupt）")]
    public void relsが無い()
    {
        var result = ReadBytes(RibbonPackageBuilder.Zip(("xl/workbook.xml", RibbonPackageBuilder.Utf8("<workbook/>"))));

        result.Status.Should().Be(VbaRibbonReadStatus.Corrupt);
        result.Reason.Should().Contain("_rels/.rels");
    }

    [Fact(DisplayName = "関係はあるが部品が無い: 部品に理由がつき、読めた部品が無いので PartsUnreadable")]
    public void 部品が無い()
    {
        var zip = RibbonPackageBuilder.Zip(
            ("_rels/.rels", RibbonPackageBuilder.Utf8(RibbonPackageBuilder.Rels((RibbonPackageBuilder.RelNew, "customUI/customUI14.xml")))));

        var result = ReadBytes(zip);

        result.Status.Should().Be(VbaRibbonReadStatus.PartsUnreadable);
        result.Parts.Should().ContainSingle().Which.Problem.Should().Contain("ありません");
    }

    [Fact(DisplayName = "巨大な部品は読まない: 上限を超えた時点で読むのをやめ、理由を返す（zip 爆弾に備える）")]
    public void 巨大な部品()
    {
        // 展開すると 8MB だが、'a' の繰り返しなので zip としては数 KB。既定の上限（2MB）を超える。
        var bomb = new string('a', 8 * 1024 * 1024);
        var zip = RibbonPackageBuilder.Workbook(customUi14: "<customUI>" + bomb + "</customUI>");
        zip.Length.Should().BeLessThan(100 * 1024, "圧縮されて小さい（zip 爆弾の形）");

        var result = ReadBytes(zip);

        result.Status.Should().Be(VbaRibbonReadStatus.PartsUnreadable);
        var part = result.Parts.Should().ContainSingle().Subject;
        part.Xml.Should().BeNull();
        part.Problem.Should().Contain("大きすぎる");
    }

    [Fact(DisplayName = "zip の見出しが小さい展開サイズを偽っていても、実際に展開した量で上限を守る")]
    public void 見出しの嘘()
    {
        var bomb = new string('a', 6 * 1024 * 1024);
        var zip = RibbonPackageBuilder.Workbook(customUi14: "<customUI>" + bomb + "</customUI>");
        ForgeUncompressedSizes(zip, newSize: 10);

        var result = ReadBytes(zip);

        // 実装（.NET の展開）が偽の見出しの長さで読みを止めるなら、短く切れた XML が読めてしまうが、いずれにしても
        // 6MB を丸ごとメモリに載せることはなく、上限（2MB）を超える中身は返らない。
        foreach (var part in result.Parts.Where(p => p.Xml is not null))
            part.Xml!.Length.Should().BeLessThanOrEqualTo(VbaRibbonLimits.DefaultMaxPartBytes);
    }

    [Fact(DisplayName = "上限は引数で変えられる（小さくすれば、普通のリボンも「大きすぎる」になる）")]
    public void 上限を変える()
    {
        var zip = RibbonPackageBuilder.Workbook(customUi14: RibbonPackageBuilder.SampleRibbon);

        var result = ReadBytes(zip, new VbaRibbonLimits(MaxPartBytes: 100));

        result.Status.Should().Be(VbaRibbonReadStatus.PartsUnreadable);
        result.Parts.Single().Problem.Should().Contain("大きすぎる");
    }

    [Fact(DisplayName = "関係の Target は、先頭の / ・大文字小文字の違い・%20 があっても部品に辿れる。外部を指す関係は無視する")]
    public void Targetの書き方の揺れ()
    {
        var rels = RibbonPackageBuilder.Rels(
            (RibbonPackageBuilder.RelNew, "/CUSTOMUI/customUI14.xml"),
            (RibbonPackageBuilder.RelOld, "https://example.com/customUI.xml\" TargetMode=\"External"));
        var zip = RibbonPackageBuilder.Zip(
            ("_rels/.rels", RibbonPackageBuilder.Utf8(rels)),
            ("customUI/customUI14.xml", RibbonPackageBuilder.Utf8("<customUI/>")));

        var result = ReadBytes(zip);

        result.Status.Should().Be(VbaRibbonReadStatus.Ok);
        result.Parts.Should().ContainSingle().Which.PartName.Should().Be("CUSTOMUI/customUI14.xml");
    }

    [Fact(DisplayName = "パッケージの外へ出る Target（..）は読まない")]
    public void 外へ出るTarget()
    {
        var rels = RibbonPackageBuilder.Rels((RibbonPackageBuilder.RelNew, "../outside.xml"));
        var zip = RibbonPackageBuilder.Zip(("_rels/.rels", RibbonPackageBuilder.Utf8(rels)), ("outside.xml", RibbonPackageBuilder.Utf8("<x/>")));

        ReadBytes(zip).Status.Should().Be(VbaRibbonReadStatus.NoRibbon);
    }

    [Fact(DisplayName = ".xls の古い形式（OLE の先頭のしるし）は対象外（LegacyFormat）")]
    public void OLEは対象外()
    {
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 };

        var result = ReadBytes(ole);

        result.Status.Should().Be(VbaRibbonReadStatus.LegacyFormat);
        result.Reason.Should().Contain(".xls");
    }

    [Fact(DisplayName = "拡張子が .xls なら、ファイルを開かずに対象外と知らせる")]
    public void 拡張子xlsは対象外()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteBytes("old.xls", new byte[] { 1, 2, 3 });

        VbaRibbonPackageReader.Read(path).Status.Should().Be(VbaRibbonReadStatus.LegacyFormat);
        VbaRibbonPackageReader.Read(Path.Combine(ws.RootPath, "ない.xls")).Status.Should().Be(VbaRibbonReadStatus.LegacyFormat);
    }

    [Fact(DisplayName = "ファイルが無い: FileNotFound")]
    public void ファイルが無い()
    {
        using var ws = new TempWorkspace();

        var result = VbaRibbonPackageReader.Read(Path.Combine(ws.RootPath, "ない.xlsm"));

        result.Status.Should().Be(VbaRibbonReadStatus.FileNotFound);
        result.Reason.Should().Contain("見つかりません");
    }

    [Fact(DisplayName = "ファイルから読める。更新日時も添えて返す")]
    public void ファイルから読める()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteBytes("計画.xlsm", RibbonPackageBuilder.Workbook(customUi14: RibbonPackageBuilder.SampleRibbon));
        File.SetLastWriteTime(path, new DateTime(2026, 11, 20, 18, 5, 0));

        var result = VbaRibbonPackageReader.Read(path);

        result.Status.Should().Be(VbaRibbonReadStatus.Ok);
        result.FileModified.Should().NotBeNull();
        result.FileModified!.Value.DateTime.Should().Be(new DateTime(2026, 11, 20, 18, 5, 0));
    }

    [Fact(DisplayName = "Excel が開いている相当（他者が書き込み権限で開いたまま、読み取りだけを許している）でも読める")]
    public void 開かれていても読める()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteBytes("開いている.xlsm", RibbonPackageBuilder.Workbook(customUi14: RibbonPackageBuilder.SampleRibbon));
        using var excel = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        VbaRibbonPackageReader.Read(path).Status.Should().Be(VbaRibbonReadStatus.Ok);
    }

    [Fact(DisplayName = "読めないほど排他で開かれているファイル: Locked（例外にしない）")]
    public void 排他ならLocked()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteBytes("排他.xlsm", RibbonPackageBuilder.Workbook(customUi14: RibbonPackageBuilder.SampleRibbon));
        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = VbaRibbonPackageReader.Read(path);

        result.Status.Should().Be(VbaRibbonReadStatus.Locked);
        result.Reason.Should().Contain("使用中");
    }

    /// <summary>
    /// 中央ディレクトリ（<c>PK\x01\x02</c>）と各ローカルヘッダ（<c>PK\x03\x04</c>）に書かれた、巨大な部品の展開後のサイズを、偽の小さな値に書き換える。
    /// </summary>
    private static void ForgeUncompressedSizes(byte[] zip, int newSize)
    {
        for (var i = 0; i + 30 < zip.Length; i++)
        {
            if (zip[i] != 0x50 || zip[i + 1] != 0x4B) continue;
            var offset = (zip[i + 2], zip[i + 3]) switch
            {
                (0x01, 0x02) => 24, // 中央ディレクトリ: 展開後サイズ
                (0x03, 0x04) => 22, // ローカルヘッダ: 展開後サイズ
                _ => -1,
            };
            // 関係の一覧など小さな部品は書き換えない（巨大な部品の見出しだけを偽る）。
            if (offset < 0 || BitConverter.ToInt32(zip, i + offset) < 1_000_000) continue;
            BitConverter.GetBytes(newSize).CopyTo(zip, i + offset);
        }
    }
}
