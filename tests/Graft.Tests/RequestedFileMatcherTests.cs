using System.Linq;
using FluentAssertions;
using Graft.Features;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// AIが求めたファイルのパスを、コンテキスト収集の走査結果と照合する純ロジック
/// （<see cref="RequestedFileMatcher"/>）のテスト。AIは自分が見ているパスの書き方
/// （バックスラッシュ、先頭の ./、絶対パス付き、大文字小文字の違い）で返してくるため、
/// それらを吸収して実在のファイルに当てられることと、当てられないものを黙って落とさず
/// 理由つきで区別することを固定する。
/// </summary>
public class RequestedFileMatcherTests
{
    private static ContextFileNode File(string path, bool excluded = false, string? reason = null)
        => new() { RelativePath = path, IsExcluded = excluded, ExcludeReason = reason, SizeBytes = 10 };

    private static ContextFileNode Dir(string path, bool excluded = false, string? reason = null)
        => new() { RelativePath = path, IsDirectory = true, IsExcluded = excluded, ExcludeReason = reason };

    private static readonly ContextFileNode[] Scan =
    {
        Dir("src"),
        File("src/Graft/Core/PatchParser.cs"),
        File("src/Graft/Core/Readme.md"),
        File("README.md"),
        File("assets/logo.png", excluded: true, reason: "バイナリ"),
        Dir("node_modules", excluded: true, reason: "既定除外"),
    };

