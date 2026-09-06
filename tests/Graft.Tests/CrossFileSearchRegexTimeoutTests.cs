using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// プロジェクト全体検索（Ctrl+Shift+F）で、破滅的バックトラックを起こす正規表現を
/// 入力したときの回帰テスト。
///
/// エディタ内検索（Ctrl+F）と違い、こちらは<c>AsyncRelayCommand</c>のSafeHandlerが
/// 受けるためアプリが落ちることはなかった。ただし
/// <see cref="RegexMatchTimeoutException"/>を専用に捕まえていなかったので、利用者には
/// 「想定外のエラー」という原因も対処も分からない文言だけが出ていた。原因（正規表現の
/// 処理に時間がかかりすぎた）と次の一手（パターンを簡単にする）が分かる文言
/// （<see cref="SearchPatternBuilder.TimeoutMessage"/>）へ落とすことを固定する。
/// </summary>
public class CrossFileSearchRegexTimeoutTests
{
    /// <summary><c>a</c>の並びの分割の仕方が指数通りあり、末尾が<c>$</c>に到達しないため
    /// 必ず全分割を試して打ち切られるパターン。</summary>
    private const string CatastrophicPattern = "(a+)+$";

    private static string CatastrophicLine => new string('a', 60) + "!";

    private static Project MakeProject(string root) => new() { Id = "p_search_timeout", Name = "search", Root = root };

    private static async Task<List<SearchHit>> CollectAsync(
        CrossFileSearchEngine engine, Project project, CrossFileSearchOptions options,
        SearchRunState state, CancellationToken ct = default)
    {
        var hits = new List<SearchHit>();
        await foreach (var hit in engine.SearchAsync(project, new Settings(), options, state, ct))
        {
            hits.Add(hit);
        }
        return hits;
    }

    [Fact(DisplayName = "対照: 同じパターンと入力で、素の照合は今もタイムアウト例外を投げる")]
    public void 対照_素の照合はタイムアウトする()
    {
        // この対照が無いと、「例外を捕まえた」のか「そもそも例外が起きない条件に
        // なってしまった（＝テストが何も守っていない）」のかを区別できない。
        var (regex, error) = SearchPatternBuilder.TryBuild(CatastrophicPattern, useRegex: true, caseSensitive: false, wholeWord: false);
        error.Should().BeNull("パターンは文法として正しく、コンパイル自体は成功する（壊れるのは照合時）");

        var act = () =>
        {
            foreach (Match _ in regex!.Matches(CatastrophicLine)) { }
        };
        act.Should().Throw<RegexMatchTimeoutException>();
    }

    [Fact(DisplayName = "破滅的バックトラックのパターンでも例外を投げず、分かる文言をPatternErrorへ出す")]
    public async Task 破滅的バックトラックでも例外を投げずPatternErrorになる()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.txt", CatastrophicLine + "\n");
        var state = new SearchRunState();
        var options = new CrossFileSearchOptions { Query = CatastrophicPattern, UseRegex = true };

        var act = async () => await CollectAsync(new CrossFileSearchEngine(), MakeProject(ws.RootPath), options, state);

