using System.Text;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// <see cref="VbaRibbonXmlValidator"/>（ブックへ書く前のリボンの XML の検査。同梱した customUI の XSD による検査を含む）のテスト。
/// 仕様書_ブックの情報と依頼の観点 16.8（v0.8）の表の #1〜#14 を、正・誤の両方で 1 件ずつ確かめる。
/// </summary>
public class VbaRibbonXmlValidatorTests
{
    private const string Ns14 = "http://schemas.microsoft.com/office/2009/07/customui";
    private const string Ns12 = "http://schemas.microsoft.com/office/2006/01/customui";

    /// <summary>タブ 1・グループ 1・ボタン 1 の、定義に合う最小のリボン。</summary>
    private static string Ribbon(string controls, string ns = Ns14, string rootAttributes = "", string ribbonAttributes = "")
        => "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
           + $"<customUI xmlns=\"{ns}\"{rootAttributes}>\n"
           + $"  <ribbon{ribbonAttributes}>\n"
           + "    <tabs>\n"
           + "      <tab id=\"tabMain\" label=\"業務\">\n"
           + "        <group id=\"grpA\" label=\"操作\">\n"
           + controls
           + "        </group>\n"
           + "      </tab>\n"
           + "    </tabs>\n"
           + "  </ribbon>\n"
           + "</customUI>\n";

    private const string OkButton = "          <button id=\"btnRun\" label=\"実行\" size=\"large\" imageMso=\"HappyFace\" onAction=\"OnRun\"/>\n";

    [Fact(DisplayName = "同梱した XSD を両形式とも読み込める（埋め込みリソースの同梱漏れの検出）")]
    public void スキーマを読み込める()
    {
        VbaRibbonXmlValidator.GetSchemaSet(VbaRibbonFormat.Office2010).Count.Should().Be(1);
        VbaRibbonXmlValidator.GetSchemaSet(VbaRibbonFormat.Office2007).Count.Should().Be(1);
        VbaRibbonXmlValidator.KnownElementCount(VbaRibbonFormat.Office2010).Should().BeGreaterThan(40);
        VbaRibbonXmlValidator.IsKnownElement(VbaRibbonFormat.Office2010, "backstage").Should().BeTrue();
        VbaRibbonXmlValidator.IsKnownElement(VbaRibbonFormat.Office2007, "backstage").Should().BeFalse("backstage は 2009/07 形式にだけある");
        VbaRibbonXmlValidator.IsKnownElement(VbaRibbonFormat.Office2007, "button").Should().BeTrue();
    }

    [Fact(DisplayName = "定義に合う XML（新しい形式）: エラーも警告も無い")]
    public void 正しい新形式()
    {
        var result = VbaRibbonXmlValidator.Validate(Ribbon(OkButton), VbaRibbonFormat.Office2010);

        result.Findings.Should().BeEmpty();
    }

    [Fact(DisplayName = "定義に合う XML（古い形式）: エラーも警告も無い")]
    public void 正しい旧形式()
    {
        var result = VbaRibbonXmlValidator.Validate(Ribbon(OkButton, Ns12), VbaRibbonFormat.Office2007);

        result.Findings.Should().BeEmpty();
    }

    [Fact(DisplayName = "読み取りのテストの典型的なリボン（コールバック 5 種・dropDown と item）も定義に合う")]
    public void 典型的なリボン()
    {
        var result = VbaRibbonXmlValidator.Validate(RibbonPackageBuilder.SampleRibbon, VbaRibbonFormat.Office2010);

        result.HasErrors.Should().BeFalse(string.Join("\n", result.Findings.Select(f => f.ToDisplayText())));
    }

    [Fact(DisplayName = "#1 整形式でない XML はエラー（行つき）")]
    public void 整形式でない()
    {
        var result = VbaRibbonXmlValidator.Validate("<customUI xmlns=\"" + Ns14 + "\">\n<ribbon>\n</customUI>", VbaRibbonFormat.Office2010);

        var finding = result.Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(Severity.Error);
        finding.Check.Should().Be(1);
        finding.Message.Should().Contain("XML として読めません").And.Contain("行目");
    }

