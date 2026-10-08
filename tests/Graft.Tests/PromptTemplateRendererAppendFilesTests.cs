using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// プロンプトのコピーで「指示文＋選んだファイル」を1回のコピーで渡す機能
/// （<see cref="PromptTemplateRenderer.RenderAsync"/> の <c>appendFilesIfAbsent</c>）のテスト。
///
/// 既定の「初回用（完全版）」は <c>{{files}}</c> を含まないため、指示文のコピーとファイルのコピーを
/// 別々に行って2回貼る必要があった。テンプレートの本文は書き換えず、コピーの選択肢として
/// 展開結果の末尾にだけファイルを足す。オフなら従来と1文字も変わらないこと、
/// <c>{{files}}</c> を含むテンプレートでは二重に付かないことを固定する。
/// </summary>
public class PromptTemplateRendererAppendFilesTests
{
    private const string Instruction = "次の形式で出力してください。\n\n# 前提\n{{standingContext}}\n";

    private static PromptTemplate Template(string body) => new() { Id = "t", Name = "t", Body = body };

    private static (PromptTemplateRenderer Renderer, ContextRequest Request) Build(TempWorkspace ws, params string[] selected)
    {
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var project = new Project { Id = "p_render", Name = "render", Root = ws.RootPath, StandingContext = "前提の文" };
        var request = new ContextRequest
        {
            Project = project,
            Settings = new Settings(),
            Mode = ContextMode.TreeAndSelected,
            SelectedPaths = selected,
        };
        return (new PromptTemplateRenderer(new ContextCollector(paths)), request);
    }

    [Fact(DisplayName = "オンのとき、{{files}}を含まないテンプレートの指示文の後ろに、選んだファイルの内容が付く")]
    public async Task オンのとき指示文の後ろにファイルが付く()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("src/main.py", "SELECTED_MARKER = 1\n");
        ws.WriteText("src/other.py", "NOT_SELECTED = 2\n");
        var (renderer, request) = Build(ws, "src/main.py");

        var result = await renderer.RenderAsync(Template(Instruction), request, null, appendFilesIfAbsent: true);

