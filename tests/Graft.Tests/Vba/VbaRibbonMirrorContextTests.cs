using System.IO;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests.Vba;

/// <summary>
/// 仕様書_ブックの情報と依頼の観点 16.14（15 章との関係。段階 F2）: ミラーに <c>CustomUI/*.xml</c> があるとき、「リボン（customUI）」の
/// 文はミラーのファイルから作り、見出しをミラーのパスにし、ブックへの反映の状態を書く。XML の全文は、依頼文に 1 回だけ出す
/// （対象ファイルとして内容つきで載るなら、リボンの文には場所の 1 行だけ）。リボンの文がブックから読んだもののとき
/// （<see cref="ContextRequest.RibbonTextFor"/> が null）は、出力が従来と 1 バイトも変わらない。
/// </summary>
public class VbaRibbonMirrorContextTests
{
    private const string Ribbon14 = "CustomUI/customUI14.xml";

    private static Task<VbaRibbonSnapshot> FromMirrorAsync(TempWorkspace ws, bool unpushed, string xml = RibbonPackageBuilder.SampleRibbon)
    {
        var mirror = ws.CreateDirectory("mirror");
        ws.WriteText("mirror/Modules/Module1.bas", "Public Sub OnRun(control As IRibbonControl)\r\nEnd Sub\r\n");
        ws.WriteText("mirror/" + Ribbon14, xml);
        var files = new[]
        {
            new VbaRibbonContextFile(VbaRibbonFormat.Office2010, Ribbon14, xml, new DateTimeOffset(2026, 11, 23, 9, 30, 0, TimeSpan.Zero), unpushed),
        };
        return VbaRibbonCollector.CollectFromMirrorAsync(@"C:\Work\計画.xlsm", mirror, files);
    }

    [Fact(DisplayName = "ミラーから読んだ文: 読んだもの・反映の状態・部品の見出しがミラーのパス。照合はミラーの VBA で行う")]
    public async Task ミラーから読んだ文()
    {
        using var ws = new TempWorkspace();
        var snapshot = await FromMirrorAsync(ws, unpushed: true);

        snapshot.IsFromMirror.Should().BeTrue();
        snapshot.Parts.Single().Raw.PartName.Should().Be(Ribbon14);
        snapshot.Callbacks.Single(c => c.Name == "OnRun").Status.Should().Be(VbaRibbonCallbackStatus.Found);

        var text = VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, 30000);
        text.Should().StartWith("# リボン（customUI）\n\n- ブック: 計画.xlsm\n");
        text.Should().Contain("- 読んだもの: **Graft のミラーのファイル（CustomUI/customUI14.xml）の内容**です。ブックへは**まだ反映していません**。\n");
        text.Should().NotContain("最後に保存したとき", "ブックのファイルではないので、15 章の注意は書かない");
        text.Should().Contain("- 部品: CustomUI/customUI14.xml（新しい形式）\n");
        text.Should().Contain("### CustomUI/customUI14.xml\n\n```xml\n", "XML の見出しはミラーのパス（AI が search/replace の対象に書ける）");

