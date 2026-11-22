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
/// ブックの情報（<see cref="ContextRequest.WorkbookInfoText"/>・<c>{{workbook}}</c>）の差し込みのテスト。
/// 仕様書_ブックの情報と依頼の観点 5.3・6.3・11.1。並びは「前提 → ブックの情報 → 観点 → 対象ファイル」。
/// 何も無いときの出力が従来と1バイトも変わらないことは <c>PromptTemplateRendererFocusTests</c> が固定している
/// （同じ新旧比較に、この機能を足したあとでも通ること）。ここでは足したものの位置と、改行の規則を固める。
/// </summary>
public class WorkbookInfoInsertionTests
{
    private const string Workbook = "# ブックの情報\n（Excel から取得: X / a.xlsm）\n\n## シート\n| シート名 |\n|---|\n| 集計 |\n";
    private const string Focus = "# 依頼の観点\n\n## 読みやすく\n- 観点の文\n\n## 進め方の決まり\n- 決まり\n";

    private static PromptTemplate Template(string body) => new() { Id = "t", Name = "t", Body = body };

    private static (PromptTemplateRenderer Renderer, ContextRequest Request) Build(
        TempWorkspace ws, string workbook = "", string focus = "", params string[] selected)
    {
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var project = new Project { Id = "p_wb", Name = "wb", Root = ws.RootPath, StandingContext = "前提の文" };
        var request = new ContextRequest
        {
            Project = project,
            Settings = new Settings(),
            Mode = ContextMode.TreeAndSelected,
            SelectedPaths = selected,
            WorkbookInfoText = workbook,
            FocusText = focus,
        };
        return (new PromptTemplateRenderer(new ContextCollector(paths)), request);
    }

    private static int Index(string text, string needle) => text.IndexOf(needle, StringComparison.Ordinal);

