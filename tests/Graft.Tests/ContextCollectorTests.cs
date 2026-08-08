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

    [Fact(DisplayName = "冒頭にプロジェクト名・生成日時・収集モード・ファイル数の概要が出る")]
    public async Task 冒頭に概要が出る()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("main.py", "print(1)\n");
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var collector = new ContextCollector(paths);
        var project = new Project { Id = "p_ov2", Name = "デモ", Root = ws.RootPath };
        var request = new ContextRequest
        {
            Project = project, Mode = ContextMode.TreeAndSelected, SelectedPaths = new[] { "main.py" }, Settings = new Settings(),
        };

        var result = await collector.CollectAsync(request);

        var text = result.Value.Text;
        text.Should().Contain("デモ", "プロジェクト名が概要に出るはず");
        text.Should().Contain("生成日時:");
        text.Should().Contain("収集モード: ツリー＋選択");
        text.Should().Contain("ファイル構成: 1 件");
        text.Should().Contain("内容を含むファイル: 1 件");

        var overviewIndex = text.IndexOf("デモ", System.StringComparison.Ordinal);
        var treeIndex = text.IndexOf("# プロジェクト構成", System.StringComparison.Ordinal);
        overviewIndex.Should().BeLessThan(treeIndex, "概要は冒頭、ツリーより前に出るはず");
    }

    [Fact(DisplayName = "ツリーと各ファイルの内容はMarkdownのフェンス付きコードブロックとして出力される")]
    public async Task Markdownのコードブロックとして出力される()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("main.py", "print(1)\n");
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);
        var request = new ContextRequest
        {
            Project = project, Mode = ContextMode.TreeAndSelected, SelectedPaths = new[] { "main.py" }, Settings = new Settings(),
        };

        var result = await collector.CollectAsync(request);

        var text = result.Value.Text;
        text.Should().Contain("```text", "ツリーはフェンス付きコードブロックのはず");
        text.Should().Contain("```python", "main.pyの内容は言語名付きのコードブロックのはず");
        text.Should().MatchRegex(@"```python\r?\nprint\(1\)\r?\n```", "コードフェンスの中に本文がそのまま入るはず");
    }

    [Fact(DisplayName = "HiddenPathsに含めたファイルはツリーからも内容からも消える（「出さない」）")]
    public async Task Hiddenファイルはツリーからも内容からも消える()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("main.py", "print(1)\n");
        ws.WriteText("lib/helper.py", "SECRET_MARKER = 1\n");
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);
        var request = new ContextRequest
        {
            Project = project, Mode = ContextMode.TreeAndSelected,
            SelectedPaths = new[] { "main.py", "lib/helper.py" },
            HiddenPaths = new[] { "lib/helper.py" },
            Settings = new Settings(),
        };

        var result = await collector.CollectAsync(request);

        var text = result.Value.Text;
        text.Should().Contain("main.py");
        text.Should().NotContain("helper.py", "出さない指定のファイルはツリーにも出ないはず");
        text.Should().NotContain("SECRET_MARKER", "出さない指定のファイルは内容も出ないはず");
    }

    [Fact(DisplayName = "配下の全ファイルをHiddenにしたディレクトリはツリーからも消える")]
    public async Task 配下が全部Hiddenのディレクトリはツリーから消える()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("lib/a.py", "x");
        ws.WriteText("lib/b.py", "x");
        ws.WriteText("main.py", "x");
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);
        var request = new ContextRequest
        {
            Project = project, Mode = ContextMode.TreeAndSelected,
            SelectedPaths = new[] { "main.py" },
            HiddenPaths = new[] { "lib/a.py", "lib/b.py" },
            Settings = new Settings(),
        };

        var result = await collector.CollectAsync(request);

        result.Value.Text.Should().NotContain("lib", "配下が全部出さない指定のフォルダはツリーからも消えるはず");
    }

    [Fact(DisplayName = "「構成だけ」（ツリーに載るが内容を含めない）ファイルにはツリー上で省略の注記が付く")]
    public async Task 構成だけのファイルはツリーに注記付きで載り内容は出ない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("main.py", "print(1)\n");
        ws.WriteText("lib/helper.py", "SECRET_MARKER = 1\n"); // 構成だけ扱い＝SelectedPathsに含めない
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);
        var request = new ContextRequest
        {
            Project = project, Mode = ContextMode.TreeAndSelected,
            SelectedPaths = new[] { "main.py" }, // helper.pyは選ばない＝「構成だけ」
            Settings = new Settings(),
        };

        var result = await collector.CollectAsync(request);

        var text = result.Value.Text;
        text.Should().Contain("helper.py", "構成だけのファイルもツリーには載るはず");
        text.Should().Contain("構成のみ・内容は省略", "内容を省いたことが分かる注記が要るはず");
        text.Should().NotContain("SECRET_MARKER", "構成だけのファイルは内容が出ないはず");
    }

    [Fact(DisplayName = "ツリーのみモードでは3状態の注記もHiddenPathsによる除去も行わない（3状態は意味を持たない）")]
    public async Task ツリーのみモードでは3状態を無視する()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("secret.py", "x");
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);
        var request = new ContextRequest
        {
            Project = project, Mode = ContextMode.TreeOnly,
            HiddenPaths = new[] { "secret.py" }, // TreeOnlyでは無視されるはず
            Settings = new Settings(),
        };

        var result = await collector.CollectAsync(request);

        result.Value.Text.Should().Contain("secret.py", "TreeOnlyモードでは3状態の指定を無視し全件出すはず");
        result.Value.Text.Should().NotContain("構成のみ・内容は省略", "TreeOnlyでは注記も付けないはず");
    }

    [Fact(DisplayName = "推定トークン数はContentに含めたファイルの分だけを数える（構成だけのファイルはパス名の分のみ）")]
    public async Task トークン数は内容を出すファイルの分だけ数える()
    {
        using var ws = new TempWorkspace();
        var longContent = new string('あ', 2000);
        ws.WriteText("full.py", "x"); // 内容も出す（短い）
        ws.WriteText("structureOnly.py", longContent); // 構成だけ（長いが本文は出ない）
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);

        var withStructureOnlyContent = new ContextRequest
        {
            Project = project, Mode = ContextMode.TreeAndSelected,
            SelectedPaths = new[] { "full.py" }, // structureOnly.pyは選ばない
            Settings = new Settings(),
        };
        var withBothContent = new ContextRequest
        {
            Project = project, Mode = ContextMode.TreeAndSelected,
            SelectedPaths = new[] { "full.py", "structureOnly.py" },
            Settings = new Settings(),
        };

        var onlyStructure = await collector.CollectAsync(withStructureOnlyContent);
        var both = await collector.CollectAsync(withBothContent);

        both.Value.EstimatedTokens.Should().BeGreaterThan(onlyStructure.Value.EstimatedTokens,
            "structureOnly.pyの本文まで含めた方がトークン数は大きく増えるはず");
        (both.Value.EstimatedTokens - onlyStructure.Value.EstimatedTokens).Should().BeGreaterThan(100,
            "2000文字ぶんの本文差はパス名の差よりはるかに大きいはず");
    }

    [Theory(DisplayName = "拡張子から言語名付きのコードフェンスを判定する")]
    [InlineData("main.py", "python")]
    [InlineData("app.cs", "csharp")]
    [InlineData("index.ts", "typescript")]
    [InlineData("style.css", "css")]
    public async Task 拡張子から言語を判定する(string fileName, string expectedLanguage)
    {
        using var ws = new TempWorkspace();
        ws.WriteText(fileName, "x");
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);
        var request = new ContextRequest
        {
            Project = project, Mode = ContextMode.SelectedFiles, SelectedPaths = new[] { fileName }, Settings = new Settings(),
        };

        var result = await collector.CollectAsync(request);

        result.Value.Text.Should().Contain($"```{expectedLanguage}");
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

    // ------------------------------------------------------------------
    // 要件G: 除外されたファイル・ディレクトリもファイル構成には名前だけ残す
    // ------------------------------------------------------------------

    [Fact(DisplayName = "既定除外の大きなディレクトリはツリー上で1行に畳んで件数付きで要約される")]
    public async Task 除外ディレクトリはツリーで畳んで要約される()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("node_modules/a/index.js", "x");
        ws.WriteText("node_modules/a/lib.js", "x");
        ws.WriteText("node_modules/b/index.js", "x");
        ws.WriteText("main.py", "x");
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);
        var request = new ContextRequest
        {
            Project = project, Mode = ContextMode.TreeAndSelected, SelectedPaths = new[] { "main.py" }, Settings = new Settings(),
        };

        var result = await collector.CollectAsync(request);

        var text = result.Value.Text;
        text.Should().Contain("node_modules/", "除外ディレクトリも名前はツリーに残るはず");
        text.Should().Contain("3 ファイル", "配下3ファイルの件数付きで畳まれるはず");
        text.Should().Contain("内容は非出力");
        text.Should().NotContain("index.js", "配下の個々のファイルは1件ずつ列挙されないはず");
    }

    [Fact(DisplayName = "個別に除外されたファイル（バイナリ・サイズ超過）は理由の注記付きでツリーに名前だけ残る")]
    public async Task 個別除外ファイルは理由付きでツリーに残る()
    {
        using var ws = new TempWorkspace();
        ws.WriteBytes("image.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        ws.WriteText("main.py", "x");
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);
        var request = new ContextRequest
        {
            Project = project, Mode = ContextMode.TreeAndSelected, SelectedPaths = new[] { "main.py" }, Settings = new Settings(),
        };

        var result = await collector.CollectAsync(request);

        var text = result.Value.Text;
        text.Should().Contain("image.png", "バイナリファイルも名前はツリーに残るはず");
        text.Should().Contain("バイナリファイルのため除外");
    }

    [Fact(DisplayName = "ツリーのみモードでも除外ディレクトリ・除外ファイルは名前だけ残る")]
    public async Task ツリーのみモードでも除外エントリは名前だけ残る()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("bin/app.dll", "x");
        ws.WriteText("main.py", "x");
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);
        var request = new ContextRequest { Project = project, Mode = ContextMode.TreeOnly, Settings = new Settings() };

        var result = await collector.CollectAsync(request);

        result.Value.Text.Should().Contain("bin/", "既定除外ディレクトリもツリーのみモードで名前は残るはず");
    }

    // ------------------------------------------------------------------
    // 要件F: ロックファイルは既定除外パターンには加えない（除外扱いにしない）
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ロックファイルは既定除外パターンには含まれず、走査結果でも除外扱いにならない")]
    public async Task ロックファイルは除外パターンの対象ではない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("package-lock.json", "{}");
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var collector = new ContextCollector(paths);
        var project = MakeProject(ws.RootPath);

        var scan = await collector.ScanAsync(project, new Settings());

        scan.Value.Single(f => f.RelativePath == "package-lock.json").IsExcluded.Should().BeFalse(
            "ロックファイルは除外パターンではなく初期チェック状態だけをオフにする対象のはず");
        ContextCollector.DefaultExcludePatterns.Should().NotContain(p => p.Contains("lock", StringComparison.OrdinalIgnoreCase),
            "既定除外パターンにロックファイルを加えると他画面（エクスプローラ等）からも見えなくなってしまう");
    }

    [Theory(DisplayName = "IsLockFileForInitialUncheckはロックファイル名を正しく判定する（マニフェストは対象外）")]
    [InlineData("package-lock.json", true)]
    [InlineData("yarn.lock", true)]
    [InlineData("go.sum", true)]
    [InlineData("Cargo.lock", true)]
    [InlineData("package.json", false)]
    [InlineData("go.mod", false)]
    [InlineData("Cargo.toml", false)]
    public void ロックファイル判定(string fileName, bool expected)
    {
        ContextCollector.IsLockFileForInitialUncheck(fileName).Should().Be(expected);
    }
}
