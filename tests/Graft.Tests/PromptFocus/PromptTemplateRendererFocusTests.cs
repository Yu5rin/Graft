using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core.PromptFocus;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 依頼の観点（<see cref="ContextRequest.FocusText"/>）の差し込みのテスト。
/// 仕様書_ブックの情報と依頼の観点 6.3・11.1。
///
/// 最重要は「<b>何も選ばなければ、依頼文が従来とバイト単位で同一</b>」。これを固定するため、観点の機能を足す前の
/// <see cref="PromptTemplateRenderer.RenderAsync"/> のアルゴリズムを <see cref="LegacyRender"/> として
/// そのまま写し、組み込みの全テンプレートで新旧の出力が一致することを確かめる。
/// </summary>
public class PromptTemplateRendererFocusTests
{
    private const string Focus = "# 依頼の観点\n\n## 読みやすく\n- 観点の文\n\n## 進め方の決まり\n- 決まり\n";

    private static PromptTemplate Template(string body) => new() { Id = "t", Name = "t", Body = body };

    private static (PromptTemplateRenderer Renderer, ContextCollector Collector, ContextRequest Request) Build(
        TempWorkspace ws, string focusText = "", params string[] selected)
    {
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var project = new Project { Id = "p_focus", Name = "focus", Root = ws.RootPath, StandingContext = "前提の文" };
        var collector = new ContextCollector(paths);
        var request = new ContextRequest
        {
            Project = project,
            Settings = new Settings(),
            Mode = ContextMode.TreeAndSelected,
            SelectedPaths = selected,
            FocusText = focusText,
        };
        return (new PromptTemplateRenderer(collector), collector, request);
    }

    /// <summary>観点の機能を足す前の RenderAsync の中身（そのまま）。新旧の同一性の物差し。</summary>
    private static async Task<string> LegacyRender(
        PromptTemplate template, ContextCollector collector, ContextRequest request, string? lastRevision, bool appendFilesIfAbsent)
    {
        var body = template.Body;
        var hasFilesVariable = body.Contains("{{files}}", StringComparison.Ordinal);
        if (body.Contains("{{tree}}", StringComparison.Ordinal))
        {
            var tree = await collector.BuildTreeTextAsync(request.Project, request.Settings);
            body = body.Replace("{{tree}}", tree.Value, StringComparison.Ordinal);
        }
        if (body.Contains("{{files}}", StringComparison.Ordinal))
        {
            var files = await collector.BuildFilesTextAsync(request);
            body = body.Replace("{{files}}", files.Value, StringComparison.Ordinal);
        }
        string? appendedFiles = null;
        if (appendFilesIfAbsent && !hasFilesVariable)
        {
            var files = await collector.BuildFilesTextAsync(request);
            if (!string.IsNullOrWhiteSpace(files.Value)) appendedFiles = files.Value;
        }
        body = body
            .Replace("{{standingContext}}", request.Project.StandingContext ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{projectName}}", request.Project.Name, StringComparison.Ordinal)
            .Replace("{{lastRevision}}", lastRevision ?? string.Empty, StringComparison.Ordinal);
        if (appendedFiles is not null)
        {
            body = body.TrimEnd('\r', '\n') + "\n\n" + PromptTemplateRenderer.AppendedFilesHeading + "\n" + appendedFiles;
        }
        return body;
    }

    [Fact(DisplayName = "何も選ばなければ、組み込みの全テンプレートで依頼文が従来とバイト単位で同一（ファイル付けのオン・オフとも）")]
    public async Task 何も選ばなければ従来と同一()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("src/main.py", "SELECTED_MARKER = 1\n");
        ws.WriteText("src/other.py", "OTHER = 2\n");
        var (renderer, collector, request) = Build(ws, focusText: string.Empty, "src/main.py");