    [Fact(DisplayName = "#1 DTD を含む XML（XXE）はエラー。外部実体を読みに行かない")]
    public void DTDはエラー()
    {
        const string xxe = "<?xml version=\"1.0\"?>\n<!DOCTYPE customUI [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>\n"
                           + "<customUI xmlns=\"" + Ns14 + "\"><ribbon><tabs><tab id=\"t\" label=\"&x;\"/></tabs></ribbon></customUI>";

        var result = VbaRibbonXmlValidator.Validate(xxe, VbaRibbonFormat.Office2010);

        var finding = result.Findings.Should().ContainSingle().Subject;
        finding.Check.Should().Be(1);
        finding.Message.Should().Contain("DTD");
    }

    [Fact(DisplayName = "#1 25 万段の入れ子でも落ちずにエラーを返す（1.2.2 の不具合と同じ入力）")]
    public void 深すぎる入れ子()
    {
        var sb = new StringBuilder();
        sb.Append("<customUI xmlns=\"").Append(Ns14).Append("\">");
        const int depth = 250_000;
        for (var i = 0; i < depth; i++) sb.Append("<a>");
        for (var i = 0; i < depth; i++) sb.Append("</a>");
        sb.Append("</customUI>");

        var result = VbaRibbonXmlValidator.Validate(sb.ToString(), VbaRibbonFormat.Office2010);

        result.HasErrors.Should().BeTrue();
        result.Findings.Should().ContainSingle().Which.Message.Should().Contain("入れ子が深すぎる");
    }

    [Fact(DisplayName = "#2 部品の上限（2 MB）を超えるものはエラー")]
    public void 大きすぎる()
    {
        var big = Ribbon($"          <button id=\"b\" label=\"{new string('あ', 800_000)}\"/>\n");

        var result = VbaRibbonXmlValidator.Validate(big, VbaRibbonFormat.Office2010);

        result.Findings.Should().ContainSingle().Which.Check.Should().Be(2);
    }

    [Fact(DisplayName = "#3 XML 宣言の encoding が UTF-8 以外ならエラー。宣言なし・utf-8（小文字）は通る")]
    public void エンコーディング()
    {
        var sjis = Ribbon(OkButton).Replace("encoding=\"UTF-8\"", "encoding=\"Shift_JIS\"");
        var lower = Ribbon(OkButton).Replace("encoding=\"UTF-8\"", "encoding=\"utf-8\"");
        var none = Ribbon(OkButton)[(Ribbon(OkButton).IndexOf('\n') + 1)..];

        VbaRibbonXmlValidator.Validate(sjis, VbaRibbonFormat.Office2010).Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("UTF-8").And.Contain("Shift_JIS");
        VbaRibbonXmlValidator.Validate(lower, VbaRibbonFormat.Office2010).Findings.Should().BeEmpty();
        VbaRibbonXmlValidator.Validate(none, VbaRibbonFormat.Office2010).Findings.Should().BeEmpty();
    }

    [Fact(DisplayName = "#4 ルート要素が customUI でなければエラー")]
    public void ルート要素()
    {
        var result = VbaRibbonXmlValidator.Validate("<ribbon xmlns=\"" + Ns14 + "\"/>", VbaRibbonFormat.Office2010);

        result.Findings.Should().ContainSingle().Which.Check.Should().Be(4);
    }

    [Fact(DisplayName = "#4 名前空間がファイルと合わなければエラー。もう一方の形式の名前空間なら、どちらのファイルの形式かを添える")]
    public void 名前空間()
    {
        var result = VbaRibbonXmlValidator.Validate(Ribbon(OkButton, Ns12), VbaRibbonFormat.Office2010);

        var finding = result.Findings.Should().ContainSingle().Subject;
        finding.Check.Should().Be(4);
        finding.Message.Should().Contain(Ns14).And.Contain(Ns12).And.Contain("CustomUI/customUI.xml");
        finding.Line.Should().Be(2);
    }

