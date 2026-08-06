using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書10章（コンテキスト収集）・11章相当（除外規則の適用結果）の単体テスト。
/// 出力形式（# 前提 / # プロジェクト構成 / # パス (ハッシュ)）・既定除外パターン・
/// トークン概算と閾値超過を実ファイル構成で検証する。
/// </summary>
public class ContextCollectorTests
{
    private static Project MakeProject(string root, string? standingContext = null)
        => new() { Id = "p_ctx", Name = "ctx", Root = root, StandingContext = standingContext };

    [Fact(DisplayName = "出力は「# 前提」「# プロジェクト構成」「# パス (ハッシュ)」の順で構成される")]
    public async Task 出力形式が仕様どおりになる()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("main.py", "print(1)\n");
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath, "Python 3.12 / FastAPI");
        var settings = new Settings();
        var request = new ContextRequest
        {
            Project = project, Mode = ContextMode.TreeAndSelected, SelectedPaths = new[] { "main.py" }, Settings = settings,
        };

        var result = await collector.CollectAsync(request);

        result.IsSuccess.Should().BeTrue();
        var text = result.Value.Text;
        text.Should().Contain("# 前提");
        text.Should().Contain("Python 3.12 / FastAPI");
        text.Should().Contain("# プロジェクト構成");
        text.Should().MatchRegex(@"# main\.py\s+\([0-9a-f]{6}\)", "パスの見出しは「# パス (ハッシュ)」形式のはず");
        text.Should().Contain("print(1)");

        var prefaceIndex = text.IndexOf("# 前提", System.StringComparison.Ordinal);
        var treeIndex = text.IndexOf("# プロジェクト構成", System.StringComparison.Ordinal);
        var fileIndex = text.IndexOf("# main.py", System.StringComparison.Ordinal);
        prefaceIndex.Should().BeLessThan(treeIndex);
        treeIndex.Should().BeLessThan(fileIndex);
    }

    [Fact(DisplayName = "既定の除外パターン（node_modules・bin・obj・.venv・dist・.git・*.min.js）が除外される")]
    public async Task 既定除外パターンが適用される()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("node_modules/pkg/index.js", "x");
        ws.WriteText("bin/app.dll", "x");
        ws.WriteText("obj/temp.obj", "x");
        ws.WriteText(".venv/lib.py", "x");
        ws.WriteText("dist/bundle.js", "x");
        ws.WriteText("app.min.js", "x");
        ws.WriteText("src/main.py", "x");
        var paths = new AppPaths(ws.CreateDirectory("app_data"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);

        var scan = await collector.ScanAsync(project, new Settings());

        scan.IsSuccess.Should().BeTrue();
        var excludedDirs = new[] { "node_modules", "bin", "obj", ".venv", "dist" };
        foreach (var dir in excludedDirs)
        {
            scan.Value.Single(f => f.RelativePath == dir).IsExcluded.Should().BeTrue($"{dir} は既定除外の対象のはず");
        }
        scan.Value.Single(f => f.RelativePath == "app.min.js").IsExcluded.Should().BeTrue();
        scan.Value.Single(f => f.RelativePath == "src/main.py").IsExcluded.Should().BeFalse();
    }

    [Fact(DisplayName = "1MBを超えるファイルとバイナリ拡張子は除外される")]
    public async Task サイズ超過とバイナリ拡張子は除外される()
    {
        using var ws = new TempWorkspace();
        ws.WriteBytes("big.txt", new byte[1024 * 1024 + 1]);
        ws.WriteBytes("image.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var paths = new AppPaths(ws.CreateDirectory("app_data"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);

        var scan = await collector.ScanAsync(project, new Settings());

        scan.Value.Single(f => f.RelativePath == "big.txt").IsExcluded.Should().BeTrue();
        scan.Value.Single(f => f.RelativePath == "image.png").IsExcluded.Should().BeTrue();
    }

    [Fact(DisplayName = "standingContextが無ければ「# 前提」セクションを出力しない")]
    public async Task standingContext無しなら前提セクションを出さない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x");
        var paths = new AppPaths(ws.CreateDirectory("app_data"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath, standingContext: null);
        var request = new ContextRequest { Project = project, Mode = ContextMode.TreeOnly, Settings = new Settings() };

        var result = await collector.CollectAsync(request);

        result.Value.Text.Should().NotContain("# 前提");
    }

    [Fact(DisplayName = "ツリーのみモードではファイル本文を含まない")]
    public async Task ツリーのみモードは本文を含まない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("secret.py", "SECRET_TOKEN = 1"); // 本文に出てはならない目印
        var paths = new AppPaths(ws.CreateDirectory("app_data"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);
        var request = new ContextRequest { Project = project, Mode = ContextMode.TreeOnly, Settings = new Settings() };

        var result = await collector.CollectAsync(request);

        result.Value.Text.Should().Contain("secret.py", "ツリーにはファイル名自体は出るはず");
        result.Value.Text.Should().NotContain("SECRET_TOKEN", "ツリーのみモードでは本文を含まないはず");
    }

    [Fact(DisplayName = "トークン概算はcontext.tokenRatioに従い、閾値超過時にExceedsWarnThresholdがtrueになる")]
    public async Task トークン概算と閾値超過が判定される()
    {
        using var ws = new TempWorkspace();
        var longContent = new string('あ', 1000);
        ws.WriteText("a.py", longContent);
        var paths = new AppPaths(ws.CreateDirectory("app_data"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);
        var settings = new Settings { Context = new ContextSettings { TokenRatio = 2.5, TokenWarnThreshold = 10 } };
        var request = new ContextRequest
        {
            Project = project, Mode = ContextMode.SelectedFiles, SelectedPaths = new[] { "a.py" }, Settings = settings,
        };

        var result = await collector.CollectAsync(request);

        result.Value.EstimatedTokens.Should().Be(TokenEstimator.Estimate(result.Value.Text, 2.5));
        result.Value.ExceedsWarnThreshold.Should().BeTrue("閾値10を大きく超える文字数のはず");
    }

    [Fact(DisplayName = "プロジェクト単位のoverrides.excludesも除外規則として反映される")]
    public async Task プロジェクト単位の除外規則が反映される()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("migrations/0001_init.py", "x");
        ws.WriteText("app.py", "x");
        var paths = new AppPaths(ws.CreateDirectory("app_data"));
        var collector = new ContextCollector(paths);
        var project = new Project
        {
            Id = "p_ov", Name = "ov", Root = ws.RootPath,
            Overrides = new ProjectOverrides { Excludes = new[] { "migrations/**" } },
        };

        var scan = await collector.ScanAsync(project, new Settings());

        scan.Value.Single(f => f.RelativePath == "migrations/0001_init.py").IsExcluded.Should().BeTrue();
        scan.Value.Single(f => f.RelativePath == "app.py").IsExcluded.Should().BeFalse();
    }
}