        foreach (var template in PromptTemplateStore.BuiltIns)
        {
            foreach (var append in new[] { false, true })
            {
                var expected = await LegacyRender(template, collector, request, "直近の要約", append);
                var actual = await renderer.RenderAsync(template, request, "直近の要約", appendFilesIfAbsent: append);

                actual.IsSuccess.Should().BeTrue();
                System.Text.Encoding.UTF8.GetBytes(actual.Value).Should().Equal(
                    System.Text.Encoding.UTF8.GetBytes(expected), $"{template.Name}（ファイル付け={append}）");
            }
        }
    }

    [Fact(DisplayName = "観点が空で FocusText を渡さない既定の要求でも、従来と同一")]
    public async Task 既定の要求でも同一()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x = 1\n");
        var (renderer, collector, request) = Build(ws, selected: "a.py");
        var withoutFocus = request with { FocusText = string.Empty };

        var template = PromptTemplateStore.BuiltIns.First();
        var expected = await LegacyRender(template, collector, withoutFocus, null, true);
        var actual = await renderer.RenderAsync(template, request, null, appendFilesIfAbsent: true);

        actual.Value.Should().Be(expected);
    }

    [Fact(DisplayName = "{{focus}} は観点の文に展開される")]
    public async Task focus変数は展開される()
    {
        using var ws = new TempWorkspace();
        var (renderer, _, request) = Build(ws, Focus);

        var result = await renderer.RenderAsync(Template("前置き\n\n{{focus}}\n\n後書き"), request, null);

        result.Value.Should().Be("前置き\n\n" + Focus.TrimEnd('\n') + "\n\n後書き");
    }

    [Fact(DisplayName = "{{focus}} は、何も選ばれていなければ前後の空行ごと消えて痕跡を残さない")]
    public async Task focus変数は空なら痕跡なし()
    {
        using var ws = new TempWorkspace();
        var (renderer, _, request) = Build(ws);

        (await renderer.RenderAsync(Template("前置き\n\n{{focus}}\n\n後書き"), request, null)).Value
            .Should().Be("前置き\n\n後書き", "変数の行と、その前後の空行の片方が詰まる");
        (await renderer.RenderAsync(Template("前置き\n{{focus}}\n後書き"), request, null)).Value
            .Should().Be("前置き\n後書き");
        (await renderer.RenderAsync(Template("{{focus}}\n\n本文"), request, null)).Value
            .Should().Be("本文");
        (await renderer.RenderAsync(Template("行の途中の{{focus}}です"), request, null)).Value
            .Should().Be("行の途中のです");
    }

    [Fact(DisplayName = "{{focus}} が無いテンプレートでは、①「# 対象ファイル」だけの行の直前に入る")]
    public async Task 位置1_対象ファイル見出しの直前()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "A_MARK = 1\n");
        var (renderer, _, request) = Build(ws, Focus, "a.py");

        var result = await renderer.RenderAsync(Template("指示\n\n# 前提\n{{standingContext}}\n\n# 対象ファイル\n{{files}}"), request, null);

        var text = result.Value;
        text.Should().Contain("# 前提\n前提の文\n\n# 依頼の観点\n");
        text.IndexOf("# 依頼の観点", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("# 対象ファイル", StringComparison.Ordinal));
        text.Should().Contain("- 決まり\n\n# 対象ファイル\n", "観点と見出しの間に空行が1つ");
        text.IndexOf("# 対象ファイル", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("A_MARK", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "「# 対象ファイル」の行が無ければ、②{{files}} を含む行の直前に入る")]
    public async Task 位置2_files行の直前()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "A_MARK = 1\n");
        var (renderer, _, request) = Build(ws, Focus, "a.py");

        var result = await renderer.RenderAsync(Template("指示\n\nファイル:\n{{files}}\n"), request, null);

        var text = result.Value;
        text.Should().StartWith("指示\n\nファイル:\n# 依頼の観点\n");
        text.IndexOf("- 決まり\n\n", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("A_MARK", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "どちらも無く、ファイルを自動付加するときは、③付加する「# 対象ファイル」の直前に入る")]
    public async Task 位置3_自動付加の見出しの直前()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "A_MARK = 1\n");
        var (renderer, _, request) = Build(ws, Focus, "a.py");

        var result = await renderer.RenderAsync(Template("指示だけ"), request, null, appendFilesIfAbsent: true);

        result.Value.Should().StartWith("指示だけ\n\n# 依頼の観点\n");
        result.Value.Should().Contain("- 決まり\n\n# 対象ファイル\n");
        result.Value.IndexOf("# 依頼の観点", StringComparison.Ordinal)
            .Should().BeLessThan(result.Value.IndexOf("# 対象ファイル", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "付加するファイルも無いときは、④末尾に入る")]
    public async Task 位置4_末尾()
    {
        using var ws = new TempWorkspace();
        var (renderer, _, request) = Build(ws, Focus);

        (await renderer.RenderAsync(Template("指示だけ"), request, null)).Value
            .Should().Be("指示だけ\n\n" + Focus, "ファイル付けオフ");
        // ファイル付けオンでも、選んだファイルが無ければ見出しごと付かないので、末尾になる。
        (await renderer.RenderAsync(Template("指示だけ"), request, null, appendFilesIfAbsent: true)).Value
            .Should().Be("指示だけ\n\n" + Focus);
    }

    [Fact(DisplayName = "観点を差し込んでも、テンプレートの本文そのものは書き換えない")]
    public async Task テンプレート本文は変えない()
    {
        using var ws = new TempWorkspace();
        var (renderer, _, request) = Build(ws, Focus);
        var template = Template("指示\n\n# 対象ファイル\n{{files}}");

        await renderer.RenderAsync(template, request, null);

        template.Body.Should().Be("指示\n\n# 対象ファイル\n{{files}}");
    }

    [Fact(DisplayName = "ファイルの中身に {{focus}} という文字列があっても、置換されない")]
    public async Task ファイルの中のfocus変数は置換されない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("note.md", "この文書には {{focus}} と書いてある\n");
        var (renderer, _, request) = Build(ws, Focus, "note.md");

        var result = await renderer.RenderAsync(Template("指示\n\n{{focus}}\n\n# 対象ファイル\n{{files}}"), request, null);

        result.Value.Should().Contain("この文書には {{focus}} と書いてある");
        System.Text.RegularExpressions.Regex.Matches(result.Value, "# 依頼の観点").Count.Should().Be(1, "観点は1回だけ");
    }

    [Fact(DisplayName = "観点の文に {{files}} などと書かれていても、再展開されない")]
    public async Task 観点の文の変数は再展開されない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "A_MARK = 1\n");
        var tricky = "# 依頼の観点\n\n## 自作\n- {{files}} と {{standingContext}} と {{projectName}} と {{tree}} をそのまま見せる\n";
        var (renderer, _, request) = Build(ws, tricky, "a.py");

        var result = await renderer.RenderAsync(
            Template("# 前提\n{{standingContext}}\n\n# プロジェクト構成\n{{tree}}\n\n# 対象ファイル\n{{files}}"), request, null);

        result.Value.Should().Contain("- {{files}} と {{standingContext}} と {{projectName}} と {{tree}} をそのまま見せる");
        System.Text.RegularExpressions.Regex.Matches(result.Value, "A_MARK").Count.Should().Be(1, "ファイルの中身が観点の文の {{files}} から二重に入らない");
    }

    [Fact(DisplayName = "{{focus}} を含むテンプレートでは、自動付加でも観点は二重に入らない")]
    public async Task focus変数があれば二重にならない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "A_MARK = 1\n");
        var (renderer, _, request) = Build(ws, Focus, "a.py");

        var result = await renderer.RenderAsync(Template("指示\n\n{{focus}}"), request, null, appendFilesIfAbsent: true);

        System.Text.RegularExpressions.Regex.Matches(result.Value, "# 依頼の観点").Count.Should().Be(1);
        result.Value.IndexOf("# 依頼の観点", StringComparison.Ordinal)
            .Should().BeLessThan(result.Value.IndexOf("# 対象ファイル", StringComparison.Ordinal), "観点は自動付加の見出しより前");
    }

    [Fact(DisplayName = "組み込みの全テンプレートに観点を差し込んでも、観点は1回だけで、観点を外せば元に戻る")]
    public async Task 組み込みの全テンプレートで一回だけ()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("src/main.py", "SELECTED_MARKER = 1\n");
        var (renderer, _, requestWithFocus) = Build(ws, Focus, "src/main.py");
        var requestWithout = requestWithFocus with { FocusText = string.Empty };

        foreach (var template in PromptTemplateStore.BuiltIns)
        {
            var with = await renderer.RenderAsync(template, requestWithFocus, null, appendFilesIfAbsent: true);
            var without = await renderer.RenderAsync(template, requestWithout, null, appendFilesIfAbsent: true);

            System.Text.RegularExpressions.Regex.Matches(with.Value, "# 依頼の観点").Count.Should().Be(1, template.Name);
            with.Value.Replace(Focus + "\n", string.Empty).Should().Be(without.Value, template.Name + "（観点の文を取り除くと観点なしと一致）");
        }
    }
}
