using System.Text;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaRibbonXmlAnalyzer"/>（customUI の XML から構成とコールバックを取り出す）のテスト。
/// 仕様書_ブックの情報と依頼の観点 15.3・15.8。XXE（外部実体）・壊れた XML・DTD を含む。
/// </summary>
public class VbaRibbonXmlAnalyzerTests
{
    private static VbaRibbonPart Analyze(string xml)
        => VbaRibbonXmlAnalyzer.Analyze(new VbaRibbonRawPart("customUI/customUI14.xml", true, xml, null));

    [Fact(DisplayName = "構成: タブ・グループ・コントロールの種類・id・label を、入れ物（tabs・ribbon の属性無し）を飛ばして字下げつきで取り出す")]
    public void 構成を取り出す()
    {
        var part = Analyze(RibbonPackageBuilder.SampleRibbon);

        part.Problem.Should().BeNull();
        part.Nodes.Select(n => (n.Depth, n.Display, n.Label)).Should().Equal(
            (0, "customUI", null),          // onLoad を持つ根は構成に出す
            (1, "ribbon", null),
            (2, "tab tabMain", "業務"),
            (3, "group grpA", "操作"),
            (4, "button btnRun", "実行"),
            (4, "button idMso=Copy", null),
            (4, "dropDown ddKind", "種類"),
            (5, "item i1", "A"));
    }

    [Fact(DisplayName = "コールバック: on*・get* の属性だけを、属性名・名前・持ち主つきで取り出す（imageMso・size などは数えない）")]
    public void コールバックを取り出す()
    {
        var part = Analyze(RibbonPackageBuilder.SampleRibbon);

        part.Nodes.SelectMany(n => n.Callbacks).Select(c => (c.Attribute, c.Name, c.Owner)).Should().Equal(
            ("onLoad", "RibbonLoaded", "customUI"),
            ("onAction", "OnRun", "button btnRun"),
            ("getEnabled", "GetEnabled", "button btnRun"),
            ("onAction", "Module1.OnCopy", "button idMso=Copy"),
            ("onAction", "OnPick", "dropDown ddKind"),
            ("getItemCount", "CountItems", "dropDown ddKind"));
    }

    [Theory(DisplayName = "コールバックの属性の判定: on／get＋大文字と loadImage。それ以外（one・getaway・id など）は違う")]
    [InlineData("onAction", true)]
    [InlineData("onChange", true)]
    [InlineData("onLoad", true)]
    [InlineData("getLabel", true)]
    [InlineData("getItemScreentip", true)]
    [InlineData("loadImage", true)]
    [InlineData("one", false)]
    [InlineData("on", false)]
    [InlineData("getaway", false)]
    [InlineData("id", false)]
    [InlineData("imageMso", false)]
    public void コールバック属性の判定(string name, bool expected)
    {
        VbaRibbonXmlAnalyzer.IsCallbackAttribute(name).Should().Be(expected);
    }

    [Fact(DisplayName = "旧形式（2006/01 の名前空間）も同じように読める")]
    public void 旧形式の名前空間()
    {
        var xml = "<customUI xmlns=\"http://schemas.microsoft.com/office/2006/01/customui\"><ribbon><tabs><tab id=\"t\" label=\"T\">"
                  + "<group id=\"g\" label=\"G\"><button id=\"b\" label=\"B\" onAction=\"Go\"/></group></tab></tabs></ribbon></customUI>";

        var part = Analyze(xml);

        part.Nodes.Select(n => n.Display).Should().Contain(new[] { "tab t", "group g", "button b" });
        part.Nodes.SelectMany(n => n.Callbacks).Should().ContainSingle().Which.Name.Should().Be("Go");
    }

    [Fact(DisplayName = "壊れた XML: 理由つきで読めない扱いにする（例外は投げない）")]
    public void 壊れたXML()
    {
        var part = Analyze("<customUI><ribbon><tabs></ribbon></customUI>");

        part.IsUsable.Should().BeFalse();
        part.Problem.Should().Contain("XML として読めません");
        part.Nodes.Should().BeEmpty();
    }

    [Fact(DisplayName = "DTD（<!DOCTYPE）を含む XML は、読まずに理由を返す")]
    public void DTD付き()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE customUI [<!ENTITY x \"y\">]><customUI><ribbon/></customUI>";

        var part = Analyze(xml);

        part.IsUsable.Should().BeFalse();
        part.Problem.Should().Contain("DTD");
    }

    [Fact(DisplayName = "XXE: 外部実体でローカルのファイルを取り込もうとする XML は、中身が展開されず、読み取りも失敗扱いになる")]
    public void XXE()
    {
        using var ws = new TempWorkspace();
        var secret = ws.WriteText("secret.txt", "TOP-SECRET-VALUE");
        var uri = new Uri(secret).AbsoluteUri;
        var xml = $"<?xml version=\"1.0\"?><!DOCTYPE customUI [<!ENTITY leak SYSTEM \"{uri}\">]>"
                  + "<customUI><ribbon><tabs><tab id=\"t\" label=\"&leak;\"/></tabs></ribbon></customUI>";

        var part = Analyze(xml);

        part.IsUsable.Should().BeFalse();
        part.Nodes.Should().BeEmpty("読まないので、ラベルにも展開されない");
        VbaRibbonXmlAnalyzer.TryParse(xml, 0, out var document, out var problem).Should().BeFalse();
        document.Should().BeNull();
        problem.Should().NotBeNull();
    }

    [Fact(DisplayName = "文字数の上限を超える XML は読まない（天井）")]
    public void 文字数の上限()
    {
        var xml = "<customUI>" + new string('a', 5000) + "</customUI>";

        VbaRibbonXmlAnalyzer.TryParse(xml, maxCharacters: 1000, out _, out var problem).Should().BeFalse();
        problem.Should().Contain("XML として読めません");
    }

    [Fact(DisplayName = "読めなかった部品（Xml が null）は、そのときの理由のまま返る")]
    public void 読めなかった部品はそのまま()
    {
        var raw = new VbaRibbonRawPart("customUI/customUI14.xml", true, null, "大きすぎるため読みません。");

        var part = VbaRibbonXmlAnalyzer.Analyze(raw);

        part.IsUsable.Should().BeFalse();
        part.Problem.Should().Be("大きすぎるため読みません。");
    }

    [Fact(DisplayName = "文字コード: BOM つき UTF-8・UTF-16 の BOM は取り除かれ、日本語が読める")]
    public void 文字コード()
    {
        var text = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><customUI><ribbon><tabs><tab id=\"t\" label=\"業務\"/></tabs></ribbon></customUI>";

        var utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(new UTF8Encoding(false).GetBytes(text)).ToArray();
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray();
        var plain = new UTF8Encoding(false).GetBytes(text);

        VbaRibbonXmlAnalyzer.DecodeXml(utf8Bom).Should().Be(text);
        VbaRibbonXmlAnalyzer.DecodeXml(utf16).Should().Be(text);
        VbaRibbonXmlAnalyzer.DecodeXml(plain).Should().Be(text);
        Analyze(VbaRibbonXmlAnalyzer.DecodeXml(utf16)).Nodes.Should().Contain(n => n.Label == "業務");
    }
}
