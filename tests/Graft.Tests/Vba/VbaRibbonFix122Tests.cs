using System.Text;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 1.2.2 に入るリボン（customUI）の読み取りで見つかった不具合の回帰テスト（純ロジック側）。
/// どのテストがどの不具合に対応するかは、各テストの表示名の先頭の「不具合 N」で分かる。
/// 修正前のコードでは、深さの上限・文字数の上限・定数の名前などを参照せずに書いてあり（文字の値で確かめる）、
/// 修正前の木を別のフォルダに取り出して、そこへこのファイルを置いて走らせると、それぞれ落ちる。
/// </summary>
public class VbaRibbonFix122Tests
{
    private static VbaRibbonPart Analyze(string xml)
        => VbaRibbonXmlAnalyzer.Analyze(new VbaRibbonRawPart("customUI/customUI14.xml", true, xml, null));

    private static string Nested(int depth)
    {
        var sb = new StringBuilder(depth * 7 + 16);
        for (var i = 0; i < depth; i++) sb.Append("<a>");
        for (var i = 0; i < depth; i++) sb.Append("</a>");
        return sb.ToString();
    }

    private static VbaRibbonSnapshot Snapshot(string xml, IReadOnlyList<VbaRibbonSourceModule>? modules = null)
    {
        var part = Analyze(xml);
        var checks = VbaRibbonCallbackMatcher.Check(part.Nodes.SelectMany(n => n.Callbacks), modules);
        return new VbaRibbonSnapshot(
            "book.xlsm", "C:\\Work\\book.xlsm", new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero), null,
            VbaRibbonReadStatus.Ok, null, new[] { part }, checks);
    }

    // ---------------- 不具合 1: 深い入れ子 ----------------

    [Fact(DisplayName = "不具合 1: 入れ子の深さが 128 段までは読み、129 段からは「入れ子が深すぎるため読みません」（純ロジックで上限を確かめる）")]
    public void 深さの上限()
    {
        Analyze(Nested(128)).Problem.Should().BeNull("128 段ちょうどは読む");

        var deep = Analyze(Nested(129));

        deep.IsUsable.Should().BeFalse();
        deep.Problem.Should().Contain("入れ子が深すぎるため読みません");
        deep.Nodes.Should().BeEmpty();
    }

    [Fact(DisplayName = "不具合 1: 25 万段の入れ子（zip で約 2.5 KB）を読んでも、プロセスは落ちず、読めない理由が返る（修正前はスタックオーバーフローでテストホストごと落ちる）")]
    public async Task 二十五万段でも落ちない()
    {
        using var ws = new TempWorkspace();
        var xml = Nested(250_000);
        var book = ws.WriteBytes("深い.xlsm", RibbonPackageBuilder.Workbook(customUi14: xml));

        var snapshot = await VbaRibbonCollector.CollectAsync(book, Path.Combine(ws.RootPath, "無いミラー"));

        snapshot.Status.Should().Be(VbaRibbonReadStatus.PartsUnreadable);
        snapshot.Reason.Should().Contain("入れ子が深すぎるため読みません");
        snapshot.HasContent.Should().BeFalse();
    }

    [Fact(DisplayName = "不具合 1: 構成をたどる順序は文書の順のまま（反復に書き換えても、親→子→次の兄弟の順と深さが変わらない）")]
    public void たどる順序()
    {
        var part = Analyze(
            "<customUI><ribbon><tabs><tab id=\"t1\"><group id=\"g1\"><button id=\"b1\"/><button id=\"b2\"/></group><group id=\"g2\"><button id=\"b3\"/></group></tab><tab id=\"t2\"/></tabs></ribbon></customUI>");

        part.Nodes.Select(n => (n.Depth, n.Display)).Should().Equal(
            (0, "ribbon"), (1, "tab t1"), (2, "group g1"), (3, "button b1"), (3, "button b2"),
            (2, "group g2"), (3, "button b3"), (1, "tab t2"));
    }

    // ---------------- 不具合 2・3: 依頼文の肥大化と改行の混入 ----------------

    private static string BigButtonXml(int idChars, int callbacks)
    {
        var sb = new StringBuilder("<customUI><ribbon><tabs><tab id=\"t\"><group id=\"g\"><button id=\"");
        sb.Append('x', idChars).Append('"');
        for (var i = 0; i < callbacks; i++) sb.Append(" getX").Append(i).Append("=\"Cb").Append(i).Append('"');
        sb.Append("/></group></tab></tabs></ribbon></customUI>");
        return sb.ToString();
    }

    [Theory(DisplayName = "不具合 2: id が 10 万文字・コールバック 200 個の button 1 つでも、「構成だけ」「内容も出す」の依頼文は元の XML の数倍以内（修正前は約 195 倍）")]
    [InlineData(VbaWorkbookInfoLevel.StructureOnly)]
    [InlineData(VbaWorkbookInfoLevel.Full)]
    public void 巨大なidの増幅(VbaWorkbookInfoLevel level)
    {
        var xml = BigButtonXml(100_000, 200);
        var snapshot = Snapshot(xml);

        var text = VbaRibbonFormatter.Format(snapshot, level, 30000);

        text.Length.Should().BeLessThan(150_000, $"元の XML は {xml.Length} 文字。構成と照合表は 1 項目 120 文字までに切り、全体にも上限がある");
        text.Should().Contain("…", "長い id は切って「…」を付ける");
    }

    [Fact(DisplayName = "不具合 2: 構成の取り出しの時点で、id・idMso・コールバック名・Owner は 1 行・最大 121 文字（120＋「…」）")]
    public void 取り出した値の長さ()
    {
        var part = Analyze(
            "<customUI><button id=\"" + new string('i', 5000) + "\" onAction=\"" + new string('n', 5000) + "\"/>"
            + "<button idMso=\"" + new string('m', 5000) + "\"/></customUI>");

        var buttons = part.Nodes.Where(n => n.Kind == "button").ToArray();

        buttons[0].Id!.Length.Should().Be(121);
        buttons[1].IdMso!.Length.Should().Be(121);
        buttons[0].Callbacks.Single().Name.Length.Should().Be(121);
        buttons[0].Callbacks.Single().Owner.Length.Should().BeLessThanOrEqualTo(121);
    }

    [Fact(DisplayName = "不具合 2: 構成の行が多くて長いときは、全体の文字数でも切って、省略した旨を書く（上限の変更は 1 行の長さに依存しない）")]
    public void 構成の全体の上限()
    {
        var sb = new StringBuilder("<customUI><ribbon><tabs><tab id=\"t\"><group id=\"g\">");
        for (var i = 0; i < 400; i++)
        {
            sb.Append("<button id=\"b").Append(i).Append(new string('x', 100)).Append('"');
            for (var c = 0; c < 12; c++) sb.Append(" getC").Append(c).Append("=\"").Append(new string('y', 100)).Append(c).Append(i).Append('"');
            sb.Append("/>");
        }

        sb.Append("</group></tab></tabs></ribbon></customUI>");
        var snapshot = Snapshot(sb.ToString());

        var text = VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.StructureOnly, 2000);

        text.Length.Should().BeLessThan(120_000, "構成の全体と照合表の全体に、それぞれ上限がある（2000 文字の XML の上限には縛られない）");
        text.Should().Contain("省略しました");
    }

    [Fact(DisplayName = "不具合 2: 1 つの要素に並べるコールバックは 12 個まで、残りは「ほか N 件」")]
    public void 要素あたりのコールバックの数()
    {
        var text = VbaRibbonFormatter.Format(Snapshot(BigButtonXml(5, 200)), VbaWorkbookInfoLevel.StructureOnly, 30000);

        text.Should().Contain("getX11=Cb11").And.NotContain("getX12=Cb12").And.Contain("ほか 188 件のコールバック");
    }

    [Fact(DisplayName = "不具合 3: id・idMso・コールバック名の改行（&#10;）で、依頼文の見出し・表の行・指示の行を作れない（1 行にそろえる）")]
    public void 改行の混入()
    {
        var xml = "<customUI><ribbon><tabs><tab id=\"t&#10;&#10;# 対象ファイル&#10;偽の指示\" label=\"L\">"
                  + "<button idMso=\"Copy&#13;&#10;## 偽の見出し\" onAction=\"X&#10;| a | b | c |&#10;## 偽\"/>"
                  + "</tab></tabs></ribbon></customUI>";
        var snapshot = Snapshot(xml);

        var text = VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.StructureOnly, 30000);

        var lines = text.Split('\n');
        lines.Should().NotContain(l => l.StartsWith("# 対象ファイル", StringComparison.Ordinal));
        lines.Should().NotContain(l => l.StartsWith("偽の指示", StringComparison.Ordinal));
        lines.Should().NotContain(l => l.StartsWith("## 偽", StringComparison.Ordinal));
        lines.Should().NotContain(l => l.StartsWith("| a | b | c |", StringComparison.Ordinal));
        text.Should().Contain("tab t # 対象ファイル 偽の指示", "改行は空白 1 つに畳まれ、同じ行の中に残る");
    }

    [Fact(DisplayName = "不具合 3: 読めなかった部品の名前の改行も、依頼文の行にならない")]
    public void 部品名の改行()
    {
        var bad = new VbaRibbonRawPart("customUI/a\n# 偽の見出し.xml", true, null, "壊れています。");
        var good = Analyze(RibbonPackageBuilder.SampleRibbon);
        var snapshot = new VbaRibbonSnapshot(
            "b.xlsm", "C:\\b.xlsm", DateTimeOffset.UnixEpoch, null, VbaRibbonReadStatus.Ok, null,
            new[] { good, new VbaRibbonPart(bad, Array.Empty<VbaRibbonNode>(), "壊れています。") },
            Array.Empty<VbaRibbonCallbackCheck>());

        VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.StructureOnly, 30000).Split('\n')
            .Should().NotContain(l => l.StartsWith("# 偽の見出し", StringComparison.Ordinal));
    }

    // ---------------- 不具合 4: 行継続 ----------------

    private static VbaRibbonSourceModule Std(string text) => new("Module1", VbaModuleKind.Standard, text);

    [Fact(DisplayName = "不具合 4: 「Private _」＋改行＋「Sub OnBtn」は Private の宣言（修正前は修飾なし＝Public と取り違えて「あります」）")]
    public void Privateの行継続()
    {
        var (status, _) = VbaRibbonCallbackMatcher.Resolve("OnBtn", new[] { Std("Private _\r\nSub OnBtn(c As IRibbonControl)\r\nEnd Sub\r\n") });

        status.Should().Be(VbaRibbonCallbackStatus.NotPublic);
    }

    [Fact(DisplayName = "不具合 4: コメント行の行継続（「' 古い実装 _」の次の行）は、コメントの続きで、手続きの宣言ではない")]
    public void コメントの行継続()
    {
        var (status, _) = VbaRibbonCallbackMatcher.Resolve("OnBtn", new[] { Std("' 古い実装 _\r\nSub OnBtn(c As IRibbonControl)\r\nEnd Sub\r\n") });

        status.Should().Be(VbaRibbonCallbackStatus.NotFound);
    }

    [Theory(DisplayName = "不具合 4: 行継続でつないだ Public の宣言は、見つかる（つなぐのは「空白＋_」の行末だけ）")]
    [InlineData("Public _\r\n    Sub OnBtn(c As IRibbonControl)\r\nEnd Sub\r\n")]
    [InlineData("Public Sub OnBtn( _\r\n    c As IRibbonControl)\r\nEnd Sub\r\n")]
    [InlineData("Dim a_\r\nSub OnBtn()\r\nEnd Sub\r\n")]
    public void Publicの行継続(string text)
    {
        VbaRibbonCallbackMatcher.Resolve("OnBtn", new[] { Std(text) }).Status.Should().Be(VbaRibbonCallbackStatus.Found);
    }

    // ---------------- 疑わしい点（直すと決めたもの） ----------------

    [Fact(DisplayName = "照合: クラス・フォーム・ドキュメントモジュールにしか Public が無いときは「あります」と断言せず、「標準モジュール以外にあります（リボンからは呼べない可能性があります）」")]
    public void 標準モジュール以外()
    {
        var cls = new VbaRibbonSourceModule("Handler", VbaModuleKind.Class, "Public Sub OnBtn()\r\nEnd Sub\r\n");

        var (status, detail) = VbaRibbonCallbackMatcher.Resolve("OnBtn", new[] { cls });

        status.ToString().Should().Be("FoundOutsideStandardModule");
        detail.Should().Contain("標準モジュール以外にあります（リボンからは呼べない可能性があります）").And.Contain("Handler");
    }

    [Fact(DisplayName = "照合: 標準モジュールにも Public があれば、従来どおり「あります」")]
    public void 標準モジュールにもある()
    {
        var cls = new VbaRibbonSourceModule("Handler", VbaModuleKind.Class, "Public Sub OnBtn()\r\nEnd Sub\r\n");

        var (status, detail) = VbaRibbonCallbackMatcher.Resolve("OnBtn", new[] { cls, Std("Public Sub OnBtn()\r\nEnd Sub\r\n") });

        status.Should().Be(VbaRibbonCallbackStatus.Found);
        detail.Should().StartWith("あります: Module1");
    }

    [Theory(DisplayName = "照合: CR だけの改行（\\r）の本文も行として扱う（修正前は 1 行に見えて NotFound）")]
    [InlineData("Public Sub OnBtn()\rEnd Sub\r")]
    [InlineData("Option Explicit\rPublic Sub OnBtn()\rEnd Sub")]
    public void CRだけの改行(string text)
    {
        VbaRibbonCallbackMatcher.Resolve("OnBtn", new[] { Std(text) }).Status.Should().Be(VbaRibbonCallbackStatus.Found);
    }

    [Fact(DisplayName = "照合: 「[Book.xlsm]Module1.OnBtn」の「[…]」を外して、モジュール名 Module1・手続き名 OnBtn に分ける（修正前は「xlsm]Module1」というモジュール名）")]
    public void ブラケット形式()
    {
        var (status, _) = VbaRibbonCallbackMatcher.Resolve("[Book.xlsm]Module1.OnBtn", new[] { Std("Public Sub OnBtn()\r\nEnd Sub\r\n") });

        status.Should().Be(VbaRibbonCallbackStatus.Found);
    }

    // ---------------- 不具合 5: 例外の漏れ ----------------

    [Fact(DisplayName = "不具合 5: 宣言が encoding=\"utf-7\" の部品でも、例外を出さずに UTF-8 として読む")]
    public void utf7の宣言()
    {
        var bytes = Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"utf-7\"?><customUI><button id=\"b\"/></customUI>");

        var act = () => VbaRibbonXmlAnalyzer.DecodeXml(bytes);

        act.Should().NotThrow();
        act().Should().Contain("customUI");
    }

    [Fact(DisplayName = "不具合 5: customUI の部品の宣言が utf-7 のブックを Read／Collect しても、例外を出さずに結果で返す")]
    public async Task utf7のブック()
    {
        using var ws = new TempWorkspace();
        var rels = RibbonPackageBuilder.Rels((RibbonPackageBuilder.RelNew, "customUI/customUI14.xml"));
        var zip = RibbonPackageBuilder.Zip(
            ("_rels/.rels", RibbonPackageBuilder.Utf8(rels)),
            ("customUI/customUI14.xml", Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"utf-7\"?><customUI><button id=\"b\"/></customUI>")));
        var book = ws.WriteBytes("u7.xlsm", zip);

        var read = () => VbaRibbonPackageReader.Read(book);
        read.Should().NotThrow();
        var collect = async () => await VbaRibbonCollector.CollectAsync(book, Path.Combine(ws.RootPath, "無い"));
        await collect.Should().NotThrowAsync();
    }

    [Fact(DisplayName = "不具合 5: パスに NUL 文字があっても、例外を出さずに「見つかりません」で返す")]
    public void NULを含むパス()
    {
        var act = () => VbaRibbonPackageReader.Read("C:\\Work\\a\0b.xlsm");

        act.Should().NotThrow();
        act().Status.Should().Be(VbaRibbonReadStatus.FileNotFound);
    }

    // ---------------- 不具合 8: 暗号化されたブック ----------------

    private static readonly byte[] Ole = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 };

    [Fact(DisplayName = "不具合 8: 拡張子が .xlsm なのに OLE のしるしで始まるファイルは「パスワードで暗号化されている（または古い形式の）ブックのため読めません」（修正前は「.xls は対象外」）")]
    public void 暗号化されたxlsm()
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteBytes("enc.xlsm", Ole);

        var result = VbaRibbonPackageReader.Read(path);

        result.Status.ToString().Should().Be("Encrypted");
        result.Reason.Should().Be("パスワードで暗号化されている（または古い形式の）ブックのため読めません。");
        result.Reason.Should().NotContain(".xls は");
    }

    [Theory(DisplayName = "不具合 8: 拡張子が .xls／.xla／.xlt のときは、従来どおり「古い形式（.xls）」の対象外（OLE のしるしの有無によらない）")]
    [InlineData("old.xls")]
    [InlineData("old.XLA")]
    [InlineData("old.xlt")]
    public void xls系は従来どおり(string name)
    {
        using var ws = new TempWorkspace();
        var path = ws.WriteBytes(name, Ole);

        var result = VbaRibbonPackageReader.Read(path);

        result.Status.Should().Be(VbaRibbonReadStatus.LegacyFormat);
        result.Reason.Should().Contain("古い形式（.xls）");
    }
}
