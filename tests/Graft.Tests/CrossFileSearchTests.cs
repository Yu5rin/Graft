using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書4.4（ファイル横断検索）の単体テスト。ヒットの行番号・列位置、除外規則の尊重、
/// バイナリの読み飛ばし、上限到達時の打ち切り報告、不正な正規表現の非例外化、
/// キャンセルの伝播を実ファイル構成で検証する。
/// </summary>
public class CrossFileSearchTests
{
    private static Project MakeProject(string root) => new() { Id = "p_search", Name = "search", Root = root };

    private static async Task<List<SearchHit>> CollectAsync(
        CrossFileSearchEngine engine, Project project, Settings settings, CrossFileSearchOptions options,
        SearchRunState state, CancellationToken ct = default)
    {
        var hits = new List<SearchHit>();
        await foreach (var hit in engine.SearchAsync(project, settings, options, state, ct))
        {
            hits.Add(hit);
        }
        return hits;
    }

    [Fact(DisplayName = "ヒットの行番号・列位置が正しい")]
    public async Task ヒットの行番号と列位置が正しい()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "def foo():\n    return needle + 1\n");
        var project = MakeProject(ws.RootPath);
        var options = new CrossFileSearchOptions { Query = "needle" };
        var state = new SearchRunState();

        var hits = await CollectAsync(new CrossFileSearchEngine(), project, new Settings(), options, state);

        var hit = hits.Single();
        hit.LineNumber.Should().Be(2);
        hit.ColumnStart.Should().Be("    return ".Length);
        hit.MatchLength.Should().Be("needle".Length);
        hit.RelativePath.Should().Be("a.py");
    }

    [Fact(DisplayName = "除外規則（.gitignore・既定除外）を尊重する")]
    public async Task 除外規則を尊重する()
    {
        using var ws = new TempWorkspace();
        ws.WriteText(".gitignore", "ignored.py\n");
        ws.WriteText("ignored.py", "needle\n");
        ws.WriteText("node_modules/lib.js", "needle\n");
        ws.WriteText("visible.py", "needle\n");
        var project = MakeProject(ws.RootPath);
        var options = new CrossFileSearchOptions { Query = "needle" };
        var state = new SearchRunState();

        var hits = await CollectAsync(new CrossFileSearchEngine(), project, new Settings(), options, state);

        hits.Select(h => h.RelativePath).Should().BeEquivalentTo(new[] { "visible.py" });
    }

    [Fact(DisplayName = "バイナリファイル（NULバイトを含む）は読み飛ばす")]
    public async Task バイナリファイルは読み飛ばす()
    {
        using var ws = new TempWorkspace();
        ws.WriteBytes("binary.dat", new byte[] { (byte)'n', (byte)'e', 0x00, (byte)'e', (byte)'d', (byte)'l', (byte)'e' });
        ws.WriteText("text.txt", "needle\n");
        var project = MakeProject(ws.RootPath);
        var options = new CrossFileSearchOptions { Query = "needle" };
        var state = new SearchRunState();

        var hits = await CollectAsync(new CrossFileSearchEngine(), project, new Settings(), options, state);

        hits.Select(h => h.RelativePath).Should().BeEquivalentTo(new[] { "text.txt" });
    }

    [Fact(DisplayName = "全体のヒット上限に到達すると打ち切りが報告される")]
    public async Task 全体上限到達で打ち切りが報告される()
    {
        using var ws = new TempWorkspace();
        for (var i = 0; i < 5; i++) ws.WriteText($"f{i}.txt", "needle\nneedle\n");
        var project = MakeProject(ws.RootPath);
        var options = new CrossFileSearchOptions { Query = "needle", MaxTotalHits = 3 };
        var state = new SearchRunState();

        var hits = await CollectAsync(new CrossFileSearchEngine(), project, new Settings(), options, state);

        hits.Should().HaveCount(3);
        state.TruncatedByTotalLimit.Should().BeTrue();
    }

    [Fact(DisplayName = "1ファイルあたりの上限に到達したファイルは一覧として報告される")]
    public async Task ファイル単位上限到達が報告される()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("many.txt", string.Concat(System.Linq.Enumerable.Repeat("needle\n", 10)));
        var project = MakeProject(ws.RootPath);
        var options = new CrossFileSearchOptions { Query = "needle", MaxHitsPerFile = 4, MaxTotalHits = 1000 };
        var state = new SearchRunState();

        var hits = await CollectAsync(new CrossFileSearchEngine(), project, new Settings(), options, state);

        hits.Should().HaveCount(4);
        state.FilesTruncatedByPerFileLimit.Should().Contain("many.txt");
    }

    [Fact(DisplayName = "不正な正規表現を指定しても例外にならずエラーメッセージが返る")]
    public async Task 不正な正規表現は例外にならない()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.txt", "content\n");
        var project = MakeProject(ws.RootPath);
        var options = new CrossFileSearchOptions { Query = "(unclosed", UseRegex = true };
        var state = new SearchRunState();

        var act = async () => await CollectAsync(new CrossFileSearchEngine(), project, new Settings(), options, state);

        await act.Should().NotThrowAsync();
        state.PatternError.Should().NotBeNullOrEmpty();
    }

    [Fact(DisplayName = "キャンセルトークンで検索を中断できる")]
    public async Task キャンセルで検索を中断できる()
    {
        using var ws = new TempWorkspace();
        for (var i = 0; i < 50; i++) ws.WriteText($"dir{i}/f.txt", "needle\n");
        var project = MakeProject(ws.RootPath);
        var options = new CrossFileSearchOptions { Query = "needle" };
        var state = new SearchRunState();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await CollectAsync(new CrossFileSearchEngine(), project, new Settings(), options, state, cts.Token);

        await act.Should().ThrowAsync<System.OperationCanceledException>();
    }

    [Fact(DisplayName = "単語単位・大文字小文字を区別する指定が反映される")]
    public async Task 単語単位と大文字小文字区別が反映される()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.txt", "Needle needless needle\n");
        var project = MakeProject(ws.RootPath);
        var options = new CrossFileSearchOptions { Query = "needle", WholeWord = true, CaseSensitive = true };
        var state = new SearchRunState();

        var hits = await CollectAsync(new CrossFileSearchEngine(), project, new Settings(), options, state);

        hits.Should().ContainSingle(h => h.ColumnStart == "Needle needless ".Length);
    }
}
