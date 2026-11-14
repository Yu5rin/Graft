using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// D4: フォルダのシンボリックリンク（Windowsではジャンクションも）が輪になっていると、
/// プロジェクト内の再帰列挙が同じファイルを何度も数えたり、終わらなかったりする不具合の回帰テスト。
/// フォルダのリンクは一覧に出てよいが、中へは入らない。
/// シンボリックリンクを作れるのは Windows 以外（Windows は管理者権限か開発者モードが要る）なので、
/// Windows では早期 return する（他のテストの Windows 専用分岐と同じ書き方）。
/// </summary>
public class SymbolicLinkLoopEnumerationTests
{
    private static Project MakeProject(string root) => new() { Id = "p_loop", Name = "loop", Root = root };

    /// <summary>
    /// root/top.py、root/src/a.py（needle を含む）、
    /// root/src/loop → root（親を指す輪）、root/alias → root/src（同じ実体を指す別名）、
    /// root/outside → 外部フォルダ（中に ext.py）を作る。
    /// </summary>
    private static (string Root, string External) BuildLoopedTree(TempWorkspace ws)
    {
        var root = ws.CreateDirectory("project");
        File.WriteAllText(Path.Combine(root, "top.py"), "needle\n");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "a.py"), "needle\n");
        var external = ws.CreateDirectory("external");
        File.WriteAllText(Path.Combine(external, "ext.py"), "needle\n");

        Directory.CreateSymbolicLink(Path.Combine(root, "src", "loop"), root);
        Directory.CreateSymbolicLink(Path.Combine(root, "alias"), Path.Combine(root, "src"));
        Directory.CreateSymbolicLink(Path.Combine(root, "outside"), external);
        return (root, external);
    }

    [Fact(DisplayName = "D4: 補助 DirectoryLinks は、リンクのフォルダへ入らずにファイルを列挙する")]
    public void 補助はリンクの中へ入らない()
    {
        if (OperatingSystem.IsWindows()) return;

        using var ws = new TempWorkspace();
        var (root, _) = BuildLoopedTree(ws);

        DirectoryLinks.IsLink(Path.Combine(root, "src", "loop")).Should().BeTrue();
        DirectoryLinks.IsLink(Path.Combine(root, "src")).Should().BeFalse();

        var files = DirectoryLinks.EnumerateFilesRecursive(root, "*.py")
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        files.Should().Equal("src/a.py", "top.py");
        DirectoryLinks.CountFilesRecursive(root).Should().Be(2);
    }

    [Fact(DisplayName = "D4: クイックオープンは輪のリンクをたどらず、同じファイルを重ねて数えない")]
    public async Task クイックオープンは輪をたどらない()
    {
        if (OperatingSystem.IsWindows()) return;

        using var ws = new TempWorkspace();
        var (root, _) = BuildLoopedTree(ws);

        var files = await new QuickOpenFileEnumerator().EnumerateAsync(MakeProject(root), new Settings())
            .WaitAsync(TimeSpan.FromSeconds(30));

        files.OrderBy(f => f, StringComparer.Ordinal).Should().Equal("src/a.py", "top.py");
    }

    [Fact(DisplayName = "D4: ファイル横断検索は輪のリンクをたどらず、ヒットを重複させない")]
    public async Task 横断検索は輪をたどらない()
    {
        if (OperatingSystem.IsWindows()) return;

        using var ws = new TempWorkspace();
        var (root, _) = BuildLoopedTree(ws);
        var hits = new List<SearchHit>();

        async Task RunAsync()
        {
            await foreach (var hit in new CrossFileSearchEngine().SearchAsync(
                MakeProject(root), new Settings(), new CrossFileSearchOptions { Query = "needle" }, new SearchRunState()))
            {
                hits.Add(hit);
            }
        }
        await RunAsync().WaitAsync(TimeSpan.FromSeconds(30));

        hits.Select(h => h.RelativePath).OrderBy(p => p, StringComparer.Ordinal).Should().Equal("src/a.py", "top.py");
    }

    [Fact(DisplayName = "D4: エクスプローラの絞り込みは輪のリンクをたどらず、同じファイルを重ねない")]
    public async Task エクスプローラ絞り込みは輪をたどらない()
    {
        if (OperatingSystem.IsWindows()) return;

        using var ws = new TempWorkspace();
        var (root, _) = BuildLoopedTree(ws);

        var result = await new ExplorerFilterService()
            .FindMatchesAsync(MakeProject(root), GitignoreFilter.Empty, ".py", includeExcluded: false)
            .WaitAsync(TimeSpan.FromSeconds(30));

        result.MatchedRelativePaths.OrderBy(p => p, StringComparer.Ordinal).Should().Equal("src/a.py", "top.py");
    }

    [Fact(DisplayName = "D4: コンテキスト収集のツリーはリンクを項目として出すが、中へは入らない")]
    public async Task コンテキスト収集はリンクの中へ入らない()
    {
        if (OperatingSystem.IsWindows()) return;

        using var ws = new TempWorkspace();
        var (root, _) = BuildLoopedTree(ws);
        var collector = new ContextCollector(new AppPaths(ws.CreateDirectory("app")));

        var result = await collector.ScanAsync(MakeProject(root), new Settings()).WaitAsync(TimeSpan.FromSeconds(30));

        result.IsSuccess.Should().BeTrue();
        var paths = result.Value.Select(n => n.RelativePath).ToList();
        paths.Should().Contain(new[] { "src", "src/a.py", "top.py", "alias", "src/loop", "outside" },
            "リンクそのものは一覧に出てよい");
        paths.Where(p => p.EndsWith("a.py", StringComparison.Ordinal)).Should().ContainSingle("同じファイルを重ねて数えない");
        paths.Should().NotContain(p => p.StartsWith("alias/", StringComparison.Ordinal)
                                       || p.StartsWith("src/loop/", StringComparison.Ordinal)
                                       || p.StartsWith("outside/", StringComparison.Ordinal),
            "リンクの中へは入らない");
    }

    [Fact(DisplayName = "D4: フォルダの取り込みコピーは輪のリンクをたどらず、終わる")]
    public async Task 取り込みコピーは輪をたどらない()
    {
        if (OperatingSystem.IsWindows()) return;

        using var ws = new TempWorkspace();
        var (root, _) = BuildLoopedTree(ws);
        var dest = ws.Combine("copied");
        var item = new FileImportPlanItem
        {
            SourceFullPath = root,
            DestinationFullPath = dest,
            DestinationRelativePath = "copied",
            IsDirectory = true,
            Overwrite = false,
        };

        var outcomes = await new FileImportService().ImportAsync(new[] { item }, progress: null, default)
            .WaitAsync(TimeSpan.FromSeconds(30));

        outcomes.Should().ContainSingle().Which.IsSuccess.Should().BeTrue();
        Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dest, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal)
            .Should().Equal("src/a.py", "top.py");
    }

    [Fact(DisplayName = "D4: .gitignore の探索も輪のリンクをたどらない")]
    public async Task gitignoreの探索は輪をたどらない()
    {
        if (OperatingSystem.IsWindows()) return;

        using var ws = new TempWorkspace();
        var (root, external) = BuildLoopedTree(ws);
        File.WriteAllText(Path.Combine(root, ".gitignore"), "root_rule.py\n");
        // リンク先の外部フォルダにある .gitignore は、プロジェクトの規則として読んではならない。
        File.WriteAllText(Path.Combine(external, ".gitignore"), "ext.py\n");

        var filter = await GitignoreFilter.LoadAsync(root).WaitAsync(TimeSpan.FromSeconds(30));

        filter.IsIgnored("root_rule.py", isDirectory: false).Should().BeTrue();
        filter.IsIgnored("outside/ext.py", isDirectory: false).Should().BeFalse("リンクの中の .gitignore は読まない");
    }
}