    [Fact(DisplayName = "#5 id の重複はエラー（スキーマの ID 型）")]
    public void id重複()
    {
        var xml = Ribbon("          <button id=\"btnRun\" label=\"A\"/>\n          <toggleButton id=\"btnRun\" label=\"B\"/>\n");

        var result = VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010);

        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("btnRun");
    }

    [Fact(DisplayName = "#6 id と idMso を 1 つの要素に書くとエラー（スキーマでは表せないので自前で調べる）")]
    public void idとidMso()
    {
        var xml = Ribbon("          <button id=\"btnRun\" idMso=\"Copy\" label=\"A\"/>\n");

        var result = VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010);

        result.Errors.Should().Contain(f => f.Check == 6 && f.Message.Contains("id と idMso"));
    }

    [Fact(DisplayName = "#7 数字で始まる id はエラー（XML の ID 型）。行と列を報告する")]
    public void id書式()
    {
        var xml = Ribbon("          <button id=\"1st\" label=\"A\"/>\n");

        var result = VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010);

        var finding = result.Errors.Should().ContainSingle().Subject;
        finding.Check.Should().Be(5);
        finding.Message.Should().Contain("1st");
        finding.Line.Should().Be(7, "ボタンは 7 行目");
        finding.Column.Should().BeGreaterThan(1);
    }

    [Fact(DisplayName = "#8 空のコールバック（onAction=\"\"）はエラー")]
    public void 空のコールバック()
    {
        var xml = Ribbon("          <button id=\"b\" label=\"A\" onAction=\"\"/>\n");

        var result = VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010);

        result.Errors.Should().ContainSingle().Which.Check.Should().Be(8);
    }

    [Fact(DisplayName = "#9 定義に無い要素（綴りの誤り）はエラー。綴りを確かめるよう案内する")]
    public void 知らない要素()
    {
        var xml = Ribbon("          <buton id=\"b\" label=\"A\"/>\n");

        var result = VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010);

        var finding = result.Errors.Should().ContainSingle().Subject;
        finding.Check.Should().Be(9);
        finding.Message.Should().Contain("『buton』は customUI の定義に無い要素です").And.Contain("綴り");
        finding.Line.Should().Be(7);
    }

    [Fact(DisplayName = "#9 定義に無い属性（lable）はエラー")]
    public void 知らない属性()
    {
        var xml = Ribbon("          <button id=\"b\" lable=\"A\"/>\n");

        var result = VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010);

        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("属性『lable』は customUI の定義に無い属性です");
    }

    [Fact(DisplayName = "#9 古い形式のファイルに新しい形式にだけある要素（backstage）はエラー。形式の取り違えと伝える")]
    public void 旧形式にbackstage()
    {
        var xml = "<customUI xmlns=\"" + Ns12 + "\"><backstage/></customUI>";

        var result = VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2007);

        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("customUI14.xml の形式にだけある要素");
    }

    [Fact(DisplayName = "#9 置き場所の誤り（tab の直下に button）はエラー")]
    public void 置き場所の誤り()
    {
        var xml = "<customUI xmlns=\"" + Ns14 + "\"><ribbon><tabs><tab id=\"t\" label=\"T\"><button id=\"b\" label=\"B\"/></tab></tabs></ribbon></customUI>";

        var result = VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010);

        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("button b が customUI の定義に合いません");
    }

    [Fact(DisplayName = "#10 列挙の値の誤り（size=\"big\"）はエラー")]
    public void 列挙の値()
    {
        var xml = Ribbon("          <button id=\"b\" label=\"A\" size=\"big\"/>\n");

        var result = VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010);

        var finding = result.Errors.Should().ContainSingle().Subject;
        finding.Check.Should().Be(10);
        finding.Message.Should().Contain("size").And.Contain("big");
    }

    [Fact(DisplayName = "#11 識別子（id・idMso・idQ）の無いコントロールは警告（止めない）")]
    public void 識別子なし()
    {
        var xml = Ribbon("          <button label=\"A\" onAction=\"OnRun\"/>\n");

        var result = VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010);

        result.HasErrors.Should().BeFalse(string.Join("\n", result.Findings.Select(f => f.ToDisplayText())));
        result.Warnings.Should().ContainSingle().Which.Check.Should().Be(11);
    }

    [Fact(DisplayName = "#11 separator（id・idQ だけを持てる要素）は識別子なしでも警告しない")]
    public void 区切りは識別子なしでよい()
    {
        var xml = Ribbon(OkButton + "          <separator/>\n");

        VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010).Findings.Should().BeEmpty();
    }

    [Fact(DisplayName = "#12 コールバックの呼び先が VBA に無い・Private なら警告。ある（標準モジュールの Public）なら何も出さない")]
    public void コールバックの照合()
    {
        var xml = Ribbon(
            "          <button id=\"b1\" label=\"A\" onAction=\"OnRun\"/>\n"
            + "          <button id=\"b2\" label=\"B\" onAction=\"OnMissing\"/>\n"
            + "          <button id=\"b3\" label=\"C\" onAction=\"OnPrivate\"/>\n");
        var modules = new[]
        {
            new VbaRibbonSourceModule("Module1", VbaModuleKind.Standard,
                "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub\r\nPrivate Sub OnPrivate(control As IRibbonControl)\r\nEnd Sub\r\n"),
        };

        var result = VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010, new VbaRibbonValidationContext(Modules: modules));

        result.HasErrors.Should().BeFalse();
        result.Warnings.Select(w => w.Check).Should().AllBeEquivalentTo(12);
        result.Warnings.Should().HaveCount(2);
        result.Warnings.Should().Contain(w => w.Message.Contains("OnMissing") && w.Message.Contains("VBA に見つかりません"));
        result.Warnings.Should().Contain(w => w.Message.Contains("OnPrivate") && w.Message.Contains("Public ではない"));
    }

    [Fact(DisplayName = "#12 モジュールを渡さないときは照合しない（「照合できていません」で一覧を埋めない）")]
    public void 照合しない()
    {
        var xml = Ribbon("          <button id=\"b1\" label=\"A\" onAction=\"OnMissing\"/>\n");

        VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010).Findings.Should().BeEmpty();
    }

    [Fact(DisplayName = "#13 画像の関係に無い image は警告。ある・loadImage があるときは出さない")]
    public void 画像()
    {
        var xml = Ribbon("          <button id=\"b1\" label=\"A\" image=\"logo\"/>\n");
        var withLoad = Ribbon("          <button id=\"b1\" label=\"A\" image=\"logo\"/>\n", rootAttributes: " loadImage=\"LoadImg\"");

        VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010, new VbaRibbonValidationContext(ImageIds: Array.Empty<string>()))
            .Warnings.Should().ContainSingle().Which.Message.Should().Contain("画像『logo』はブックにありません");
        VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010, new VbaRibbonValidationContext(ImageIds: new[] { "logo" }))
            .Findings.Should().BeEmpty();
        VbaRibbonXmlValidator.Validate(withLoad, VbaRibbonFormat.Office2010, new VbaRibbonValidationContext(ImageIds: Array.Empty<string>()))
            .Findings.Should().BeEmpty();
    }

    [Fact(DisplayName = "#14 startFromScratch=\"true\" は警告（標準のタブが隠れる）")]
    public void 標準のタブを隠す()
    {
        var xml = Ribbon(OkButton, ribbonAttributes: " startFromScratch=\"true\"");

        var result = VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010);

        result.HasErrors.Should().BeFalse();
        result.Warnings.Should().ContainSingle().Which.Check.Should().Be(14);
    }

    [Fact(DisplayName = "誤りが多いときは先頭 100 件と「ほか N 件」にまとめる")]
    public void 件数の上限()
    {
        // 列挙の値の誤りは要素ごとに 1 件ずつ出る（要素名の誤りは、スキーマの検査がその親の残りの子を読み飛ばすので 1 件になる）。
        var controls = string.Concat(Enumerable.Range(0, 150).Select(i => $"          <button id=\"b{i}\" label=\"A\" size=\"big\"/>\n"));

        var result = VbaRibbonXmlValidator.Validate(Ribbon(controls), VbaRibbonFormat.Office2010);

        result.Findings.Should().HaveCount(VbaRibbonXmlValidator.MaxFindings + 1);
        result.Findings[^1].Message.Should().StartWith("ほか ");
        result.HasErrors.Should().BeTrue();
    }

    [Fact(DisplayName = "エラーは警告より先に並ぶ")]
    public void 並び順()
    {
        var xml = Ribbon("          <button label=\"A\"/>\n          <button id=\"b\" label=\"A\" size=\"big\"/>\n");

        var result = VbaRibbonXmlValidator.Validate(xml, VbaRibbonFormat.Office2010);

        result.Findings.Select(f => f.Severity).Should().Equal(Severity.Error, Severity.Warning);
    }
}
