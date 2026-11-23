using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// リボン（<see cref="ContextRequest.RibbonText"/>・<c>{{ribbon}}</c>）の差し込みのテスト。
/// 仕様書_ブックの情報と依頼の観点 15.6・15.8。並びは「前提 → ブックの情報 → リボン → 観点 → 対象ファイル」。
/// リボンが空なら、ブックの情報・観点だけのときの出力が従来と1バイトも変わらないことは
/// <c>WorkbookInfoInsertionTests</c>・<c>PromptTemplateRendererFocusTests</c> が固定している（この機能を足したあとでも通ること）。
/// </summary>
public class RibbonInsertionTests
{
    private const string Workbook = "# ブックの情報\n\n## シート\n| 集計 |\n";
    private const string Ribbon = "# リボン（customUI）\n\n## 構成\n- tab tabMain\n";
    private const string Focus = "# 依頼の観点\n\n## 読みやすく\n- 観点の文\n";

    private static PromptTemplate Template(string body) => new() { Id = "t", Name = "t", Body = body };

    private static (PromptTemplateRenderer Renderer, ContextRequest Request) Build(
        TempWorkspace ws, string workbook = "", string ribbon = "", string focus = "", params string[] selected)
    {
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var project = new Project { Id = "p_rb", Name = "rb", Root = ws.RootPath, StandingContext = "前提の文" };
        var request = new ContextRequest
        {
            Project = project,
            Settings = new Settings(),
            Mode = ContextMode.TreeAndSelected,
            SelectedPaths = selected,
            WorkbookInfoText = workbook,
            RibbonText = ribbon,
            FocusText = focus,
        };
        return (new PromptTemplateRenderer(new ContextCollector(paths)), request);
    }

    private static string Block(string text) => text.TrimEnd('\n');

    private static int Index(string text, string needle) => text.IndexOf(needle, StringComparison.Ordinal);