        result.IsSuccess.Should().BeTrue();
        var text = result.Value;
        text.Should().StartWith("次の形式で出力してください。", "指示文が先頭にある");
        text.Should().Contain("前提の文", "指示文の変数は従来どおり展開される");
        text.Should().Contain("# 対象ファイル");
        text.Should().Contain("SELECTED_MARKER = 1", "選んだファイルの内容が付く");
        text.Should().NotContain("NOT_SELECTED", "選んでいないファイルは付かない");
        text.IndexOf("前提の文", System.StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("SELECTED_MARKER", System.StringComparison.Ordinal), "ファイルは指示文の後ろ");
    }

    [Fact(DisplayName = "オフ（既定）のときは従来どおり指示文だけで、ファイルは付かない（出力は従来と同一）")]
    public async Task オフのとき従来どおり()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("src/main.py", "SELECTED_MARKER = 1\n");
        var (renderer, request) = Build(ws, "src/main.py");

        var byDefault = await renderer.RenderAsync(Template(Instruction), request, null);
        var explicitOff = await renderer.RenderAsync(Template(Instruction), request, null, appendFilesIfAbsent: false);

        byDefault.Value.Should().Be("次の形式で出力してください。\n\n# 前提\n前提の文\n", "従来の展開結果と1文字も変わらない");
        explicitOff.Value.Should().Be(byDefault.Value);
        byDefault.Value.Should().NotContain("SELECTED_MARKER");
    }

    [Fact(DisplayName = "{{files}}を含むテンプレートでは、オンでもファイルが二重に付かず、オフと同じ結果になる")]
    public async Task files変数を含むテンプレートでは二重に付かない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("src/main.py", "SELECTED_MARKER = 1\n");
        var (renderer, request) = Build(ws, "src/main.py");
        var template = Template("お願いします。\n\n# 対象ファイル\n{{files}}");

        var off = await renderer.RenderAsync(template, request, null, appendFilesIfAbsent: false);
        var on = await renderer.RenderAsync(template, request, null, appendFilesIfAbsent: true);

        on.Value.Should().Be(off.Value, "{{files}}がある場合、オンでも結果は変わらない");
        CountOccurrences(on.Value, "SELECTED_MARKER").Should().Be(1, "ファイルの内容は1回だけ出る");
        CountOccurrences(on.Value, "# 対象ファイル").Should().Be(1, "見出しも重ならない");
    }

    [Fact(DisplayName = "{{tree}}だけを持つテンプレート（新規実装）でもオンならファイルが付き、構成ツリーは重ならない")]
    public async Task tree変数だけのテンプレートにもファイルが付く()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("src/main.py", "SELECTED_MARKER = 1\n");
        var (renderer, request) = Build(ws, "src/main.py");
        var template = Template("新規実装です。\n\n# プロジェクト構成\n{{tree}}");

        var result = await renderer.RenderAsync(template, request, null, appendFilesIfAbsent: true);

        result.Value.Should().Contain("SELECTED_MARKER = 1");
        CountOccurrences(result.Value, "# プロジェクト構成").Should().Be(1, "構成ツリーは{{tree}}の1回だけ");
        CountOccurrences(result.Value, "main.py").Should().BeGreaterThan(0);
    }

    [Fact(DisplayName = "テンプレート自体の本文は書き換わらない（展開結果にだけ足す）")]
    public async Task テンプレートの本文は書き換わらない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "A = 1\n");
        var (renderer, request) = Build(ws, "a.py");
        var template = Template(Instruction);

        await renderer.RenderAsync(template, request, null, appendFilesIfAbsent: true);

        template.Body.Should().Be(Instruction);
    }

    [Fact(DisplayName = "選んだファイルが1つも無いときは、見出しだけが残らないよう何も足さない（オフと同じ結果）")]
    public async Task 選択が空なら何も足さない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "A = 1\n");
        var (renderer, request) = Build(ws /* 選択なし */);

        var off = await renderer.RenderAsync(Template(Instruction), request, null, appendFilesIfAbsent: false);
        var on = await renderer.RenderAsync(Template(Instruction), request, null, appendFilesIfAbsent: true);

        on.Value.Should().Be(off.Value);
        on.Value.Should().NotContain("# 対象ファイル");
    }

    [Fact(DisplayName = "収集モードが「ツリーのみ」のときは、オンでもファイルは付かない")]
    public async Task ツリーのみモードではファイルは付かない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "A_MARKER = 1\n");
        var (renderer, request) = Build(ws, "a.py");
        request = request with { Mode = ContextMode.TreeOnly };

        var on = await renderer.RenderAsync(Template(Instruction), request, null, appendFilesIfAbsent: true);

        on.Value.Should().NotContain("A_MARKER").And.NotContain("# 対象ファイル");
    }

    [Fact(DisplayName = "付けたファイルの中身に変数のような文字列があっても、展開されない（そのまま出る）")]
    public async Task ファイルの中身は変数として展開されない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("note.md", "テンプレートでは {{standingContext}} と {{projectName}} を使う\n");
        var (renderer, request) = Build(ws, "note.md");

        var on = await renderer.RenderAsync(Template(Instruction), request, null, appendFilesIfAbsent: true);

        on.Value.Should().Contain("{{standingContext}} と {{projectName}} を使う", "ファイルの中身はそのまま渡す");
    }

    [Fact(DisplayName = "除外されていて内容を出せない選択ファイルがあっても、省略の注記つきで付く（欠落を黙らない）")]
    public async Task 除外された選択ファイルは注記が付く()
    {
        using var ws = new TempWorkspace();
        ws.WriteText(".gitignore", "secret.env\n");
        ws.WriteText("secret.env", "KEY=1");
        ws.WriteText("ok.py", "OK_MARKER = 1\n");
        var (renderer, request) = Build(ws, "ok.py", "secret.env");

        var on = await renderer.RenderAsync(Template(Instruction), request, null, appendFilesIfAbsent: true);

        on.Value.Should().Contain("OK_MARKER").And.Contain("内容を省略したファイル").And.Contain("secret.env");
        on.Value.Should().NotContain("KEY=1");
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