        var pushed = await FromMirrorAsync(new TempWorkspace(), unpushed: false);
        VbaRibbonFormatter.Format(pushed, VbaWorkbookInfoLevel.Full, 30000).Should().Contain("の内容**です。ブックへは反映済みです。\n");
        pushed.Describe().Should().Contain("Graft のミラーのファイル CustomUI/customUI14.xml を読みました。ブックへは反映済みです。");
        snapshot.Describe().Should().Contain("まだ反映していません").And.NotContain("**");
    }

    [Fact(DisplayName = "XML は片方にだけ出す: 対象ファイルとして載るなら場所の 1 行だけ。載らなければ全文（見出しはミラーのパス）")]
    public async Task XMLは片方にだけ()
    {
        using var ws = new TempWorkspace();
        var snapshot = await FromMirrorAsync(ws, unpushed: false);
        var inFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "customui/CUSTOMUI14.XML" };

        var oneLine = VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, 30000, inFiles);
        oneLine.Should().Contain("## XML\n\nXML の全文は、対象ファイルの `CustomUI/customUI14.xml` にあります。\n");
        oneLine.Should().NotContain("```xml").And.NotContain("<customUI");
        oneLine.Should().Contain("## 構成", "構成とコールバックの照合は、リボンの文に残す");

        VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, 30000, new HashSet<string> { "Modules/Module1.bas" })
            .Should().Contain("<customUI");
        VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.StructureOnly, 30000, inFiles)
            .Should().Be(VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.StructureOnly, 30000), "構成だけなら XML はもともと出さない");
    }

    [Fact(DisplayName = "ブックから読んだ文は、対象ファイルの集合を渡しても変わらない（ブックの部品は対象ファイルにならない）")]
    public async Task ブックから読んだ文は変わらない()
    {
        using var ws = new TempWorkspace();
        var book = ws.WriteBytes("計画.xlsm", RibbonPackageBuilder.Workbook(RibbonPackageBuilder.SampleRibbon));
        var snapshot = await VbaRibbonCollector.CollectAsync(book, ws.CreateDirectory("mirror"));
        snapshot.IsFromMirror.Should().BeFalse();

        VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, 30000, new HashSet<string> { "customUI/customUI14.xml", Ribbon14 })
            .Should().Be(VbaRibbonFormatter.Format(snapshot, VbaWorkbookInfoLevel.Full, 30000));
    }

    // ------------------------------------------------------------------
    // 収集（ContextCollector）と依頼文（PromptTemplateRenderer）での差し込み
    // ------------------------------------------------------------------

    private const string Full = "# リボン（customUI）\n\n全文 <customUI/>\n";
    private const string Short = "# リボン（customUI）\n\nXML の全文は、対象ファイルの `CustomUI/customUI14.xml` にあります。\n";

    private static (ContextCollector Collector, ContextRequest Request) Build(
        TempWorkspace ws, bool withRibbonTextFor, ContextMode mode, params string[] selected)
    {
        ws.WriteText(Ribbon14, "<customUI/>\n");
        ws.WriteText("Modules/Module1.bas", "Sub A()\r\nEnd Sub\r\n");
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var project = new Project { Id = "p_rb", Name = "rb", Root = ws.RootPath };
        var request = new ContextRequest
        {
            Project = project,
            Settings = new Settings(),
            Mode = mode,
            SelectedPaths = selected,
            RibbonText = Full,
            RibbonTextFor = withRibbonTextFor ? files => files.Contains(Ribbon14) ? Short : Full : null,
        };
        return (new ContextCollector(paths), request);
    }

    [Fact(DisplayName = "収集: ミラーのリボンのファイルを選んでいれば、リボンの文は短い方。選んでいなければ全文。RibbonTextFor が無ければ従来のまま")]
    public async Task 収集での出し分け()
    {
        using var ws = new TempWorkspace();
        var (collector, selected) = Build(ws, true, ContextMode.TreeAndSelected, Ribbon14);
        var text = (await collector.CollectAsync(selected)).Value.Text;
        text.Should().Contain(Short.TrimEnd('\n')).And.NotContain("全文 <customUI/>");
        text.Should().Contain("# CustomUI/customUI14.xml", "対象ファイルとして全文が載る");

        var (_, notSelected) = Build(ws, true, ContextMode.TreeAndSelected, "Modules/Module1.bas");
        (await collector.CollectAsync(notSelected)).Value.Text.Should().Contain("全文 <customUI/>");

        var (_, treeOnly) = Build(ws, true, ContextMode.TreeOnly, Ribbon14);
        (await collector.CollectAsync(treeOnly)).Value.Text.Should().Contain("全文 <customUI/>", "ファイルの中身を出さないモードでは、リボンの文に全文");

        // RibbonTextFor が無い（ブックから読んだリボン・リボンが無い）なら、RibbonText をそのまま使う＝従来と 1 バイトも変わらない。
        var (_, plain) = Build(ws, false, ContextMode.TreeAndSelected, Ribbon14);
        var plainText = (await collector.CollectAsync(plain)).Value.Text;
        plainText.Should().Contain("全文 <customUI/>");
        Normalize(plainText).Should().Be(Normalize((await collector.CollectAsync(plain with { RibbonTextFor = null })).Value.Text));
    }

    [Fact(DisplayName = "依頼文: {{files}}・自動付加の対象ファイルにミラーのリボンのファイルが載るなら短い方。ファイルを出さないテンプレートでは全文")]
    public async Task 依頼文での出し分け()
    {
        using var ws = new TempWorkspace();
        var (collector, request) = Build(ws, true, ContextMode.TreeAndSelected, Ribbon14);
        var renderer = new PromptTemplateRenderer(collector);

        (await renderer.RenderAsync(Template("指示\n\n# 対象ファイル\n{{files}}"), request, null)).Value
            .Should().Contain(Short.TrimEnd('\n')).And.NotContain("全文 <customUI/>");
        (await renderer.RenderAsync(Template("指示だけ"), request, null, appendFilesIfAbsent: true)).Value
            .Should().Contain(Short.TrimEnd('\n'));
        (await renderer.RenderAsync(Template("指示だけ"), request, null, appendFilesIfAbsent: false)).Value
            .Should().Contain("全文 <customUI/>", "対象ファイルが載らないので、リボンの文に全文を出す");

        var (_, plain) = Build(ws, false, ContextMode.TreeAndSelected, Ribbon14);
        var withFor = await renderer.RenderAsync(Template("指示\n\n{{ribbon}}\n\n# 対象ファイル\n{{files}}"), plain, null);
        var without = await renderer.RenderAsync(Template("指示\n\n{{ribbon}}\n\n# 対象ファイル\n{{files}}"), plain with { RibbonText = Full }, null);
        withFor.Value.Should().Be(without.Value, "RibbonTextFor が無ければ、従来と同じ");
    }

    private static PromptTemplate Template(string body) => new() { Id = "t", Name = "t", Body = body };

    /// <summary>収集の出力の冒頭の生成日時（実行のたびに変わる）を除く。</summary>
    private static string Normalize(string text)
        => string.Join("\n", text.Split('\n').Where(l => !l.Contains("生成日時", StringComparison.Ordinal)));
}
