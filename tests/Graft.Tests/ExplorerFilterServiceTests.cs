using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 細かいユーザビリティ改善4: エクスプローラのファイル名絞り込みが使う<see cref="ExplorerFilterService"/>。
/// 除外規則の尊重・大文字小文字を無視した部分一致・上限到達時の打ち切りを、ディスクI/Oを
/// 実際に行う統合テストとして検証する。デバウンス・View側の展開/表示反映は
/// Graft.UiTests側のExplorerFilterTestsが担当する。
/// </summary>
public class ExplorerFilterServiceTests
{
    private static Project MakeProject(string root) => new() { Id = "p_test", Name = "test", Root = root };

    [Fact(DisplayName = "ファイル名に含まれる文字列で大文字小文字を無視して一致する")]
    public async Task 大文字小文字を無視して部分一致する()
    {
        using var ws = new TestSupport.TempWorkspace();
        var root = ws.CreateDirectory("project");
        File.WriteAllText(Path.Combine(root, "MyFile.txt"), "x");
        File.WriteAllText(Path.Combine(root, "other.txt"), "x");

        var service = new ExplorerFilterService();
        var result = await service.FindMatchesAsync(MakeProject(root), GitignoreFilter.Empty, "myfile", includeExcluded: false);

        result.MatchedRelativePaths.Should().ContainSingle().Which.Should().Be("MyFile.txt");
        result.Truncated.Should().BeFalse();
    }

    [Fact(DisplayName = "除外規則に一致するファイルは、除外ファイルを表示しない設定の間は対象に含めない")]
    public async Task 除外ファイルは既定では対象に含まれない()
    {
        using var ws = new TestSupport.TempWorkspace();
        var root = ws.CreateDirectory("project");
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        File.WriteAllText(Path.Combine(root, "node_modules", "match_in_excluded.txt"), "x");
        File.WriteAllText(Path.Combine(root, "match_visible.txt"), "x");

        var filter = GitignoreFilter.FromPatterns(new[] { "node_modules/" }, "既定除外");
        var service = new ExplorerFilterService();

        var result = await service.FindMatchesAsync(MakeProject(root), filter, "match", includeExcluded: false);

        result.MatchedRelativePaths.Should().ContainSingle().Which.Should().Be("match_visible.txt");
    }

    [Fact(DisplayName = "除外ファイルを表示中は、除外フォルダ配下も検索対象に含める")]
    public async Task 除外ファイルを表示中は除外フォルダ配下も対象になる()
    {
        using var ws = new TestSupport.TempWorkspace();
        var root = ws.CreateDirectory("project");
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        File.WriteAllText(Path.Combine(root, "node_modules", "match_in_excluded.txt"), "x");

        var filter = GitignoreFilter.FromPatterns(new[] { "node_modules/" }, "既定除外");
        var service = new ExplorerFilterService();

        var result = await service.FindMatchesAsync(MakeProject(root), filter, "match", includeExcluded: true);

        result.MatchedRelativePaths.Should().ContainSingle().Which.Should().Be("node_modules/match_in_excluded.txt");
    }

    [Fact(DisplayName = "一致件数がMaxMatchesに達すると打ち切りTruncated=trueになる")]
    public async Task 上限に達すると打ち切られる()
    {
        using var ws = new TestSupport.TempWorkspace();
        var root = ws.CreateDirectory("project");
        for (var i = 0; i < ExplorerFilterService.MaxMatches + 20; i++)
        {
            File.WriteAllText(Path.Combine(root, $"match_{i:D4}.txt"), "x");
        }

        var service = new ExplorerFilterService();
        var result = await service.FindMatchesAsync(MakeProject(root), GitignoreFilter.Empty, "match", includeExcluded: false);

        result.MatchedRelativePaths.Count.Should().Be(ExplorerFilterService.MaxMatches);
        result.Truncated.Should().BeTrue();
    }

    [Fact(DisplayName = "空文字での絞り込みは何もヒットさせない（重い全件走査をしない安全側）")]
    public async Task 空文字は何も一致しない()
    {
        using var ws = new TestSupport.TempWorkspace();
        var root = ws.CreateDirectory("project");
        File.WriteAllText(Path.Combine(root, "a.txt"), "x");

        var service = new ExplorerFilterService();
        var result = await service.FindMatchesAsync(MakeProject(root), GitignoreFilter.Empty, string.Empty, includeExcluded: false);

        result.MatchedRelativePaths.Should().BeEmpty();
        result.Truncated.Should().BeFalse();
    }
}
