using FluentAssertions;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// コンテキスト収集の窓の「ファイル名で絞り込み」の照合（<see cref="FileNameFilter"/>）の単体テスト。
/// 部分一致・大文字小文字・区切り文字・複数語のAND・空入力の扱いを固定する。
/// </summary>
public class FileNameFilterTests
{
    private static bool Match(string path, string query)
        => FileNameFilter.Matches(path, FileNameFilter.ParseTerms(query));

    [Theory(DisplayName = "相対パスの一部を含めば一致し、含まなければ一致しない（フォルダ名も対象）")]
    [InlineData("src/Views/MainView.cs", "mainview", true)]
    [InlineData("src/Views/MainView.cs", "views/main", true)]
    [InlineData("src/Views/MainView.cs", "src", true)]
    [InlineData("src/Views/MainView.cs", ".cs", true)]
    [InlineData("src/Views/MainView.cs", "model", false)]
    [InlineData("src/Views/MainView.cs", "mainview.axaml", false)]
    public void 部分一致で照合される(string path, string query, bool expected)
        => Match(path, query).Should().Be(expected);

    [Theory(DisplayName = "大文字小文字は区別しない")]
    [InlineData("README.md", "readme", true)]
    [InlineData("readme.md", "README", true)]
    [InlineData("docs/変更履歴.md", "変更履歴", true)]
    [InlineData("src/Ünicode.cs", "ünicode", true)]
    public void 大文字小文字を区別しない(string path, string query, bool expected)
        => Match(path, query).Should().Be(expected);

    [Theory(DisplayName = "区切りの「\\」と「/」は同一視する（入力側・パス側のどちらがバックスラッシュでも）")]
    [InlineData("src/Views/MainView.cs", @"Views\Main", true)]
    [InlineData("src/Views/MainView.cs", "Views/Main", true)]
    [InlineData(@"src\Views\MainView.cs", "Views/Main", true)]
    [InlineData(@"src\Views\MainView.cs", @"src\Views", true)]
    [InlineData("src/Views/MainView.cs", @"Models\Main", false)]
    public void 区切り文字を同一視する(string path, string query, bool expected)
        => Match(path, query).Should().Be(expected);

    [Fact(DisplayName = "空白区切りの複数語は、すべてを含むものだけに一致する（AND・順序は問わない）")]
    public void 複数語はANDで照合される()
    {
        Match("src/api/user.ts", "api user").Should().BeTrue();
        Match("src/api/user.ts", "user api").Should().BeTrue("語の順序は問わない");
        Match("src/api/order.ts", "api user").Should().BeFalse("userを含まないので一致しない");
        Match("src/web/user.ts", "api user").Should().BeFalse("apiを含まないので一致しない");
        Match("src/api/user.ts", "api  user\t.ts").Should().BeTrue("連続した空白・タブでも区切れる");
    }

    [Fact(DisplayName = "全角空白でも語を区切れる（日本語入力中に入りやすいため）")]
    public void 全角空白でも区切れる()
    {
        FileNameFilter.ParseTerms("api　user").Should().Equal("api", "user");
        Match("src/api/user.ts", "api　user").Should().BeTrue();
        Match("src/api/order.ts", "api　user").Should().BeFalse();
    }

    [Theory(DisplayName = "空欄・空白だけの入力は語を持たず、絞り込みなし（すべてに一致）として扱う")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　 \t")]
    public void 空入力は絞り込みなし(string? query)
    {
        FileNameFilter.ParseTerms(query).Should().BeEmpty();
        Match("anything/at/all.txt", query ?? string.Empty).Should().BeTrue();
    }

    [Fact(DisplayName = "語の前後の空白は無視され、バックスラッシュは語の中でもスラッシュに揃う")]
    public void 語が正規化される()
    {
        FileNameFilter.ParseTerms("  src\\api   user  ").Should().Equal("src/api", "user");
    }

    [Fact(DisplayName = "10万件のパスに対する1回の絞り込みが、入力の引っかかりにならない時間で終わる")]
    public void 十万件でも短時間で絞り込める()
    {
        var paths = Enumerable.Range(0, 100_000)
            .Select(i => $"src/module{i % 500}/feature{i / 500}/Component{i}.cs")
            .ToArray();
        var terms = FileNameFilter.ParseTerms("feature7 component35");

        // JITの初回コストを除くため、同じ処理を1回流してから測る。
        _ = paths.Count(p => FileNameFilter.Matches(p, terms));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var matched = paths.Count(p => FileNameFilter.Matches(p, terms));
        stopwatch.Stop();

        matched.Should().BeGreaterThan(0);
        // 実測は数十ミリ秒。CIの揺れを見込んで十分緩い上限にしてある（桁違いに遅くなる退行だけを捉える）。
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(1500);
    }

    [Theory(DisplayName = "正規表現やワイルドカードの記号は、打った文字そのものとして探す（例外にも全件一致にもならない）")]
    [InlineData("a[1].txt", "[1]", true)]
    [InlineData("a1.txt", "[1]", false)]
    [InlineData("a.txt", "*", false)]
    [InlineData("a*b.txt", "*", true)]
    [InlineData("c++/main.cpp", "c++", true)]
    public void 記号はリテラルとして扱われる(string path, string query, bool expected)
        => Match(path, query).Should().Be(expected);
}