        await act.Should().NotThrowAsync("例外を素通しすると呼び出し元では「想定外のエラー」としか表示できない");
        state.PatternError.Should().Be(SearchPatternBuilder.TimeoutMessage);
        state.TimedOutByRegex.Should().BeTrue();
    }

    [Fact(DisplayName = "タイムアウトしたら残りのファイルを走査せず検索全体を打ち切る")]
    public async Task タイムアウトしたら検索全体を打ち切る()
    {
        // どのファイルに対しても同じように破滅的バックトラックを起こすため、1ファイル2秒ずつ
        // 待ちながら全ファイルを走り続けても結果は得られない。最初の1回で止めること。
        using var ws = new TempWorkspace();
        for (var i = 0; i < 5; i++)
        {
            ws.WriteText($"f{i}.txt", CatastrophicLine + "\n");
        }
        var state = new SearchRunState();
        var options = new CrossFileSearchOptions { Query = CatastrophicPattern, UseRegex = true };

        var hits = await CollectAsync(new CrossFileSearchEngine(), MakeProject(ws.RootPath), options, state);

        hits.Should().BeEmpty();
        state.TimedOutByRegex.Should().BeTrue();
        state.FilesScanned.Should().Be(1, "最初の1ファイルでタイムアウトを検出したら、そこで打ち切る");
    }

    [Fact(DisplayName = "一括置換でタイムアウトしても例外を投げず、失敗として理由を返す")]
    public async Task 一括置換でタイムアウトしても失敗として返る()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.txt", CatastrophicLine + "\n");
        var target = System.IO.Path.Combine(ws.RootPath, "a.txt");
        var before = System.IO.File.ReadAllText(target);
        var options = new CrossFileSearchOptions { Query = CatastrophicPattern, UseRegex = true };

        var engine = new CrossFileSearchEngine();
        ReplaceOutcome outcome = null!;
        var act = async () => outcome = await engine.ReplaceInFilesAsync(new[] { target }, ws.RootPath, options, "X");

        await act.Should().NotThrowAsync();
        outcome.ReplacedCount.Should().Be(0);
        outcome.FilesChanged.Should().Be(0);
        outcome.Failures.Should().ContainSingle().Which.Reason.Should().Be(SearchPatternBuilder.TimeoutMessage);
        System.IO.File.ReadAllText(target).Should().Be(before, "照合の時点で失敗しているのでファイルは書き換わらない");
    }

    [Fact(DisplayName = "デグレ防止: 正常な正規表現での全体検索はこれまでどおり動く")]
    public async Task 正常な正規表現での全体検索はこれまでどおり動く()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.txt", "foo1\nbar\nfoo22\n");
        ws.WriteText("b.txt", "foo333\n");
        var state = new SearchRunState();
        var options = new CrossFileSearchOptions { Query = @"foo\d+", UseRegex = true };

        var hits = await CollectAsync(new CrossFileSearchEngine(), MakeProject(ws.RootPath), options, state);

        state.PatternError.Should().BeNull();
        state.TimedOutByRegex.Should().BeFalse();
        hits.Select(h => (h.RelativePath, h.LineNumber, h.MatchLength))
            .Should().BeEquivalentTo(new[]
            {
                ("a.txt", 1, 4),
                ("a.txt", 3, 5),
                ("b.txt", 1, 6),
            });
    }

    [Fact(DisplayName = "デグレ防止: 正常な正規表現での一括置換はこれまでどおり動く")]
    public async Task 正常な正規表現での一括置換はこれまでどおり動く()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.txt", "foo1 bar foo22\n");
        var target = System.IO.Path.Combine(ws.RootPath, "a.txt");
        var options = new CrossFileSearchOptions { Query = @"foo(\d+)", UseRegex = true };

        var outcome = await new CrossFileSearchEngine().ReplaceInFilesAsync(
            new[] { target }, ws.RootPath, options, "[$1]");

        outcome.ReplacedCount.Should().Be(2);
        outcome.FilesChanged.Should().Be(1);
        outcome.Failures.Should().BeEmpty();
        System.IO.File.ReadAllText(target).Should().Contain("[1] bar [22]");
    }

    [Fact(DisplayName = "同種の穴: 病的な .gitignore の行があっても除外判定は例外を投げない")]
    public void 病的なGitignoreの行でも除外判定は例外を投げない()
    {
        // .gitignore の各行も利用者が書いた文字列から正規表現を組み立てるため、検索欄と同じく
        // 破滅的バックトラックを起こしうる（"**/" の連続は "(?:.*/)?" の連続になる）。
        // 例外が飛ぶと検索の走査やコンテキスト収集の途中で機能全体が落ちるため、
        // タイムアウト付きで作り、タイムアウトした規則は「一致しなかった」として読み飛ばす。
        var filter = GitignoreFilter.FromPatterns(new[]
        {
            string.Concat(Enumerable.Repeat("**/", 24)) + "見つからない名前",
        });
        var deepPath = string.Join('/', Enumerable.Range(0, 40).Select(i => $"dir{i}")) + "/file.txt";

        var act = () => filter.IsIgnored(deepPath, isDirectory: false);

        act.Should().NotThrow("除外規則1行の書き方のせいで検索やコンテキスト収集が落ちてはならない");
    }

    [Fact(DisplayName = "デグレ防止: 普通の .gitignore の除外判定はこれまでどおり効く")]
    public void 普通のGitignoreの除外判定はこれまでどおり効く()
    {
        var filter = GitignoreFilter.FromPatterns(new[] { "*.log", "build/", "!keep.log" });

        filter.IsIgnored("a/b/app.log", isDirectory: false).Should().BeTrue();
        filter.IsIgnored("keep.log", isDirectory: false).Should().BeFalse();
        filter.IsIgnored("build", isDirectory: true).Should().BeTrue();
        filter.IsIgnored("src/main.cs", isDirectory: false).Should().BeFalse();
    }
}