    [Fact(DisplayName = "区切りがスラッシュの相対パスは、そのまま該当する")]
    public void スラッシュの相対パスは該当する()
    {
        var result = RequestedFileMatcher.Match(Scan, new[] { "src/Graft/Core/PatchParser.cs" });

        result.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = RequestedFileMatchKind.Matched,
            RelativePath = "src/Graft/Core/PatchParser.cs",
        });
    }

    [Theory(DisplayName = "区切り文字（\\ と /）・先頭の ./ や /・前後の空白・重なった区切りの違いを吸収して該当する")]
    [InlineData(@"src\Graft\Core\PatchParser.cs")]
    [InlineData("./src/Graft/Core/PatchParser.cs")]
    [InlineData(@".\src\Graft\Core\PatchParser.cs")]
    [InlineData("/src/Graft/Core/PatchParser.cs")]
    [InlineData("  src//Graft/Core/PatchParser.cs  ")]
    [InlineData(@"src/Graft\Core/PatchParser.cs")]
    public void 区切り文字の違いを吸収して該当する(string requested)
    {
        var result = RequestedFileMatcher.Match(Scan, new[] { requested });

        result.Single().Kind.Should().Be(RequestedFileMatchKind.Matched);
        result.Single().RelativePath.Should().Be("src/Graft/Core/PatchParser.cs");
        result.Single().Requested.Should().Be(requested, "表示用にはAIが書いたままを残す");
    }

    [Theory(DisplayName = "大文字小文字の違いを吸収して該当する（完全一致が無いとき、候補が1件に定まる場合）")]
    [InlineData("SRC/GRAFT/CORE/PATCHPARSER.CS", "src/Graft/Core/PatchParser.cs")]
    [InlineData("readme.md", "README.md")]
    public void 大文字小文字の違いを吸収する(string requested, string expected)
    {
        var result = RequestedFileMatcher.Match(Scan, new[] { requested }).Single();

        result.Kind.Should().Be(RequestedFileMatchKind.Matched);
        result.RelativePath.Should().Be(expected, "実在するファイルの正しい表記で返す");
    }

    [Fact(DisplayName = "完全一致があれば、大文字小文字だけが違う別のファイルより優先する")]
    public void 完全一致を優先する()
    {
        var scan = new[] { File("a.cs"), File("A.cs") };

        RequestedFileMatcher.Match(scan, new[] { "A.cs" }).Single().RelativePath.Should().Be("A.cs");
        RequestedFileMatcher.Match(scan, new[] { "a.cs" }).Single().RelativePath.Should().Be("a.cs");
    }

    [Fact(DisplayName = "大文字小文字だけが違う候補が複数あり特定できないときは、片方を選ばず見つからない扱いにする")]
    public void 曖昧な大文字小文字は選ばない()
    {
        var scan = new[] { File("a.cs"), File("A.cs") };

        RequestedFileMatcher.Match(scan, new[] { "A.CS" }).Single().Kind.Should().Be(RequestedFileMatchKind.NotFound);
    }

    [Fact(DisplayName = "プロジェクトルートの絶対パス付きで返されても、相対パスとして該当する（Windows形式・Linux形式とも）")]
    public void 絶対パス付きでも該当する()
    {
        RequestedFileMatcher.Match(Scan, new[] { @"C:\Users\me\proj\src\Graft\Core\PatchParser.cs" }, @"C:\Users\me\proj")
            .Single().RelativePath.Should().Be("src/Graft/Core/PatchParser.cs");
        RequestedFileMatcher.Match(Scan, new[] { "/home/me/proj/README.md" }, "/home/me/proj/")
            .Single().RelativePath.Should().Be("README.md");
        RequestedFileMatcher.Match(Scan, new[] { @"c:\users\me\PROJ\README.md" }, @"C:\Users\me\proj")
            .Single().Kind.Should().Be(RequestedFileMatchKind.Matched, "ドライブ文字・フォルダ名の大文字小文字は区別しない");
    }

    [Fact(DisplayName = "プロジェクト内に見つからないパスは NotFound で報告する")]
    public void 見つからないパスは報告する()
    {
        var result = RequestedFileMatcher.Match(Scan, new[] { "src/Nope.cs", "src/Graft/Core/PatchParser.cs", "../outside.txt" });

        result.Select(r => r.Kind).Should().Equal(
            RequestedFileMatchKind.NotFound, RequestedFileMatchKind.Matched, RequestedFileMatchKind.NotFound);
        result[0].Requested.Should().Be("src/Nope.cs");
        result[0].RelativePath.Should().BeNull();
    }

    [Fact(DisplayName = "除外されていて選べないファイルは Excluded として理由つきで報告する")]
    public void 除外されたファイルは理由つきで報告する()
    {
        var result = RequestedFileMatcher.Match(Scan, new[] { @"assets\logo.png" }).Single();

        result.Kind.Should().Be(RequestedFileMatchKind.Excluded);
        result.ExcludeReason.Should().Be("バイナリ");
        result.RelativePath.Should().Be("assets/logo.png");
    }

    [Fact(DisplayName = "除外ディレクトリの配下のファイル（走査されていない）も、見つからないではなく除外として報告する")]
    public void 除外ディレクトリ配下は除外として報告する()
    {
        var result = RequestedFileMatcher.Match(Scan, new[] { "node_modules/left-pad/index.js" }).Single();

        result.Kind.Should().Be(RequestedFileMatchKind.Excluded);
        result.ExcludeReason.Should().Be("既定除外");
        result.RelativePath.Should().Be("node_modules");
    }

    [Fact(DisplayName = "フォルダを指された場合は Directory として区別する（ファイルとして選べないことを伝えるため）")]
    public void フォルダは区別する()
    {
        var result = RequestedFileMatcher.Match(Scan, new[] { "src/", "src" });

        result.Select(r => r.Kind).Should().OnlyContain(k => k == RequestedFileMatchKind.Directory);
    }

    [Theory(DisplayName = "空・空白・区切りだけのパスは見つからない扱いにする")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("./")]
    public void 空のパスは見つからない(string requested)
        => RequestedFileMatcher.Match(Scan, new[] { requested }).Single().Kind.Should().Be(RequestedFileMatchKind.NotFound);

    [Fact(DisplayName = "結果は要求と同じ並び・同じ件数で返る")]
    public void 結果は要求と同じ並びで返る()
    {
        var requested = new[] { "README.md", "x.cs", "assets/logo.png", "src/Graft/Core/Readme.md" };

        var result = RequestedFileMatcher.Match(Scan, requested);

        result.Select(r => r.Requested).Should().Equal(requested);
        result.Select(r => r.Kind).Should().Equal(
            RequestedFileMatchKind.Matched, RequestedFileMatchKind.NotFound,
            RequestedFileMatchKind.Excluded, RequestedFileMatchKind.Matched);
    }
}