    [Fact(DisplayName = "{{ribbon}} はリボンの文に展開される。空なら前後の空行ごと消えて痕跡を残さない")]
    public async Task ribbon変数()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws, ribbon: Ribbon);
        (await renderer.RenderAsync(Template("前置き\n\n{{ribbon}}\n\n後書き"), request, null)).Value
            .Should().Be("前置き\n\n" + Block(Ribbon) + "\n\n後書き");

        var (renderer2, empty) = Build(ws);
        (await renderer2.RenderAsync(Template("前置き\n\n{{ribbon}}\n\n後書き"), empty, null)).Value
            .Should().Be("前置き\n\n後書き");
        (await renderer2.RenderAsync(Template("{{ribbon}}\n\n本文"), empty, null)).Value.Should().Be("本文");
    }

    [Fact(DisplayName = "変数が無いテンプレートでは、ブックの情報の後・観点の前・対象ファイルの前に入る")]
    public async Task 変数が無ければ並びを守る()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "A_MARK = 1\n");
        var (renderer, request) = Build(ws, Workbook, Ribbon, Focus, "a.py");

        var text = (await renderer.RenderAsync(
            Template("指示\n\n# 前提\n{{standingContext}}\n\n# 対象ファイル\n{{files}}"), request, null)).Value;

        var order = new[] { "# 前提", "# ブックの情報", "# リボン（customUI）", "# 依頼の観点", "# 対象ファイル", "A_MARK" }
            .Select(n => Index(text, n)).ToArray();
        order.Should().OnlyContain(i => i >= 0);
        order.Should().BeInAscendingOrder("前提 → ブックの情報 → リボン → 観点 → 対象ファイル");
        text.Should().Contain("| 集計 |\n\n# リボン（customUI）\n").And.Contain("- tab tabMain\n\n# 依頼の観点\n");
    }

    [Fact(DisplayName = "リボンだけがあるとき（ブックの情報も観点も無い）: 対象ファイルの直前・自動付加の直前・末尾")]
    public async Task リボンだけの位置()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "A_MARK = 1\n");
        var (renderer, request) = Build(ws, ribbon: Ribbon, selected: "a.py");

        var one = (await renderer.RenderAsync(Template("指示\n\n# 対象ファイル\n{{files}}"), request, null)).Value;
        one.Should().Contain("- tab tabMain\n\n# 対象ファイル\n");

        var three = (await renderer.RenderAsync(Template("指示だけ"), request, null, appendFilesIfAbsent: true)).Value;
        three.Should().StartWith("指示だけ\n\n# リボン（customUI）\n");
        three.Should().Contain("- tab tabMain\n\n# 対象ファイル\n");

        (await renderer.RenderAsync(Template("指示だけ"), request, null)).Value.Should().Be("指示だけ\n\n" + Ribbon);
    }

    [Fact(DisplayName = "{{workbook}} だけがあるテンプレート: リボンと観点は、その直後にこの順で付く")]
    public async Task workbook変数だけ()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws, Workbook, Ribbon, Focus);

        var text = (await renderer.RenderAsync(Template("前置き\n\n{{workbook}}\n\n後書き"), request, null)).Value;

        text.Should().Be("前置き\n\n" + Block(Workbook) + "\n\n" + Block(Ribbon) + "\n\n" + Block(Focus) + "\n\n後書き");
    }

    [Fact(DisplayName = "{{focus}} だけがあるテンプレート: ブックの情報とリボンは、その直前にこの順で付く")]
    public async Task focus変数だけ()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws, Workbook, Ribbon, Focus);

        var text = (await renderer.RenderAsync(Template("前置き\n\n{{focus}}\n\n後書き"), request, null)).Value;

        text.Should().Be("前置き\n\n" + Block(Workbook) + "\n\n" + Block(Ribbon) + "\n\n" + Block(Focus) + "\n\n後書き");
    }

    [Fact(DisplayName = "{{ribbon}} だけがあるテンプレート: ブックの情報はその直前、観点は直後に付く")]
    public async Task ribbon変数だけ()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws, Workbook, Ribbon, Focus);

        var text = (await renderer.RenderAsync(Template("前置き\n\n{{ribbon}}\n\n後書き"), request, null)).Value;

        text.Should().Be("前置き\n\n" + Block(Workbook) + "\n\n" + Block(Ribbon) + "\n\n" + Block(Focus) + "\n\n後書き");
    }

    [Fact(DisplayName = "3 つの変数があれば、それぞれの位置に入る")]
    public async Task 三つの変数()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws, Workbook, Ribbon, Focus);

        var text = (await renderer.RenderAsync(Template("{{workbook}}\n\nA\n\n{{ribbon}}\n\nB\n\n{{focus}}"), request, null)).Value;

        text.Should().Be(Block(Workbook) + "\n\nA\n\n" + Block(Ribbon) + "\n\nB\n\n" + Block(Focus));
    }

    [Fact(DisplayName = "{{workbook}} と {{focus}} があり、リボンの変数が無いとき: リボンはブックの情報の直後に付く（並びを守る）")]
    public async Task 変数が無いリボンはブックの情報の直後()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws, Workbook, Ribbon, Focus);

        var text = (await renderer.RenderAsync(Template("{{workbook}}\n\n中間\n\n{{focus}}"), request, null)).Value;

        text.Should().Be(Block(Workbook) + "\n\n" + Block(Ribbon) + "\n\n中間\n\n" + Block(Focus));
    }

    [Fact(DisplayName = "リボンの文が空なら、{{ribbon}} は消え、ほかの文は従来どおりの位置に入る（ブックの情報・観点だけの出力と同じ）")]
    public async Task リボンが空なら従来と同じ()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "A_MARK = 1\n");
        foreach (var body in new[]
                 {
                     "指示\n\n# 対象ファイル\n{{files}}", "{{workbook}}\n\n中間\n\n{{focus}}", "前置き\n\n{{focus}}\n\n後書き",
                     "前置き\n\n{{workbook}}\n\n後書き", "指示だけ",
                 })
        {
            var (renderer, without) = Build(ws, Workbook, "", Focus, "a.py");
            var plain = (await renderer.RenderAsync(Template(body), without, null)).Value;
            var withVariable = (await renderer.RenderAsync(Template(body.Replace("\n\n中間", "\n\n{{ribbon}}\n\n中間")), without, null)).Value;

            // {{ribbon}} を足したテンプレートでも、リボンが空なら痕跡が残らない（足した行ごと消える）。
            withVariable.Should().Be(plain, $"テンプレート「{body}」");
            plain.Should().NotContain("リボン");
        }
    }

    [Fact(DisplayName = "ファイルの中身や自作の観点に {{ribbon}} があっても、再展開されない")]
    public async Task 再展開されない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x = '{{ribbon}}'\n");
        var (renderer, request) = Build(ws, Workbook, Ribbon, "# 依頼の観点\n- {{ribbon}} と書いてある\n", "a.py");

        var text = (await renderer.RenderAsync(Template("{{ribbon}}\n\n{{focus}}\n\n# 対象ファイル\n{{files}}"), request, null)).Value;

        Regex.Matches(text, Regex.Escape("# リボン（customUI）")).Count.Should().Be(1);
        text.Should().Contain("x = '{{ribbon}}'").And.Contain("- {{ribbon}} と書いてある");
    }

    [Fact(DisplayName = "テンプレート本文が CRLF なら、リボンの文の改行も CRLF にそろう（混ざらない）")]
    public async Task 改行をそろえる()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws, Workbook, Ribbon, Focus);

        var text = (await renderer.RenderAsync(Template("前置き\r\n\r\n{{ribbon}}\r\n\r\n後書き"), request, null)).Value;

        Regex.IsMatch(text, @"(?<!\r)\n").Should().BeFalse("LF だけの改行が残っていない");
        text.Should().Contain("- tab tabMain\r\n");
    }

    // ------------------------------------------------------------------
    // コンテキスト収集の出力（CollectAsync）
    // ------------------------------------------------------------------

    private static ContextRequest Collect(string root, string workbook, string ribbon, string focus)
        => new()
        {
            Project = new Project { Id = "p_rbc", Name = "rbc", Root = root, StandingContext = "前提" },
            Settings = new Settings(),
            Mode = ContextMode.TreeAndSelected,
            SelectedPaths = new[] { "main.py" },
            WorkbookInfoText = workbook,
            RibbonText = ribbon,
            FocusText = focus,
        };

    private static string Normalize(string text) => Regex.Replace(text, @"- 生成日時: .*", "- 生成日時: X");

    [Fact(DisplayName = "収集の出力: 「# 前提」→ブックの情報→リボン→観点→構成の順に入る")]
    public async Task 収集の出力の位置()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("main.py", "print(1)\n");
        var collector = new ContextCollector(new AppPaths(ws.CreateDirectory("app")));

        var text = (await collector.CollectAsync(Collect(ws.RootPath, Workbook, Ribbon, Focus))).Value.Text;

        var order = new[] { "# 前提", "# ブックの情報", "# リボン（customUI）", "# 依頼の観点", "# プロジェクト構成" }
            .Select(n => Index(text, n)).ToArray();
        order.Should().OnlyContain(i => i >= 0);
        order.Should().BeInAscendingOrder();
        Regex.IsMatch(text, @"- tab tabMain\r?\n\r?\n# 依頼の観点").Should().BeTrue();
    }

    [Fact(DisplayName = "収集の出力: リボンが空なら従来と同一（ブックの情報・観点だけのとき、何も無いときも）")]
    public async Task 空なら従来と同一()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("main.py", "print(1)\n");
        var collector = new ContextCollector(new AppPaths(ws.CreateDirectory("app")));

        var baseline = Normalize((await collector.CollectAsync(Collect(ws.RootPath, Workbook, "", Focus))).Value.Text);
        var explicitEmpty = Normalize((await collector.CollectAsync(Collect(ws.RootPath, Workbook, string.Empty, Focus))).Value.Text);
        var nothing = Normalize((await collector.CollectAsync(Collect(ws.RootPath, "", "", ""))).Value.Text);
        var withRibbon = Normalize((await collector.CollectAsync(Collect(ws.RootPath, Workbook, Ribbon, Focus))).Value.Text);

        explicitEmpty.Should().Be(baseline);
        baseline.Should().NotContain("リボン");
        nothing.Should().NotContain("リボン").And.NotContain("ブックの情報");
        withRibbon.Should().Contain("# リボン（customUI）");
    }
}
