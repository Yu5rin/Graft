using System.IO;
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
/// コンテキスト収集（<see cref="ContextCollector.CollectAsync"/>）への依頼の観点の差し込みと、
/// <c>Project</c> の後方互換・保存先の移行のテスト。仕様書_ブックの情報と依頼の観点 6.3・4.1・4.5・11.1。
/// </summary>
public class ContextCollectorFocusTests
{
    private const string Focus = "# 依頼の観点\n\n## 速く\n- 観点の文\n\n## 進め方の決まり\n- 決まり\n";

    private static ContextRequest Request(string root, string focus, string? standing)
        => new()
        {
            Project = new Project { Id = "p_cf", Name = "cf", Root = root, StandingContext = standing },
            Settings = new Settings(),
            Mode = ContextMode.TreeAndSelected,
            SelectedPaths = new[] { "main.py" },
            FocusText = focus,
        };

    /// <summary>「生成日時: …」の行は分単位で変わるので、比べる前に固定の文字へ置き換える。</summary>
    private static string Normalize(string text) => Regex.Replace(text, @"- 生成日時: .*", "- 生成日時: X");

    [Fact(DisplayName = "観点の文は「# 前提」の直後、「# プロジェクト構成」の前に差し込まれる")]
    public async Task 前提の直後に入る()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("main.py", "print(1)\n");
        var collector = new ContextCollector(new AppPaths(ws.CreateDirectory("app")));

        var result = await collector.CollectAsync(Request(ws.RootPath, Focus, "Python 3.12"));

        var text = result.Value.Text;
        var standing = text.IndexOf("# 前提", StringComparison.Ordinal);
        var focus = text.IndexOf("# 依頼の観点", StringComparison.Ordinal);
        var tree = text.IndexOf("# プロジェクト構成", StringComparison.Ordinal);
        (standing >= 0 && standing < focus && focus < tree).Should().BeTrue("前提 → 観点 → 構成 の順");
        text.Substring(text.IndexOf("Python 3.12", StringComparison.Ordinal), 40).Should().Contain("Python 3.12");
        // 観点と次の見出しの間は空行1つ。
        Regex.IsMatch(text, @"- 決まり\r?\n\r?\n# プロジェクト構成").Should().BeTrue();
    }

    [Fact(DisplayName = "前提が無いときは、概要の直後（構成の前）に入る")]
    public async Task 前提が無ければ概要の直後()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("main.py", "print(1)\n");
        var collector = new ContextCollector(new AppPaths(ws.CreateDirectory("app")));

        var text = (await collector.CollectAsync(Request(ws.RootPath, Focus, null))).Value.Text;

        text.Should().NotContain("# 前提");
        text.IndexOf("- 内容を含むファイル:", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("# 依頼の観点", StringComparison.Ordinal));
        text.IndexOf("# 依頼の観点", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("# プロジェクト構成", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "観点が空なら、出力は従来と同一（観点の見出しも空行も増えない）")]
    public async Task 空なら従来と同一()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("main.py", "print(1)\n");
        var collector = new ContextCollector(new AppPaths(ws.CreateDirectory("app")));
        var baseline = Request(ws.RootPath, string.Empty, "Python 3.12");

        var a = Normalize((await collector.CollectAsync(baseline)).Value.Text);
        var withFocus = Normalize((await collector.CollectAsync(baseline with { FocusText = Focus })).Value.Text);

        a.Should().NotContain("依頼の観点");
        // 観点の文（と後ろの空行1つ）を取り除くと、観点なしの出力と一致する。
        withFocus.Replace(Focus + "\n", string.Empty).Should().Be(a);
    }

    // ---- Project の後方互換 ----

    [Fact(DisplayName = "promptFocusIds を持たない既存の projects.json が読め、空（何も選ばない）になる")]
    public async Task 旧形式のprojects_jsonが読める()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        paths.EnsureCoreDirectoriesExist();
        var root = ws.CreateDirectory("proj");
        await File.WriteAllTextAsync(paths.ProjectsFilePath,
            "{\"projects\":[{\"id\":\"p_old\",\"name\":\"旧\",\"root\":" + System.Text.Json.JsonSerializer.Serialize(root)
            + ",\"overrides\":{\"excludes\":[\"x/\"]}}]}");

        var loaded = await new ProjectStore(paths).LoadAsync();

        loaded.IsSuccess.Should().BeTrue();
        loaded.Value.Single().Overrides.PromptFocusIds.Should().BeEmpty();
        loaded.Value.Single().Overrides.Excludes.Should().Equal("x/");
    }

    [Fact(DisplayName = "promptFocusIds は projects.json に往復でき、他の項目を巻き戻さない")]
    public async Task PromptFocusIdsが往復する()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        paths.EnsureCoreDirectoriesExist();
        var store = new ProjectStore(paths);
        var project = (await store.RegisterAsync(ws.CreateDirectory("proj"), "p")).Value;

        await store.UpdateAsync(project.Id, p => p with
        {
            Overrides = p.Overrides with { PromptFocusIds = new[] { "focus-readable", "custom-1" } },
        });

        var json = await File.ReadAllTextAsync(paths.ProjectsFilePath);
        json.Should().Contain("\"promptFocusIds\"", "JSON は camelCase");
        var reloaded = (await new ProjectStore(paths).LoadAsync()).Value.Single();
        reloaded.Overrides.PromptFocusIds.Should().Equal("focus-readable", "custom-1");
    }

    // ---- 保存先の移行 ----

    [Fact(DisplayName = "focus.json も保存先の移行でコピーされる")]
    public void focus_jsonが移行される()
    {
        using var ws = new TempWorkspace();
        var sourceDir = ws.CreateDirectory("source");
        var targetDir = ws.Combine("target");
        ws.WriteText("source/settings.json", "{}");
        ws.WriteText("source/projects.json", "{}");
        ws.WriteText("source/focus.json", "{\"focuses\":[]}");

        var result = DataDirectoryMigrator.Migrate(sourceDir, targetDir);

        result.IsSuccess.Should().BeTrue();
        File.ReadAllText(Path.Combine(targetDir, "focus.json")).Should().Be("{\"focuses\":[]}");
    }
}