    [Fact(DisplayName = "{{workbook}} はブックの情報の文に展開される")]
    public async Task workbook変数は展開される()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws, Workbook);

        var result = await renderer.RenderAsync(Template("前置き\n\n{{workbook}}\n\n後書き"), request, null);

        result.Value.Should().Be("前置き\n\n" + Workbook.TrimEnd('\n') + "\n\n後書き");
    }

    [Fact(DisplayName = "{{workbook}} は、ブックの情報が無ければ前後の空行ごと消えて痕跡を残さない")]
    public async Task workbook変数は空なら痕跡なし()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws);

        (await renderer.RenderAsync(Template("前置き\n\n{{workbook}}\n\n後書き"), request, null)).Value
            .Should().Be("前置き\n\n後書き");
        (await renderer.RenderAsync(Template("{{workbook}}\n\n本文"), request, null)).Value
            .Should().Be("本文");
    }

    [Fact(DisplayName = "変数が無いテンプレートでは、観点と同じ位置（# 対象ファイルの直前）に、観点より前に入る")]
    public async Task 変数が無ければ観点の前に入る()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "A_MARK = 1\n");
        var (renderer, request) = Build(ws, Workbook, Focus, "a.py");

        var text = (await renderer.RenderAsync(
            Template("指示\n\n# 前提\n{{standingContext}}\n\n# 対象ファイル\n{{files}}"), request, null)).Value;

        var order = new[] { "# 前提", "# ブックの情報", "# 依頼の観点", "# 対象ファイル", "A_MARK" }
            .Select(n => Index(text, n)).ToArray();
        order.Should().OnlyContain(i => i >= 0);
        order.Should().BeInAscendingOrder("前提 → ブックの情報 → 観点 → 対象ファイル");
        text.Should().Contain("| 集計 |\n\n# 依頼の観点\n", "ブックの情報と観点の間は空行1つ");
        text.Should().Contain("- 決まり\n\n# 対象ファイル\n");
    }

    [Fact(DisplayName = "ブックの情報だけがあるとき: ①対象ファイル見出しの直前、③自動付加の見出しの直前、④末尾")]
    public async Task ブックの情報だけの位置()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "A_MARK = 1\n");
        var (renderer, request) = Build(ws, Workbook, "", "a.py");

        var one = (await renderer.RenderAsync(Template("指示\n\n# 対象ファイル\n{{files}}"), request, null)).Value;
        one.Should().Contain("| 集計 |\n\n# 対象ファイル\n");
        Index(one, "# ブックの情報").Should().BeLessThan(Index(one, "A_MARK"));

        var three = (await renderer.RenderAsync(Template("指示だけ"), request, null, appendFilesIfAbsent: true)).Value;
        three.Should().StartWith("指示だけ\n\n# ブックの情報\n");
        three.Should().Contain("| 集計 |\n\n# 対象ファイル\n");

        var four = (await renderer.RenderAsync(Template("指示だけ"), request, null)).Value;
        four.Should().Be("指示だけ\n\n" + Workbook);
    }

    [Fact(DisplayName = "{{focus}} だけがあるテンプレート: ブックの情報は {{focus}} の直前に付く（並びを守る）")]
    public async Task focus変数だけならその直前()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws, Workbook, Focus);

        var text = (await renderer.RenderAsync(Template("前置き\n\n{{focus}}\n\n後書き"), request, null)).Value;

        text.Should().Be("前置き\n\n" + Workbook.TrimEnd('\n') + "\n\n" + Focus.TrimEnd('\n') + "\n\n後書き");
    }

    [Fact(DisplayName = "{{focus}} だけがあり観点が空なら、ブックの情報は通常の位置（末尾など）に入り、{{focus}} は痕跡なしで消える")]
    public async Task focus変数が空ならブックの情報は別の位置()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws, Workbook);

        var text = (await renderer.RenderAsync(Template("前置き\n\n{{focus}}\n\n後書き"), request, null)).Value;

        text.Should().Be("前置き\n\n後書き\n\n" + Workbook);
    }

    [Fact(DisplayName = "{{workbook}} だけがあるテンプレート: 観点はその直後に付く（並びを守る）")]
    public async Task workbook変数だけなら観点は直後()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws, Workbook, Focus);

        var text = (await renderer.RenderAsync(Template("前置き\n\n{{workbook}}\n\n後書き"), request, null)).Value;

        text.Should().Be("前置き\n\n" + Workbook.TrimEnd('\n') + "\n\n" + Focus.TrimEnd('\n') + "\n\n後書き");
    }

    [Fact(DisplayName = "両方の変数があれば、それぞれの位置に入る")]
    public async Task 両方の変数()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws, Workbook, Focus);

        var text = (await renderer.RenderAsync(Template("{{workbook}}\n\n中間\n\n{{focus}}"), request, null)).Value;

        text.Should().Be(Workbook.TrimEnd('\n') + "\n\n中間\n\n" + Focus.TrimEnd('\n'));
    }

    [Fact(DisplayName = "ファイルの中身や自作の観点に {{workbook}} があっても、再展開されない")]
    public async Task 再展開されない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x = '{{workbook}}'  # {{focus}}\n");
        var focusWithVariable = "# 依頼の観点\n\n## 自作\n- {{workbook}} と {{files}} と書いてある\n";
        var (renderer, request) = Build(ws, Workbook, focusWithVariable, "a.py");

        var text = (await renderer.RenderAsync(Template("{{workbook}}\n\n{{focus}}\n\n# 対象ファイル\n{{files}}"), request, null)).Value;

        Regex.Matches(text, Regex.Escape("# ブックの情報")).Count.Should().Be(1, "ブックの情報は1回だけ");
        text.Should().Contain("x = '{{workbook}}'  # {{focus}}", "ファイルの中身は置換されない");
        text.Should().Contain("- {{workbook}} と {{files}} と書いてある", "自作の観点の文は再展開されない");
    }

    [Fact(DisplayName = "テンプレート本文が CRLF なら、差し込む文の改行も本文に合わせて CRLF になる（混ざらない）")]
    public async Task テンプレートの改行に合わせる()
    {
        using var ws = new TempWorkspace();
        var (renderer, request) = Build(ws, Workbook, Focus);

        var text = (await renderer.RenderAsync(Template("前置き\r\n\r\n{{workbook}}\r\n\r\n後書き"), request, null)).Value;

        Regex.IsMatch(text, @"(?<!\r)\n").Should().BeFalse("LF だけの改行が残っていない");
        text.Should().Contain("| 集計 |\r\n");
    }

    // ------------------------------------------------------------------
    // コンテキスト収集の出力（CollectAsync）
    // ------------------------------------------------------------------

    private static ContextRequest Collect(string root, string workbook, string focus, string? standing)
        => new()
        {
            Project = new Project { Id = "p_wbc", Name = "wbc", Root = root, StandingContext = standing },
            Settings = new Settings(),
            Mode = ContextMode.TreeAndSelected,
            SelectedPaths = new[] { "main.py" },
            WorkbookInfoText = workbook,
            FocusText = focus,
        };

    private static string Normalize(string text) => Regex.Replace(text, @"- 生成日時: .*", "- 生成日時: X");

    [Fact(DisplayName = "収集の出力: 「# 前提」の直後・観点より前・構成の前に入る")]
    public async Task 収集の出力の位置()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("main.py", "print(1)\n");
        var collector = new ContextCollector(new AppPaths(ws.CreateDirectory("app")));

        var text = (await collector.CollectAsync(Collect(ws.RootPath, Workbook, Focus, "Python 3.12"))).Value.Text;

        var order = new[] { "# 前提", "# ブックの情報", "# 依頼の観点", "# プロジェクト構成" }.Select(n => Index(text, n)).ToArray();
        order.Should().OnlyContain(i => i >= 0);
        order.Should().BeInAscendingOrder();
        Regex.IsMatch(text, @"\| 集計 \|\r?\n\r?\n# 依頼の観点").Should().BeTrue();
    }

    [Fact(DisplayName = "収集の出力: ブックの情報が空なら、従来と同一（観点だけのときも、何も無いときも）")]
    public async Task 空なら従来と同一()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("main.py", "print(1)\n");
        var collector = new ContextCollector(new AppPaths(ws.CreateDirectory("app")));

        var plain = Normalize((await collector.CollectAsync(Collect(ws.RootPath, "", "", "前提"))).Value.Text);
        var withEmptyWorkbook = Normalize((await collector.CollectAsync(Collect(ws.RootPath, string.Empty, "", "前提"))).Value.Text);
        var focusOnly = Normalize((await collector.CollectAsync(Collect(ws.RootPath, "", Focus, "前提"))).Value.Text);
        var both = Normalize((await collector.CollectAsync(Collect(ws.RootPath, Workbook, Focus, "前提"))).Value.Text);

        withEmptyWorkbook.Should().Be(plain);
        plain.Should().NotContain("ブックの情報");
        focusOnly.Should().Contain("# 依頼の観点").And.NotContain("ブックの情報");
        both.Should().Contain("# ブックの情報");
    }

    [Theory(DisplayName = "改行のそろえ方: LF・CRLF・CR の混在を、指定の改行ひとつにそろえる")]
    [InlineData("a\nb\r\nc\rd", "\n", "a\nb\nc\nd")]
    [InlineData("a\nb\r\nc\rd", "\r\n", "a\r\nb\r\nc\r\nd")]
    [InlineData("", "\r\n", "")]
    [InlineData("改行なし", "\r\n", "改行なし")]
    public void 改行をそろえる(string input, string newLine, string expected)
    {
        ContextCollector.NormalizeNewLines(input, newLine).Should().Be(expected);
    }
}
